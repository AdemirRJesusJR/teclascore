using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Teclas.Core;

if (args.FirstOrDefault() == "--hold") { await Task.Delay(30000); return 0; }
if (args.FirstOrDefault() == "--echo") { Console.WriteLine(JsonSerializer.Serialize(args.Skip(1))); return 0; }
string root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "Teclas.Dependencies-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
string run = Path.Combine(root, "teste com espaço e acento-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
string source = Path.Combine(run, "python original.exe"), muse = Path.Combine(run, "MuseScore4.exe");
await File.WriteAllTextAsync(source, "immutable original python"); await File.WriteAllTextAsync(muse, "immutable original MuseScore");
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
string sourceHash = Hash(source), museHash = Hash(muse);
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
var runner = new FakeRunner();
string managed = Path.Combine(run, "managed"), scratch = Path.Combine(run, "scratch");
var service = new DependencyPreparation(managed, scratch, runner);

var missing = await service.CheckPythonAsync(Path.Combine(run, "missing.exe"), CancellationToken.None);
Check(!missing.Supported && !missing.Ready && runner.Requests.Count == 0, "missing Python does not launch a process");
Check(DependencyPreparation.IsStoreAlias(@"C:\Users\Someone\AppData\Local\Microsoft\WindowsApps\python.exe"), "Windows Store alias is identified without opening it");
runner.PythonVersion = "3.10.14"; runner.Supported = false;
var old = await service.CheckPythonAsync(source, CancellationToken.None);
Check(!old.Supported && old.Message.Contains("3.11"), "old Python is rejected explicitly");
runner.PythonVersion = "3.13.9"; runner.Supported = true;
var noPillow = await service.CheckPythonAsync(source, CancellationToken.None);
Check(noPillow.Supported && !noPillow.Ready && noPillow.Message.Contains("separado") == false, "missing Pillow keeps base Python available for private preparation");
runner.BasePillow = true;
var ready = await service.CheckPythonAsync(source, CancellationToken.None);
Check(ready.Ready && ready.PillowVersion == DependencyPreparation.PillowVersion, "working Python and Pillow are accepted");
Check(runner.Requests.Last().Arguments.Take(2).SequenceEqual(new[] { "-I", "-c" }), "Python probe uses isolated mode and structured arguments");
runner.MalformedPython = true;
Check(!(await service.CheckPythonAsync(source, CancellationToken.None)).Ready, "malformed diagnostic cannot pass");
runner.MalformedPython = false; runner.ProbeTimeout = true;
Check(!(await service.CheckPythonAsync(source, CancellationToken.None)).Ready, "Python timeout is explicit failure");
runner.ProbeTimeout = false;
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel(); int before = runner.Requests.Count;
    bool rejected = false; try { await service.CheckPythonAsync(source, cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
    Check(rejected && runner.Requests.Count == before, "pre-cancel starts no process");
}

Check(!(await service.CheckMuseScoreAsync(Path.Combine(run, "missing-muse.exe"), null, CancellationToken.None)).Ready, "missing MuseScore is visible");
runner.MuseVersion = "MuseScore Studio 5.0.0";
Check(!(await service.CheckMuseScoreAsync(muse, null, CancellationToken.None)).Ready && !Directory.Exists(scratch), "wrong MuseScore major never renders");
runner.MuseVersion = "MuseScore Studio 4.6.5";
var goodMuse = await service.CheckMuseScoreAsync(muse, null, CancellationToken.None);
Check(goodMuse.Ready && goodMuse.Version == "4.6.5", "MuseScore version and synthetic SVG render pass");
Check(!Directory.EnumerateDirectories(scratch).Any(), "synthetic score and render are cleaned");
runner.BadSvg = true;
Check(!(await service.CheckMuseScoreAsync(muse, null, CancellationToken.None)).Ready, "invalid render does not pass");
runner.BadSvg = false; runner.RenderFails = true;
Check(!(await service.CheckMuseScoreAsync(muse, null, CancellationToken.None)).Ready, "native render exit failure does not pass");
runner.RenderFails = false;
using (var cancellation = new CancellationTokenSource())
{
    runner.CancelRendering = cancellation;
    bool rejected = false; try { await service.CheckMuseScoreAsync(muse, null, cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
    Check(rejected && !Directory.EnumerateDirectories(scratch).Any(), "render cancellation cleans only synthetic check files");
    runner.CancelRendering = null;
}

runner.BasePillow = false;
var prepared = await service.PreparePythonAsync(source, null, CancellationToken.None);
Check(prepared.Ready && prepared.Path.StartsWith(managed, StringComparison.OrdinalIgnoreCase), "Pillow preparation returns private validated environment");
string goodEnvironment = Path.GetDirectoryName(Path.GetDirectoryName(prepared.Path)!)!;
Check(File.Exists(Path.Combine(goodEnvironment, "ready.json")), "ready marker is published only after validation");
var pip = runner.Requests.Single(request => request.Arguments.Contains("pip"));
Check(pip.Executable == prepared.Path && pip.Executable != source, "pip never targets original Python");
Check(pip.Arguments.Contains("--isolated") && pip.Arguments.Contains("--only-binary=:all:") && pip.Arguments.Contains("https://pypi.org/simple")
    && pip.Arguments.Contains("Pillow==" + DependencyPreparation.PillowVersion), "pip uses fixed wheel package and official index without shell");
Check(!pip.Arguments.Contains("--user") && !pip.Arguments.Contains("--upgrade"), "preparation cannot upgrade global or user packages");
var candidates = service.Discover(source, muse);
Check(candidates.Python.First() == source && candidates.MuseScore.First() == muse, "configured paths have priority");
Check(candidates.Python.Contains(prepared.Path) && candidates.Python.Distinct(StringComparer.OrdinalIgnoreCase).Count() == candidates.Python.Count, "completed private Python is discovered without duplicate candidates");
int environments = Directory.GetDirectories(managed).Length;
runner.PipFails = true;
bool failed = false; try { await service.PreparePythonAsync(source, null, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
Check(failed && Directory.GetDirectories(managed).Length == environments && File.Exists(prepared.Path), "download failure preserves previous environment and removes current attempt");
runner.PipFails = false; runner.VenvFails = true;
failed = false; try { await service.PreparePythonAsync(source, null, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
Check(failed && Directory.GetDirectories(managed).Length == environments, "venv creation failure cannot leave eligible partial installation");
runner.VenvFails = false;
using (var cancellation = new CancellationTokenSource())
{
    runner.CancelPip = cancellation;
    bool rejected = false; try { await service.PreparePythonAsync(source, null, cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
    Check(rejected && Directory.GetDirectories(managed).Length == environments && File.Exists(prepared.Path), "cancel preparation preserves previous installation");
    runner.CancelPip = null;
}
runner.FinalProbeFails = true;
failed = false; try { await service.PreparePythonAsync(source, null, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
Check(failed && Directory.GetDirectories(managed).Length == environments, "failed final image test prevents publication");
runner.FinalProbeFails = false;
Check(Hash(source) == sourceHash && Hash(muse) == museHash, "all original executable fixtures stay byte identical");

// The actual process wrapper is exercised only with this test apphost, never pip or installed dependencies.
string self = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Teclas.Dependencies.Tests.exe" : "Teclas.Dependencies.Tests");
var processRunner = new DependencyProcessRunner();
string literal = "espaço á ' \" ; $(not-a-command) & x";
var echo = await processRunner.RunAsync(new(self, ["--echo", literal], run, TimeSpan.FromSeconds(10)), CancellationToken.None);
Check(echo.ExitCode == 0 && JsonSerializer.Deserialize<string[]>(echo.Output)!.Single() == literal, "process arguments preserve literal symbols and spaces");
var watch = Stopwatch.StartNew(); bool timedOut = false;
try { await processRunner.RunAsync(new(self, ["--hold"], run, TimeSpan.FromMilliseconds(150)), CancellationToken.None); } catch (TimeoutException) { timedOut = true; }
Check(timedOut && watch.Elapsed < TimeSpan.FromSeconds(8), "owned process timeout is bounded");
using (var cancellation = new CancellationTokenSource(150))
{
    watch.Restart(); bool rejected = false;
    try { await processRunner.RunAsync(new(self, ["--hold"], run, TimeSpan.FromSeconds(30)), cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
    Check(rejected && watch.Elapsed < TimeSpan.FromSeconds(8), "user cancellation is distinguished from timeout and terminates owned process");
}
await File.WriteAllTextAsync(Path.Combine(root, "dependency-checks.json"), JsonSerializer.Serialize(new { status = "passed", checks, installationsPerformed = 0, networkRequests = 0 }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {checks} dependency preparation checks; no installations or network requests.");
return 0;

sealed class FakeRunner : IDependencyProcessRunner
{
    public List<DependencyProcessRequest> Requests { get; } = [];
    public string PythonVersion { get; set; } = "3.13.9";
    public string MuseVersion { get; set; } = "MuseScore Studio 4.6.5";
    public bool Supported { get; set; } = true;
    public bool BasePillow { get; set; }
    public bool MalformedPython { get; set; }
    public bool ProbeTimeout { get; set; }
    public bool BadSvg { get; set; }
    public bool RenderFails { get; set; }
    public bool PipFails { get; set; }
    public bool VenvFails { get; set; }
    public bool FinalProbeFails { get; set; }
    public CancellationTokenSource? CancelRendering { get; set; }
    public CancellationTokenSource? CancelPip { get; set; }
    private readonly HashSet<string> installed = [];
    public async Task<DependencyProcessResult> RunAsync(DependencyProcessRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Requests.Add(request);
        if (request.Arguments.Contains("-c"))
        {
            if (ProbeTimeout) throw new TimeoutException();
            if (MalformedPython) return new(0, "not json", "");
            bool works = BasePillow || (installed.Contains(request.Executable) && !FinalProbeFails);
            return new(0, JsonSerializer.Serialize(new { python = PythonVersion, supported = Supported, works, pillow = works ? DependencyPreparation.PillowVersion : "" }), "");
        }
        if (request.Arguments.Contains("--version")) return new(0, MuseVersion, "");
        if (request.Arguments.Contains("venv"))
        {
            string scripts = Path.Combine(request.Arguments.Last(), "Scripts"); Directory.CreateDirectory(scripts);
            await File.WriteAllTextAsync(Path.Combine(scripts, "python.exe"), "fake private Python", token);
            return new(VenvFails ? 1 : 0, "", "");
        }
        if (request.Arguments.Contains("pip"))
        {
            if (CancelPip is not null) { CancelPip.Cancel(); token.ThrowIfCancellationRequested(); }
            if (!PipFails) installed.Add(request.Executable);
            return new(PipFails ? 1 : 0, "", "");
        }
        if (request.Arguments.Contains("-o"))
        {
            if (CancelRendering is not null) { CancelRendering.Cancel(); token.ThrowIfCancellationRequested(); }
            await File.WriteAllTextAsync(request.Arguments[1], BadSvg ? "bad" : "<?xml version='1.0'?><!DOCTYPE svg SYSTEM 'https://example.invalid/never-fetch'><svg xmlns='http://www.w3.org/2000/svg'><path d='M 0 0 L 1 1'/></svg>", token);
            return new(RenderFails ? 1 : 0, "", "");
        }
        throw new InvalidOperationException("Unrecognized synthetic process request.");
    }
}
