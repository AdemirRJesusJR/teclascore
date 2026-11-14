using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>A sung syllable at a MIDI tick. The text ends with "-" when the word goes on, with a space when it ends.</summary>
public sealed record NotationSyllable(long Tick, string Text);

/// <summary>
/// The lyrics of a MusicXML or Guitar Pro file, placed on the ticks of the MIDI MuseScore made from it.
///
/// MuseScore's MIDI carries no lyrics, so the same MuseScore run also writes the score as MusicXML (the syllables on
/// their notes) and the measure positions in playback order (.mpos: repeats and voltas already unrolled, in
/// milliseconds). Each time a measure is played, its syllables go to the measure's start plus their place in it; a
/// measure with a second verse uses it on the second pass.
///
/// Guitar Pro 3 to 5 files are the exception: MuseScore numbers the lyric beats across all tracks and scatters the
/// words, so the lyric line is read from the file itself and laid on the notes of its track, one syllable per beat,
/// as Guitar Pro does (spaces and hyphens move to the next beat, "+" keeps two words on one beat, [comments] are left out).
/// </summary>
public static class NotationLyrics
{
    /// <summary>The engine and the library take fewer syllables than this for credits, not for a lyric.</summary>
    public const int MinimumSyllables = 16;

    private sealed record Beat(double Quarter, bool Rest, int Staff, int Voice);
    private sealed record Lyric(double Quarter, string Verse, string Text, bool Continues);
    private sealed class Measure { public List<Beat> Beats { get; } = []; public List<Lyric> Lyrics { get; } = []; }
    public sealed record GuitarProLine(int StartMeasure, string Text);
    public sealed record GuitarProLyrics(int Track, IReadOnlyList<GuitarProLine> Lines);

    /// <summary>
    /// The syllables in singing order, or an empty list when the file has no lyric. Throws InvalidDataException when the
    /// exports do not describe the same score, or when the syllables found do not fall on the MIDI's notes.
    /// </summary>
    public static IReadOnlyList<NotationSyllable> Read(string source, string musicXmlPath, string measurePositionsPath, MidiDocument midi)
    {
        if (!File.Exists(musicXmlPath) || !File.Exists(measurePositionsPath)) return [];
        var parts = ReadParts(musicXmlPath);
        if (parts.Count == 0) return [];
        List<Measure> part;
        if (Path.GetExtension(source).ToLowerInvariant() is ".gp3" or ".gp4" or ".gp5" or ".gtp")
        {
            var declared = ReadGuitarProLyrics(source);
            var line = declared?.Lines.FirstOrDefault(l => Chunks(l.Text).Count > 0);
            if (declared is null || line is null || declared.Track < 1 || declared.Track > parts.Count) return [];
            part = parts[declared.Track - 1];
            foreach (var measure in part) measure.Lyrics.Clear(); // MuseScore's scattered words
            Distribute(part, Math.Max(0, line.StartMeasure - 1), Chunks(line.Text));
        }
        else part = parts.MaxBy(p => p.Sum(m => m.Lyrics.Count))!;
        if (part.Sum(m => m.Lyrics.Count) == 0) return [];

        var playback = ReadPlayback(measurePositionsPath);
        if (playback.Count == 0 || playback.Any(p => p.Measure >= part.Count))
            throw new InvalidDataException("os compassos exportados pelo MuseScore não correspondem aos da partitura.");
        // Verses in the order the file numbers them: "1", "2"… (or in order of appearance when they are named).
        var verses = part.SelectMany(m => m.Lyrics).Select(l => l.Verse).Distinct()
            .OrderBy(v => int.TryParse(v, out int number) ? number : int.MaxValue).ToList();
        var tempo = new TempoMap(midi);
        long[] attacks = midi.Notes.Where(n => n.Channel != 10).Select(n => n.Start).Distinct().Order().ToArray();
        var passes = new Dictionary<int, int>();
        var result = new List<NotationSyllable>();
        foreach (var (index, milliseconds) in playback)
        {
            int pass = passes.GetValueOrDefault(index); passes[index] = pass + 1;
            var measure = part[index];
            if (measure.Lyrics.Count == 0) continue;
            var present = measure.Lyrics.Select(l => verses.IndexOf(l.Verse)).Distinct().Order().ToArray();
            // The second time through, the second verse; a measure without it (a chorus, a volta) repeats what it has.
            int verse = present.Contains(pass) ? pass : present[0];
            long start = tempo.TickAt(milliseconds / 1000);
            foreach (var lyric in measure.Lyrics.Where(l => verses.IndexOf(l.Verse) == verse).OrderBy(l => l.Quarter))
                result.Add(new(Snap(attacks, start + (long)Math.Round(lyric.Quarter * midi.Ppq), Math.Max(2, midi.Ppq / 120)), lyric.Text + (lyric.Continues ? "-" : " ")));
        }
        result = result.OrderBy(s => s.Tick).ToList();
        long tolerance = Math.Max(1, midi.Ppq / 8);
        int sung = result.Count(s => Near(attacks, s.Tick, tolerance));
        if (sung * 2 < result.Count) throw new InvalidDataException("a letra do arquivo não coincide com as notas tocadas.");
        return result;
    }

