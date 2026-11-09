using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Teclas.App;

public partial class App
{
    // The open song stays open when its card changes group (Em preparo → Prontas) or moves inside one.
    private async void VerifyLibraryGroupUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "group-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "library group smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            window.HandleShortcut(Key.D2, ModifierKeys.Control, null); Flush();
            var library = (ListBox)window.FindName("Library");
            string score = Path.Combine(work, "pronta.mscz"); WriteVerseScore(score);
            var rows = new List<MidiRow>();
            foreach (var (name, shift) in new[] { ("Primeira", 1), ("Segunda", 2), ("Terceira", 3) })
            {
                string path = Path.Combine(work, name + ".mid"); File.WriteAllBytes(path, TwoPartMidi(shift));
                window.ImportForSmoke(path); Flush(); rows.Add(window.Files.Single(r => r.FullPath == path));
            }
            var open = rows[1];
            library.SelectedItem = open; Flush();
            Check(ReferenceEquals(window.SelectedFile, open), "song-opened");
            Check(open.StatusShort == window.FlowProgressText && open.StatusShort.EndsWith('%'), $"card-shows-the-panel-progress card={open.StatusShort} panel={window.FlowProgressText}");
            Check(open.StatusGroup == "EM PREPARO" && open.StatusTone == (open.ProgressPercent == 0 ? "new" : "accent")
                && open.ProgressDoneWidth.Value == open.ProgressPercent && open.ProgressBarVisibility == Visibility.Visible, "open-card-is-in-progress-with-its-bar " + Where());
            string Where() => $"group={open.StatusGroup} sel={(library.SelectedItem as MidiRow)?.ScoreTitle ?? "null"} open={window.SelectedFile?.ScoreTitle ?? "null"}";

            open.ScorePath = score; open.HasPendingScoreProperties = false; Flush();
            Check(open.StatusGroup == "PRONTAS" && open.StatusTone == "ready", "generated-card-is-ready-and-green " + Where());
            Check(open.StatusShort == "100%" && window.FlowProgressText == "100%" && open.ProgressToolTip.Contains("100%"), $"generated-card-shows-full-progress card={open.StatusShort}");
            Check(ReferenceEquals(library.SelectedItem, open) && ReferenceEquals(window.SelectedFile, open), "stays-open-when-generated " + Where());

            open.Approved = true; Flush();
            Check(open.StatusGroup == "PRONTAS", "approved-card-is-ready " + Where());
            Check(ReferenceEquals(library.SelectedItem, open) && ReferenceEquals(window.SelectedFile, open), "stays-open-when-approved " + Where());

            open.Approved = false; Flush();
            Check(open.StatusShort == "100%", "unapproved-card-keeps-full-progress " + open.StatusShort);
            Check(ReferenceEquals(library.SelectedItem, open) && ReferenceEquals(window.SelectedFile, open), "stays-open-when-approval-is-undone " + Where());

            // A card that is not open changing group does not open anything nor close the open one.
            rows[0].ScorePath = score; rows[0].HasPendingScoreProperties = false; Flush();
            Check(ReferenceEquals(window.SelectedFile, open), "other-card-moving-keeps-the-open-song " + Where());

            // Picture of the cards in each tone: green 100% and ✓ Aprovada, blue 50%, with the bar at the foot.
            open.Approved = true; rows[2].FlowDoneSteps = 2; Flush(); window.UpdateLayout(); Flush();
            Check(rows[2].StatusTone == "accent" && rows[2].StatusShort == "50%" && open.StatusShort == "✓ Aprovada", "card-tones-for-the-picture");
            foreach (var theme in new[] { "ocean", "classic", "ocean:dark" })
            {
                var parts = theme.Split(':');
                window.SetThemeForSmoke(parts[0]); window.SetThemeModeForSmoke(parts.Length > 1 ? parts[1] : "light", null); Flush(); window.UpdateLayout(); Flush();
                var list = (FrameworkElement)library.Parent;
                int lw = (int)list.ActualWidth, lh = (int)list.ActualHeight;
                var drawing = new System.Windows.Media.DrawingVisual();
                using (var context = drawing.RenderOpen())
                    context.DrawRectangle(new System.Windows.Media.VisualBrush(list) { Stretch = System.Windows.Media.Stretch.None }, null, new Rect(0, 0, lw, lh));
                var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(lw, lh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); picture.Render(drawing);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
                using var stream = File.Create(Path.Combine(reports, "library-cards-" + theme.Replace(':', '-') + ".png")); png.Save(stream);
            }
            window.SetThemeForSmoke("classic"); window.SetThemeModeForSmoke("light", null);
            open.Approved = false; rows[2].FlowDoneSteps = null; Flush();

