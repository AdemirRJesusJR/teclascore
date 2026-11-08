using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Teclas.Core;

// The same test executable is a deterministic, local fake worker/renderer. It never opens MuseScore.
if (args.Length >= 3 && args[0] == "-u")
{
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    var request = JsonNode.Parse(await File.ReadAllTextAsync(args[2]))!;
    string testCase = request["syntheticCase"]?.GetValue<string>() ?? "success";
    if (testCase == "cancel") { await Task.Delay(30000); return 0; }
    if (testCase == "failure") { Console.Error.WriteLine("PRIVATE music-title source-path exception-message"); return 9; }
    if (testCase == "review")
    {
        Console.WriteLine("{\"kind\":\"error\",\"message\":\"Revisão rítmica necessária\",\"reviewPoints\":[{\"quarter\":110.875,\"measure\":28,\"beat\":3.875,\"measureStartQuarter\":108,\"measureEndQuarter\":112,\"blocking\":true,\"kind\":\"rhythm_collision\"}],\"diagnosticPath\":\"local-diagnostic.json\"}");
        return 1;
    }
    if (testCase == "timed")
    {
        // Tempos por etapa do motor, com nomes desconhecidos e valores inválidos que precisam ser ignorados.
        Console.WriteLine("{\"kind\":\"result\",\"data\":{\"pages\":3,\"timings\":{\"version\":1,\"stages\":{\"midi_read\":0.05,\"analysis\":0.4,\"musicxml\":0.3,\"musescore\":4.5,\"svg_analysis\":0.6,\"preview\":0.2,\"total\":6.5,\"PRIVATE source-path\":9,\"verification\":-1},"
            + "\"musescore_calls\":[{\"kind\":\"importacao\",\"seconds\":0.8},{\"kind\":\"gravacao\",\"seconds\":1.1},{\"kind\":\"ajuste_espacamento\",\"seconds\":1.0},{\"kind\":\"ajuste_espacamento\",\"seconds\":1.2},{\"kind\":\"render\",\"seconds\":0.4},{\"kind\":\"PRIVATE music-title\",\"seconds\":0.2},{\"kind\":\"render\",\"seconds\":\"PRIVATE\"}],"
            + "\"spacing_passes\":3,\"conversions\":1}}}");
        return 0;
    }
    if (testCase == "timed-failure")
    {
        Console.WriteLine("{\"kind\":\"error\",\"message\":\"Diagramas\",\"errorKind\":\"diagram_layout\",\"reviewPoints\":[],\"timings\":{\"stages\":{\"musescore\":9.5,\"total\":12},\"musescore_calls\":[{\"kind\":\"ajuste_espacamento\",\"seconds\":9.5}],\"spacing_passes\":10,\"conversions\":1}}");
        return 1;
    }
    BurnCpu();
    Console.WriteLine("{\"kind\":\"result\",\"data\":{\"pages\":7,\"chords\":[{},{}],\"title\":\"PRIVATE music-title\"}}");
    return 0;
}
if (args.Length >= 3 && args[0] == "-o")
{
    string testCase = await File.ReadAllTextAsync(args[2]);
    if (testCase == "cancel") { await Task.Delay(30000); return 0; }
    if (testCase == "failure") { Console.Error.WriteLine("PRIVATE renderer-error path-and-title"); return 9; }
    BurnCpu();
    await File.WriteAllTextAsync(args[1], "%PDF-1.4\n" + new string(' ', 120) + "\n%%EOF\n");
    return 0;
}

string directory = args.Length == 2 && args[0] == "--qa-root" ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "Teclas.PerformanceTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
string Folder(string name) => Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N"));
static void BurnCpu()
{
    var watch = Stopwatch.StartNew(); double value = 0;
    while (watch.ElapsedMilliseconds < 120) for (int i = 1; i < 1000; i++) value += Math.Sqrt(i);
    GC.KeepAlive(value);
}

