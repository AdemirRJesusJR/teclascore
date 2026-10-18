using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Teclas.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--verify-v034-ui")
        { VerifyV034Ui(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-v033-ui")
        { VerifyV033Ui(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-v032-ui")
        { VerifyV032Ui(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-v031-ui")
        { VerifyV031Ui(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-v030-ui")
        { VerifyV030Ui(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-v029-ui")
        { VerifyV029Ui(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-v028-ui")
        { VerifyV028Ui(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-bulk-pdf-ui")
        { VerifyBulkPdfUi(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-track-mix-ui")
        { VerifyTrackMixUi(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-diagram-propagation-ui")
        { VerifyDiagramPropagationUi(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-midi-folder-ui")
        { VerifyMidiFolderUi(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-metronome-ui")
        { VerifyMetronomeUi(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-score-audio-ui")
        { VerifyScoreAudioUi(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-output-directory-ui")
        { VerifyOutputDirectoryUi(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-audio-preparation-ui")
        {
            VerifyAudioPreparationUi(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-library-playback-ui")
        {
            VerifyLibraryPlaybackUi(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-diagram-preview-ui")
        {
            VerifyDiagramPreviewUi(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-v019-ui")
        {
            VerifyV019Ui(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-v018-ui")
        {
            VerifyV018Ui(e.Args[1]);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--verify-score-metadata")
        {
            VerifyScoreMetadata(e.Args[1], e.Args[2]);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--verify-track-audition")
        {
            VerifyTrackAudition(e.Args[1], e.Args[2]);
            return;
        }
        if (e.Args.Length == 4 && e.Args[0] == "--verify-import")
        {
            VerifyImport(e.Args[1], e.Args[2], e.Args[3]);
            return;
        }
        if (e.Args.Length is 5 or 6 && e.Args[0] == "--verify-playback")
        {
            VerifyPlayback(e.Args[1], e.Args[2], e.Args[3], e.Args[4], e.Args.Length == 6 ? e.Args[5] : null);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--verify-preferences")
        {
            VerifyPreferences(e.Args[1], e.Args[2]);
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--smoke")
        {
            RenderSmoke(e.Args);
            return;
        }
        new MainWindow().Show();
    }

    // Render the real visual tree without creating a native window or saving settings.
    private void RenderSmoke(string[] args)
    {
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var bindingSource = PresentationTraceSources.DataBindingSource;
        var originalLevel = bindingSource.Switch.Level;
        bindingSource.Switch.Level = SourceLevels.Warning;
        bindingSource.Listeners.Add(listener);
        try
        {
            var options = SmokeOptions.Parse(args);
            var window = new MainWindow(true);
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var failures = new List<object>();
            int requested = 0;
            if (options.Input is { } input)
            {
                IEnumerable<string> paths = Directory.Exists(input)
                    ? Directory.EnumerateFiles(input).Where(IsMidi).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    : [input];
                foreach (var path in paths)
                {
                    requested++;
                    try { window.ImportForSmoke(path); }
                    catch (Exception ex) { failures.Add(new { path, error = ex.Message }); }
                }
            }

            if (options.Result is { } resultPath) LoadResultForSmoke(window, resultPath);
            var navigationChecks = options.VerifyNavigation ? VerifyNavigation(window) : null;
            if (options.Result is not null && navigationChecks is not null)
                navigationChecks["conversion-completion-preserves-selection"] = 1;
            var themeChecks = options.VerifyThemes ? VerifyThemes(window, options.Output) : null;
            if (options.Theme is not null) window.SetThemeForSmoke(options.Theme);
            window.SetSidebarForSmoke(options.Collapsed);
            var sidebarChecks = options.VerifyNavigation ? VerifySidebar(window) : null;

            // Reflection keeps the renderer usable for old releases during visual comparison.
            var navigation = typeof(MainWindow).GetMethod("SelectPageForSmoke", BindingFlags.Instance | BindingFlags.Public);
            if (navigation is not null) navigation.Invoke(window, [options.Page, options.Tab]);
            else if (options.Page != "dashboard" || options.Tab != 0)
                throw new InvalidOperationException("Esta versão não expõe a navegação para a inspeção visual.");

            var content = (FrameworkElement)window.Content;
            content.Width = options.Width;
            content.Height = options.Height;
            content.Measure(new Size(options.Width, options.Height));
            content.Arrange(new Rect(0, 0, options.Width, options.Height));
            content.UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            content.UpdateLayout();
            if (options.Page == "about")
            {
                string version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
                var labels = Descendants(content).OfType<TextBlock>()
                    .Where(label => IsInVisibleBranch(label, content)).Select(label => label.Text).ToArray();
                if (window.AppDisplayName != "TeclaScore" || window.AppVersion != version
                    || !labels.Any(label => label.Contains("TeclaScore", StringComparison.Ordinal))
                    || !labels.Any(label => label.Contains(version, StringComparison.Ordinal)))
                    throw new InvalidOperationException("About must display TeclaScore and the running assembly version.");
            }
            var bitmap = new RenderTargetBitmap(options.Width, options.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(options.Output)) encoder.Save(stream);

            listener.Flush();
            File.WriteAllText(options.Output + ".diagnostics.json", JsonSerializer.Serialize(new
            {
                options.Page, options.Tab, options.Width, options.Height,
                requestedFiles = requested,
                importedFiles = window.Files.Count,
                unreadableFiles = window.Files.Count(f => f.Document is null),
                reviewedFiles = window.Files.Count(f => f.HasReviewedProfile),
                generatedFiles = window.GeneratedCount,
                completedPages = window.CompletedPages,
                selectedFile = window.SelectedFile?.Name,
                themeId = ThemeManager.Current.Id,
                appDisplayName = window.AppDisplayName,
                appVersion = window.AppVersion,
                sidebarCollapsed = window.IsSidebarCollapsed,
                sidebarWidth = window.SidebarWidth.Value,
                importFailures = failures,
                navigationChecks,
                themeChecks,
                sidebarChecks,
                bindingDiagnostics = bindingOutput.ToString(),
                scrollAreas = Descendants(content).OfType<ScrollViewer>()
                    .Where(s => IsInVisibleBranch(s, content) && s.ActualWidth > 0 && s.ActualHeight > 0)
                    .Select(s => new { s.Name, s.ActualWidth, s.ActualHeight, s.ExtentWidth, s.ViewportWidth, s.ExtentHeight, s.ViewportHeight })
                    .ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(args[1] + ".error.txt", ex.ToString());
            Shutdown(1);
        }
        finally
        {
            bindingSource.Listeners.Remove(listener);
            bindingSource.Switch.Level = originalLevel;
        }
    }

    private static bool IsMidi(string path) => new[] { ".mid", ".midi", ".kar" }.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static void LoadResultForSmoke(MainWindow window, string path)
    {
        // QA-only metadata reconstructed from an actual native conversion and its validation report.
        using var result = JsonDocument.Parse(File.ReadAllText(path));
        var data = result.RootElement;
        string sourceHash = data.GetProperty("source_sha256").GetString()!;
        string sourcePath = Path.GetFullPath(data.GetProperty("source").GetString()!);
        var row = window.Files.SingleOrDefault(row => row.Document is not null
            && string.Equals(row.Document.Sha256, sourceHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.FullPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("A conversão de teste não corresponde ao MIDI importado.");
        string score = data.GetProperty("scorePath").GetString()!;
        string preview = data.GetProperty("previewPath").GetString()!;
        int pages = data.GetProperty("pages").GetInt32();
        if (!File.Exists(score) || !File.Exists(preview) || Path.GetExtension(score) != ".mscz" || pages < 1)
            throw new InvalidDataException("A conversão de teste precisa de uma partitura e prévia existentes.");
        // Exercise the same completion controller used by GenerateClick, with genuine
        // existing output metadata and no call to the musical engine.
        window.CurrentFilter = row.CategoryCode;
        if (window.FindName("Library") is Selector library) library.SelectedItem = row;
        var runOperation = typeof(MainWindow).GetMethod("RunOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Func<Task> applyResult = () =>
        {
            row.PreviewPath = preview;
            row.PageCount = pages;
            row.GeneratedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(score));
            row.ScorePath = score;
            row.Status = $"Gerada · {row.PageCountText}";
            if (data.TryGetProperty("chords", out var chords))
            {
                row.Chords.Clear();
                foreach (var chord in chords.EnumerateArray()) row.Chords.Add(new ChordRow
                {
                    Quarter = chord.GetProperty("quarter").GetDouble(), Root = chord.GetProperty("root").GetString()!,
                    Quality = chord.GetProperty("quality").GetString()!, Confidence = chord.GetProperty("confidence").GetDouble()
                });
                row.KeyFifths = data.GetProperty("keyFifths").GetInt32();
                row.KeyMinor = data.GetProperty("keyMinor").GetBoolean();
            }
            return Task.CompletedTask;
        };
        var operation = (Task)runOperation.Invoke(window, [applyResult, row, "Preparando prévia", null])!;
        var wait = Stopwatch.StartNew();
        while (!operation.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(5))
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (!operation.IsCompletedSuccessfully || window.CurrentFilter != "all"
            || !ReferenceEquals(window.SelectedFile, row) || !window.IsIdle)
            throw new InvalidOperationException("A conclusão da conversão perdeu a seleção do editor filtrado.");
    }

    private static bool IsInVisibleBranch(DependencyObject element, DependencyObject root)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(current, root)) return true;
        }
        return false;
    }

    private static Dictionary<string, int> VerifyNavigation(MainWindow window)
    {
        string originalPage = window.CurrentPage, originalFilter = window.CurrentFilter, originalSearch = window.SearchText;
        var originalSelection = window.SelectedFile;
        var results = new Dictionary<string, int>();
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("UI regression: " + message); }
        try
        {
            var commit = typeof(MainWindow).GetMethod("CommitEdits", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Require(commit.Invoke(window, null) is true, "A non-editing grid prevented navigation.");
            Require(window.TotalCount == window.Files.Count, "Total count differs from imported library.");
            Require(window.ReadyCount + window.ReviewCount + window.GeneratedCount + window.ErrorCount == window.TotalCount,
                "Dashboard categories do not partition the library.");
            foreach (var page in new[] { "dashboard", "library", "production", "settings", "about" })
            {
                window.SelectPageForSmoke(page);
                Require(window.CurrentPage == page, "Navigation failed for " + page);
                Require(new[] { window.DashboardVisible, window.LibraryVisible, window.ProductionVisible, window.SettingsVisible, window.AboutVisible }
                    .Count(value => value == Visibility.Visible) == 1, "More than one page is visible.");
                results["page-" + page] = 1;
            }
            window.SearchText = "";
            foreach (var (filter, expected) in new[]
            {
                ("all", window.TotalCount), ("ready", window.ReadyCount), ("review", window.ReviewCount),
                ("generated", window.GeneratedCount), ("error", window.ErrorCount)
            })
            {
                window.CurrentFilter = filter;
                int actual = window.LibraryView.Cast<object>().Count();
                Require(actual == expected, $"Filter {filter} returned {actual}; expected {expected}.");
                results["filter-" + filter] = actual;
            }
            window.CurrentFilter = "all";
            window.SearchText = "__no_corresponding_midi_" + Guid.NewGuid().ToString("N");
            Require(!window.LibraryView.Cast<object>().Any(), "Search returned files for a nonexistent name.");
            results["search-no-match"] = 0;
            if (window.Files.FirstOrDefault() is { } first)
            {
                window.SearchText = first.Name.ToUpperInvariant();
                int expected = window.Files.Count(row => CultureInfo.GetCultureInfo("pt-BR").CompareInfo.IndexOf(
                    row.Name, first.Name, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
                int actual = window.LibraryView.Cast<object>().Count();
                Require(actual == expected, "Filename search is not case insensitive.");
                results["search-known-file"] = actual;
            }
            window.SearchText = "";
            Require(window.LibraryView.Cast<object>().Count() == window.TotalCount, "Clearing the search did not restore files.");
            if (window.FindName("Library") is Selector library && window.Files.FirstOrDefault(row => row.CategoryCode == "ready") is { } ready)
            {
                string originalStatus = ready.Status;
                string? originalAnalysis = ready.AnalysisPath;
                var originalChords = ready.Chords.ToArray();
                window.CurrentFilter = "ready";
                library.SelectedItem = ready;
                window.UseReviewedProfile = false;
                Require(window.CurrentFilter == "all" && ReferenceEquals(window.SelectedFile, ready),
                    "Switching a filtered reviewed MIDI to manual selection lost the editor selection.");
                Require(ready.CategoryCode == "review", "Manual selection did not update the dashboard category.");
                window.UseReviewedProfile = true;
                ready.Status = originalStatus;
                ready.AnalysisPath = originalAnalysis;
                ready.Chords.Clear();
                foreach (var chord in originalChords) ready.Chords.Add(chord);
                Require(ready.CategoryCode == "ready", "Restoring the reviewed profile did not update the dashboard category.");
                results["profile-toggle-preserves-selection"] = 1;
            }
            return results;
        }
        finally
        {
            window.SearchText = originalSearch;
            window.CurrentFilter = originalFilter;
            window.SelectPageForSmoke(originalPage);
            if (window.FindName("Library") is Selector library) library.SelectedItem = originalSelection;
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Dictionary<string, object> VerifyThemes(MainWindow window, string output)
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Theme regression: " + message); }
        string originalTheme = ThemeManager.Current.Id;
        string originalPage = window.CurrentPage;
        string fixtureDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "theme-settings-fixtures");
        Directory.CreateDirectory(fixtureDirectory);
        var results = new Dictionary<string, object>();
        try
        {
            Require(ThemeManager.Themes.Count == 5, "The five approved palettes must remain available.");
            Require(ThemeManager.Themes.Select(theme => theme.Id).Distinct().Count() == 5, "Theme IDs must be unique.");
            var content = (FrameworkElement)window.Content;
            var canvasSamples = new HashSet<string>();
            foreach (var theme in ThemeManager.Themes)
            {
                // Use the production property setter, without saving the real user's settings.
                string previousTheme = ThemeManager.Current.Id;
                int categoryNotifications = 0;
                var firstRow = window.Files.FirstOrDefault();
                System.ComponentModel.PropertyChangedEventHandler categoryChanged = (_, change) =>
                { if (change.PropertyName == nameof(MidiRow.CategoryColor)) categoryNotifications++; };
                if (firstRow is not null) firstRow.PropertyChanged += categoryChanged;
                window.SetThemeForSmoke(theme.Id);
                if (firstRow is not null) firstRow.PropertyChanged -= categoryChanged;
                Require(window.SelectedTheme.Id == theme.Id && ThemeManager.Current.Id == theme.Id,
                    "The picker and applied palette disagree for " + theme.Id);
                if (firstRow is not null && previousTheme != theme.Id)
                    Require(categoryNotifications > 0, "Library status colors did not notify their live bindings.");
                foreach (var row in window.Files)
                {
                    string token = row.CategoryCode switch { "ready" => "Ready", "generated" => "Generated", "error" => "Error", _ => "Review" };
                    Require(row.CategoryColor == theme.Palette[token], "A library status uses another theme's palette.");
                }
                content.Measure(new Size(1400, 900));
                content.Arrange(new Rect(0, 0, 1400, 900));
                content.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                foreach (var token in theme.Palette)
                {
                    string key = token.Key == "Card" ? "CardBrush" : token.Key;
                    var resource = Application.Current.TryFindResource(key);
                    Color actual = resource switch
                    {
                        SolidColorBrush brush => brush.Color,
                        Color color => color,
                        _ => throw new InvalidOperationException($"Theme resource {key} is missing or has the wrong type.")
                    };
                    Require(actual == (Color)ColorConverter.ConvertFromString(token.Value),
                        $"Resource {key} retained another palette's color after {theme.Id}.");
                }
                var rootBackground = content is Panel panel ? panel.Background : window.Background;
                Require(rootBackground is SolidColorBrush, "The root visual has no solid theme background.");
                var rootBrush = (SolidColorBrush)rootBackground;
                Require(rootBrush.Color == theme.Background.Color, "The root visual retained its old background.");
                canvasSamples.Add(rootBrush.Color.ToString());
                var contrast = new Dictionary<string, double>();
                foreach (var (foreground, background) in new[]
                {
                    ("Ink", "Card"), ("Ink", "Background"), ("Muted", "Card"), ("Muted", "Background"),
                    ("AccentInk", "Accent"), ("SoftInk", "Soft"), ("SidebarInk", "Sidebar"),
                    ("SidebarInk", "SidebarActive"), ("SidebarInk", "SidebarPanel"), ("SidebarMuted", "Sidebar"),
                    ("HeroInk", "HeroStartColor"), ("HeroInk", "HeroEndColor"),
                    ("HeroMuted", "HeroStartColor"), ("HeroMuted", "HeroEndColor"),
                    ("TableHeaderInk", "TableHeader"), ("Ready", "Card"), ("Generated", "Card"),
                    ("Review", "Card"), ("Error", "Card")
                })
                {
                    double ratio = Contrast(theme.Palette[foreground], theme.Palette[background]);
                    contrast[foreground + "/" + background] = Math.Round(ratio, 2);
                    Require(ratio >= 4.5, $"{theme.Id}: {foreground}/{background} contrast is {ratio:0.00}:1 (requires 4.5:1).");
                }
                results["contrast-" + theme.Id] = contrast;
                results["live-switch-" + theme.Id] = true;

                string settingsPath = Path.Combine(fixtureDirectory, theme.Id + ".json");
                var settings = new Settings
                {
                    ThemeId = theme.Id,
                    PythonPath = Environment.ProcessPath!,
                    MuseScorePath = Environment.ProcessPath!,
                    OutputDirectory = Path.Combine(fixtureDirectory, "output")
                };
                settings.Save(settingsPath);
                var restored = Settings.Load(settingsPath);
                Require(restored.ThemeId == theme.Id, "The selected theme did not survive settings reload.");
                Require(restored.OutputDirectory == settings.OutputDirectory && restored.PythonPath == settings.PythonPath
                    && restored.MuseScorePath == settings.MuseScorePath, "Saving the theme changed other settings.");
                settings.ThemeId = "classic";
                settings.Save(settingsPath);
                Require(Settings.Load(settingsPath).ThemeId == "classic", "Atomic replacement did not save the second choice.");
                results["persist-" + theme.Id] = true;
            }
            Require(canvasSamples.Count >= 4, "The root visual did not change when switching themes.");
            results["distinct-live-backgrounds"] = canvasSamples.Count;

            foreach (string value in new[] { "", "outdated-theme-id", "OCEAN", "../../outside" })
            {
                string normalized = ThemeManager.NormalizeId(value);
                Require(ThemeManager.Themes.Any(theme => theme.Id == normalized), "Unknown theme did not normalize safely.");
                if (value != "OCEAN") Require(normalized == "classic", "Invalid theme did not fall back to the original palette.");
            }
            string oldSettings = Path.Combine(fixtureDirectory, "legacy.json");
            File.WriteAllText(oldSettings, JsonSerializer.Serialize(new
            {
                PythonPath = Environment.ProcessPath!, MuseScorePath = Environment.ProcessPath!,
                OutputDirectory = fixtureDirectory
            }));
            var legacy = Settings.Load(oldSettings);
            Require(legacy.ThemeId == "classic" && legacy.OutputDirectory == fixtureDirectory,
                "Version 0.2 settings were not migrated without changing the output folder.");
            string invalidSettings = Path.Combine(fixtureDirectory, "invalid-theme.json");
            File.WriteAllText(invalidSettings, "{\"ThemeId\":\"removed-theme\"}");
            Require(Settings.Load(invalidSettings).ThemeId == "classic", "Unknown persisted theme did not fall back safely.");
            string corruptSettings = Path.Combine(fixtureDirectory, "corrupt.json");
            File.WriteAllText(corruptSettings, "{broken");
            Require(Settings.Load(corruptSettings).ThemeId == "classic", "Corrupt settings prevented the default theme.");
            Require(Settings.Load(Path.Combine(fixtureDirectory, "missing.json")).ThemeId == "classic", "First launch lacks a default theme.");
            results["legacy-settings-migration"] = true;
            results["invalid-theme-fallback"] = true;
            results["missing-and-corrupt-settings-fallback"] = true;
            return results;
        }
        finally
        {
            window.SetThemeForSmoke(originalTheme);
            window.SelectPageForSmoke(originalPage);
        }
    }

    private static double Contrast(string foreground, string background)
    {
        static double Luminance(string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            static double Linear(byte component)
            { double value = component / 255.0; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double first = Luminance(foreground), second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static Dictionary<string, bool> VerifySidebar(MainWindow window)
    {
        bool original = window.IsSidebarCollapsed;
        var results = new Dictionary<string, bool>();
        try
        {
            foreach (bool collapsed in new[] { false, true, false })
            {
                window.SetSidebarForSmoke(collapsed);
                window.UpdateLayout();
                if (window.SidebarWidth.Value != (collapsed ? 72 : 232)
                    || window.ExpandedSidebarVisible != (collapsed ? Visibility.Collapsed : Visibility.Visible))
                    throw new InvalidOperationException("Sidebar dimensions do not follow the compact preference.");
                if (window.FindName("SidebarToggle") is not Button toggle || toggle.ToolTip is null)
                    throw new InvalidOperationException("The sidebar toggle must remain identifiable in compact mode.");
                if (window.FindName("SidebarMenu") is not DependencyObject menu)
                    throw new InvalidOperationException("The sidebar menu is missing.");
                var buttons = Descendants(menu).OfType<Button>().ToArray();
                string[] pages = ["dashboard", "library", "production", "settings", "about"];
                if (buttons.Length != 7 || buttons.Any(button => string.IsNullOrWhiteSpace(button.ToolTip?.ToString()))
                    || !buttons.Where(button => button.Tag is string).Select(button => (string)button.Tag).ToHashSet().SetEquals(pages)
                    || !buttons.Any(button => button.Name == "PresentationButton")
                    || !buttons.Any(button => button.Name == "RecoverDraftsButton"))
                    throw new InvalidOperationException("Each menu icon needs a tooltip in compact mode.");
                foreach (var page in pages)
                {
                    window.SelectPageForSmoke(page);
                    if (window.CurrentPage != page) throw new InvalidOperationException("Compact navigation failed: " + page);
                }
                results[collapsed ? "collapsed-navigation" : "expanded-navigation"] = true;
            }
            return results;
        }
        finally { window.SetSidebarForSmoke(original); }
    }

    private sealed record SmokeOptions(string Output, string? Input, string? Result, string Page, int Tab, int Width, int Height, bool VerifyNavigation, string? Theme, bool VerifyThemes, bool Collapsed)
    {
        public static SmokeOptions Parse(string[] args)
        {
            string? input = null;
            string? result = null;
            string page = "dashboard";
            int tab = 0, width = 1400, height = 900;
            bool verifyNavigation = false, verifyThemes = false, collapsed = false;
            string? theme = null;
            for (int i = 2; i < args.Length; i++)
            {
                string value = args[i];
                if (value == "--verify-navigation") { verifyNavigation = true; continue; }
                if (value == "--verify-themes") { verifyThemes = true; continue; }
                if (value == "--collapsed") { collapsed = true; continue; }
                if (!value.StartsWith("--", StringComparison.Ordinal))
                {
                    if (input is not null) throw new ArgumentException("Informe um único arquivo ou pasta MIDI.");
                    input = value;
                    continue;
                }
                if (++i >= args.Length) throw new ArgumentException("Valor ausente para " + value);
                switch (value)
                {
                    case "--page": page = args[i].ToLowerInvariant(); break;
                    case "--result": result = args[i]; break;
                    case "--theme": theme = args[i]; break;
                    case "--tab": tab = int.Parse(args[i], CultureInfo.InvariantCulture); break;
                    case "--width": width = int.Parse(args[i], CultureInfo.InvariantCulture); break;
                    case "--height": height = int.Parse(args[i], CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException("Opção desconhecida: " + value);
                }
            }
            if (page is not ("dashboard" or "library" or "production" or "settings" or "about")) throw new ArgumentException("Página inválida.");
            if (width is < 1000 or > 2560 || height is < 600 or > 1800 || tab is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(args));
            return new(args[1], input, result, page, tab, width, height, verifyNavigation, theme, verifyThemes, collapsed);
        }
    }
}
