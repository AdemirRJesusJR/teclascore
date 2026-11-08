using System.Security.Cryptography;
using System.IO;
using Teclas.Core;

int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
string work = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "teclas-export-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(work);
string raw = Path.Combine(work, "float-source.wav"), pcm = Path.Combine(work, "pcm16.wav");
float[] values = [-1, -.5f, 0, .5f, 1.5f, float.NaN];
using (var writer = new BinaryWriter(File.Create(raw)))
{
    writer.Write("RIFF"u8); writer.Write((uint)(36 + values.Length * 4)); writer.Write("WAVEfmt "u8); writer.Write(16u);
    writer.Write((ushort)3); writer.Write((ushort)1); writer.Write(44100u); writer.Write(176400u); writer.Write((ushort)4); writer.Write((ushort)32);
    writer.Write("data"u8); writer.Write((uint)(values.Length * 4)); foreach (float value in values) writer.Write(value);
}
var input = ScoreExporter.ReadWaveInfo(raw);
var converted = ScoreExporter.ConvertToPcm16(raw, pcm, CancellationToken.None);
Check(input.Format == 3 && input.BitsPerSample == 32, "Read float WAV");
Check(converted.Format == 1 && converted.BitsPerSample == 16, "PCM16 conversion");
Check(Math.Abs(input.Duration.TotalSeconds - converted.Duration.TotalSeconds) < .000001, "Preserve duration");
using (var reader = new BinaryReader(File.OpenRead(pcm)))
{
    reader.BaseStream.Position = 44;
    short[] expected = [short.MinValue, -16384, 0, 16384, short.MaxValue, 0];
    foreach (short value in expected) Check(reader.ReadInt16() == value, "PCM amplitude, clipping and nonfinite handling");
}
try { ScoreExporter.ConvertToPcm16(raw, raw, CancellationToken.None); throw new Exception("Source overwrite accepted"); }
catch (InvalidOperationException) { checks++; }
string truncated = Path.Combine(work, "truncated.wav");
File.WriteAllBytes(truncated, File.ReadAllBytes(raw)[..^2]);
try { ScoreExporter.ReadWaveInfo(truncated); throw new Exception("Truncated WAV accepted"); }
catch (InvalidDataException) { checks++; }