PerformanceDiagnostics.Recorder = null;
using (var disabled = PerformanceDiagnostics.Begin(PerformanceOperation.AppOperation)) disabled.Success();
var recorder = new LocalPerformanceRecorder(Folder("outcomes"));
using (var measurement = recorder.Begin(PerformanceOperation.AppAnalyze))
{
    measurement.SetCount(PerformanceCount.Notes, 42);
    measurement.SetCount((PerformanceCount)999, 99); measurement.SetCount(PerformanceCount.Items, -1);
    BurnCpu(); measurement.Success(); measurement.Dispose();
}
using (recorder.Begin(PerformanceOperation.AppGenerate)) { }
using (var measurement = recorder.Begin(PerformanceOperation.AppPdfBatch)) measurement.Cancelled();
using (var measurement = recorder.Begin(PerformanceOperation.ScoreExportPdf, PerformanceProcessKind.MuseScore, PerformancePhase.Process)) measurement.TimedOut();
var report = recorder.ReadReport();
Check(report.Records.Count == 4, "one record per scope; disposal is idempotent");
Check(report.Records.Select(record => record.Outcome).SequenceEqual(new[] { PerformanceOutcome.Success, PerformanceOutcome.Failure, PerformanceOutcome.Cancelled, PerformanceOutcome.Timeout }), "all terminal outcomes, failure by default");
var appRecord = report.Records[0];
Check(appRecord.DurationMilliseconds >= 100 && appRecord.CpuMilliseconds is > 0 && appRecord.PeakWorkingSetBytes is > 0 && appRecord.ResourceSamples > 0, "app resource measurement is available for actual CPU work");
Check(appRecord.Counts.Count == 1 && appRecord.Counts[PerformanceCount.Notes] == 42, "counts use a fixed nonnegative whitelist");
Check(report.Records[3].CpuMilliseconds is null && report.Records[3].PeakWorkingSetBytes is null, "unstarted child resources are unavailable, never zero estimates");
Check(report.CpuScope.Contains("excludes child") && report.MemoryScope.Contains("lifetime") && report.DurationScope.Contains("must not be summed"), "report states process scopes and overlap limits");

string rotationDirectory = Folder("rotation");
var rotating = new LocalPerformanceRecorder(rotationDirectory, 2, 3);
for (int index = 1; index <= 9; index++)
    using (var measurement = rotating.Begin(PerformanceOperation.AppImport)) { measurement.SetCount(PerformanceCount.Items, index); measurement.Success(); }
var rotated = rotating.ReadReport();
Check(rotated.Records.Count == 5 && rotated.Records.Select(record => record.Counts[PerformanceCount.Items]).SequenceEqual(new long[] { 5, 6, 7, 8, 9 }), "rotation retains bounded latest records in order");
Check(Directory.GetFiles(rotationDirectory).Length == 3 && Directory.GetFiles(rotationDirectory).All(path => new FileInfo(path).Length <= LocalPerformanceRecorder.MaximumFileBytes), "file count and byte size bounded with no temporary leftovers");
using (var active = rotating.Begin(PerformanceOperation.AppAnalyze))
{
    Check(rotating.Clear(), "clear succeeds"); active.Success();
}
Check(rotating.ReadReport().Records.Count == 0, "clear suppresses scopes started before clearing");
for (int index = 0; index < 5; index++) using (var measurement = rotating.Begin(PerformanceOperation.AppOperation)) measurement.Success();
Check(new LocalPerformanceRecorder(rotationDirectory, 1, 1).Clear() && Directory.GetFiles(rotationDirectory, "performance-*.json").Length == 0,
    "clear removes older segments even after configured retention shrinks");

var concurrent = new LocalPerformanceRecorder(Folder("concurrent"), 64, 2);
await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() =>
{
    using var measurement = concurrent.Begin(PerformanceOperation.AppOperation);
    measurement.SetCount(PerformanceCount.Items, index); measurement.Success();
})));
Check(concurrent.ReadReport().Records.Select(record => record.Counts[PerformanceCount.Items]).Distinct().Count() == 40, "concurrent app operations persist without lost records");

string badDirectory = Folder("bounded-reader"); Directory.CreateDirectory(badDirectory);
File.WriteAllText(Path.Combine(badDirectory, "performance-0.json"), new string('x', LocalPerformanceRecorder.MaximumFileBytes + 1));
File.WriteAllText(Path.Combine(badDirectory, "performance-1.json"), "{PRIVATE invalid json");
var damaged = new LocalPerformanceRecorder(badDirectory);
Check(damaged.ReadReport() is { UnreadableFiles: 2, StorageAvailable: false } && damaged.ReadReport().Records.Count == 0, "oversized and malformed files are bounded and safely skipped");
string blockedPath = Path.Combine(directory, "not-a-directory-" + Guid.NewGuid().ToString("N")); File.WriteAllText(blockedPath, "fixture");
var blocked = new LocalPerformanceRecorder(blockedPath);
using (var measurement = blocked.Begin(PerformanceOperation.AppOperation)) measurement.Success();
Check(!blocked.ReadReport().StorageAvailable && !blocked.ExportReport(Path.Combine(blockedPath, "export.json")), "storage failures never throw through measurements or export");

