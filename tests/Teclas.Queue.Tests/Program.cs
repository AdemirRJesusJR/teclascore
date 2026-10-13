using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Teclas.Core;

string work = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "teclas-queue-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(work);
var passed = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); passed.Add(message); }
async Task<bool> Throws<T>(Func<Task> action) where T : Exception { try { await action(); return false; } catch (T) { return true; } }
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
ConversionQueueRecipe Recipe(string name, JsonObject? generate = null)
{
    string source = Path.Combine(work, name + ".mid"); File.WriteAllText(source, "MThd synthetic " + name);
    return new(source, Path.Combine(work, "python.exe"), Path.Combine(work, "engine"), Path.Combine(work, "MuseScore.exe"),
        Path.Combine(work, "outputs"), new() { ["useReviewedProfile"] = false, ["melodySources"] = new JsonArray(new JsonObject { ["track"] = 1, ["channel"] = 1, ["program"] = 0 }) },
        generate ?? new() { ["scoreTitle"] = "Título original", ["diagramInversion"] = 2 }) { SourceSha256 = Hash(source), DisplayName = name };
}
void PatchState(string directory, Action<JsonObject> change)
{
    string path = Path.Combine(directory, "queue.json"); var envelope = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    var state = JsonNode.Parse(envelope["Payload"]!.GetValue<string>())!.AsObject(); change(state);
    string payload = state.ToJsonString(); envelope["Payload"] = payload; envelope["Sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    File.WriteAllText(path, envelope.ToJsonString());
}
var firstRecipe = Recipe("freezing");
string firstDirectory = Path.Combine(work, "queue-first"); var worker = new FakeWorker();
ConversionQueueItem first;
using (var queue = new ConversionQueueService(firstDirectory, worker))
{
    int notifications = 0; queue.Changed += (_, _) => notifications++;
    first = await queue.EnqueueAsync(firstRecipe);
    firstRecipe.GenerateOptions["scoreTitle"] = "Changed outside";
    firstRecipe.AnalyzeOptions["melodySources"]![0]!["track"] = 9;
    Check(queue.Items.Single().Recipe.GenerateOptions["scoreTitle"]!.GetValue<string>() == "Título original" && queue.Items.Single().Recipe.AnalyzeOptions["melodySources"]![0]!["track"]!.GetValue<int>() == 1,
        "Enqueue freezes all metadata and source selection independently from caller mutations");
    first.Recipe.GenerateOptions["scoreTitle"] = "Changed returned item";
    Check(queue.Items[0].Recipe.GenerateOptions["scoreTitle"]!.GetValue<string>() == "Título original", "Returned recipe snapshots cannot mutate the durable queue");
    Check(first.Status == ConversionQueueStatus.Pending && worker.Requests.Count == 0 && File.Exists(first.SnapshotPath) && Hash(first.SnapshotPath) == first.SourceSha256,
        "Enqueue copies and hashes the source without starting work");
    Check(await Throws<IOException>(() => Task.Run(() => { using var duplicate = new ConversionQueueService(firstDirectory); })),
        "An exclusive lease prevents a second service from changing the same queue");
    File.Move(firstRecipe.SourcePath, firstRecipe.SourcePath + ".offline");
    await queue.RunAsync();
    first = queue.Items.Single();
    Check(first.Status == ConversionQueueStatus.Completed && first.Attempts == 1 && first.Result is not null, "A disconnected original source does not prevent execution from the frozen snapshot");
    Check(worker.Requests.All(request => request["inputMidi"]!.GetValue<string>() == first.SnapshotPath), "Both engine stages receive the private frozen MIDI");
    var generate = worker.Requests.Single(request => request["mode"]!.GetValue<string>() == "generate");
    Check(generate["keyMinor"]!.GetValue<bool>() && generate["keyFifths"]!.GetValue<int>() == -3 && generate["chords"]![0]!["root"]!.GetValue<string>() == "Cm-test",
        "Unanalyzed recipes inherit minor key and exact analyzed chord spellings instead of major defaults");
    Check(first.Result!.AnalysisPath == Path.Combine(first.Workspace, "analysis.json") && File.Exists(first.Result.AnalysisPath),
        "Completed results expose the effective analysis checkpoint for library reconciliation");
    Check(File.Exists(first.Result.ScorePath) && File.Exists(first.Result.PreviewPath) && Directory.GetFiles(first.Result.ReviewDirectory).Length == 3,
        "Publication includes the score, every preview page and the validation file");
    int before = worker.Requests.Count; await queue.RunAsync();
    Check(worker.Requests.Count == before && queue.Items[0].Attempts == 1, "Completed entries never regenerate on repeated Run");
    Check(await Throws<InvalidOperationException>(() => queue.ResumeAsync(first.Id)), "Completed entries cannot be resumed as duplicate conversions");
    Check(notifications > 3 && !queue.IsRunning, "State changes and progress are observable and execution resets its busy state");
}
using (var restored = new ConversionQueueService(firstDirectory, new FakeWorker()))
{
    Check(restored.Items.Single().Status == ConversionQueueStatus.Completed && restored.Items.Single().Result!.ScorePath == first.Result!.ScorePath,
        "Completed state and its exact result survive reopening");
    await restored.RemoveAsync(first.Id);
    Check(restored.Items.Count == 0 && File.Exists(first.Result!.ScorePath) && File.Exists(first.SnapshotPath), "Removing a queue entry preserves published results and private evidence");
}

string cancelDirectory = Path.Combine(work, "queue-cancel");
var cancelWorker = new FakeWorker(); using var cancel = new CancellationTokenSource();
cancelWorker.Before = (_, _, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
string pausedId;
using (var queue = new ConversionQueueService(cancelDirectory, cancelWorker))
{
    pausedId = (await queue.EnqueueAsync(Recipe("cancel-one"))).Id; await queue.EnqueueAsync(Recipe("cancel-two"));
    await queue.RunAsync(token: cancel.Token);
    Check(queue.Items[0].Status == ConversionQueueStatus.Paused && queue.Items[1].Status == ConversionQueueStatus.Pending,
        "Cancellation pauses only the active item and leaves the remaining items pending");
    cancelWorker.Progress[0]?.Report("late worker report");
    Check(queue.Items[0].Stage.Contains("Pausada", StringComparison.Ordinal), "Late worker progress cannot change a paused item");
}
var retryWorker = new FakeWorker();
using (var queue = new ConversionQueueService(cancelDirectory, retryWorker))
{
    Check(retryWorker.Requests.Count == 0 && queue.Items[0].Status == ConversionQueueStatus.Paused, "Reopening never starts paused or pending work automatically");
    await queue.ResumeAsync(pausedId);
    Check(queue.Items[0].Status == ConversionQueueStatus.Pending && retryWorker.Requests.Count == 0, "Resume explicitly requeues without executing");
    await queue.RunAsync();
    Check(queue.Items.All(item => item.Status == ConversionQueueStatus.Completed) && queue.Items[0].Attempts == 2 && queue.Items[1].Attempts == 1,
        "An explicit retry completes paused and pending work while counting attempts");
}

var failedWorker = new FakeWorker();
failedWorker.Before = (request, _, _) => Path.GetFileName(request["inputMidi"]!.GetValue<string>()) == "failure.mid" ? Task.FromException(new IOException("Injected engine failure")) : Task.CompletedTask;
using (var queue = new ConversionQueueService(Path.Combine(work, "queue-failure"), failedWorker))
{
    await queue.EnqueueAsync(Recipe("failure")); await queue.EnqueueAsync(Recipe("after-failure")); await queue.RunAsync();
    Check(queue.Items[0].Status == ConversionQueueStatus.Failed && queue.Items[0].Error == "Injected engine failure" && queue.Items[1].Status == ConversionQueueStatus.Completed,
        "One failed conversion does not discard or stop the following pending entry");
}

using (var queue = new ConversionQueueService(Path.Combine(work, "queue-hash"), new FakeWorker()))
{
    var recipe = Recipe("bad-hash") with { SourceSha256 = new string('0', 64) };
    Check(await Throws<IOException>(() => queue.EnqueueAsync(recipe)) && queue.Items.Count == 0, "Enqueue rejects a MIDI that differs from the imported document hash");
    var snapshot = await queue.EnqueueAsync(Recipe("snapshot-tamper")); File.AppendAllText(snapshot.SnapshotPath, "changed"); await queue.RunAsync();
    Check(queue.Items.Single().Status == ConversionQueueStatus.Failed && queue.Items.Single().Error!.Contains("alterado", StringComparison.Ordinal),
        "A modified private source is rejected before running the engine");
}

string receiptDirectory = Path.Combine(work, "queue-receipt"); var crashWorker = new FakeWorker { CrashAfterReceipt = true }; string crashId;
using (var queue = new ConversionQueueService(receiptDirectory, crashWorker))
{
    crashId = (await queue.EnqueueAsync(Recipe("crash-after-render"))).Id; await queue.RunAsync();
    Check(queue.Items[0].Status == ConversionQueueStatus.Failed && File.Exists(Path.Combine(queue.Items[0].Workspace, "queue-receipt.json")),
        "A crash after rendering retains a verifiable private receipt");
}
var recoveryWorker = new FakeWorker(); string recoveredScore;
using (var queue = new ConversionQueueService(receiptDirectory, recoveryWorker))
{
    await queue.ResumeAsync(crashId); await queue.RunAsync(); recoveredScore = queue.Items.Single().Result!.ScorePath;
    Check(queue.Items[0].Status == ConversionQueueStatus.Completed && recoveryWorker.Requests.Count == 0 && File.Exists(recoveredScore),
        "Receipt recovery publishes a completed render without analyzing or generating again");
}
PatchState(receiptDirectory, state => { state["Jobs"]![0]!["Status"] = (int)ConversionQueueStatus.Running; state["Jobs"]![0]!["Result"] = null; });
var renameWorker = new FakeWorker();
using (var queue = new ConversionQueueService(receiptDirectory, renameWorker))
{
    Check(queue.Items[0].Status == ConversionQueueStatus.Paused && renameWorker.Requests.Count == 0, "A process crash restores Running as Paused without starting");
    await queue.ResumeAsync(crashId); await queue.RunAsync();
    Check(queue.Items[0].Result!.ScorePath == recoveredScore && renameWorker.Requests.Count == 0,
        "A crash after final rename recovers the same result and never publishes a duplicate");
}

string corruptDirectory = Path.Combine(work, "queue-backup");
using (var queue = new ConversionQueueService(corruptDirectory, new FakeWorker()))
{ await queue.EnqueueAsync(Recipe("backup-one")); await queue.EnqueueAsync(Recipe("backup-two")); }
File.WriteAllText(Path.Combine(corruptDirectory, "queue.json"), "{corrupted");
using (var queue = new ConversionQueueService(corruptDirectory, new FakeWorker()))
{
    Check(queue.RecoveryMessage is not null && queue.Items.Count == 1 && queue.Items[0].DisplayName == "backup-one",
        "A corrupt main state is recovered from the last atomic backup");
    Check(Directory.GetFiles(corruptDirectory, "queue-corrupt-*.json").Length == 1, "Recovery preserves corrupt evidence instead of overwriting it");
}
File.WriteAllText(Path.Combine(corruptDirectory, "queue.json"), "bad main"); File.WriteAllText(Path.Combine(corruptDirectory, "queue.json.bak"), "bad backup");
Check(await Throws<InvalidDataException>(() => Task.Run(() => { using var queue = new ConversionQueueService(corruptDirectory); })) &&
    File.ReadAllText(Path.Combine(corruptDirectory, "queue.json")) == "bad main", "Two invalid state copies produce an explicit error and remain untouched");

async Task CheckTamper(string name, Action<ConversionQueueItem> tamper, string expected)
{
    string directory = Path.Combine(work, "queue-" + name); var fake = new FakeWorker { CrashAfterReceipt = true };
    using var queue = new ConversionQueueService(directory, fake);
    var item = await queue.EnqueueAsync(Recipe(name)); await queue.RunAsync(); tamper(item);
    await queue.ResumeAsync(item.Id); await queue.RunAsync();
    Check(queue.Items[0].Status == ConversionQueueStatus.Failed && fake.Requests.Count == 2, expected);
}
await CheckTamper("receipt-file-tamper", item => File.AppendAllText(Path.Combine(item.Workspace, "output", "score.mscz"), "tamper"),
    "Recovery rejects a modified rendered file instead of silently regenerating or publishing it");
await CheckTamper("receipt-page-missing", item => File.Delete(Path.Combine(item.Workspace, "output", "review", "pagina-2.png")),
    "Recovery rejects an incomplete page set");
await CheckTamper("receipt-escape", item =>
{
    string path = Path.Combine(item.Workspace, "queue-receipt.json"); var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    json["artifacts"]![0]!["path"] = item.SnapshotPath; json["artifacts"]![0]!["sha256"] = Hash(item.SnapshotPath); File.WriteAllText(path, json.ToJsonString());
}, "Receipt paths cannot escape the private output directory even with a matching file hash");

var explicitRecipe = Recipe("explicit-key", new() { ["keyMinor"] = false, ["keyFifths"] = 4, ["chords"] = new JsonArray(new JsonObject { ["root"] = "F#", ["quality"] = "", ["quarter"] = 0 }), ["outputDirectory"] = "untrusted", ["workspace"] = "untrusted" });
var explicitWorker = new FakeWorker();
using (var queue = new ConversionQueueService(Path.Combine(work, "queue-explicit"), explicitWorker))
{
    await queue.EnqueueAsync(explicitRecipe); await queue.RunAsync(); var request = explicitWorker.Requests.Last();
    Check(!request["keyMinor"]!.GetValue<bool>() && request["keyFifths"]!.GetValue<int>() == 4 && request["chords"]![0]!["root"]!.GetValue<string>() == "F#",
        "Explicit user key and chord edits take precedence over analysis defaults");
    Check(request["workspace"]!.GetValue<string>() == queue.Items[0].Workspace && request["outputDirectory"]!.GetValue<string>() == Path.Combine(queue.Items[0].Workspace, "output"),
        "User-supplied request fields cannot override controlled queue paths");
}

string externalAnalysis = Path.Combine(work, "external-analysis.json"); var cacheRecipe = Recipe("cached-analysis");
await File.WriteAllTextAsync(externalAnalysis, FakeWorker.Analysis(cacheRecipe.SourcePath, cacheRecipe.AnalyzeOptions).ToJsonString());
var cacheWorker = new FakeWorker();
using (var queue = new ConversionQueueService(Path.Combine(work, "queue-cached"), cacheWorker))
{
    var item = await queue.EnqueueAsync(cacheRecipe with { ExistingAnalysisPath = externalAnalysis }); await queue.RunAsync();
    var copied = JsonNode.Parse(File.ReadAllText(Path.Combine(item.Workspace, "analysis.json")))!;
    Check(cacheWorker.Requests.Count == 1 && cacheWorker.Requests[0]["mode"]!.GetValue<string>() == "generate" &&
        copied["source"]!.GetValue<string>() == item.SnapshotPath && copied["config"]!["source_path"]!.GetValue<string>() == item.SnapshotPath,
        "A matching existing analysis is copied, rebased and resumed directly at generation");
}
var reordered = Recipe("reordered-analysis"); reordered.AnalyzeOptions["melodySources"] = new JsonArray(new JsonObject { ["track"] = 1, ["channel"] = 1, ["program"] = 0 }, new JsonObject { ["track"] = 2, ["channel"] = 1, ["program"] = 0 });
var oldAnalysis = FakeWorker.Analysis(reordered.SourcePath, reordered.AnalyzeOptions);
oldAnalysis["config"]!["melody_sources"] = new JsonArray(reordered.AnalyzeOptions["melodySources"]![1]!.DeepClone(), reordered.AnalyzeOptions["melodySources"]![0]!.DeepClone());
string reorderedPath = Path.Combine(work, "reordered-analysis.json"); File.WriteAllText(reorderedPath, oldAnalysis.ToJsonString());
var reorderedWorker = new FakeWorker();
using (var queue = new ConversionQueueService(Path.Combine(work, "queue-reordered"), reorderedWorker))
{
    await queue.EnqueueAsync(reordered with { ExistingAnalysisPath = reorderedPath }); await queue.RunAsync();
    Check(reorderedWorker.Requests.Count == 2, "An analysis with the same sources in a different priority order is rebuilt");
}
var blockWorker = new FakeWorker(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
blockWorker.Before = async (_, _, token) => { started.TrySetResult(); await release.Task.WaitAsync(token); };
using (var queue = new ConversionQueueService(Path.Combine(work, "queue-concurrent"), blockWorker))
{
    var item = await queue.EnqueueAsync(Recipe("concurrent")); var running = queue.RunAsync(); await started.Task;
    Check(await Throws<InvalidOperationException>(() => queue.RunAsync()) && await Throws<InvalidOperationException>(() => queue.RemoveAsync(item.Id)),
        "Concurrent execution and removing an active item are rejected");
    release.SetResult(); await running;
    blockWorker.Progress[0]?.Report("late completed");
    Check(queue.Items[0].Stage == "Concluída" && !queue.IsRunning, "Late progress cannot overwrite a completed status");
}

File.WriteAllText(Path.Combine(work, "report.json"), JsonSerializer.Serialize(new { checks = passed.Count, passed }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {passed.Count} durable queue checks.");

sealed class FakeWorker : IConversionQueueWorker
{
    public List<JsonObject> Requests { get; } = [];
    public List<IProgress<string>?> Progress { get; } = [];
    public Func<JsonObject, IProgress<string>?, CancellationToken, Task>? Before { get; set; }
    public bool CrashAfterReceipt { get; init; }
    public static JsonObject Analysis(string source, JsonObject options) => new()
    {
        ["source"] = source, ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))),
        ["reviewedProfile"] = options["useReviewedProfile"]?.DeepClone() ?? JsonValue.Create(false),
        ["config"] = new JsonObject { ["source_path"] = source, ["filename"] = Path.GetFileName(source), ["melody_sources"] = options["melodySources"]!.DeepClone(), ["merge_mode"] = "priority" },
        ["harmony"] = new JsonObject { ["key"] = new JsonObject { ["fifths"] = -3, ["minor"] = true } },
        ["keyFifths"] = -3, ["keyMinor"] = true, ["chords"] = new JsonArray(new JsonObject { ["root"] = "Cm-test", ["quality"] = "m", ["quarter"] = 0 })
    };
    public async Task<JsonObject> RunAsync(ConversionQueueRecipe recipe, JsonObject request, string workspace, IProgress<string>? progress, CancellationToken token)
    {
        Requests.Add((JsonObject)request.DeepClone()); Progress.Add(progress);
        if (Before is not null) await Before(request, progress, token);
        token.ThrowIfCancellationRequested(); progress?.Report("Fake engine running");
        string source = request["inputMidi"]!.GetValue<string>();
        if (request["mode"]!.GetValue<string>() == "analyze")
        {
            string path = Path.Combine(workspace, "analysis.json"); await File.WriteAllTextAsync(path, Analysis(source, request).ToJsonString(), token);
            return new() { ["analysisPath"] = path };
        }
        string output = request["outputDirectory"]!.GetValue<string>(), review = Path.Combine(output, "review");
        Directory.CreateDirectory(review);
        string score = Path.Combine(output, "score.mscz"), first = Path.Combine(review, "pagina-1.png");
        await File.WriteAllTextAsync(score, "synthetic score", token); await File.WriteAllTextAsync(first, "page 1", token);
        await File.WriteAllTextAsync(Path.Combine(review, "pagina-2.png"), "page 2", token); await File.WriteAllTextAsync(Path.Combine(review, "validacao.json"), "{}", token);
        var result = new JsonObject { ["scorePath"] = score, ["previewPath"] = first, ["reviewDirectory"] = review, ["pages"] = 2, ["message"] = "Done" };
        var artifacts = new JsonArray(Directory.GetFiles(output, "*", SearchOption.AllDirectories).Select(path => (JsonNode)new JsonObject
            { ["path"] = path, ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), ["kind"] = "file" }).ToArray());
        var receipt = new JsonObject { ["schemaVersion"] = 1, ["jobId"] = request["queueJobId"]!.DeepClone(), ["recipeSha256"] = request["queueRecipeSha256"]!.DeepClone(),
            ["sourceSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), ["result"] = result.DeepClone(), ["artifacts"] = artifacts };
        await File.WriteAllTextAsync(request["queueReceipt"]!.GetValue<string>(), receipt.ToJsonString(), token);
        if (CrashAfterReceipt) throw new IOException("Crash after committed receipt");
        return result;
    }
}
