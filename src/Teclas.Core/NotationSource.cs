using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Teclas.Core;

/// <summary>Title and names declared inside a MusicXML or Guitar Pro file; empty strings when absent.</summary>
public sealed record NotationMetadata(string Title, string Artist, string Composer)
{
    public static readonly NotationMetadata Empty = new("", "", "");
    public bool IsEmpty => Title.Length == 0 && Artist.Length == 0 && Composer.Length == 0;
}

/// <summary>
/// Songs written as notation by other programs (MusicXML, Guitar Pro). They enter the library as MIDI: MuseScore plays
/// the file into a new MIDI (one track per instrument, repeats unrolled) and the original is never changed. The MIDI
/// carries the title and names declared in the source, plus the source's hash, so the same file is converted only once.
/// </summary>
public static class NotationSource
{
    public static readonly string[] MusicXmlExtensions = [".musicxml", ".mxl", ".xml"];
    public static readonly string[] GuitarProExtensions = [".gp", ".gp3", ".gp4", ".gp5", ".gpx", ".gtp"];
    /// <summary>Subfolder of the output folder that receives the converted MIDIs.</summary>
    public const string Folder = "Convertidos para MIDI";
    private const string OriginTag = "@ITeclaScore origem=";
    // Names editors write when the author left the field untouched.
    private static readonly string[] Placeholders =
        ["untitled", "untitled score", "title", "sem título", "sem titulo", "partitura sem título", "partitura sem titulo", "título", "titulo",
         "composer", "composer / arranger", "compositor", "compositor / arranjador", "artist", "artista", "unknown", "desconhecido"];

    public static bool IsMusicXml(string path) => MusicXmlExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsGuitarPro(string path) => GuitarProExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsSupported(string path) => IsMusicXml(path) || IsGuitarPro(path);
    public static string KindName(string path) => IsGuitarPro(path) ? "Guitar Pro" : "MusicXML";

    /// <summary>File-dialog pattern lists ("*.musicxml;*.mxl;*.xml").</summary>
    public static string MusicXmlPattern => string.Join(";", MusicXmlExtensions.Select(x => "*" + x));
    public static string GuitarProPattern => string.Join(";", GuitarProExtensions.Select(x => "*" + x));