string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Teclas.PerformanceTests.exe" : "Teclas.PerformanceTests");
Check(File.Exists(executable), "fake subprocess apphost exists");
try
{
    await new EngineClient(executable, directory).RunAsync(new JsonObject { ["mode"] = "generate", ["syntheticCase"] = "review" }, Folder("worker-review"), null, CancellationToken.None);
    throw new Exception("structured worker failure accepted");
}
catch (EngineOperationException error)
{
    Check(error.Message == "Revisão rítmica necessária", "structured engine failure keeps readable message");
    Check(error.Details["reviewPoints"] is JsonArray { Count: 1 } points && points[0]!["measure"]!.GetValue<int>() == 28 && points[0]!["blocking"]!.GetValue<bool>(), "structured engine failure preserves actionable review points");
    Check(error.Details["diagnosticPath"]!.GetValue<string>() == "local-diagnostic.json", "structured engine failure preserves local diagnostic reference");
}
string processDirectory = Folder("processes"); Directory.CreateDirectory(processDirectory);
var processRecorder = new LocalPerformanceRecorder(Folder("process-records"));
var engine = new EngineClient(executable, processDirectory, processRecorder);
var result = await engine.RunAsync(new JsonObject { ["mode"] = "analyze", ["syntheticCase"] = "success", ["inputMidi"] = "PRIVATE source-path" }, Folder("worker-success"), null, CancellationToken.None);
Check(result["pages"]!.GetValue<int>() == 7, "instrumented worker returns original result");
await engine.RunAsync(new JsonObject { ["mode"] = "PRIVATE user-defined-mode" }, Folder("worker-other"), null, CancellationToken.None);
try { await engine.RunAsync(new JsonObject { ["mode"] = "generate", ["syntheticCase"] = "failure" }, Folder("worker-failure"), null, CancellationToken.None); throw new Exception("worker failure accepted"); }
catch (InvalidOperationException) { checks++; }
using (var cancellation = new CancellationTokenSource(250))
{
    try { await engine.RunAsync(new JsonObject { ["mode"] = "measureMap", ["syntheticCase"] = "cancel" }, Folder("worker-cancel"), null, cancellation.Token); throw new Exception("worker cancellation accepted"); }
    catch (OperationCanceledException) { checks++; }
}
try { await new EngineClient(Path.Combine(processDirectory, "missing-executable"), processDirectory, processRecorder).RunAsync(new JsonObject { ["mode"] = "analyze" }, Folder("worker-start-failure"), null, CancellationToken.None); throw new Exception("missing executable accepted"); }
catch (System.ComponentModel.Win32Exception) { checks++; }
var workerRecords = processRecorder.ReadReport().Records;
Check(workerRecords.Count == 5 && workerRecords.All(record => record.ProcessKind == PerformanceProcessKind.Worker && record.Phase == PerformancePhase.Process), "worker subprocess identity is separate from app and renderer");
Check(workerRecords.Select(record => record.Outcome).SequenceEqual(new[] { PerformanceOutcome.Success, PerformanceOutcome.Success, PerformanceOutcome.Failure, PerformanceOutcome.Cancelled, PerformanceOutcome.Failure }), "worker success, failure, cancellation, start failure recorded");
Check(workerRecords[1].Operation == PerformanceOperation.EngineOther, "unknown request modes never become log strings");
Check(workerRecords[0].Counts[PerformanceCount.Pages] == 7 && workerRecords[0].Counts[PerformanceCount.Chords] == 2, "worker only logs approved result counts");
Check(workerRecords[0].CpuMilliseconds is > 0 && workerRecords[0].ResourceSamples > 0, "worker CPU measured independently");
Check(workerRecords[4].CpuMilliseconds is null && workerRecords[4].PeakWorkingSetBytes is null, "start failure does not invent process metrics");