            // Advancing a step of a generated song runs an operation that changes its group (here the analysis fails without
            // Python and the card goes to Com falha). WPF regroups the cards live and, after other cards moved, removes the card
            // from its old group in a way that drops the ListBox selection: the editor went blank until the card was clicked again.
            // A fresh library with only the four generated songs, as in the reported session.
            window.Close(); Flush();
            string walkSettings = Path.Combine(work, "walk.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe"), KeyboardKeys = 61 }.Save(walkSettings);
            window = new MainWindow(true, walkSettings, () => new SilentMidiOutput()) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1600, Height = 1000 };
            window.Show(); Flush();
            library = (ListBox)window.FindName("Library");
            window.diagramGeneration = async row => { await Task.Delay(200); row.GeneratedAt = DateTimeOffset.Now; row.HasPendingScoreProperties = false; };
            var tabs = (TabControl)window.FindName("EditorTabs");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            void Next() => typeof(MainWindow).GetMethod("FlowNextClick", flags)!.Invoke(window, [new Button(), new RoutedEventArgs()]);
            var walked = new List<MidiRow>();
            int n = 0;
            foreach (var name in new[] { "Chupadinha", "O bebe", "Sexta-feira", "Mamae" })
            {
                string path = Path.Combine(work, name + ".mid"); File.WriteAllBytes(path, TwoPartMidi(++n));
                window.ImportForSmoke(path); Flush(); var row = window.Files.Single(r => r.FullPath == path); walked.Add(row);
                foreach (var t in row.Tracks) t.IsMelody = t.Source.Key.Track == 1;
                string analysis = Path.Combine(work, name + ".json"); File.WriteAllText(analysis, "{}");
                row.AnalysisPath = analysis;
                foreach (var (q, root, conf) in new[] { (0.0, "C", .95), (4.0, "G", .9), (8.0, "F", n == 2 ? .5 : .93), (12.0, "C", .97) })
                    row.Chords.Add(new ChordRow { Quarter = q, Root = root, Quality = "", Confidence = conf });
                if (n % 2 == 0) row.ReviewPoints.Add(new ScoreReviewPoint { Quarter = 4, Measure = 2, Beat = 1, MeasureStartQuarter = 4, MeasureEndQuarter = 8, Kind = "rhythm", Message = "Confira o ritmo" });
                string generated = Path.Combine(work, name + ".mscz"); WriteVerseScore(generated);
                row.ScorePath = generated; row.GeneratedAt = DateTimeOffset.Now; row.PageCount = 1; row.HasPendingScoreProperties = false;
            }
            window.SelectPageForSmoke("library", 0); Flush();
            void Kept(MidiRow row, string action) => Check(ReferenceEquals(library.SelectedItem, row) && ReferenceEquals(window.SelectedFile, row),
                $"{action}-keeps-{row.ScoreTitle}-open group={row.StatusGroup} sel={(library.SelectedItem as MidiRow)?.ScoreTitle ?? "null"} open={window.SelectedFile?.ScoreTitle ?? "null"}");
            async Task Settle() { Flush(); await Task.Delay(120); Flush(); }
            foreach (var row in walked)
            {
                library.SelectedItem = row; await Settle();
                foreach (int tab in new[] { 0, 3, 1, 2 }) { tabs.SelectedIndex = tab; await Settle(); }
                window.CompleteFlowForSmoke(row); tabs.SelectedIndex = 0; await Settle();
                Next(); await Settle(); Kept(row, "next-step");
                for (int i = 0; i < 3; i++) { window.HandleShortcut(Key.Left, ModifierKeys.Alt, null); await Settle(); }
                for (int i = 0; i < 3; i++) { window.HandleShortcut(Key.Right, ModifierKeys.Alt, null); await Settle(); }
                Kept(row, "alt-arrows");
                tabs.SelectedIndex = 1; await Settle();
                await (Task)typeof(MainWindow).GetMethod("ApplyDiagramPositions", flags)!.Invoke(window, [row, (IReadOnlyList<int?>)row.Chords.Select(_ => (int?)2).ToList(), false])!;
                await Settle(); Kept(row, "diagram-positions");
                foreach (int tab in new[] { 2, 1, 3, 0, 2 }) { tabs.SelectedIndex = tab; await Settle(); }
                Kept(row, "steps");
            }

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            try { window?.Close(); } catch (InvalidOperationException) { }
            Shutdown(1);
        }
        await Task.CompletedTask;
    }
}
