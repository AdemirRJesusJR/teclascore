using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // The background conversion queue: the Piloto automático runs outside the busy state with a synthetic engine (no
    // Python or MuseScore). Checks the card locks, the footer, a song ready while another one is still generating, the
    // failure cause in the report, the retry by cause and the coalesced settings saves.
    private async void VerifyBackgroundQueueUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "background-queue-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Background queue smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task<bool> WaitFor(Func<bool> condition, double seconds = 20)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) { await Task.Delay(25); Flush(); }
            return condition();
        }
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Environment.ProcessPath!, MuseScorePath = Environment.ProcessPath!, ConversionParallelism = 2 }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new SmokeQueueWorker(gate.Task);
            window.conversionQueueWorker = worker;
            window.Show(); Flush();
            var rows = new List<MidiRow>();
            foreach (var (name, shift) in new[] { ("Pronta logo", 1), ("Demorada", 2), ("Tom impossivel", 3) })
            {
                string path = Path.Combine(work, name + ".mid"); File.WriteAllBytes(path, TwoPartMidi(shift));
                window.ImportForSmoke(path); Flush();
                var row = window.Files.Single(r => r.FullPath == path);
                foreach (var t in row.Tracks) t.IsMelody = t.Source.Key.Track == 1;
                row.FoldRepeats = true;
                rows.Add(row);
            }
            worker.Slow = rows[1].FullPath; worker.Failing = rows[2].FullPath;
            Check(window.ConversionParallelismSetting == 2 && window.ConversionParallelismHint.Contains("2 ao mesmo tempo"), "parallelism-setting-is-loaded");
            int requestsBefore = window.AutosaveRequests;

            var autopilot = window.RunAutopilotAsync(rows, exportPdf: false, keepAwake: false, quiet: true);
            // The first song comes back to its card while the slow one is still generating, and the window stays free.
            Check(await WaitFor(() => rows[0].ScorePath is not null), "a-finished-song-returns-to-its-card-at-once · " + window.Status + " · " + window.QueueDebugText);
            Check(window.IsIdle && !window.IsBusy && window.OperationVisibility != Visibility.Visible, "the-queue-runs-outside-the-busy-state");
            Check(await WaitFor(() => rows[1].QueueState == QueueCardState.Generating), "the-generating-card-is-marked");
            Check(rows[1].QueueBadge == "Gerando…" && rows[0].QueueState == QueueCardState.None, "only-the-cards-in-the-queue-are-locked");
            window.SelectSongForSmoke(rows[1]); Flush();
            Check(!window.CanEditMidi && !window.CanGenerate, "a-locked-card-cannot-be-edited");
            window.SelectSongForSmoke(rows[0]); Flush();
            Check(window.CanEditMidi, "a-ready-card-stays-editable");
            Check(window.BackgroundQueueVisibility == Visibility.Visible && window.BackgroundQueueText.StartsWith("Fila ", StringComparison.Ordinal)
                && window.CanPauseBackgroundQueue && window.BackgroundQueueToolTip.Contains("Demorada", StringComparison.Ordinal), "the-footer-shows-the-queue");
            rows[0].Approved = true;
            Check(rows[0].Approved, "a-ready-song-can-be-approved-while-others-generate");
            gate.SetResult();
            var outcomes = await autopilot; Flush();
            Check(await WaitFor(() => !window.IsBackgroundQueueRunning) && window.BackgroundQueueVisibility == Visibility.Collapsed, "the-footer-hides-when-the-queue-ends");
            Check(rows.All(r => r.QueueState == QueueCardState.None), "the-cards-are-unlocked-at-the-end");
            Check(rows[1].ScorePath is not null && outcomes.Single(o => o.Row == rows[1]).Group is "Prontas" or "Para revisar", "the-slow-song-is-generated-too");
            var failed = outcomes.Single(o => o.Row == rows[2]);
            Check(failed.Group == "Falharam" && failed.Detail.Contains("transposição fora da extensão", StringComparison.Ordinal), "the-report-names-the-failure-cause " + failed.Detail);
            Check(worker.Generations(rows[2].FullPath) == 1 && rows[2].FoldRepeats, "a-cause-that-folding-cannot-fix-is-not-retried");
            Check(worker.MaxConcurrent == 2, $"two-songs-generate-at-the-same-time ({worker.MaxConcurrent})");
            // Many PersistPreferences calls became few writes, and the file read back is current.
            Check(window.AutosaveRequests > requestsBefore && window.AutosaveWrites < window.AutosaveRequests, $"settings-saves-are-coalesced ({window.AutosaveRequests} requests, {window.AutosaveWrites} writes)");
            var saved = Settings.Load(settingsPath);
            Check(saved.Library.Single(s => s.FullPath == rows[0].FullPath) is { Approved: true, ScorePath: not null }, "reading-the-settings-flushes-the-pending-save");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks }));
            window.Close(); Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }));
            window?.Close(); Shutdown(1);
        }
    }

    /// <summary>A synthetic engine for the queue: writes the analysis, the score files and the receipt the queue checks.</summary>
    private sealed class SmokeQueueWorker(Task slowGate) : IConversionQueueWorker
    {
        private readonly Dictionary<string, int> generations = new(StringComparer.OrdinalIgnoreCase);
        private int running;
        public string Slow { get; set; } = "";
        public string Failing { get; set; } = "";
        public int MaxConcurrent { get; private set; }
        public int Generations(string source) { lock (generations) return generations.GetValueOrDefault(source); }

        public async Task<JsonObject> RunAsync(ConversionQueueRecipe recipe, JsonObject request, string workspace, IProgress<string>? progress, CancellationToken token)
        {
            string source = request["inputMidi"]!.GetValue<string>();
            string sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source, token)));
            if (request["mode"]!.GetValue<string>() == "analyze")
            {
                string path = Path.Combine(workspace, "analysis.json");
                var analysis = new JsonObject
                {
                    ["source"] = source, ["sha256"] = sha, ["reviewedProfile"] = request["useReviewedProfile"]?.DeepClone() ?? JsonValue.Create(false),
                    ["config"] = new JsonObject { ["source_path"] = source, ["filename"] = Path.GetFileName(source), ["melody_sources"] = request["melodySources"]!.DeepClone(),
                        ["melody_sections"] = request["melodySections"]?.DeepClone() ?? new JsonArray(), ["merge_mode"] = "priority" },
                    ["harmony"] = new JsonObject { ["key"] = new JsonObject { ["fifths"] = 0, ["minor"] = false } },
                    ["keyFifths"] = 0, ["keyMinor"] = false, ["chords"] = new JsonArray(new JsonObject { ["root"] = "C", ["quality"] = "", ["quarter"] = 0, ["confidence"] = 0.95 })
                };
                await File.WriteAllTextAsync(path, analysis.ToJsonString(), token);
                return new() { ["analysisPath"] = path };
            }
            lock (generations) { generations[recipe.SourcePath] = generations.GetValueOrDefault(recipe.SourcePath) + 1; MaxConcurrent = Math.Max(MaxConcurrent, ++running); }
            try
            {
                progress?.Report("Gerando (motor sintético)");
                if (string.Equals(recipe.SourcePath, Slow, StringComparison.OrdinalIgnoreCase)) await slowGate.WaitAsync(token);
                else await Task.Delay(150, token);
                if (string.Equals(recipe.SourcePath, Failing, StringComparison.OrdinalIgnoreCase))
                    throw new EngineOperationException("A transposição tira a melodia da extensão", new JsonObject { ["kind"] = "error", ["message"] = "fora da extensão", ["errorKind"] = "transpose_range" });
                string output = request["outputDirectory"]!.GetValue<string>(), review = Path.Combine(output, "revisao");
                Directory.CreateDirectory(review);
                string score = Path.Combine(output, "partitura.mscz"), first = Path.Combine(review, "pagina-1.png");
                WriteVerseScore(score);
                await File.WriteAllTextAsync(first, "page", token);
                await File.WriteAllTextAsync(Path.Combine(review, "validacao.json"), "{}", token);
                var result = new JsonObject { ["scorePath"] = score, ["previewPath"] = first, ["reviewDirectory"] = review, ["pages"] = 1, ["message"] = "Pronta" };
                var artifacts = new JsonArray(Directory.GetFiles(output, "*", SearchOption.AllDirectories).Select(path => (JsonNode)new JsonObject
                    { ["path"] = path, ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), ["kind"] = "file" }).ToArray());
                var receipt = new JsonObject { ["schemaVersion"] = 1, ["jobId"] = request["queueJobId"]!.DeepClone(), ["recipeSha256"] = request["queueRecipeSha256"]!.DeepClone(),
                    ["sourceSha256"] = sha, ["result"] = result.DeepClone(), ["artifacts"] = artifacts };
                await File.WriteAllTextAsync(request["queueReceipt"]!.GetValue<string>(), receipt.ToJsonString(), token);
                return result;
            }
            finally { lock (generations) running--; }
        }
    }
}