    private static bool Near(long[] sorted, long tick, long tolerance)
    {
        int index = Array.BinarySearch(sorted, tick); if (index >= 0) return true;
        index = ~index;
        return index < sorted.Length && sorted[index] - tick <= tolerance || index > 0 && tick - sorted[index - 1] <= tolerance;
    }

    /// <summary>The nearest attack when it is within <paramref name="tolerance"/> ticks (millisecond positions round), else the tick itself.</summary>
    private static long Snap(long[] sorted, long tick, long tolerance)
    {
        int index = Array.BinarySearch(sorted, tick); if (index >= 0) return tick;
        index = ~index;
        long best = tick, distance = tolerance + 1;
        if (index < sorted.Length && sorted[index] - tick < distance) { best = sorted[index]; distance = best - tick; }
        if (index > 0 && tick - sorted[index - 1] < distance) best = sorted[index - 1];
        return best;
    }

    /// <summary>Guitar Pro places one syllable on each beat that has notes, starting at the line's first measure.</summary>
    private static void Distribute(List<Measure> part, int firstMeasure, IReadOnlyList<(string Text, bool Continues)> chunks)
    {
        var all = part.SelectMany(m => m.Beats).ToArray();
        if (all.Length == 0) return;
        int staff = all.Min(b => b.Staff), voice = all.Where(b => b.Staff == staff).Min(b => b.Voice);
        int next = 0;
        for (int index = firstMeasure; index < part.Count && next < chunks.Count; index++)
            foreach (var beat in part[index].Beats.Where(b => !b.Rest && b.Staff == staff && b.Voice == voice).OrderBy(b => b.Quarter))
            {
                if (next >= chunks.Count) break;
                part[index].Lyrics.Add(new(beat.Quarter, "1", chunks[next].Text, chunks[next].Continues)); next++;
            }
    }

    /// <summary>
    /// Syllables of a Guitar Pro lyric line: a space or a line break ends a word, a hyphen ends a syllable of a word that
    /// goes on, "+" joins two words on the same beat and text in [brackets] is a comment.
    /// </summary>
    public static IReadOnlyList<(string Text, bool Continues)> Chunks(string line)
    {
        var chunks = new List<(string, bool)>(); var current = new StringBuilder(); bool comment = false;
        void End(bool continues)
        {
            string text = current.ToString().Trim(); current.Clear();
            if (text.Length > 0) chunks.Add((text, continues));
        }
        foreach (char c in line)
        {
            if (comment) { if (c == ']') comment = false; continue; }
            if (c == '[') comment = true;
            else if (char.IsWhiteSpace(c) || char.IsControl(c)) End(false);
            else if (c == '-') End(true);
            else current.Append(c == '+' ? ' ' : c);
        }
        End(false);
        return chunks;
    }