var timedRecorder = new LocalPerformanceRecorder(Folder("timed-records"));
var timedEngine = new EngineClient(executable, processDirectory, timedRecorder);
await timedEngine.RunAsync(new JsonObject { ["mode"] = "generate", ["syntheticCase"] = "timed" }, Folder("worker-timed"), null, CancellationToken.None);
try { await timedEngine.RunAsync(new JsonObject { ["mode"] = "generate", ["syntheticCase"] = "timed-failure" }, Folder("worker-timed-failure"), null, CancellationToken.None); throw new Exception("timed failure accepted"); }
catch (EngineOperationException error) { Check(error.Details["errorKind"]!.GetValue<string>() == "diagram_layout", "structured failure forwards the engine error kind"); }
var timedRecords = timedRecorder.ReadReport().Records;
Check(timedRecords.Count == 2 && timedRecords[0].Outcome == PerformanceOutcome.Success && timedRecords[1].Outcome == PerformanceOutcome.Failure, "timed generation records success and failure");
var timedStages = timedRecords[0].StageSeconds!;
Check(timedStages[PerformanceStage.MusicXml] == 0.3 && timedStages[PerformanceStage.EngineTotal] == 6.5 && timedStages[PerformanceStage.Preview] == 0.2, "engine stage seconds are recorded");
Check(Math.Abs(timedStages[PerformanceStage.MuseScoreSpacing] - 2.2) < 1e-9 && timedStages[PerformanceStage.MuseScoreImport] == 0.8 && timedStages[PerformanceStage.MuseScoreRender] == 0.4, "MuseScore calls add up by fixed call type");
Check(timedStages[PerformanceStage.MuseScoreOther] == 0.2 && timedStages.Count == 12, "unknown stage names and invalid seconds are ignored; unknown call types become Other");
Check(timedRecords[0].Counts[PerformanceCount.MuseScoreCalls] == 6 && timedRecords[0].Counts[PerformanceCount.LayoutPasses] == 3 && timedRecords[0].Counts[PerformanceCount.Conversions] == 1, "MuseScore calls, layout passes and conversions are counted");
Check(timedRecords[1].StageSeconds![PerformanceStage.MuseScoreSpacing] == 9.5 && timedRecords[1].Counts[PerformanceCount.LayoutPasses] == 10, "failed generation keeps its stage times");
Check(timedRecords.Take(1).All(record => record.StageSeconds is not null) && workerRecords.All(record => record.StageSeconds is null), "records without engine timings keep no stage map");
string timedExport = Path.Combine(directory, "timed-report.json");
Check(timedRecorder.ExportReport(timedExport) && !File.ReadAllText(timedExport).Contains("PRIVATE") && File.ReadAllText(timedExport).Contains("MuseScoreSpacing"), "stage times export only fixed vocabulary");
string legacyDirectory = Folder("legacy-records"); Directory.CreateDirectory(legacyDirectory);
File.WriteAllText(Path.Combine(legacyDirectory, "performance-0.json"), "[{\"StartedUtc\":\"2026-10-01T10:00:00+00:00\",\"Operation\":\"EngineGenerate\",\"Phase\":\"Process\",\"ProcessKind\":\"Worker\",\"Outcome\":\"Success\",\"DurationMilliseconds\":7800,\"CpuMilliseconds\":null,\"PeakWorkingSetBytes\":null,\"ResourceSamples\":0,\"Counts\":{}}]");
Check(new LocalPerformanceRecorder(legacyDirectory).ReadReport() is { UnreadableFiles: 0 } legacy && legacy.Records.Count == 1 && legacy.Records[0].StageSeconds is null, "records written before stage times are still readable");
var tampered = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(timedExport)!, "timed-report.json")))!;
string tamperedDirectory = Folder("tampered-stages"); Directory.CreateDirectory(tamperedDirectory);
var tamperedRecords = tampered["Records"]!.AsArray(); tamperedRecords[0]!["StageSeconds"]!["MusicXml"] = -5;
File.WriteAllText(Path.Combine(tamperedDirectory, "performance-0.json"), tamperedRecords.ToJsonString());
Check(new LocalPerformanceRecorder(tamperedDirectory).ReadReport().UnreadableFiles == 1, "negative stage seconds are rejected on read");

