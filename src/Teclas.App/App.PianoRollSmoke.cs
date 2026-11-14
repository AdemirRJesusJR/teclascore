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
    // Piano roll under the score: the notes placed on the playback clock of the measure timeline (repeats unfolded),
    // the keys and the chord of the playback position, the end of the score, the toggle. No MuseScore or audio runs:
    // the timeline is injected. With a real score and its page image (two extra arguments) the capture shows that song.
    private async void VerifyPianoRollUi(string reportPath, string? realScore = null, string? realPage = null)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "piano-roll-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(realScore is null ? 60 : 240) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "piano roll smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException(what);
            Flush();
        }
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), CurrentPage = "library", FollowScorePlayback = false }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = realScore is null ? 1300 : 1440, Height = realScore is null ? 900 : 1040 };
            window.Show(); Flush();
            Check(window.PianoRollEnabled && window.PianoRollVisibility == Visibility.Collapsed, "piano-roll-is-on-by-default-and-waits-for-a-score");
            string midi = Path.Combine(work, realScore is null ? "versos.mid" : Path.GetFileNameWithoutExtension(realScore).Replace("Cifras - ", "") + ".mid"); File.WriteAllBytes(midi, SyntheticTwoTrackMidi());
            window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single();
            string score = Path.Combine(work, "versos.mscz");
            if (realScore is null) WriteVerseScore(score); else File.Copy(realScore, score);
            row.ScorePath = score;
            if (realPage is not null) { string page = Path.Combine(work, "pagina-1.png"); File.Copy(realPage, page); row.PreviewPath = page; }
            var library = (ListBox)window.FindName("Library");
            library.SelectedItem = null; Flush(); library.SelectedItem = row; Flush();
            window.SelectPageForSmoke("library", 2); Flush();
            var panel = (Border)window.FindName("PianoRollPanel");
            var view = window.PianoRollView!;
            double Height(string name) => ((FrameworkElement)window.FindName(name)).ActualHeight;
            // Before the first Play there is no timeline: the roll takes no room from the page.
            Check(window.PianoRollVisibility == Visibility.Collapsed && panel.Visibility == Visibility.Collapsed && view.Notes.Count == 0
                && view.Hint == Teclas.App.MainWindow.PianoRollIdleHint, "piano-roll-takes-no-room-before-the-timeline-exists");

            // The measure timeline of the audio: the synthetic score (2 measures at 120 bpm) is played twice, as after a repeat.
            var model = NativeMusicEditor.Open(score, CancellationToken.None);
            var starts = PracticeSession.MeasureStarts(model).OrderBy(p => p.Value).ToArray();
            var order = realScore is null ? starts.Concat(starts).ToArray() : starts;
            var timeline = new List<ScorePlaybackOccurrence>();
            double clock = 0;
            for (int i = 0; i < order.Length; i++)
            {
                int next = Array.FindIndex(starts, p => p.Key == order[i].Key) + 1;
                double quarters = next < starts.Length ? starts[next].Value - order[i].Value : 4;
                timeline.Add(new ScorePlaybackOccurrence(i, order[i].Key, clock, clock + quarters * .5, null)); clock += quarters * .5;
            }
            var map = new ScorePlaybackMap(score, "", 1, [], [], [], timeline, [], true) { MeasureTimeline = timeline };
            const string museScore = @"C:\Program Files\MuseScore 4\bin\MuseScore4.exe";
            if (realScore is not null && File.Exists(museScore))
            {
                // The real timeline, as Play prepares it: MuseScore's own measure positions and tempo.
                map = await new ScorePlaybackMapService(museScore, Path.Combine(work, "score-maps")).PrepareAsync(score, 1, null, CancellationToken.None);
                timeline = map.MeasureTimeline.ToList();
                Check(map.CanSynchronize && timeline.Count >= starts.Length, $"real-score-has-a-measure-timeline ({timeline.Count} measures, {timeline[^1].EndSeconds:0.0} s)");
            }
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetField("scorePlaybackMap", hidden)!.SetValue(window, map);
            typeof(MainWindow).GetField("scoreMapIdentity", hidden)!.SetValue(window,
                typeof(MainWindow).GetMethod("SelectedScoreIdentity", hidden)!.Invoke(window, null));
            window.UpdatePianoRoll(.5);
            try { await Wait(() => view.Notes.Count > 0 && panel.IsVisible, "the piano roll did not show the notes"); }
            catch (TimeoutException) { throw new TimeoutException($"the piano roll did not show the notes: {view.Notes.Count} notes, binding {window.PianoRollVisibility}, hidden by {HiddenAncestor(panel)}"); }
            Check(panel.ActualHeight >= Math.Min(120, Height("PreviewScoreArea") - Teclas.App.MainWindow.PianoRollScoreRoom - 8) - 1 && Height("MainScoreViewer") >= Teclas.App.MainWindow.PianoRollScoreRoom - 1 && Height("MainScoreViewer") + panel.ActualHeight + 8 <= Height("PreviewScoreArea") + 1,
                $"piano-roll-grows-and-leaves-the-score-its-room (roll {panel.ActualHeight:0}, score {Height("MainScoreViewer"):0}, area {Height("PreviewScoreArea"):0})");
            if (realScore is null)
            {
                // Melody G4 (half) F4 (half) | E4 (whole), chords G/B (labelled G) then C: each comes down twice.
                Check(view.Notes.Count(n => !n.Chord) == 6 && view.Notes.Where(n => !n.Chord && n.Pitch == 67).Select(n => n.Start).SequenceEqual([0d, 4d]),
                    "piano-roll-plays-the-repeat-again " + string.Join(",", view.Notes.Select(n => $"{(n.Chord ? "c" : "m")}{n.Pitch}@{n.Start:0.##}")));
                Check(view.LitKeys.Contains(67) && view.ChordName == "G" && view.Hint.Length == 0, "piano-roll-lights-the-melody-and-the-chord " + view.ChordName);
                window.UpdatePianoRoll(2.5);
                Check(view.LitKeys.Contains(64) && !view.LitKeys.Contains(67) && view.ChordName == "C", "piano-roll-follows-the-position " + view.ChordName);
                Check(view.BarLines.SequenceEqual([0d, 2d, 4d, 6d]) && view.EndMarker == 8, "piano-roll-knows-the-bars-and-the-end");
                window.UpdatePianoRoll(5.5); Flush();
                CaptureFolderWindow(window, Path.Combine(reports, "piano-roll-end.png"));
                window.UpdatePianoRoll(.6); Flush();
            }
            else
            {
                double at = timeline[Math.Min(8, timeline.Count - 1)].StartSeconds + .3;
                window.UpdatePianoRoll(at); Flush();
                Check(view.ChordName.Length > 0, "piano-roll-shows-the-chord-of-the-real-score " + view.ChordName);
            }
            CaptureFolderWindow(window, Path.Combine(reports, "piano-roll.png"));
            Check(view.KeyboardRange == (21, 108), $"piano-roll-draws-the-whole-88-key-keyboard ({view.KeyboardRange})");
            // Four ways to turn it on and off: the toolbar toggle, the ✕ on the roll, Configurações › Estudo and Ctrl+K.
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("PianoRollToggle");
            var setting = (CheckBox)window.FindName("PianoRollSettingCheckBox");
            Check(toggle.IsChecked == true && setting.IsChecked == true, "piano-roll-toggle-and-setting-show-it-on");
            toggle.IsChecked = false; Flush();
            Check(panel.Visibility == Visibility.Collapsed && !window.PianoRollEnabled && setting.IsChecked == false, "piano-roll-toolbar-toggle-hides-the-panel");
            setting.IsChecked = true; Flush();
            Check(panel.Visibility == Visibility.Visible && window.PianoRollEnabled && toggle.IsChecked == true && view.Notes.Count > 0, "piano-roll-setting-shows-it-again");
            ((Button)window.FindName("PianoRollCloseButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Flush();
            Check(panel.Visibility == Visibility.Collapsed && !window.PianoRollEnabled && window.Status == Teclas.App.MainWindow.PianoRollOffMessage, "piano-roll-close-button-turns-it-off");
            window.PaletteItems().Single(item => item.Label == "Ligar ou desligar o piano roll").Run(); Flush();
            Check(panel.Visibility == Visibility.Visible && window.PianoRollEnabled, "piano-roll-command-palette-turns-it-on");
            window.SaveSessionForSmoke();
            Check(Settings.Load(settingsPath).PianoRoll, "piano-roll-choice-is-saved");
            typeof(MainWindow).GetMethod("ClearScoreSynchronization", hidden)!.Invoke(window, null); Flush();
            Check(view.Notes.Count == 0 && view.Hint == Teclas.App.MainWindow.PianoRollIdleHint, "piano-roll-clears-with-the-timeline");
            window.Close(); window = null;
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }

    private static string HiddenAncestor(DependencyObject element)
    {
        string found = "";
        for (var node = element; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible } ui) found += $" {ui.GetType().Name}:{(ui as FrameworkElement)?.Name}:{ui.Visibility}";
        return found.Length == 0 ? "nothing" : found;
    }
}
