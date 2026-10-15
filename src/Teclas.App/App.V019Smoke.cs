using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // The dependency runner and transpose renderer/publisher are explicit fakes. No installer,
    // network, native renderer, personal library or audio device is used by this CI route.
    private async void VerifyV019Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        await Task.Yield();
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "ui019-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>(); var windows = new List<Window>();
        var previewGeometry = new List<object>();
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var trace = PresentationTraceSources.DataBindingSource; var previousLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning; trace.Listeners.Add(listener);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.19 UI verification exceeded 90 seconds." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("v0.19 UI regression: " + name); checks[name] = true; }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
        static T Find<T>(Window window, string name) where T : FrameworkElement => window.FindName(name) as T
            ?? Descendants((DependencyObject)window.Content).OfType<T>().Single(element => element.Name == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        static void InvokeAccessible(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        static void Escape(Window window) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        void Capture(Window window, string name, int width, int height)
        {
            window.Width = width; window.Height = height; Flush(); window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            int w = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
            int h = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
            var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight), Stretch = Stretch.Fill }, null,
                    new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(reports, name + ".png")); encoder.Save(stream);
        }
        bool? Modal(Window window, Func<Task> test)
        {
            windows.Add(window); window.Opacity = 0; window.ShowInTaskbar = false; window.ShowActivated = false;
            Exception? failure = null;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                try { window.UpdateLayout(); await test(); }
                catch (Exception ex)
                {
                    failure = ex;
                    if (window is NativeTransposeWindow) Find<ComboBox>(window, "NativeTransposeInterval").SelectedValue = 0;
                    window.Close();
                }
            }));
            bool? result = window.ShowDialog(); windows.Remove(window); Flush();
            if (failure is not null) throw new InvalidOperationException("Modal UI check failed.", failure);
            return result;
        }
        MainWindow Open(string path)
        {
            var window = new MainWindow(true, path) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1360, Height = 920 };
            windows.Add(window); window.Show(); Flush(); window.UpdateLayout(); return window;
        }
        void Select(MainWindow window, MidiRow row)
        {
            window.SearchText = ""; window.CurrentFilter = "all"; window.SelectPageForSmoke("library", 2);
            Find<Selector>(window, "Library").SelectedItem = row; Flush(); window.UpdateLayout();
        }
        Rect VisibleBounds(FrameworkElement element, FrameworkElement root)
        {
            if (!element.IsVisible) return Rect.Empty;
            Rect visible = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            visible.Intersect(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null && parent != root; parent = VisualTreeHelper.GetParent(parent))
                if (parent is FrameworkElement frame)
                {
                    if (frame is ScrollContentPresenter || frame.ClipToBounds)
                        visible.Intersect(frame.TransformToAncestor(root).TransformBounds(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight)));
                    // WPF also applies a layout clip when a control's MinHeight exceeds
                    // its available grid row, even when ClipToBounds itself is false.
                    if (VisualTreeHelper.GetClip(frame) is Geometry clip)
                        visible.Intersect(frame.TransformToAncestor(root).TransformBounds(clip.Bounds));
                }
            return visible;
        }
        double VisibleHeight(FrameworkElement element, FrameworkElement root)
        { var bounds = VisibleBounds(element, root); return bounds.IsEmpty ? 0 : bounds.Height; }
        bool FitsHorizontally(FrameworkElement element, FrameworkElement root)
        { var bounds = VisibleBounds(element, root); return !bounds.IsEmpty && bounds.Width >= element.ActualWidth - 1; }
        try
        {
            string settingsPath = Path.Combine(work, "settings.json"), unused = Path.Combine(work, "never-execute.exe");
            File.WriteAllText(unused, "Synthetic executable fixture, never execute.");
            new Settings { ThemeId = "graphite", PythonPath = unused, MuseScorePath = unused, OutputDirectory = Path.Combine(work, "output") }.Save(settingsPath);
            string python = Path.Combine(work, "Python com espaço á.exe"), alternate = Path.Combine(work, "Python alternativo.exe"), muse = Path.Combine(work, "MuseScore4.exe");
            foreach (string path in new[] { python, alternate, muse }) File.WriteAllText(path, "Synthetic executable fixture.");
            var hashes = new[] { unused, python, alternate, muse }.ToDictionary(path => path, Hash);
            var main = Open(settingsPath);
            var fake = new V019DependencyRunner();
            var preparation = new DependencyPreparation(Path.Combine(work, "managed"), Path.Combine(work, "dependency-checks"), fake);
            string beforeSetup = Hash(settingsPath);
            var setup = new DependencySetupWindow(python, muse, preparation) { Owner = main };
            bool? setupAccepted = Modal(setup, async () =>
            {
                var apply = Find<Button>(setup, "ApplyDependenciesButton"); var verify = Find<Button>(setup, "CheckDependenciesButton");
                var pathBox = Find<TextBox>(setup, "PythonPathBox");
                Check(fake.Requests == 0 && !apply.IsEnabled, "setup-opening-does-not-execute-or-install");
                Check(AutomationProperties.GetName(pathBox) == "Caminho do Python" && pathBox.IsTabStop && verify.IsTabStop, "setup-path-and-action-keyboard-accessibility");
                Check(Find<Button>(setup, "BrowsePythonButton").IsEnabled && Find<Button>(setup, "BrowseMuseScoreButton").IsEnabled, "manual-path-selection-is-available");
                Click(verify); await V018Wait(() => apply.IsEnabled);
                Check(fake.Requests == 3 && Hash(settingsPath) == beforeSetup, "verification-never-applies-settings");
                Check(!Directory.Exists(preparation.ManagedRoot), "verification-never-creates-python-environment");
                pathBox.Text = alternate;
                Check(!apply.IsEnabled && !Find<Button>(setup, "PreparePythonButton").IsEnabled, "manual-path-change-invalidates-validation");
                fake.Hold = true; Click(verify); await V018Wait(() => fake.Waiting);
                int calls = fake.Requests; Click(verify); Click(apply);
                Check(fake.Requests == calls && setup.SelectedPythonPath == "" && !Find<StackPanel>(setup, "SetupFields").IsEnabled,
                    "setup-busy-rejects-double-click-and-apply");
                Check(Find<ProgressBar>(setup, "SetupProgress").IsVisible, "setup-shows-live-progress");
                pathBox.Text = python; await V018Wait(() => verify.IsEnabled);
                Check(!apply.IsEnabled && pathBox.Text == python && setup.SelectedPythonPath == "", "changed-path-cancels-in-flight-check-and-rejects-stale-answer");
                fake.Hold = false; Click(verify); await V018Wait(() => apply.IsEnabled);
                foreach (string theme in new[] { "graphite", "light" })
                {
                    main.SetThemeForSmoke(theme); Capture(setup, "v019-setup-" + theme, 640, 620);
                    Check(apply.ActualHeight >= 30 && apply.TranslatePoint(new Point(0, apply.ActualHeight), (UIElement)setup.Content).Y <= ((FrameworkElement)setup.Content).ActualHeight + 2,
                        "setup-actions-fit-minimum-window-" + theme);
                }
                main.SetThemeForSmoke("graphite"); InvokeAccessible(apply);
            });
            Check(setupAccepted == true && setup.SelectedPythonPath == python && setup.SelectedMuseScorePath == muse, "setup-confirmation-returns-exact-validated-paths");
            Invoke(main, "ApplyDependencyPaths", setup.SelectedPythonPath, setup.SelectedMuseScorePath);
            var configured = Settings.Load(settingsPath);
            Check(configured.PythonPath == python && configured.MuseScorePath == muse, "confirmed-paths-persist-through-host-callback");
            string afterSetup = Hash(settingsPath);
            var canceled = new DependencySetupWindow(alternate, muse, preparation) { Owner = main };
            bool? canceledResult = Modal(canceled, async () =>
            {
                fake.Hold = true; Click(Find<Button>(canceled, "CheckDependenciesButton")); await V018Wait(() => fake.Waiting);
                Escape(canceled); await V018Wait(() => Find<Button>(canceled, "CheckDependenciesButton").IsEnabled);
                Check(canceled.IsVisible && !Find<Button>(canceled, "ApplyDependenciesButton").IsEnabled, "escape-cancels-operation-before-closing");
                Escape(canceled);
            });
            fake.Hold = false;
            Check(canceledResult != true && canceled.SelectedPythonPath == "" && canceled.SelectedMuseScorePath == "" && Hash(settingsPath) == afterSetup,
                "canceled-setup-keeps-confirmed-settings");

            string firstPath = Path.Combine(work, "Canção Um.mid"), secondPath = Path.Combine(work, "Canção Dois.mid");
            File.WriteAllBytes(firstPath, V018Midi(60)); File.WriteAllBytes(secondPath, V018Midi(64));
            hashes[firstPath] = Hash(firstPath); hashes[secondPath] = Hash(secondPath);
            main.ImportForSmoke(firstPath); main.ImportForSmoke(secondPath);
            var first = main.Files.Single(row => row.FullPath == firstPath); var second = main.Files.Single(row => row.FullPath == secondPath);
            Select(main, first);
            Button Remove(MidiRow row) => Descendants((DependencyObject)main.FindName("Library")).OfType<Button>()
                .Single(button => button.Name == "RemoveLibraryCardButton" && ReferenceEquals(button.Tag, row));
            var removeSecond = Remove(second);
            Check(removeSecond.IsTabStop && AutomationProperties.GetName(removeSecond).Contains(second.Name) && removeSecond.ToolTip?.ToString() == "Remover", "library-trash-icon-has-label-and-keyboard-access");
            Invoke(main, "SetBusy", true); Click(removeSecond);
            Check(main.Files.Contains(second) && !main.CanUndoLibraryRemoval, "library-trash-rejects-removal-while-busy");
            Invoke(main, "SetBusy", false); InvokeAccessible(removeSecond); await V018Wait(() => !main.Files.Contains(second));
            Check(ReferenceEquals(main.SelectedFile, first) && main.Files.Count == 1 && main.CanUndoLibraryRemoval, "trash-removes-unselected-card-and-keeps-selection");
            Check(Hash(firstPath) == hashes[firstPath] && Hash(secondPath) == hashes[secondPath], "trash-keeps-original-midi-files");
            Click(Find<Button>(main, "UndoLibraryRemovalButton")); Flush();
            Check(main.Files.SequenceEqual(new[] { first, second }) && ReferenceEquals(main.SelectedFile, second), "undo-restores-card-order-and-selects-restored-item");
            foreach (string theme in new[] { "graphite", "light" })
            { main.SetThemeForSmoke(theme); Capture(main, "v019-library-" + theme, 1040, 700); Check(Remove(first).ActualWidth >= 30, "trash-visible-at-compact-size-" + theme); }
            main.SetThemeForSmoke("graphite");

            string source = Path.Combine(work, "Partitura original.mscz"), preview = Path.Combine(work, "original.png"), proposedPreview = Path.Combine(work, "proposta.png");
            V018Score(source); V018Preview(preview); V018Preview(proposedPreview); hashes[source] = Hash(source); hashes[preview] = Hash(preview);
            var native = new MidiRow { FullPath = source, NativeSourceSha256 = Hash(source), ScorePath = source, PreviewPath = preview, PageCount = 1,
                ScoreTitle = "Partitura sintética", GeneratedAt = DateTimeOffset.UtcNow, Status = "Importada" };
            main.Files.Add(native); Select(main, native);
            // Compact preview is a visual preference: it must not change the selected score,
            // playback state, eligibility of commands, or original files.
            Check(!main.IsPreviewControlsCompact, "compact-preview-default-is-expanded");
            var compactToggle = Find<Button>(main, "PreviewControlsToggleButton");
            var propertiesPanel = Find<FrameworkElement>(main, "PreviewPropertiesPanel");
            var advancedPanel = Find<FrameworkElement>(main, "PreviewAdvancedPlaybackPanel");
            var toolsPanel = Find<FrameworkElement>(main, "PreviewToolsPanel");
            var scoreViewer = Find<ScoreViewer>(main, "MainScoreViewer");
            await V018Wait(() => scoreViewer.FindName("PageImage") is Image { Source: not null });
            var pageImage = (Image)scoreViewer.FindName("PageImage");
            var playerPanel = Find<FrameworkElement>(main, "PreviewPlayerPanel");
            var essential = new FrameworkElement[] { Find<Button>(main, "PlayerPlayButton"), Find<Button>(main, "PlayerPauseButton"),
                Find<Button>(main, "PlayerStopButton"), Find<Slider>(main, "PlayerPositionSlider") };
            var commandStates = essential.Select(control => control.IsEnabled).ToArray();
            string? selectedScore = main.SelectedFile?.ScorePath;
            double position = main.PlaybackPositionSeconds;
            Check(compactToggle.IsTabStop && AutomationProperties.GetName(compactToggle).Contains("controles", StringComparison.OrdinalIgnoreCase),
                "compact-toggle-is-keyboard-and-screen-reader-accessible");
            foreach (var theme in ThemeManager.Themes.OrderBy(theme => theme.Id == "graphite" ? 0 : theme.Id == "light" ? 1 : 2))
            {
                main.SetThemeForSmoke(theme.Id);
                foreach (var size in new[] { (Width: 1100, Height: 740), (Width: 1400, Height: 960) })
                {
                    main.Width = size.Width; main.Height = size.Height; main.IsPreviewControlsCompact = false; Flush(); main.UpdateLayout();
                    for (DependencyObject? parent = VisualTreeHelper.GetParent(scoreViewer); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                        if (parent is ScrollViewer scroll) scroll.ScrollToTop();
                    Flush(); main.UpdateLayout();
                    double expandedHeight = VisibleHeight(scoreViewer, (FrameworkElement)main.Content);
                    double expandedPageHeight = VisibleHeight(pageImage, (FrameworkElement)main.Content);
                    if (theme.Id is "graphite" or "light") Capture(main, $"v019-expanded-{theme.Id}-{size.Width}", size.Width, size.Height);
                    Check(propertiesPanel.IsVisible && advancedPanel.IsVisible && toolsPanel.IsVisible && compactToggle.Content?.ToString() == "Compactar controles",
                        $"expanded-preview-restores-tools-{theme.Id}-{size.Width}");
                    Check(essential.Append(compactToggle).All(control => FitsHorizontally(control, (FrameworkElement)main.Content)),
                        $"expanded-preview-player-and-toggle-fit-width-{theme.Id}-{size.Width}");
                    InvokeAccessible(compactToggle); await V018Wait(() => main.IsPreviewControlsCompact); Flush(); main.UpdateLayout();
                    Check(propertiesPanel.Visibility == Visibility.Collapsed && advancedPanel.Visibility == Visibility.Collapsed && toolsPanel.Visibility == Visibility.Collapsed
                        && compactToggle.Content?.ToString() == "Expandir controles", $"compact-preview-hides-only-extra-panels-{theme.Id}-{size.Width}");
                    Check(essential.All(control => control.IsVisible) && essential.Select(control => control.IsEnabled).SequenceEqual(commandStates),
                        $"compact-preview-keeps-player-controls-and-state-{theme.Id}-{size.Width}");
                    Check(VisibleHeight(scoreViewer, (FrameworkElement)main.Content) > expandedHeight + 40,
                        $"compact-preview-increases-visible-score-area-{theme.Id}-{size.Width}");
                    Check(VisibleHeight(compactToggle, (FrameworkElement)main.Content) >= compactToggle.ActualHeight - 1,
                        $"compact-toggle-stays-in-view-{theme.Id}-{size.Width}");
                    Check(essential.Append(compactToggle).All(control => FitsHorizontally(control, (FrameworkElement)main.Content)),
                        $"compact-preview-player-and-toggle-fit-width-{theme.Id}-{size.Width}");
                    double compactPageHeight = VisibleHeight(pageImage, (FrameworkElement)main.Content);
                    previewGeometry.Add(new { theme = theme.Id, width = size.Width, height = size.Height,
                        expandedPageHeight, compactPageHeight, expandedViewerHeight = expandedHeight,
                        compactViewerHeight = VisibleHeight(scoreViewer, (FrameworkElement)main.Content) });
                    if (theme.Id is "graphite" or "light") Capture(main, $"v019-compact-{theme.Id}-{size.Width}", size.Width, size.Height);
                    Check(compactPageHeight >= 150 && compactPageHeight > expandedPageHeight + 40,
                        $"compact-preview-exposes-at-least-150px-of-page-{theme.Id}-{size.Width}");
                    string previousStatus = main.PlaybackStatus;
                    const string longError = "Áudio não preparado. Falha de reprodução sintética: confira o caminho da partitura e tente novamente após revisar as configurações.";
                    typeof(MainWindow).GetProperty(nameof(Teclas.App.MainWindow.PlaybackStatus))!.SetValue(main, longError); Flush(); main.UpdateLayout();
                    var errorStatus = Descendants(playerPanel).OfType<TextBlock>().Single(block => block.Text == longError);
                    Check(errorStatus.IsVisible && VisibleHeight(errorStatus, (FrameworkElement)main.Content) >= errorStatus.ActualHeight - 1
                        && FitsHorizontally(errorStatus, (FrameworkElement)main.Content) && errorStatus.ToolTip?.ToString() == longError
                        && errorStatus.TextTrimming == TextTrimming.CharacterEllipsis,
                        $"compact-preview-error-remains-visible-with-complete-tooltip-{theme.Id}-{size.Width}");
                    if (theme.Id is "graphite" or "light") Capture(main, $"v019-compact-error-{theme.Id}-{size.Width}", size.Width, size.Height);
                    typeof(MainWindow).GetProperty(nameof(Teclas.App.MainWindow.PlaybackStatus))!.SetValue(main, previousStatus); Flush(); main.UpdateLayout();
                    if (theme.Id is "graphite" or "light") Capture(main, $"v019-compact-{theme.Id}-{size.Width}", size.Width, size.Height);
                }
            }
            Check(main.SelectedFile?.ScorePath == selectedScore && main.PlaybackPositionSeconds == position && native.ScorePath == source,
                "compact-toggle-preserves-selection-score-and-playback-position");
            Check(Settings.Load(settingsPath).IsPreviewControlsCompact, "compact-preference-is-saved-immediately");
            main.SetThemeForSmoke("graphite");
            Check(main.CanTransposeScore, "native-score-transposition-is-enabled");
            native.HasPendingScoreProperties = true; Check(!main.CanTransposeScore, "native-transposition-blocks-pending-properties"); native.HasPendingScoreProperties = false;
            Invoke(main, "SetBusy", true); Check(!main.CanTransposeScore, "native-transposition-blocks-busy-host"); Invoke(main, "SetBusy", false);
            string? originalPath = native.ScorePath; native.ScorePath = Path.Combine(work, "missing.mscz");
            Check(!main.CanTransposeScore, "native-transposition-blocks-missing-score"); native.ScorePath = originalPath;
            int analyzed = 0, prepared = 0, published = 0; bool hold = false, waiting = false, failPrepare = false, failPublish = false;
            var initial = NativeScoreTransposition.CreatePlan(source, new(0));
            var transpose = new NativeTransposeWindow(initial, preview,
                (options, progress, token) => { analyzed++; token.ThrowIfCancellationRequested(); return Task.FromResult(NativeScoreTransposition.CreatePlan(source, options, token)); },
                async (plan, progress, token) =>
                {
                    prepared++; progress.Report("Prévia sintética em preparação");
                    if (hold) { waiting = true; try { await Task.Delay(Timeout.Infinite, token); } finally { waiting = false; } }
                    if (failPrepare) throw new IOException("Falha sintética de preparação");
                    string path = Path.Combine(work, "draft-" + prepared + ".mscz"); NativeScoreTransposition.WriteDraft(plan, path, token);
                    return new(path, [proposedPreview], NativeScoreDocument.Inspect(path), NativeScoreDocument.MusicalFingerprint(path));
                },
                (plan, result, progress, token) =>
                {
                    published++; token.ThrowIfCancellationRequested();
                    if (failPublish) throw new IOException("Falha sintética ao salvar");
                    string path = Path.Combine(work, "Partitura transposta.mscz"); File.Copy(result.Path, path, false);
                    return Task.FromResult(result with { Path = path });
                }, new EditorScorePlayback(unused, Path.Combine(work, "audio-never-used"))) { Owner = main };
            bool? transposed = Modal(transpose, async () =>
            {
                var interval = Find<ComboBox>(transpose, "NativeTransposeInterval"); var prepare = Find<Button>(transpose, "NativeTransposePrepare");
                var save = Find<Button>(transpose, "NativeTransposeSave"); var status = Find<TextBlock>(transpose, "NativeTransposeStatus");
                Check(!prepare.IsEnabled && !save.IsEnabled && analyzed == 0, "native-dialog-opens-without-processing-or-saving");
                interval.SelectedValue = 2; hold = true; Click(prepare); await V018Wait(() => waiting);
                Check(!save.IsEnabled && !interval.IsEnabled && Find<ProgressBar>(transpose, "NativeTransposeProgress").IsVisible, "native-preparation-shows-progress-and-locks-actions");
                Click(prepare); Click(save); Check(analyzed == 1 && prepared == 1 && published == 0, "native-busy-rejects-double-prepare-and-save");
                Click(Find<Button>(transpose, "NativeTransposeCancel")); await V018Wait(() => prepare.IsEnabled);
                Check(!save.IsEnabled && transpose.Result is null && status.Text.Contains("cancelada"), "native-cancel-restores-idle-without-result");
                hold = false; failPrepare = true; Click(prepare); await V018Wait(() => prepare.IsEnabled);
                Check(!save.IsEnabled && status.Text == "Falha sintética de preparação", "native-render-failure-never-enables-save");
                failPrepare = false; Click(prepare); await V018Wait(() => save.IsEnabled);
                Check(Find<DataGrid>(transpose, "NativeTransposeChanges").Items.Count > 0 && Find<RadioButton>(transpose, "NativeTransposeProposed").IsChecked == true,
                    "native-preview-lists-changes-and-shows-proposal");
                var viewer = Descendants((DependencyObject)transpose.Content).OfType<ScoreViewer>().Single();
                Find<RadioButton>(transpose, "NativeTransposeOriginal").IsChecked = true;
                Check(viewer.FirstPagePath == preview, "native-comparison-original-keeps-original-page");
                Find<RadioButton>(transpose, "NativeTransposeProposed").IsChecked = true;
                Check(viewer.FirstPagePath == proposedPreview, "native-comparison-proposal-shows-prepared-page");
                interval.SelectedValue = 3;
                Check(!save.IsEnabled && viewer.FirstPagePath == preview, "native-interval-change-invalidates-preview");
                Click(save); Check(published == 0, "native-stale-preview-cannot-be-published");
                Click(prepare); await V018Wait(() => save.IsEnabled);
                Find<CheckBox>(transpose, "NativeTransposeRemoveDiagrams").IsChecked = true;
                Check(!save.IsEnabled, "native-diagram-policy-change-invalidates-preview");
                Click(prepare); await V018Wait(() => save.IsEnabled);
                foreach (string theme in new[] { "graphite", "light" })
                {
                    main.SetThemeForSmoke(theme); Capture(transpose, "v019-transpose-" + theme, 940, 700);
                    Check(Find<DataGrid>(transpose, "NativeTransposeChanges").ActualHeight >= 60 && save.ActualHeight >= 30,
                        "native-transposition-content-visible-at-minimum-size-" + theme);
                }
                failPublish = true; Click(save); await V018Wait(() => save.IsEnabled);
                Check(transpose.Result is null && status.Text == "Falha sintética ao salvar" && native.ScorePath == source, "native-publish-failure-keeps-original-and-allows-retry");
                failPublish = false; InvokeAccessible(save);
            });
            Check(transposed == true && transpose.Result is not null && File.Exists(transpose.Result.Path) && published == 2, "native-confirmed-publication-returns-new-copy");
            Invoke(main, "ApplyMusicalEditorResult", native, transpose.Result!);
            Check(native.ScorePath == transpose.Result!.Path && native.HasMusicalEdits && native.Versions.Any(version => version.ScorePath == source)
                && native.Versions.Any(version => version.ScorePath == transpose.Result.Path), "native-publication-updates-library-and-history");
            Check(hashes.All(pair => Hash(pair.Key) == pair.Value), "all-synthetic-originals-preserved-byte-for-byte");
            main.Close(); windows.Remove(main); Flush();
            var reopened = Open(settingsPath);
            Check(reopened.IsPreviewControlsCompact, "compact-preference-survives-app-reopening");
            Check(reopened.PythonPath == python && reopened.MuseScorePath == muse, "dependency-paths-survive-app-reopening");
            var restored = reopened.Files.Single(row => row.FullPath == source);
            Check(restored.ScorePath == transpose.Result.Path && restored.Versions.Any(version => version.ScorePath == source)
                && restored.Versions.Any(version => version.ScorePath == transpose.Result.Path), "transposed-score-and-original-history-survive-reopening");
            Check(reopened.Files.Any(row => row.FullPath == firstPath) && reopened.Files.Any(row => row.FullPath == secondPath), "undone-card-removal-survives-reopening");
            reopened.Width = 1100; reopened.Height = 740; Select(reopened, restored);
            var reopenedViewer = Find<ScoreViewer>(reopened, "MainScoreViewer");
            await V018Wait(() => reopenedViewer.FindName("PageImage") is Image { Source: not null }); Flush(); reopened.UpdateLayout();
            Check(VisibleHeight((Image)reopenedViewer.FindName("PageImage"), (FrameworkElement)reopened.Content) >= 150,
                "compact-reopened-preview-aligns-useful-page-area-at-minimum-window");
            InvokeAccessible(Find<Button>(reopened, "PreviewControlsToggleButton"));
            await V018Wait(() => !reopened.IsPreviewControlsCompact);
            Check(Find<FrameworkElement>(reopened, "PreviewToolsPanel").IsVisible && Find<Button>(reopened, "ScorePropertiesButton").IsEnabled,
                "expand-after-reopening-restores-available-tools");
            reopened.Close(); windows.Remove(reopened); Flush();
            Check(!Settings.Load(settingsPath).IsPreviewControlsCompact, "expanded-preference-is-persisted-after-reopening");
            Check(fake.Requests > 0 && fake.InstallationRequests == 0 && !Directory.Exists(Path.Combine(work, "audio-never-used")), "smoke-performs-no-installation-network-or-audio");
            listener.Flush(); Check(string.IsNullOrWhiteSpace(bindingOutput.ToString()), "no-wpf-binding-errors");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, previewGeometry, installationsPerformed = 0, networkRequests = 0, nativeRendererCalls = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            timeout.Stop(); Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, previewGeometry, error = ex.ToString(), bindingDiagnostics = bindingOutput.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            timeout.Stop(); foreach (var window in windows.ToArray()) try
            { if (window is NativeTransposeWindow) Find<ComboBox>(window, "NativeTransposeInterval").SelectedValue = 0; window.Close(); } catch { }
            Shutdown(1);
        }
        finally { timeout.Stop(); trace.Listeners.Remove(listener); trace.Switch.Level = previousLevel; }
    }

    private sealed class V019DependencyRunner : IDependencyProcessRunner
    {
        public int Requests { get; private set; }
        public int InstallationRequests { get; private set; }
        public bool Hold { get; set; }
        public bool Waiting { get; private set; }
        public async Task<DependencyProcessResult> RunAsync(DependencyProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests++;
            if (request.Arguments.Contains("venv") || request.Arguments.Contains("pip"))
            { InstallationRequests++; throw new InvalidOperationException("Installations are forbidden in this UI smoke test."); }
            if (Hold) { Waiting = true; try { await Task.Delay(Timeout.Infinite, token); } finally { Waiting = false; } }
            if (request.Arguments.Contains("-c")) return new(0, JsonSerializer.Serialize(new { python = "3.13.9", supported = true, works = true, pillow = DependencyPreparation.PillowVersion }), "");
            if (request.Arguments.Contains("--version")) return new(0, "MuseScore Studio 4.6.5", "");
            if (request.Arguments.Contains("-o"))
            {
                await File.WriteAllTextAsync(request.Arguments[1], "<svg xmlns='http://www.w3.org/2000/svg'><path d='M 0 0 L 1 1'/></svg>", token);
                return new(0, "", "");
            }
            throw new InvalidOperationException("Unexpected synthetic dependency operation.");
        }
    }
}