if (args.Length >= 3)
{
    string source = Path.GetFullPath(args[1]), executable = Path.GetFullPath(args[2]);
    var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
    var exporter = new ScoreExporter(executable, Path.Combine(work, "cache"));
    string pdf = Path.Combine(work, "partitura.pdf");
    File.WriteAllText(pdf, "existing PDF must survive cancellation");
    byte[] previous = File.ReadAllBytes(pdf);
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { await exporter.ExportPdfAsync(source, pdf, null, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { checks++; }
    }
    Check(File.ReadAllBytes(pdf).SequenceEqual(previous), "Cancellation preserves existing PDF");
    Check(!Directory.EnumerateFiles(work, ".teclas-*.pdf").Any(), "Cancellation cleans PDF temporary files");
    using (var duringExport = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
    {
        try { await exporter.ExportPdfAsync(source, pdf, null, duringExport.Token); throw new Exception("Running export ignored cancellation"); }
        catch (OperationCanceledException) { checks++; }
    }
    Check(File.ReadAllBytes(pdf).SequenceEqual(previous), "Interrupted native export preserves existing PDF");
    Check(!Directory.EnumerateFiles(work, ".teclas-*.pdf").Any(), "Interrupted native export cleans temporary files");
    await exporter.ExportPdfAsync(source, pdf, null, CancellationToken.None);
    Check(File.ReadAllBytes(pdf).AsSpan(0, 5).SequenceEqual("%PDF-"u8), "MuseScore exports real PDF");
    var audio = await exporter.PrepareAudioAsync(source, null, CancellationToken.None);
    var info = ScoreExporter.ReadWaveInfo(audio.Path);
    Check(info.Format == 1 && info.BitsPerSample == 16 && info.Channels == 2, "Compatible stereo PCM playback file");
    Check(info.Duration.TotalSeconds > 1 && info.Duration == audio.Duration, "Audio duration available before playback");
    DateTime timestamp = File.GetLastWriteTimeUtc(audio.Path);
    var cached = await exporter.PrepareAudioAsync(source, null, CancellationToken.None);
    Check(cached.Path == audio.Path && File.GetLastWriteTimeUtc(audio.Path) == timestamp, "Repeat playback reuses audio cache");
    Check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(sourceHash), "Score source preserved after exports");
    Console.WriteLine(await MediaPlaybackSmoke.RunAsync(audio.Path)); checks++;
    Console.WriteLine($"PDF: {pdf}\nAudio: {audio.Path}\nDuration: {audio.Duration}");
}
// Chord page and document properties appended to a MuseScore-style PDF (classic xref table), without MuseScore.
{
    string pdfPath = Path.Combine(work, "acordes.pdf"), scorePath = Path.Combine(work, "acordes.mscz");
    var body = new System.Text.StringBuilder("%PDF-1.4\n");
    var offsets = new List<int>();
    void Obj(string text) { offsets.Add(body.Length); body.Append($"{offsets.Count} 0 obj\n{text}\nendobj\n"); }
    Obj("<<\n/Title (Old)\n>>");
    Obj("<<\n/Type /Catalog\n/Pages 3 0 R\n>>");
    Obj("<<\n/Type /Pages\n/Kids \n[\n4 0 R\n]\n/Count 1\n>>");
    Obj("<<\n/Type /Page\n/Parent 3 0 R\n/MediaBox [0 0 595.000000 842.000000]\n>>");
    int xrefAt = body.Length;
    body.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
    foreach (int offset in offsets) body.Append($"{offset:D10} 00000 n \n");
    body.Append($"trailer\n<<\n/Size {offsets.Count + 1} \n/Info 1 0 R\n/Root 2 0 R\n/ID [ <00> <00> ]\n>>\nstartxref\n{xrefAt} \n%%EOF\n");
    File.WriteAllText(pdfPath, body.ToString(), System.Text.Encoding.Latin1);
    var svg = KeyboardDiagramSvg.Build(0, "m", 2, KeyboardDiagramSvg.Highlight, false, "C");
    using (var zip = System.IO.Compression.ZipFile.Open(scorePath, System.IO.Compression.ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open()))
            writer.Write("<museScore><Score><metaTag name=\"workTitle\">Luz divina</metaTag><metaTag name=\"artist\">Roberto Carlos</metaTag><Staff><Measure><voice>" +
                "<Harmony><harmonyInfo><name>m</name><root>14</root></harmonyInfo></Harmony><Image><path>cm.svg</path></Image>" +
                "<Harmony><harmonyInfo><name>m</name><root>14</root></harmonyInfo></Harmony><Image><path>cm.svg</path></Image>" +
                "<Harmony><harmonyInfo><root>12</root></harmonyInfo></Harmony></voice></Measure></Staff></Score></museScore>");
        using (var writer = new StreamWriter(zip.CreateEntry("Pictures/cm.svg").Open())) writer.Write(svg.ToString());
    }
    var (songTitle, songArtist, songChords) = PdfChordPage.ReadScore(scorePath);
    Check(songTitle == "Luz divina" && songArtist == "Roberto Carlos", "Chord page reads title and performer");
    Check(songChords.Select(c => c.Label).SequenceEqual(["Cm", "Bb"]) && songChords[0].Diagram is not null && songChords[1].Diagram is null, "Chord page lists each chord once, with its diagram");
    PdfChordPage.Append(pdfPath, songTitle, songArtist, songChords);
    string finished = File.ReadAllText(pdfPath, System.Text.Encoding.Latin1);
    Check(finished.StartsWith(body.ToString(), StringComparison.Ordinal), "Original PDF bytes kept (incremental update)");
    Check(System.Text.RegularExpressions.Regex.IsMatch(finished, @"3 0 obj\s*<<\s*/Type /Pages\s*/Kids \[4 0 R \d+ 0 R\]\s*/Count 2"), "Page tree gains the chord page");
    Check(finished.Contains("/Prev " + xrefAt) && finished.Contains("/Author <FEFF0052006F0062"), "New trailer links the old one and names the performer");
    int lastXref = int.Parse(System.Text.RegularExpressions.Regex.Matches(finished, @"startxref\s+(\d+)")[^1].Groups[1].Value);
    Check(finished.AsSpan(lastXref, 4).SequenceEqual("xref"), "startxref points at the new table");
    foreach (System.Text.RegularExpressions.Match entry in System.Text.RegularExpressions.Regex.Matches(finished[lastXref..], @"(\d{10}) 00000 n "))
        Check(System.Text.RegularExpressions.Regex.IsMatch(finished[int.Parse(entry.Groups[1].Value)..], @"^\d+ 0 obj"), "Every new xref offset points at an object");
    Check(finished.Contains("(Acordes desta música - Luz divina) Tj"), "Heading written in WinAnsi");
    Check(KeyboardDiagramSvg.ToneNames(0, KeyboardDiagramVoicing.IntervalsOf("m"), "C", false, false)!.Values.Order().SequenceEqual(["C", "Eb", "G"]), "Diagram names follow the chord");
    Check(KeyboardDiagramSvg.MarkedPitches(svg).SequenceEqual(KeyboardDiagramVoicing.Pitches(0, "m", 2)), "Highlighted diagram marks its keys");
}
Console.WriteLine($"PASS: {checks} export checks.");