    /// <summary>
    /// The lyric block of a Guitar Pro 4 or 5 file (version 3 has none): the track it belongs to (from 1) and up to five
    /// lines, each with its first measure (from 1). It comes right after the song information.
    /// </summary>
    public static GuitarProLyrics? ReadGuitarProLyrics(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.Latin1);
            int length = reader.ReadByte(); byte[] raw = reader.ReadBytes(30);
            if (raw.Length < 30) return null;
            string version = Encoding.ASCII.GetString(raw, 0, Math.Min(length, 30));
            if (!version.StartsWith("FICHIER GUITAR PRO", StringComparison.Ordinal)) return null;
            bool five = version.Contains("v5", StringComparison.Ordinal);
            if (!five && !version.Contains("v4", StringComparison.Ordinal)) return null;
            void Skip() { int total = reader.ReadInt32(); if (total is < 1 or > 1 << 20) throw new InvalidDataException(); if (reader.ReadBytes(total).Length < total) throw new EndOfStreamException(); }
            // title, subtitle, artist, album, words, (music), copyright, tab, instructions; then the notice lines.
            for (int field = 0; field < (five ? 9 : 8); field++) Skip();
            int notices = reader.ReadInt32(); if (notices is < 0 or > 4096) throw new InvalidDataException();
            for (int i = 0; i < notices; i++) Skip();
            if (!five) reader.ReadByte(); // triplet feel
            int track = reader.ReadInt32(); var lines = new List<GuitarProLine>();
            for (int i = 0; i < 5; i++)
            {
                int start = reader.ReadInt32(), size = reader.ReadInt32();
                if (size is < 0 or > 1 << 22) throw new InvalidDataException();
                byte[] text = reader.ReadBytes(size); if (text.Length < size) throw new EndOfStreamException();
                lines.Add(new(start, DecodeText(text)));
            }
            return new(track, lines);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    private static string DecodeText(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            const string windows1252 = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008dŽ\u008f\u0090‘’“”•–—˜™š›œ\u009džŸ";
            return new string(bytes.Select(b => b is >= 0x80 and <= 0x9F ? windows1252[b - 0x80] : (char)b).ToArray());
        }
    }

    /// <summary>Every part of a partwise MusicXML: per measure, the beats (chords and rests with their place) and the syllables.</summary>
    private static List<List<Measure>> ReadParts(string path)
    {
        if (new FileInfo(path).Length > 128 * 1024 * 1024) throw new InvalidDataException("a partitura exportada é grande demais para ler a letra.");
        XDocument document;
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "score-partwise") return [];
        var parts = new List<List<Measure>>();
        foreach (var partElement in document.Root.Elements("part"))
        {
            var measures = new List<Measure>(); double divisions = 1;
            foreach (var measureElement in partElement.Elements("measure"))
            {
                var measure = new Measure(); double cursor = 0, onset = 0;
                foreach (var item in measureElement.Elements())
                {
                    switch (item.Name.LocalName)
                    {
                        case "attributes":
                            if (Number(item.Element("divisions")) is { } value and > 0) divisions = value;
                            break;
                        case "backup": cursor = Math.Max(0, cursor - (Number(item.Element("duration")) ?? 0)); break;
                        case "forward": cursor += Number(item.Element("duration")) ?? 0; break;
                        case "note":
                            bool chord = item.Element("chord") is not null, grace = item.Element("grace") is not null;
                            if (!chord) onset = cursor;
                            if (!chord && !grace)
                            {
                                cursor += Number(item.Element("duration")) ?? 0;
                                if (item.Element("cue") is null)
                                    measure.Beats.Add(new(onset / divisions, item.Element("rest") is not null,
                                        (int)(Number(item.Element("staff")) ?? 1), (int)(Number(item.Element("voice")) ?? 1)));
                            }
                            foreach (var lyric in item.Elements("lyric"))
                            {
                                // An elision puts two words on the note: both texts, with a space.
                                string text = string.Join(' ', lyric.Elements("text").Select(t => t.Value.Trim()).Where(t => t.Length > 0));
                                if (text.Length == 0) continue;
                                string syllabic = lyric.Elements("syllabic").LastOrDefault()?.Value.Trim() ?? "single";
                                measure.Lyrics.Add(new(onset / divisions, lyric.Attribute("number")?.Value ?? "1", text, syllabic is "begin" or "middle"));
                            }
                            break;
                    }
                }
                measures.Add(measure);
            }
            parts.Add(measures);
        }
        return parts;
    }

    private static double? Number(XElement? element) =>
        element is not null && double.TryParse(element.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;

    /// <summary>MuseScore's measure positions: (measure index from 0, milliseconds) each time a measure is played, in order.</summary>
    private static List<(int Measure, double Milliseconds)> ReadPlayback(string path)
    {
        XDocument document;
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            document = XDocument.Load(reader);
        var events = document.Root?.Element("events")?.Elements("event").Select(e => (Measure: (int)(Number(e.Attribute("elid")) ?? -1), Milliseconds: Number(e.Attribute("position")) ?? -1)).ToList() ?? [];
        if (events.Any(e => e.Measure < 0 || e.Milliseconds < 0)) throw new InvalidDataException("as posições dos compassos exportadas pelo MuseScore são inválidas.");
        return events;
    }

    private static double? Number(XAttribute? attribute) =>
        attribute is not null && double.TryParse(attribute.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;

    /// <summary>Seconds to ticks along the MIDI's tempo changes.</summary>
    private sealed class TempoMap
    {
        private readonly List<(long Tick, double Seconds, int Tempo)> segments = [(0, 0, 500000)];
        private readonly int ppq;
        internal TempoMap(MidiDocument midi)
        {
            ppq = midi.Ppq;
            foreach (var change in midi.TempoChanges.OrderBy(t => t.Tick).ThenBy(t => t.Track).ThenBy(t => t.Order))
            {
                var previous = segments[^1];
                segments.Add((change.Tick, previous.Seconds + (change.Tick - previous.Tick) * previous.Tempo / (1000000d * ppq), change.MicrosecondsPerQuarter));
            }
        }
        internal long TickAt(double seconds)
        {
            var segment = segments.Last(s => s.Seconds <= seconds + 1e-9);
            return segment.Tick + (long)Math.Round((seconds - segment.Seconds) * 1000000d * ppq / segment.Tempo);
        }
    }

    /// <summary>Adds the syllables as lyric events (type 5) in a new last track, so the tracks with notes keep their numbers.</summary>
    public static byte[] Append(byte[] midi, IReadOnlyList<NotationSyllable> syllables)
    {
        if (midi.Length < 14 || Encoding.ASCII.GetString(midi, 0, 4) != "MThd") throw new InvalidDataException("MIDI inválido.");
        int tracks = (midi[10] << 8) | midi[11];
        if (tracks >= 0xFFFF) throw new InvalidDataException("O MIDI não comporta mais uma pista.");
        var track = new List<byte>(); long last = 0;
        foreach (var syllable in syllables.OrderBy(s => s.Tick))
        {
            long delta = Math.Max(0, syllable.Tick - last); last = Math.Max(last, syllable.Tick);
            WriteVlq(track, delta);
            byte[] text = Encoding.UTF8.GetBytes(syllable.Text);
            track.Add(0xFF); track.Add(0x05); WriteVlq(track, text.Length); track.AddRange(text);
        }
        track.AddRange([0, 0xFF, 0x2F, 0]);
        byte[] result = [.. midi, .. "MTrk"u8, (byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count, .. track];
        // A single-track file (format 0) becomes a multi-track one.
        if (result[8] == 0 && result[9] == 0) result[9] = 1;
        result[10] = (byte)((tracks + 1) >> 8); result[11] = (byte)(tracks + 1);
        return result;
    }

    internal static void WriteVlq(List<byte> bytes, long value)
    {
        if (value is < 0 or > 0x0FFFFFFF) throw new InvalidDataException("Intervalo MIDI longo demais.");
        Span<byte> buffer = stackalloc byte[4]; int count = 0;
        do { buffer[count++] = (byte)(value & 0x7F); value >>= 7; } while (value > 0);
        for (int i = count - 1; i >= 0; i--) bytes.Add((byte)(buffer[i] | (i > 0 ? 0x80 : 0)));
    }
}
