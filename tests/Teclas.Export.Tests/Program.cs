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
Console.WriteLine($"PASS: {checks} export checks.");