    /// <summary>
    /// A plain .xml can be anything: only a document whose root is a MusicXML score is accepted. The other extensions
    /// are left for MuseScore to judge.
    /// </summary>
    public static bool IsScoreFile(string path)
    {
        if (!IsSupported(path)) return false;
        if (!string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var reader = XmlReader.Create(path, XmlSettings);
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element) return reader.LocalName is "score-partwise" or "score-timewise";
        }
        catch (Exception error) when (error is XmlException or IOException or UnauthorizedAccessException) { }
        return false;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Title, performer and composer as declared in the file. A file this reader does not understand gives no metadata.</summary>
    public static NotationMetadata ReadMetadata(string path)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".musicxml" or ".xml":
                    using (var stream = File.OpenRead(path)) return ReadMusicXml(stream);
                case ".mxl":
                    using (var archive = ZipFile.OpenRead(path))
                    {
                        var entry = MxlRoot(archive);
                        if (entry is null) return NotationMetadata.Empty;
                        using var stream = entry.Open();
                        return ReadMusicXml(stream);
                    }
                case ".gp":
                    using (var archive = ZipFile.OpenRead(path))
                    {
                        var entry = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals("Content/score.gpif", StringComparison.OrdinalIgnoreCase));
                        if (entry is null) return NotationMetadata.Empty;
                        using var stream = entry.Open();
                        return ReadGpif(stream);
                    }
                case ".gp3" or ".gp4" or ".gp5":
                    using (var stream = File.OpenRead(path)) return ReadGuitarProBinary(stream);
                default: return NotationMetadata.Empty; // .gpx (compressed container) and .gtp: the file name is used.
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or XmlException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        { return NotationMetadata.Empty; }
    }

    private static XmlReaderSettings XmlSettings => new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };

    private static ZipArchiveEntry? MxlRoot(ZipArchive archive)
    {
        var container = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals("META-INF/container.xml", StringComparison.OrdinalIgnoreCase));
        if (container is not null)
        {
            using var stream = container.Open();
            using var reader = XmlReader.Create(stream, XmlSettings);
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rootfile" && reader.GetAttribute("full-path") is { Length: > 0 } root)
                {
                    var declared = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(root, StringComparison.OrdinalIgnoreCase));
                    if (declared is not null) return declared;
                    break;
                }
        }
        return archive.Entries.FirstOrDefault(e => !e.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(e.FullName).ToLowerInvariant() is ".musicxml" or ".xml");
    }

    /// <summary>Header of a MusicXML score: work-title (else movement-title), the creator of type "composer" and the performer MuseScore writes.</summary>
    public static NotationMetadata ReadMusicXml(Stream stream)
    {
        string work = "", movement = "", composer = "", artist = "";
        string? field = null; string type = ""; var text = new StringBuilder();
        using var reader = XmlReader.Create(stream, XmlSettings);
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.Depth == 0 && reader.LocalName is not ("score-partwise" or "score-timewise")) return NotationMetadata.Empty;
                    // The header ends where the music starts.
                    if (reader.LocalName is "part" or "measure") return Result();
                    field = !reader.IsEmptyElement && reader.LocalName is "work-title" or "movement-title" or "creator" or "miscellaneous-field" ? reader.LocalName : null;
                    type = reader.GetAttribute("type") ?? reader.GetAttribute("name") ?? ""; text.Clear();
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace:
                    if (field is not null) text.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement:
                    if (field == "work-title") work = text.ToString();
                    else if (field == "movement-title") movement = text.ToString();
                    else if (field == "creator" && type.Equals("composer", StringComparison.OrdinalIgnoreCase) && composer.Length == 0) composer = text.ToString();
                    // MusicXML has no performer; MuseScore (and TeclaScore's own exports) keep it as a miscellaneous field.
                    else if (field == "miscellaneous-field" && type.ToLowerInvariant() is "artist" or "performer" && Useful(artist).Length == 0) artist = text.ToString();
                    field = null;
                    break;
            }
        }
        return Result();
        NotationMetadata Result() => new(Useful(work).Length > 0 ? Useful(work) : Useful(movement), Useful(artist), Useful(composer));
    }

    /// <summary>Score header of a Guitar Pro 7/8 file (Content/score.gpif inside the .gp archive).</summary>
    public static NotationMetadata ReadGpif(Stream stream)
    {
        string title = "", artist = "", music = "", words = "";
        string? field = null; bool inScore = false; var text = new StringBuilder();
        using var reader = XmlReader.Create(stream, XmlSettings);
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.Depth == 0 && reader.LocalName != "GPIF") return NotationMetadata.Empty;
                    if (reader.Depth == 1) { if (inScore) return Result(); inScore = reader.LocalName == "Score" && !reader.IsEmptyElement; }
                    field = inScore && reader.Depth == 2 && !reader.IsEmptyElement ? reader.LocalName : null; text.Clear();
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace:
                    if (field is not null) text.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement:
                    if (reader.Depth == 1 && inScore) return Result();
                    switch (field) { case "Title": title = text.ToString(); break; case "Artist": artist = text.ToString(); break;
                        case "Music": music = text.ToString(); break; case "Words": case "WordsAndMusic": if (words.Length == 0) words = text.ToString(); break; }
                    field = null;
                    break;
            }
        }
        return Result();
        NotationMetadata Result() => new(Useful(title), Useful(artist), Useful(music).Length > 0 ? Useful(music) : Useful(words));
    }

    /// <summary>
    /// Header of a Guitar Pro 3, 4 or 5 file: a 30-character version string, then title, subtitle, artist, album,
    /// words and (version 5) music, each a length-prefixed string.
    /// </summary>
    public static NotationMetadata ReadGuitarProBinary(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        int length = reader.ReadByte(); byte[] raw = reader.ReadBytes(30);
        if (raw.Length < 30) throw new EndOfStreamException();
        string version = Encoding.ASCII.GetString(raw, 0, Math.Min(length, 30));
        if (!version.StartsWith("FICHIER GUITAR PRO", StringComparison.Ordinal)) return NotationMetadata.Empty;
        string Next()
        {
            int total = reader.ReadInt32();
            if (total is < 1 or > 65536) throw new InvalidDataException("Cabeçalho Guitar Pro inválido.");
            int size = reader.ReadByte(); byte[] bytes = reader.ReadBytes(total - 1);
            if (bytes.Length < total - 1) throw new EndOfStreamException();
            return DecodeText(bytes.AsSpan(0, Math.Min(size, bytes.Length)));
        }
        string title = Next(); Next(); string artist = Next(); Next(); string words = Next();
        string music = version.Contains("v5", StringComparison.Ordinal) ? Next() : "";
        return new(Useful(title), Useful(artist), Useful(music).Length > 0 ? Useful(music) : Useful(words));
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static string Useful(string value)
    {
        value = string.Join(' ', new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (value.Length > 160) value = value[..160].TrimEnd();
        return Placeholders.Contains(value, StringComparer.OrdinalIgnoreCase) ? "" : value;
    }

    /// <summary>
    /// Adds, at the start of the MIDI's first track, the source's hash and the declared title, performer and composer
    /// as karaoke-style header texts ("@T…", "@I…"): the library reads them as the song's properties and nothing sings them.
    /// </summary>
    public static byte[] Stamp(byte[] midi, NotationMetadata metadata, string sourceSha256)
    {
        if (midi.Length < 22 || Encoding.ASCII.GetString(midi, 0, 4) != "MThd") throw new InvalidDataException("O MuseScore não produziu um MIDI válido.");
        int header = 8 + ReadInt(midi, 4), position = header;
        while (position + 8 <= midi.Length && Encoding.ASCII.GetString(midi, position, 4) != "MTrk") position += 8 + ReadInt(midi, position + 4);
        if (position + 8 > midi.Length) throw new InvalidDataException("O MIDI convertido não tem pistas.");
        int trackLength = ReadInt(midi, position + 4);
        if (trackLength < 0 || position + 8 + trackLength > midi.Length) throw new InvalidDataException("O MIDI convertido está truncado.");
        var events = new List<byte>();
        void Text(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            events.Add(0); events.Add(0xFF); events.Add(0x01);
            // Variable-length quantity; these texts are far below the two-byte limit (16383).
            if (bytes.Length > 127) events.Add((byte)(0x80 | (bytes.Length >> 7)));
            events.Add((byte)(bytes.Length & 0x7F)); events.AddRange(bytes);
        }
        Text(OriginTag + sourceSha256);
        if (metadata.Title.Length > 0) Text("@TTitle: " + metadata.Title);
        if (metadata.Artist.Length > 0) Text("@TArtist: " + metadata.Artist);
        if (metadata.Composer.Length > 0) Text("@TComposer: " + metadata.Composer);
        var result = new byte[midi.Length + events.Count];
        Array.Copy(midi, 0, result, 0, position + 8);
        int total = trackLength + events.Count;
        result[position + 4] = (byte)(total >> 24); result[position + 5] = (byte)(total >> 16); result[position + 6] = (byte)(total >> 8); result[position + 7] = (byte)total;
        events.CopyTo(result, position + 8);
        Array.Copy(midi, position + 8, result, position + 8 + events.Count, midi.Length - position - 8);
        return result;
    }

    /// <summary>
    /// MuseScore reads the 8-bit texts of Guitar Pro 3 to 5 files with the sign extended, so "Violão" arrives in the
    /// MIDI as "Viol\uFFE3o". Track names and other texts get their accented letters back; a MIDI this walker does not
    /// understand is returned as it came.
    /// </summary>
    public static byte[] RepairGuitarProTexts(byte[] midi)
    {
        const string windows1252 = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008dŽ\u008f\u0090‘’“”•–—˜™š›œ\u009džŸ";
        static string Repair(string text) => text.Any(c => c >= '\uFF80')
            ? new string(text.Select(c => c < '\uFF80' ? c : (c & 0xFF) is >= 0x80 and <= 0x9F ? windows1252[(c & 0xFF) - 0x80] : (char)(c & 0xFF)).ToArray())
            : text;
        try { return RewriteTexts(midi, Repair); }
        catch (Exception error) when (error is InvalidDataException or DecoderFallbackException) { return midi; }
    }

    /// <summary>Copies the MIDI, passing the text of every text meta event (types 1 to 7) through <paramref name="rewrite"/>.</summary>
    private static byte[] RewriteTexts(byte[] midi, Func<string, string> rewrite)
    {
        if (midi.Length < 14 || Encoding.ASCII.GetString(midi, 0, 4) != "MThd") throw new InvalidDataException("MIDI inválido.");
        int position = 8 + ReadInt(midi, 4);
        if (position < 14 || position > midi.Length) throw new InvalidDataException("MIDI inválido.");
        var output = new List<byte>(midi.Length); output.AddRange(midi.AsSpan(0, position));
        var strict = new UTF8Encoding(false, true);
        while (position + 8 <= midi.Length)
        {
            int length = ReadInt(midi, position + 4), end = position + 8 + length;
            if (length < 0 || end > midi.Length) throw new InvalidDataException("Pista MIDI truncada.");
            if (Encoding.ASCII.GetString(midi, position, 4) != "MTrk") { output.AddRange(midi.AsSpan(position, 8 + length)); position = end; continue; }
            var track = new List<byte>(length);
            int cursor = position + 8, running = 0;
            int Byte() => cursor < end ? midi[cursor++] : throw new InvalidDataException("Pista MIDI truncada.");
            int Vlq() { int value = 0; for (int n = 0; n < 4; n++) { int b = Byte(); value = (value << 7) | (b & 127); if ((b & 128) == 0) return value; } throw new InvalidDataException("Duração MIDI inválida."); }
            while (cursor < end)
            {
                int start = cursor; Vlq(); int afterDelta = cursor, status = Byte();
                if (status == 0xFF)
                {
                    int type = Byte(), size = Vlq(), payload = cursor;
                    if (size > end - cursor) throw new InvalidDataException("Metaevento MIDI truncado.");
                    cursor += size;
                    if (type is >= 1 and <= 7)
                    {
                        string text = strict.GetString(midi, payload, size), replaced = rewrite(text);
                        if (replaced != text)
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(replaced);
                            if (bytes.Length > 16383) throw new InvalidDataException("Texto MIDI longo demais.");
                            track.AddRange(midi.AsSpan(start, afterDelta - start)); track.Add(0xFF); track.Add((byte)type);
                            if (bytes.Length > 127) track.Add((byte)(0x80 | (bytes.Length >> 7)));
                            track.Add((byte)(bytes.Length & 0x7F)); track.AddRange(bytes);
                            continue;
                        }
                    }
                }
                else if (status is 0xF0 or 0xF7) { int size = Vlq(); if (size > end - cursor) throw new InvalidDataException("SysEx truncado."); cursor += size; running = 0; }
                else
                {
                    if (status >= 0x80) running = status; else { if (running == 0) throw new InvalidDataException("Running status MIDI ausente."); cursor--; }
                    if (running >= 0xF0) throw new InvalidDataException("Status MIDI não suportado.");
                    cursor += (running >> 4) is 0xC or 0xD ? 1 : 2;
                    if (cursor > end) throw new InvalidDataException("Pista MIDI truncada.");
                }
                track.AddRange(midi.AsSpan(start, cursor - start));
            }
            output.AddRange("MTrk"u8); output.AddRange([(byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count]); output.AddRange(track);
            position = end;
        }
        output.AddRange(midi.AsSpan(position));
        return [.. output];
    }

    private static int ReadInt(byte[] bytes, int offset) => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    /// <summary>The MIDI already converted from this exact source in <paramref name="folder"/>, if any.</summary>
    public static string? FindConverted(string folder, string sourceSha256)
    {
        if (!Directory.Exists(folder)) return null;
        byte[] needle = Encoding.ASCII.GetBytes(OriginTag + sourceSha256);
        foreach (string file in Directory.EnumerateFiles(folder, "*.mid").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.OpenRead(file);
                // The stamp is the first event of the first track, right after the 14-byte header.
                byte[] head = new byte[512]; int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                if (head.AsSpan(0, read).IndexOf(needle) >= 0) return file;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>
    /// Converts <paramref name="source"/> into a new MIDI at <paramref name="destination"/> and stamps it.
    /// <paramref name="exportMidi"/> is MuseScore's part (<see cref="ScoreExporter.ExportMidiAsync"/>). The destination
    /// must not exist; a failed or cancelled conversion leaves no file behind.
    /// </summary>
    public static async Task ConvertAsync(Func<string, string, CancellationToken, Task> exportMidi, string source, string destination, CancellationToken token)
    {
        if (File.Exists(destination)) throw new IOException("A conversão precisa de um novo arquivo de destino.");
        string hash = Sha256(source); var metadata = ReadMetadata(source);
        try
        {
            await exportMidi(source, destination, token);
            byte[] converted = await File.ReadAllBytesAsync(destination, token);
            if (Path.GetExtension(source).ToLowerInvariant() is ".gp3" or ".gp4" or ".gp5" or ".gtp") converted = RepairGuitarProTexts(converted);
            byte[] stamped = Stamp(converted, metadata, hash);
            // A score without a single note (only rests, or text) is not a song the library can use.
            MidiReader.Read(stamped);
            await File.WriteAllBytesAsync(destination, stamped, token);
        }
        catch
        {
            try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
