using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Batch melody choice, simplified chords and target key. No engine or MuseScore runs.
    private async void VerifyV028Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v028-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        MainWindow? window = null;
        var checks = new Dictionary<string, bool>();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.28 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException(what + " " + window?.Status);
            Flush();
        }
        try
        {
            // Confidence rules.
            MelodySourceSuggestion S(int channel, double score, string label = "Boa candidata", string reason = "") => new(new(0, channel, 0), 0, score, label, reason, 0, 0);
            MelodySourceSuggestion[] Ranked(params MelodySourceSuggestion[] items) => items.Select((item, index) => item with { Rank = index + 1 }).ToArray();
            Check(MelodyBatchAdvisor.Propose(Ranked(S(1, 80), S(2, 50, "Conferir"))) is { Confident: true, Source.Channel: 1 }, "clear-winner-is-confident");
            Check(!MelodyBatchAdvisor.Propose(Ranked(S(1, 70), S(2, 65))).Confident, "close-scores-are-not-confident");
            Check(!MelodyBatchAdvisor.Propose(Ranked(S(1, 50))).Confident && !MelodyBatchAdvisor.Propose(Ranked(S(1, 90, "Acompanhamento provável"))).Confident, "low-or-non-melodic-is-not-confident");
            Check(!MelodyBatchAdvisor.Propose(Ranked(S(1, 90, reason: "Há outra fonte com linha semelhante; compare."))).Confident, "similar-line-is-not-confident");
            // Simplifier.
            Check(new[] { "m7", "7", "maj7", "m7b5", "dim7", "7sus4", "sus2", "aug", "6", "madd9" }.Select(ChordSimplifier.Simplify).SequenceEqual(new[] { "m", "", "", "dim", "dim", "sus4", "sus2", "aug", "", "m" }),
                "simplifier-maps-to-triads");
            var simplified = ChordSimplifier.SimplifyAll(JsonNode.Parse("""[{"root":"A","quality":"m7"},{"root":"A","quality":"m"},{"root":"G","quality":"7","diagramInversion":1}]""")!.AsArray());
            Check(simplified.Count == 2 && (string?)simplified[0]!["quality"] == "m" && (int?)simplified[1]!["diagramInversion"] == 1, "simplifier-drops-repeats-and-keeps-positions");

            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "output") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            window.Show(); Flush();
            string midi = Path.Combine(work, "duas-pistas.mid"); File.WriteAllBytes(midi, SyntheticTwoTrackMidi());
            window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single();
            ((ListBox)window.FindName("Library")).SelectedItem = row; Flush();

            // Batch melody: proposal shown, chosen track becomes the melody and goes to the queue.
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            MelodyBatchWindow? shown = null; var enqueued = new List<MidiRow>();
            typeof(MainWindow).GetField("melodyBatchDialog", hidden)!.SetValue(window, new Func<MelodyBatchWindow, bool?>(dialog =>
            {
                shown = dialog;
                foreach (var item in dialog.Rows) item.Selected = item.CanSelect;
                return dialog.SelectedRows.Count > 0;
            }));
            typeof(MainWindow).GetField("melodyBatchEnqueue", hidden)!.SetValue(window, new Func<MidiRow, CancellationToken, Task>((target, _) => { enqueued.Add(target); return Task.CompletedTask; }));
            typeof(MainWindow).GetMethod("MelodyBatchClick", hidden)!.Invoke(window, [window, new RoutedEventArgs()]);
            await Wait(() => window.IsIdle && enqueued.Count == 1, "batch melody did not enqueue");
            Check(shown is { Rows.Count: 1 } && row.Tracks.Count(track => track.IsMelody) == 1 && enqueued[0] == row, "batch-melody-selects-one-track-and-enqueues");
            Check(MelodyBatchWindow.Build(window.Files).Count == 0, "songs-with-a-melody-leave-the-batch");

            // Simplified chords: per song, saved, chart follows.
            row.Chords.Add(new ChordRow { Root = "A", Quality = "m7" }); row.Chords.Add(new ChordRow { Root = "G", Quality = "7" });
            window.SimplifiedChords = true; Flush();
            Check(row.SimplifiedChords && ChordChartWindow.BuildEntries(row).Select(entry => entry.Symbol).SequenceEqual(new[] { "Am", "G" }), "simplified-chords-change-the-chart");
            Check(row.Chords.Select(chord => chord.Quality).SequenceEqual(new[] { "m7", "7" }), "chord-table-keeps-analyzed-symbols");
            Check(Settings.Load(settingsPath).Library.Single().SimplifiedChords, "simplified-choice-is-saved");

            // Target key: D minor to E minor is +2; to A minor is −5 (shortest).
            var transpose = new TransposeWindow(0, -1, true);
            Check(transpose.TargetKey is { } target && ((IEnumerable<object>)target.ItemsSource).Count() == 12, "transpose-offers-twelve-keys");
            transpose.TargetKey!.SelectedValue = 4; Check(transpose.Semitones == 2, "target-key-up-a-tone");
            transpose.TargetKey.SelectedValue = 9; Check(transpose.Semitones == -5, "target-key-takes-the-shortest-way");
            Check(new TransposeWindow(0).TargetKey is null, "without-analysis-there-is-no-target-key");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
