using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Floating preview bar, resizable score area, and the 0.34 workflow features.
    private async void VerifyV034Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v034-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.34 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException(what);
            Flush();
        }
        string Page(string path)
        {
            var bitmap = new RenderTargetBitmap(850, 1100, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 850, 1100));
                for (int line = 0; line < 9; line++)
                    for (int staff = 0; staff < 5; staff++) context.DrawRectangle(Brushes.Black, null, new Rect(70, 120 + line * 105 + staff * 9, 710, 1.2));
            }
            bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path); encoder.Save(file); return path;
        }
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1440, Height = 1000 };
            window.Show(); Flush();
            string midi = Path.Combine(work, "musica.mid"); File.WriteAllBytes(midi, SyntheticTwoTrackMidi());
            window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single();
            string pages = Path.Combine(work, "paginas"); Directory.CreateDirectory(pages);
            Page(Path.Combine(pages, "pagina-1.png")); Page(Path.Combine(pages, "pagina-2.png"));
            string score = Path.Combine(work, "musica.mscz"); File.WriteAllBytes(score, [1]);
            row.ScorePath = score; row.PreviewPath = Path.Combine(pages, "pagina-1.png"); row.PageCount = 2;
            ((ListBox)window.FindName("Library")).SelectedItem = null; Flush();
            ((ListBox)window.FindName("Library")).SelectedItem = row; Flush();
            window.SelectPageForSmoke("library", 2); Flush();
            var viewer = (ScoreViewer)window.FindName("MainScoreViewer");
            await Wait(() => viewer.PageCount == 2, "preview pages did not load");

            // Floating bar inside the preview, instead of a row of buttons above it.
            var bar = (Border)viewer.FindName("ViewerToolbar");
            Check(bar.IsVisible && bar.VerticalAlignment == VerticalAlignment.Bottom && VisualTreeHelper.GetParent(bar) is Grid host && host.Children.Contains((UIElement)viewer.FindName("PageScroll")), "toolbar-floats-over-the-page");
            foreach (var name in new[] { "ThumbnailsButton", "PreviousPageButton", "NextPageButton", "ZoomOutButton", "ZoomInButton", "FitWidthButton", "FitPageButton", "FullscreenButton" })
                Check(viewer.FindName(name) is Button button && bar.IsAncestorOf(button) && button.ToolTip is string, "bar-has-" + name);
            ((Button)viewer.FindName("NextPageButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(viewer.CurrentPage == 2 && ((TextBlock)viewer.FindName("PageCounter")).Text == "2 / 2", "bar-turns-pages");
            // Thumbnails: hidden together with their splitter, then back at the dragged width.
            var thumbs = (ColumnDefinition)viewer.FindName("ThumbnailColumn");
            thumbs.Width = new GridLength(180); Flush(); viewer.UpdateLayout();
            var toggle = (Button)viewer.FindName("ThumbnailsButton");
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(thumbs.Width.Value == 0 && ((GridSplitter)viewer.FindName("ThumbnailSplitter")).Visibility == Visibility.Collapsed, "thumbnails-hide-with-their-splitter");
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(Math.Abs(thumbs.Width.Value - 180) < 1, "thumbnails-come-back-at-their-width");
            CaptureFolderWindow(window, Path.Combine(reports, "preview-bar.png"));

            // Resizable score area: a splitter between the controls and the score; remembered; double click resets.
            viewer.UpdateLayout(); double automatic = viewer.ActualHeight;
            Check(window.FindName("PreviewSplitter") is GridSplitter, "preview-has-a-splitter");
            window.PreviewControlsHeight = 60; Flush(); window.UpdateLayout(); Flush();
            Check(viewer.ActualHeight > automatic + 40, $"smaller-controls-give-the-score-room ({automatic:0} → {viewer.ActualHeight:0})");
            var barTop = bar.TranslatePoint(new Point(0, bar.ActualHeight), window).Y;
            Check(barTop < window.ActualHeight - 60, $"bar-stays-on-screen ({barTop:0} of {window.ActualHeight:0})");
            typeof(MainWindow).GetMethod("PreviewSplitterDragged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new DragCompletedEventArgs(0, 0, false)]);
            Check(Settings.Load(settingsPath).PreviewControlsHeight == 60, "split-is-remembered");
            CaptureFolderWindow(window, Path.Combine(reports, "preview-split.png"));
            typeof(MainWindow).GetMethod("PreviewSplitterReset", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, null]);
            Flush();
            Check(window.PreviewControlsHeight is null && Settings.Load(settingsPath).PreviewControlsHeight is null, "double-click-resets-the-split");

            // Queue summary: ready, to check (doubtful chords / review points) and failed.
            string analysisOk = Path.Combine(work, "ok.json"), analysisDoubt = Path.Combine(work, "duvida.json");
            File.WriteAllText(analysisOk, """{"chords":[{"confidence":0.9}],"reviewPoints":[],"warnings":[]}""");
            File.WriteAllText(analysisDoubt, """{"chords":[{"confidence":0.4},{"confidence":0.5},{"confidence":0.95}],"reviewPoints":[{"m":3}],"warnings":[]}""");
            var recipe = new ConversionQueueRecipe("a.mid", "", "", "", "", new(), new());
            ConversionQueueItem Item(string id, string name, ConversionQueueStatus status, string? analysis, string? error = null) =>
                new(id, name, name + ".mid", "", "", "", status, 1, "", error, DateTimeOffset.Now, DateTimeOffset.Now,
                    analysis is null ? null : new ConversionQueueResult("x.mscz", "x.png", "r", 2, "") { AnalysisPath = analysis }, recipe);
            var outcomes = ConversionQueueSummary.Build([Item("1", "Asa Branca", ConversionQueueStatus.Completed, analysisOk),
                Item("2", "Xote", ConversionQueueStatus.Completed, analysisDoubt), Item("3", "Baião", ConversionQueueStatus.Failed, null, "MuseScore 1"),
                Item("4", "Pendente", ConversionQueueStatus.Pending, null)]);
            Check(outcomes.Count == 3 && outcomes[0] is { Level: QueueOutcomeLevel.Ready, Detail: "2 páginas" }
                && outcomes[1] is { Level: QueueOutcomeLevel.Review } && outcomes[1].Detail.StartsWith("2 cifras com dúvida · 1 ponto de revisão")
                && outcomes[2] is { Level: QueueOutcomeLevel.Failed, Detail: "MuseScore 1" }, "queue-summary-classifies");
            Check(ConversionQueueSummary.Headline(outcomes) == "1 prontas · 1 para conferir · 1 com falha", "queue-summary-headline");
            var summary = new QueueSummaryWindow(outcomes) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            summary.Show(); Flush(); CaptureFolderWindow(summary, Path.Combine(reports, "queue-summary.png")); summary.Close();

            // Chords from the score: the measure's symbols map to the card's chords by order.
            row.Chords.Clear();
            foreach (var (root, quality) in new[] { ("C", ""), ("G", "7"), ("A", "m") }) row.Chords.Add(new ChordRow { Quarter = row.Chords.Count * 4, Root = root, Quality = quality });
            row.HasPendingScoreProperties = false; Flush();
            await Task.Delay(450); Flush(); // the setup above settles as its own undo step
            MusicHarmony H(int measure, string root, string quality) => new($"h{measure}", 1, measure, 1, 0, root, quality, true, "", true, null);
            var model = new NativeMusicModel(score, "", null!, [new MusicMeasure(1, 1, "", [], [H(1, "C", "")]), new MusicMeasure(1, 2, "", [], [H(2, "G", "7")]), new MusicMeasure(1, 3, "", [], [H(3, "A", "m")])], []);
            typeof(MainWindow).GetField("previewChordModel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new Func<string, NativeMusicModel>(_ => model));
            MeasureChordsWindow? measureDialog = null;
            typeof(MainWindow).GetField("measureChordsDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new Func<MeasureChordsWindow, bool?>(dialog =>
            {
                measureDialog = dialog;
                dialog.Editors[0].Root.SelectedItem = "D"; dialog.Editors[0].Quality.SelectedValue = "m7";
                dialog.Accept(); return true;
            }));
            await window.EditMeasureChords(2); Flush();
            Check(measureDialog is { Editors.Count: 1 } && measureDialog.Editors[0].Chord == row.Chords[1], "measure-shows-its-own-chord");
            Check(row.Chords[1] is { Root: "D", Quality: "m7" } && row.HasPendingScoreProperties && window.Status.Contains("Compasso 2"), "measure-chord-is-changed");
            await window.EditMeasureChords(0);
            Check(window.Status.Contains("Preparar"), "without-map-explains-how-to-prepare");

            // Undo / redo: the measure edit is one step; Ctrl+Z brings back G7, Ctrl+Y returns to Dm7.
            await Task.Delay(450); Flush();
            Check(window.CanUndo, "edit-can-be-undone");
            window.HandleShortcut(System.Windows.Input.Key.Z, System.Windows.Input.ModifierKeys.Control, null); Flush();
            Check(row.Chords[1] is { Root: "G", Quality: "7" } && !row.HasPendingScoreProperties && window.CanRedo, "ctrl-z-undoes");
            window.HandleShortcut(System.Windows.Input.Key.Y, System.Windows.Input.ModifierKeys.Control, null); Flush();
            Check(row.Chords[1] is { Root: "D", Quality: "m7" } && row.HasPendingScoreProperties, "ctrl-y-redoes");
            row.TransposeSemitones = 2; row.Chords[0].DiagramInversion = 1; await Task.Delay(450); Flush();
            window.Undo(); Flush();
            Check(row.TransposeSemitones == 0 && row.Chords[0].DiagramInversion is null && row.Chords[1].Root == "D", "close-changes-undo-together");
            var textBox = new TextBox();
            Check(!window.HandleShortcut(System.Windows.Input.Key.Z, System.Windows.Input.ModifierKeys.Control, textBox), "text-fields-keep-their-own-undo");

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
