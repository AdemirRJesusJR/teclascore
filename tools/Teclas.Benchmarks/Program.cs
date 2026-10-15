using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Teclas.Core;

string? output = null;
int iterations = 5;
for (int index = 0; index < args.Length; index++)
{
    if (args[index] == "--output" && index + 1 < args.Length) output = Path.GetFullPath(args[++index]);
    else if (args[index] == "--iterations" && index + 1 < args.Length && int.TryParse(args[++index], out int count) && count is >= 1 and <= 50) iterations = count;
    else { Console.Error.WriteLine("Usage: Teclas.Benchmarks --output <report.json> [--iterations 1..50]"); return 2; }
}
if (output is null) { Console.Error.WriteLine("An explicit --output report path is required."); return 2; }
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var workloads = new List<object>();
var failures = new List<string>();
using var process = Process.GetCurrentProcess();
string fixtureDirectory = Path.Combine(Path.GetDirectoryName(output)!, "benchmark-fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureDirectory);
int validations = 0;
void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); validations++; }
double Percentile(double[] values, double percentile) => values.Order().ElementAt(Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1));
void Measure<T>(string name, string unit, int units, Func<T> execute, Action<T> validate)
{
    var samples = new List<Sample>();
    try
    {
        // Warm up JIT and the specific workload. Validation is outside timed regions.
        validate(execute());
        var timer = new Stopwatch();
        for (int index = 0; index < iterations; index++)
        {
            TimeSpan cpu = process.TotalProcessorTime;
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            timer.Restart(); T result = execute(); timer.Stop();
            long managedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            samples.Add(new(timer.Elapsed.TotalMilliseconds, cpuMs, managedBytes));
            validate(result);
        }
        var wall = samples.Select(sample => sample.WallMs).ToArray();
        workloads.Add(new { name, unit, units, status = "passed", warmupIterations = 1, iterations,
            medianWallMs = Percentile(wall, .5), p95WallMs = Percentile(wall, .95),
            medianCpuMs = Percentile(samples.Select(sample => sample.ProcessCpuMs).ToArray(), .5),
            p95CpuMs = Percentile(samples.Select(sample => sample.ProcessCpuMs).ToArray(), .95),
            medianManagedAllocatedBytes = Percentile(samples.Select(sample => (double)sample.ManagedAllocatedBytes).ToArray(), .5),
            p95ManagedAllocatedBytes = Percentile(samples.Select(sample => (double)sample.ManagedAllocatedBytes).ToArray(), .95), samples });
        Console.WriteLine($"PASS {name}: median {Percentile(wall, .5):0.###} ms, p95 {Percentile(wall, .95):0.###} ms");
    }
    catch (Exception error)
    {
        // Do not serialize exception paths, machine names or user folders in shared reports.
        failures.Add(name + ": " + error.GetType().Name);
        workloads.Add(new { name, unit, units, status = "failed", errorType = error.GetType().Name, iterations, samples });
        Console.Error.WriteLine($"FAIL {name}: {error.GetType().Name}");
    }
}
try
{
    foreach (int noteCount in new[] { 100, 1000, 10000 })
    {
        byte[] bytes = SyntheticMidi(noteCount);
        string digest = Convert.ToHexString(SHA256.HashData(bytes));
        var baseline = MidiReader.Read(bytes);
        int[] programs = [0, 24, 40, 73];
        var expectedNotes = Enumerable.Range(0, noteCount).Select(index =>
            new MidiNote(0, index % 4 + 1, programs[index % 4], 60 + index % 12, 85, index * 120L, index * 120L + 90)
                { StartOrder = 5 + index * 2, EndOrder = 6 + index * 2 }).ToArray();
        var expectedSources = programs.Select((program, index) => new SourceKey(0, index + 1, program)).ToHashSet();
        void ValidateMidi(MidiDocument document)
        {
            Require(document.Notes.Count == noteCount && document.Sources.Count == 4 && document.Ppq == 480, "Synthetic MIDI counts or PPQ changed.");
            Require(document.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase), "Synthetic MIDI hash changed.");
            Require(document.Notes.SequenceEqual(expectedNotes), "Synthetic note times, pitches, channels or programs changed.");
            Require(expectedSources.SetEquals(document.Sources.Select(source => source.Key)), "Synthetic source identity changed.");
        }
        Measure("midi-parse-" + noteCount, "notes", noteCount, () => MidiReader.Read(bytes), ValidateMidi);
        var expectedSuggestions = MelodySourceAdvisor.Analyze(baseline);
        Measure("melody-advisor-" + noteCount, "notes", noteCount, () => MelodySourceAdvisor.Analyze(baseline), actual =>
        {
            Require(actual.Count == 4 && actual.Select(s => s.Key).Distinct().Count() == 4, "Advisor lost or duplicated a source.");
            Require(JsonSerializer.Serialize(actual) == JsonSerializer.Serialize(expectedSuggestions), "Advisor changed its deterministic ranking or evidence.");
            Require(Convert.ToHexString(SHA256.HashData(bytes)) == digest && baseline.Notes.SequenceEqual(expectedNotes), "Analysis changed the input.");
        });
    }
    string[] filenames = Enumerable.Range(1, 1000).Select(index => $"Grupo Exemplo - Estudo Sintético {index} (karaoplay.com).mid").ToArray();
    Measure("metadata-1000", "filenames", filenames.Length,
        () => filenames.Select(name => ScoreMetadataOrganizer.Suggest(name, [])).ToArray(), suggestions =>
        {
            Require(suggestions.Length == filenames.Length, "Metadata suggestion count changed.");
            for (int index = 0; index < suggestions.Length; index++)
                Require(suggestions[index].Title == $"Estudo Sintético {index + 1}" && suggestions[index].Artist == "Grupo Exemplo" && suggestions[index].Composer == "",
                    "Metadata changed the known synthetic title/artist or invented a composer.");
        });
    string input = Path.Combine(fixtureDirectory, "float.wav"), pcm = Path.Combine(fixtureDirectory, "pcm16.wav");
    const int sampleCount = 44100 * 4;
    using (var writer = new BinaryWriter(File.Create(input)))
    {
        writer.Write("RIFF"u8); writer.Write((uint)(36 + sampleCount * 4)); writer.Write("WAVEfmt "u8); writer.Write(16u);
        writer.Write((ushort)3); writer.Write((ushort)1); writer.Write(44100u); writer.Write(176400u); writer.Write((ushort)4); writer.Write((ushort)32);
        writer.Write("data"u8); writer.Write((uint)(sampleCount * 4));
        for (int index = 0; index < sampleCount; index++) writer.Write((float)(.5 * Math.Sin(2 * Math.PI * 440 * index / 44100)));
    }
    string audioHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
    Measure("float-wav-to-pcm16", "audioSamples", sampleCount,
        () => ScoreExporter.ConvertToPcm16(input, pcm, CancellationToken.None), converted =>
        {
            Require(converted.Format == 1 && converted.BitsPerSample == 16 && converted.Channels == 1 && converted.SampleRate == 44100, "PCM format changed.");
            Require(converted.DataLength == sampleCount * 2 && Math.Abs(converted.Duration.TotalSeconds - 4) < .000001, "Audio samples or duration changed.");
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))) == audioHash, "Audio source was overwritten.");
        });
}
catch (Exception error) { failures.Add("fixture-preparation: " + error.GetType().Name); }
finally
{
    // This directory is created by this process, under the explicitly chosen report folder.
    string reportParent = Path.GetFullPath(Path.GetDirectoryName(output)!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (Path.GetFullPath(fixtureDirectory).StartsWith(reportParent, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(fixtureDirectory).StartsWith("benchmark-fixtures-", StringComparison.Ordinal))
    { try { Directory.Delete(fixtureDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
process.Refresh();
string? sourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA");
if (sourceCommit is null || sourceCommit.Length != 40 || !sourceCommit.All(Uri.IsHexDigit)) sourceCommit = null;
var report = new
{
    schemaVersion = 1, status = failures.Count == 0 ? "passed" : "failed", generatedUtc = DateTimeOffset.UtcNow, sourceCommit, iterations, validations,
    environment = new { runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
        coreAssemblyVersion = typeof(MidiReader).Assembly.GetName().Version?.ToString() },
    measurement = new { timingPolicy = "informational; no absolute speed gate", percentileMethod = "nearest-rank",
        managedAllocatedBytes = "GC.GetTotalAllocatedBytes delta during the workload; includes managed allocations on all process threads",
        processCpuMs = "process CPU delta; clock resolution can round short samples to zero",
        peakProcessWorkingSetBytes = process.PeakWorkingSet64 },
    workloads, failures
};
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
return failures.Count == 0 ? 0 : 1;

static byte[] SyntheticMidi(int count)
{
    int[] programs = [0, 24, 40, 73];
    var events = new List<(long Tick, int Order, byte[] Bytes)>();
    for (int channel = 0; channel < programs.Length; channel++) events.Add((0, 0, [(byte)(0xc0 + channel), (byte)programs[channel]]));
    for (int index = 0; index < count; index++)
    {
        int channel = index % 4, pitch = 60 + index % 12; long tick = index * 120L;
        events.Add((tick, 2, [(byte)(0x90 + channel), (byte)pitch, 85]));
        events.Add((tick + 90, 1, [(byte)(0x80 + channel), (byte)pitch, 0]));
    }
    using var track = new MemoryStream(); long previous = 0; Span<byte> buffer = stackalloc byte[4];
    foreach (var item in events.OrderBy(e => e.Tick).ThenBy(e => e.Order))
    {
        long delta = item.Tick - previous; int position = 3; buffer[position] = (byte)(delta & 127);
        while ((delta >>= 7) != 0) buffer[--position] = (byte)((delta & 127) | 128);
        track.Write(buffer[position..]); track.Write(item.Bytes); previous = item.Tick;
    }
    track.Write([0, 0xff, 0x2f, 0]);
    using var midi = new MemoryStream();
    midi.Write("MThd"u8); midi.Write([0, 0, 0, 6, 0, 0, 0, 1, 1, 224]); midi.Write("MTrk"u8);
    int length = checked((int)track.Length); midi.Write([(byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length]);
    track.Position = 0; track.CopyTo(midi); return midi.ToArray();
}
sealed record Sample(double WallMs, double ProcessCpuMs, long ManagedAllocatedBytes);