var exporter = new ScoreExporter(executable, Folder("audio-cache"), processRecorder);
string input = Path.Combine(processDirectory, "PRIVATE score-title.mscz");
await File.WriteAllTextAsync(input, "success");
string destination = Path.Combine(processDirectory, "PRIVATE output.pdf");
await exporter.ExportPdfAsync(input, destination, null, CancellationToken.None);
Check(File.Exists(destination), "instrumented renderer preserves normal PDF export");
await File.WriteAllTextAsync(input, "failure");
try { await exporter.ExportPdfAsync(input, Path.Combine(processDirectory, "failed.pdf"), null, CancellationToken.None); throw new Exception("renderer failure accepted"); }
catch (InvalidOperationException) { checks++; }
await File.WriteAllTextAsync(input, "cancel");
using (var cancellation = new CancellationTokenSource(250))
{
    try { await exporter.ExportPdfAsync(input, Path.Combine(processDirectory, "cancelled.pdf"), null, cancellation.Token); throw new Exception("renderer cancellation accepted"); }
    catch (OperationCanceledException) { checks++; }
}
try { await new ScoreExporter(executable, processDirectory, processRecorder, TimeSpan.FromMilliseconds(250)).ExportPdfAsync(input, Path.Combine(processDirectory, "timeout.pdf"), null, CancellationToken.None); throw new Exception("renderer timeout accepted"); }
catch (TimeoutException) { checks++; }
var rendererRecords = processRecorder.ReadReport().Records.Where(record => record.ProcessKind == PerformanceProcessKind.MuseScore).ToArray();
Check(rendererRecords.Select(record => record.Outcome).SequenceEqual(new[] { PerformanceOutcome.Success, PerformanceOutcome.Failure, PerformanceOutcome.Cancelled, PerformanceOutcome.Timeout }) && rendererRecords.All(record => record.Operation == PerformanceOperation.ScoreExportPdf), "renderer terminal outcomes and stable export operation recorded");
Check(!Directory.EnumerateFiles(processDirectory, ".teclas-*").Any(), "renderer cleanup is unchanged by measurements");

// Diagnostic persistence must not change either client result, even when its directory is unusable.
await new EngineClient(executable, processDirectory, blocked).RunAsync(new JsonObject { ["mode"] = "analyze" }, Folder("worker-disk-failure"), null, CancellationToken.None);
await File.WriteAllTextAsync(input, "success");
await new ScoreExporter(executable, processDirectory, blocked).ExportPdfAsync(input, Path.Combine(processDirectory, "disk-failure.pdf"), null, CancellationToken.None);
Check(File.Exists(Path.Combine(processDirectory, "disk-failure.pdf")), "diagnostic disk errors cannot fail worker or renderer results");

// Tampered extra fields are discarded on read and never forwarded into a shareable report.
string privacyDirectory = Folder("privacy"); var privacy = new LocalPerformanceRecorder(privacyDirectory);
using (var measurement = privacy.Begin(PerformanceOperation.AppOperation)) measurement.Success();
string privacyLog = Path.Combine(privacyDirectory, "performance-0.json");
var array = JsonNode.Parse(File.ReadAllText(privacyLog))!.AsArray(); array[0]!["SecretPath"] = "PRIVATE source-path";
File.WriteAllText(privacyLog, array.ToJsonString());
string export = Path.Combine(directory, "performance-report.json");
Check(privacy.ExportReport(export) && !File.ReadAllText(export).Contains("PRIVATE"), "report only exports known typed fields even after tampering");
array[0]!["Operation"] = "PRIVATE invalid-enum"; File.WriteAllText(privacyLog, array.ToJsonString());
Check(privacy.ReadReport().UnreadableFiles == 1 && privacy.ReadReport().Records.Count == 0, "unknown enum strings rejected");
Check(processRecorder.ExportReport(export), "real process report exported");
string exported = File.ReadAllText(export);
Check(!exported.Contains("PRIVATE") && !exported.Contains(processDirectory) && !exported.Contains(executable) && !exported.Contains("exception-message"), "process report contains no source names, paths, arguments, exception text, or arbitrary payloads");
PerformanceDiagnostics.Recorder = recorder;
using (var measurement = PerformanceDiagnostics.Begin(PerformanceOperation.AppBackup)) measurement.Success();
await new EngineClient(executable, processDirectory).RunAsync(new JsonObject { ["mode"] = "analyze" }, Folder("worker-global"), null, CancellationToken.None);
await new ScoreExporter(executable, processDirectory).ExportPdfAsync(input, Path.Combine(processDirectory, "global.pdf"), null, CancellationToken.None);
PerformanceDiagnostics.Recorder = null;
Check(recorder.ReadReport().Records.TakeLast(3).Select(record => record.Operation).SequenceEqual(new[] { PerformanceOperation.AppBackup, PerformanceOperation.EngineAnalyze, PerformanceOperation.ScoreExportPdf }), "optional global recorder instruments compatible two-argument constructors and can be detached");
Console.WriteLine($"PASS: {checks} performance checks. QA: {directory}");
return 0;
