using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>Speed (0.25–1.5), one measure of count-in clicks, and whether the melody is muted (only the chords sound).</summary>
public sealed record StudyAudioOptions(double Speed, bool CountIn, bool AccompanimentOnly);

/// <summary>
/// An MP3 for practicing away from the computer, rendered by MuseScore from private copies of the score: slowed down,
/// optionally without the melody (the chord symbols keep playing), optionally preceded by a measure of woodblock clicks
/// at the song's tempo. The user's score never changes.
/// </summary>
public static class StudyAudioExport
{
    public static async Task ExportAsync(string museScorePath, string scorePath, string target, StudyAudioOptions options, string workDirectory, CancellationToken token)
    {
        if (!ScoreStudyAudio.IsSupportedSpeed(options.Speed)) throw new ArgumentOutOfRangeException(nameof(options));
        Directory.CreateDirectory(workDirectory);
        string input = Path.Combine(workDirectory, "origem.mscz");
        File.Copy(scorePath, input, true);
        if (options.AccompanimentOnly) { string muted = Path.Combine(workDirectory, "acompanhamento.mscz"); MuteMelody(input, muted); input = muted; }
        if (options.Speed != 1) { string slower = Path.Combine(workDirectory, "estudo.mscz"); ScoreStudyAudio.CreateTempoCopy(input, slower, options.Speed); input = slower; }
        string music = Path.Combine(workDirectory, "musica.mp3");
        await RunAsync(museScorePath, music, input, token).ConfigureAwait(false);
        byte[] audio = await File.ReadAllBytesAsync(music, token).ConfigureAwait(false);
        if (options.CountIn)
        {
            var tempo = ScoreTempo.Read(scorePath);
            string countIn = Path.Combine(workDirectory, "contagem.musicxml"), clicks = Path.Combine(workDirectory, "contagem.mp3");
            await File.WriteAllTextAsync(countIn, CountInMusicXml(tempo with { Bpm = tempo.Bpm * options.Speed }), token).ConfigureAwait(false);
            await RunAsync(museScorePath, clicks, countIn, token).ConfigureAwait(false);
            // MuseScore adds a silent tail after the last click; only one measure of the count-in is kept.
            audio = Concatenate(await File.ReadAllBytesAsync(clicks, token).ConfigureAwait(false), audio, tempo.Beats * tempo.BeatSeconds / options.Speed);
        }
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, ".teclas-" + Guid.NewGuid().ToString("N") + ".mp3");
        try { await File.WriteAllBytesAsync(temporary, audio, token).ConfigureAwait(false); File.Move(temporary, target, true); }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>Copies a score with every note silenced (play = 0); chord symbols and the rest of the file are untouched.</summary>
    public static void MuteMelody(string source, string destination)
    {
        File.Copy(source, destination, true);
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Update);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("A partitura não contém o arquivo principal do MuseScore.");
        XDocument document; using (var stream = entry.Open()) document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        foreach (var note in document.Descendants("Note"))
        {
            var play = note.Element("play");
            if (play is null) note.Add(new XElement("play", "0")); else play.Value = "0";
        }
        string name = entry.FullName; entry.Delete();
        using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        document.Save(output);
    }

    /// <summary>One measure in the song's meter on a woodblock (GM 116): a high click on the first beat, low ones after.</summary>
    public static string CountInMusicXml(ScoreTempo tempo)
    {
        int divisions = 4, beat = tempo.Compound ? 6 : 16 / tempo.Denominator;
        string type = tempo.Compound ? "quarter" : tempo.Denominator switch { 1 => "whole", 2 => "half", 8 => "eighth", 16 => "16th", _ => "quarter" };
        string notes = string.Concat(Enumerable.Range(0, tempo.Beats).Select(i =>
            $"<note><pitch><step>{(i == 0 ? "E" : "C")}</step><octave>5</octave></pitch><duration>{beat}</duration><type>{type}</type>{(tempo.Compound ? "<dot/>" : "")}</note>"));
        string bpm = tempo.Bpm.ToString("0.###", CultureInfo.InvariantCulture);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?><score-partwise version=\"4.0\"><part-list><score-part id=\"P1\"><part-name>Contagem</part-name>" +
            "<score-instrument id=\"I1\"><instrument-name>Woodblock</instrument-name></score-instrument><midi-instrument id=\"I1\"><midi-channel>1</midi-channel><midi-program>116</midi-program></midi-instrument>" +
            $"</score-part></part-list><part id=\"P1\"><measure number=\"1\"><attributes><divisions>{divisions}</divisions><key><fifths>0</fifths></key>" +
            $"<time><beats>{tempo.Numerator}</beats><beat-type>{tempo.Denominator}</beat-type></time><clef><sign>G</sign><line>2</line></clef></attributes>" +
            $"<direction placement=\"above\"><direction-type><metronome><beat-unit>quarter</beat-unit><per-minute>{bpm}</per-minute></metronome></direction-type><sound tempo=\"{bpm}\"/></direction>" +
            notes + "</measure></part></score-partwise>";
    }

    /// <summary>
    /// Joins two MP3 files of the same format frame by frame: ID3 tags and the Xing/Info header frames are dropped, so
    /// players measure the length from the frames themselves.
    /// </summary>
    public static byte[] Concatenate(byte[] first, byte[] second, double firstSeconds = double.PositiveInfinity)
    {
        using var output = new MemoryStream(); double kept = 0;
        foreach (var (offset, length, seconds) in Frames(first))
        {
            if (kept >= firstSeconds - 1e-6) break;
            output.Write(first, offset, length); kept += seconds;
        }
        foreach (var (offset, length, _) in Frames(second)) output.Write(second, offset, length);
        return output.ToArray();
    }

    private static readonly int[] Mpeg1Rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] Mpeg2Rates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    /// <summary>Audio frames of a Layer III file (MPEG 1, 2 or 2.5), without tags and without the Xing/Info frame.</summary>
    public static IEnumerable<(int Offset, int Length, double Seconds)> Frames(byte[] data)
    {
        int position = 0;
        if (data.Length > 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
            position = 10 + (data[6] << 21 | data[7] << 14 | data[8] << 7 | data[9]) + ((data[5] & 0x10) != 0 ? 10 : 0);
        bool firstFrame = true;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF || (data[position + 1] & 0xE0) != 0xE0) { position++; continue; }
            int version = (data[position + 1] >> 3) & 3, layer = (data[position + 1] >> 1) & 3;
            int bitrateIndex = data[position + 2] >> 4, rateIndex = (data[position + 2] >> 2) & 3, padding = (data[position + 2] >> 1) & 1;
            if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) { position++; continue; }
            int baseRate = new[] { 44100, 48000, 32000 }[rateIndex];
            int sampleRate = version == 3 ? baseRate : version == 2 ? baseRate / 2 : baseRate / 4;
            int bitrate = (version == 3 ? Mpeg1Rates : Mpeg2Rates)[bitrateIndex] * 1000;
            int length = (version == 3 ? 144 : 72) * bitrate / sampleRate + padding;
            if (length < 4 || position + length > data.Length) break;
            bool header = firstFrame && ContainsTag(data, position, length);
            firstFrame = false;
            if (!header) yield return (position, length, (version == 3 ? 1152.0 : 576.0) / sampleRate);
            position += length;
        }
    }

    private static bool ContainsTag(byte[] data, int offset, int length)
    {
        for (int i = offset + 4; i + 4 <= offset + Math.Min(length, 64); i++)
            if (data[i] == 'X' && data[i + 1] == 'i' && data[i + 2] == 'n' && data[i + 3] == 'g' || data[i] == 'I' && data[i + 1] == 'n' && data[i + 2] == 'f' && data[i + 3] == 'o')
                return true;
        return false;
    }

    private static async Task RunAsync(string museScorePath, string output, string input, CancellationToken token)
    {
        var start = new ProcessStartInfo(museScorePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-o"); start.ArgumentList.Add(output); start.ArgumentList.Add(input);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o MuseScore.");
        var errors = process.StandardError.ReadToEndAsync(token); _ = process.StandardOutput.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            throw new InvalidOperationException($"O MuseScore não gerou o áudio (código {process.ExitCode}). {(await errors.ConfigureAwait(false)).Trim()}".Trim());
    }
}

