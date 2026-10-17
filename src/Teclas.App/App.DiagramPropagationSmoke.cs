using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Synthetic library; generation is replaced, so no engine or MuseScore runs.
    private async void VerifyDiagramPropagationUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "diagram-propagation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        MainWindow? window = null;
        var checks = new Dictionary<string, bool>();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Diagram propagation smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException("Diagram propagation transition failed. " + window?.Status);
            Flush();
        }
        try
        {
            // Core rules first: printed chord shape, enharmonics, only changed and unified chords.
            ChordPositionEntry E(string root, string quality = "", int? inversion = null) => new(root, quality, inversion);
            Check(DiagramPositionPropagation.Shape("G#", "m", 0) == DiagramPositionPropagation.Shape("Ab", "m", 0), "enharmonic-chords-match");
            Check(DiagramPositionPropagation.Shape("D", "", 2) == DiagramPositionPropagation.Shape("E", "", 0), "transposed-songs-match-by-printed-chord");
            Check(DiagramPositionPropagation.Shape("E", "m", 0) != DiagramPositionPropagation.Shape("E", "", 0), "quality-distinguishes-chords");
            var before = new[] { E("E"), E("G#", "m"), E("E"), E("B", "6", 1) };
            var changes = DiagramPositionPropagation.Changes(before, [0, null, 0, 1], 0);
            Check(changes.Count == 1 && changes[0] == new ChordPositionChange(DiagramPositionPropagation.Shape("E", "", 0)!.Value, 0), "only-changed-chords-propagate");
            Check(DiagramPositionPropagation.Changes(before, [0, null, 1, 1], 0).Count == 0, "mixed-positions-do-not-propagate");
            var plan = DiagramPositionPropagation.Plan([E("Ab", "m"), E("E", "", 0), E("E", "", 2), E("F")], 0, changes);
            Check(plan.Count == 1 && plan[2] == 0, "plan-skips-same-and-unrelated-chords");

            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "output") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            window.Show(); Flush();
            MidiRow Song(string name, int transpose, bool generated, params (string Root, string Quality, int? Inversion)[] chords)
            {
                string path = Path.Combine(work, name + ".mid"); File.WriteAllBytes(path, TinyMidi());
                window!.ImportForSmoke(path);
                var row = window.Files.Single(f => f.FullPath == Path.GetFullPath(path));
                row.TransposeSemitones = transpose;
                foreach (var (root, quality, inversion) in chords) row.Chords.Add(new ChordRow { Root = root, Quality = quality, DiagramInversion = inversion });
                if (generated) { string score = Path.Combine(work, name + ".mscz"); File.WriteAllText(score, "synthetic"); row.ScorePath = score; }
                return row;
            }
            var current = Song("atual", 0, false, ("E", "", null), ("G#", "m", null), ("E", "", null));
            var same = Song("mesmas-cifras", 0, true, ("E", "", null), ("Ab", "m", null), ("C#", "m", 1));
            var transposed = Song("transposta", 2, false, ("D", "", null), ("A", "", null));
            var unrelated = Song("sem-relacao", 0, true, ("F", "", null));
            var pending = Song("gerada-sem-analise", 0, true, ("E", "", null));
            same.AnalysisPath = Path.Combine(work, "analise-sintetica.json");
            ((ListBox)window.FindName("Library")).SelectedItem = current; Flush();

            var regenerated = new List<MidiRow>();
            typeof(MainWindow).GetField("diagramGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new Func<MidiRow, Task>(row => { regenerated.Add(row); row.HasPendingScoreProperties = false; return Task.CompletedTask; }));
            var dialogField = typeof(MainWindow).GetField("diagramPositionsDialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Choose(string symbol, int? inversion, bool? applyToLibrary, Action<DiagramPositionsWindow>? inspect = null) =>
                dialogField.SetValue(window, new Func<DiagramPositionsWindow, bool?>(dialog =>
                {
                    inspect?.Invoke(dialog);
                    var table = (DataGrid)dialog.FindName("DiagramPositionsTable");
                    var draft = table.Items.Cast<object>().Single(item => (string)item.GetType().GetProperty("Symbol")!.GetValue(item)! == symbol);
                    draft.GetType().GetProperty("Inversion")!.SetValue(draft, inversion);
                    if (applyToLibrary is { } apply) ((CheckBox)dialog.FindName("PropagateDiagramPositionsCheckBox")).IsChecked = apply;
                    return true;
                }));
            var click = typeof(MainWindow).GetMethod("DiagramPositionsClick", BindingFlags.Instance | BindingFlags.NonPublic)!;

            string? offer = null;
            Choose("E", 0, null, dialog => offer = (dialog.FindName("PropagateDiagramPositionsCheckBox") as CheckBox) is { IsChecked: true } box ? box.Content?.ToString() : null);
            click.Invoke(window, [window, new RoutedEventArgs()]);
            await Wait(() => window.IsIdle && regenerated.Count > 0);
            Check(offer is not null && offer.Contains("outras 3 músicas"), "dialog-offers-the-songs-sharing-chords");
            Check(pending.Chords.Single().DiagramInversion == 0 && pending.HasPendingScoreProperties && !regenerated.Contains(pending),
                "score-without-analysis-keeps-new-position-as-pending");
            Check(current.Chords.Select(c => c.DiagramInversion).SequenceEqual(new int?[] { 0, null, 0 }), "current-song-updated");
            Check(same.Chords.Select(c => c.DiagramInversion).SequenceEqual(new int?[] { 0, null, 1 }), "same-chord-updated-others-untouched");
            Check(transposed.Chords.Select(c => c.DiagramInversion).SequenceEqual(new int?[] { 0, null }), "transposed-song-updated-by-printed-chord");
            Check(unrelated.Chords.Single().DiagramInversion is null && !unrelated.HasPendingScoreProperties, "unrelated-song-untouched");
            Check(regenerated.Contains(same) && !regenerated.Contains(transposed) && !regenerated.Contains(unrelated), "only-generated-scores-are-refreshed");
            Check(same.Versions.Count > 0, "previous-version-kept-in-history");
            Check(window.Status.Contains("3 outras músicas"), "status-reports-library-update");

            regenerated.Clear();
            Choose("G#m", 1, false);
            click.Invoke(window, [window, new RoutedEventArgs()]);
            await Wait(() => window.IsIdle);
            Check(current.Chords[1].DiagramInversion == 1 && same.Chords[1].DiagramInversion is null, "unchecked-box-keeps-other-songs");
            Check(!Settings.Load(settingsPath).PropagateDiagramPositions, "propagation-choice-is-remembered");

            // Library memory: chords analyzed later start with the positions chosen elsewhere.
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var fresh = Song("analisada-depois", 0, false, ("E", "", null), ("G#", "m", null), ("C", "", null));
            int filled = (int)typeof(MainWindow).GetMethod("ApplyChordPreferences", hidden)!.Invoke(window, [fresh])!;
            Check(filled == 2 && fresh.Chords[0].DiagramInversion == 0 && fresh.Chords[1].DiagramInversion == 1 && fresh.Chords[2].DiagramInversion is null,
                "new-analysis-receives-remembered-positions-only");
            Check(Settings.Load(settingsPath).ChordPositionPreferences.Any(p => p.PitchClass == 4 && p.Quality == "" && p.Inversion == 0)
                && Settings.Load(settingsPath).ChordPositionPreferences.Any(p => p.PitchClass == 8 && p.Quality == "m" && p.Inversion == 1),
                "every-choice-is-remembered-even-without-updating-other-songs");
            var later = Song("dialogo-preenchido", 0, false, ("E", "", null), ("B", "", null));
            int?[] prefill = (int?[])typeof(MainWindow).GetMethod("PreferredInversions", hidden)!.Invoke(window, [later])!;
            Check(prefill.SequenceEqual(new int?[] { 0, null }) && later.Chords[0].DiagramInversion is null, "dialog-prefills-without-changing-song-until-applied");
            var prefilledDialog = new DiagramPositionsWindow(later, 12, 0, true, prefill);
            Check(prefilledDialog.Inversions.SequenceEqual(new int?[] { 0, null }) && prefilledDialog.FindName("PropagateDiagramPositionsCheckBox") is null,
                "dialog-shows-remembered-positions-and-hides-option-without-other-songs");
            prefilledDialog.Close();

            // First run: learn from choices already in the library and fill songs that were not generated yet.
            var settingsField = typeof(MainWindow).GetField("settings", hidden)!;
            var live = (Settings)settingsField.GetValue(window)!;
            live.ChordPositionPreferences = []; live.ChordPreferencesSeeded = false;
            ((System.Collections.IDictionary)typeof(MainWindow).GetField("chordPreferences", hidden)!.GetValue(window)!).Clear();
            var notGenerated = Song("ainda-nao-gerada", 0, false, ("Ab", "m", null));
            var generatedUntouched = Song("ja-gerada", 0, true, ("G#", "m", null));
            typeof(MainWindow).GetMethod("InitializeChordPreferences", hidden)!.Invoke(window, []);
            Check(notGenerated.Chords[0].DiagramInversion == 1, "first-run-fills-songs-not-generated-from-existing-choices");
            Check(generatedUntouched.Chords[0].DiagramInversion is null && !generatedUntouched.HasPendingScoreProperties, "first-run-never-changes-generated-scores");
            Check(Settings.Load(settingsPath).ChordPreferencesSeeded, "first-run-happens-once");

            // Conversion queue: songs analyzed there (future additions) receive the same remembered positions.
            var queued = Song("adicionada-pela-fila", 0, false);
            var options = (System.Text.Json.Nodes.JsonObject)typeof(MainWindow).GetMethod("QueueGenerationOptions", hidden)!.Invoke(window, [queued])!;
            Check(options["chordPreferences"] is System.Text.Json.Nodes.JsonArray { Count: > 0 }, "queue-recipe-carries-remembered-positions");
            var analyzed = System.Text.Json.Nodes.JsonNode.Parse("""[{"root":"Ab","quality":"m"},{"root":"C","quality":""},{"root":"G#","quality":"m","diagramInversion":2}]""")!.AsArray();
            int queuedFilled = DiagramPositionPropagation.ApplyPreferences(analyzed, (System.Text.Json.Nodes.JsonArray)options["chordPreferences"]!, 0);
            Check(queuedFilled == 1 && (int?)analyzed[0]!["diagramInversion"] == 1 && analyzed[1]!["diagramInversion"] is null && (int?)analyzed[2]!["diagramInversion"] == 2,
                "queue-fills-only-chords-without-a-choice");
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