/// <summary>Length of a MIDI file: the last note's end, through its tempo changes.</summary>
public static class MidiDuration
{
    public static double Seconds(MidiDocument midi)
    {
        if (midi.Notes.Count == 0 || midi.Ppq <= 0) return 0;
        long end = midi.Notes.Max(note => note.End);
        double seconds = 0; long tick = 0; int tempo = 500000;
        foreach (var change in midi.TempoChanges.Where(c => c.Tick < end).OrderBy(c => c.Tick))
        { seconds += (change.Tick - tick) * tempo / (1e6 * midi.Ppq); tick = change.Tick; tempo = change.MicrosecondsPerQuarter; }
        return seconds + (end - tick) * tempo / (1e6 * midi.Ppq);
    }

    public static string Format(double seconds) => seconds <= 0 ? "—" : $"{(int)(seconds / 60)}:{(int)seconds % 60:00}";
}

/// <summary>The keys of common keyboards and whether a melody fits them, with the octave move that makes it fit.</summary>
public static class KeyboardRange
{
    public static readonly IReadOnlyList<int> Sizes = [49, 61, 76, 88];

    /// <summary>Lowest and highest MIDI pitch: 49 keys C2–C6, 61 keys C2–C7, 76 keys E1–G7, 88 keys A0–C8.</summary>
    public static (int Low, int High) Of(int keys) => keys switch { 49 => (36, 84), 76 => (28, 103), 88 => (21, 108), _ => (36, 96) };

    /// <summary>0 when it fits; ±1 or ±2 octaves when moving makes it fit; null when the melody is wider than the keyboard.</summary>
    public static int? OctaveToFit(int low, int high, int keys)
    {
        var (min, max) = Of(keys);
        foreach (int octave in new[] { 0, -1, 1, -2, 2 })
            if (low + 12 * octave >= min && high + 12 * octave <= max) return octave;
        return null;
    }
}

/// <summary>Library cards that look like the same song: the same file (hash) or the same name once copies' marks are removed.</summary>
public sealed record DuplicateCandidate(string Id, string Title, string? Sha256, string Path);
public sealed record DuplicateGroup(string Reason, IReadOnlyList<DuplicateCandidate> Items);

public static class DuplicateFinder
{
    public static IReadOnlyList<DuplicateGroup> Find(IReadOnlyList<DuplicateCandidate> items)
    {
        var groups = new List<DuplicateGroup>(); var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var same in items.Where(i => !string.IsNullOrEmpty(i.Sha256)).GroupBy(i => i.Sha256!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        { groups.Add(new("Mesmo arquivo", same.ToArray())); foreach (var item in same) grouped.Add(item.Id); }
        foreach (var named in items.Where(i => !grouped.Contains(i.Id)).GroupBy(i => Normalize(i.Title)).Where(g => g.Key.Length > 0 && g.Count() > 1))
            groups.Add(new("Mesmo nome", named.ToArray()));
        return groups;
    }

    /// <summary>Lower case, no accents or punctuation, without copy marks such as "(1)", "- cópia", "copy" or "final".</summary>
    public static string Normalize(string title)
    {
        string text = new string((title ?? "").Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\(\s*\d+\s*\)|\b(copia|copy|final|novo|nova|v\d+)\b", " ");
        text = new string(text.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
