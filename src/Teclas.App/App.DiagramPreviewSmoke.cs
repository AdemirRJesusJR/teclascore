using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Synthetic, in-memory chords only. This route never loads personal settings,
    // opens a score, renders through MuseScore, starts audio, or performs network I/O.
    private async void VerifyDiagramPreviewUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        await Task.Yield();
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        var checks = new Dictionary<string, bool>();
        var windows = new List<Window>();
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var trace = PresentationTraceSources.DataBindingSource;
        var previousLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning; trace.Listeners.Add(listener);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) =>
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Diagram preview verification exceeded 90 seconds." }));
            Shutdown(1);
        };
        timeout.Start();
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Diagram preview regression: " + name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        static object? Value(object draft, string name) => draft.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(draft);
        static DataGrid Table(DiagramPositionsWindow window) => (DataGrid)window.FindName("DiagramPositionsTable");
        static object Draft(DataGrid table, string root, string quality) => table.Items.Cast<object>().Single(item =>
            ((string)Value(item, "Root")!).Trim().Replace('♯', '#').Replace('♭', 'b').Equals(root, StringComparison.OrdinalIgnoreCase)
            && ((string)Value(item, "Quality")!).Trim() == quality);
        static void Position(object draft, int selectedIndex) => draft.GetType().GetProperty("SelectedIndex")!.SetValue(draft, selectedIndex);
        static Button Button(Window window, string name) => (Button)window.FindName(name);
        static void Click(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        static string Snapshot(MidiRow row) => JsonSerializer.Serialize(row.Chords.Select(chord => new
            { chord.Quarter, chord.Root, chord.Quality, chord.DiagramInversion }));
        static MidiRow Music(params (string Root, string Quality, int? Inversion)[] chords)
        {
            var row = new MidiRow { FullPath = "Synthetic diagram preview.mid", DefaultDiagramInversion = 2 };
            foreach (var (root, quality, inversion) in chords)
                row.Chords.Add(new ChordRow { Quarter = row.Chords.Count * 4, Root = root, Quality = quality, DiagramInversion = inversion });
            return row;
        }
        static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)
        {
            if (drawing is GeometryDrawing geometry) yield return geometry;
            if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                    foreach (var shape in Shapes(child)) yield return shape;
        }
        static int[] Markers(DrawingImage image)
        {
            // Coordinates are specified by the score SVG, independent of the preview implementation.
            int[] white = [48, 50, 52, 53, 55, 57, 59, 60, 62, 64, 65, 67, 69, 71];
            var expected = Enumerable.Range(48, 24).ToDictionary(pitch => pitch, pitch =>
            {
                int whiteIndex = Array.IndexOf(white, pitch);
                return whiteIndex >= 0 ? new Point(11 + 20 * whiteIndex, 49)
                    : new Point(21 + 20 * Array.IndexOf(white, pitch - 1), 29);
            });
            var result = new List<int>();
            foreach (var ellipse in Shapes(image.Drawing).Select(shape => shape.Geometry).OfType<EllipseGeometry>())
            {
                if (Math.Abs(ellipse.RadiusX - 4.2) > .001 || Math.Abs(ellipse.RadiusY - 4.2) > .001) continue;
                var keys = expected.Where(pair => (pair.Value - ellipse.Center).Length < .001).Select(pair => pair.Key).ToArray();
                if (keys.Length != 1) throw new InvalidOperationException("A note marker is not centered on a valid piano key: " + ellipse.Center);
                result.Add(keys[0]);
            }
            if (result.Count == 0 || result.Count != result.Distinct().Count()) throw new InvalidOperationException("Missing or duplicate piano markers.");
            return result.Order().ToArray();
        }
        void Pitches(object draft, int[] expected, string name)
        {
            Check(Value(draft, "PreviewImage") is DrawingImage image && Markers(image).SequenceEqual(expected), name);
            Check(Value(draft, "PreviewUnavailableVisibility") is Visibility.Collapsed && !string.IsNullOrWhiteSpace(Value(draft, "PreviewDescription") as string), name + "-description");
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
                    if (VisualTreeHelper.GetClip(frame) is Geometry clip)
                        visible.Intersect(frame.TransformToAncestor(root).TransformBounds(clip.Bounds));
                }
            return visible;
        }
        bool FullyVisible(FrameworkElement element, Window window)
        {
            var bounds = VisibleBounds(element, (FrameworkElement)window.Content);
            return !bounds.IsEmpty && bounds.Width >= element.ActualWidth - 1 && bounds.Height >= element.ActualHeight - 1;
        }
        void Capture(Window window, string name)
        {
            Flush(); window.UpdateLayout();
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
        bool? Modal(DiagramPositionsWindow window, Func<Task> action)
        {
            windows.Add(window); window.Opacity = 0; window.ShowInTaskbar = false; window.ShowActivated = false;
            Exception? failure = null;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                try { window.UpdateLayout(); await action(); }
                catch (Exception ex) { failure = ex; window.Close(); }
            }));
            bool? accepted = window.ShowDialog(); windows.Remove(window); Flush();
            if (failure is not null) throw new InvalidOperationException("Diagram preview modal check failed.", failure);
            return accepted;
        }
        async Task<DataGridRow> Realize(DataGrid table, object item)
        {
            table.SelectedItem = item; table.ScrollIntoView(item); table.UpdateLayout(); Flush();
            await V018Wait(() => table.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow);
            return (DataGridRow)table.ItemContainerGenerator.ContainerFromItem(item);
        }
        async Task InspectSuggestions(DiagramPositionsWindow parent, Action<ComfortableVoicingsWindow> inspect, bool accept)
        {
            Exception? failure = null; bool finished = false;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var suggestion = Current.Windows.OfType<ComfortableVoicingsWindow>().SingleOrDefault();
                if (suggestion is null) { failure = new InvalidOperationException("The grouped suggestion window did not open."); return; }
                try
                {
                    inspect(suggestion);
                    Click(Button(suggestion, accept ? "ApplyComfortableVoicingsButton" : "CancelComfortableVoicingsButton"));
                    finished = true;
                }
                catch (Exception ex) { failure = ex; suggestion.Close(); }
            }));
            Click(Button(parent, "SuggestComfortableVoicingsButton"));
            await V018Wait(() => finished || failure is not null); Flush();
            if (failure is not null) throw failure;
        }
        try
        {
            ThemeManager.Apply("graphite");
            var row = Music(("C", "", null), ("F♯", "m7", null), ("C", "", 1), ("C", "m", 2),
                ("C", "9", 1), ("C", "7", 0), ("C", "maj7", 2), ("C", "mMaj7", 1),
                ("F#", " m7 ", null), ("B♭", "", 0), ("Bb", "", 0), ("C", "m", 2),
                ("H", "", null), ("C", "unsupported", null));
            string original = Snapshot(row);
            var dialog = new DiagramPositionsWindow(row);
            var table = Table(dialog); object first = table.Items[0];
            var originalInversions = row.Chords.Select(chord => chord.DiagramInversion).ToArray();
            Check(table.Items.Count == 10 && table.Items.Cast<object>().Sum(item => (int)Value(item, "OccurrenceCount")!) == 14,
                "repeated-chords-collapse-to-ten-groups-with-fourteen-occurrences");
            Check(table.Columns.Any(column => column.Header?.ToString() == "Ocorrências")
                && table.Columns.All(column => !column.Header!.ToString()!.Contains("Semínima"))
                && dialog.FindName("ApplySameChordButton") is null, "grouped-editor-shows-occurrences-without-obsolete-copy-action");
            Check((int)Value(first, "OccurrenceCount")! == 2 && (bool)Value(first, "HasMixedPositions")!
                && (int)Value(first, "SelectedIndex")! == 4 && Value(first, "PreviewImage") is null
                && !string.IsNullOrWhiteSpace(Value(first, "PreviewDescription") as string), "mixed-group-explains-saved-positions-without-false-preview");
            Check(dialog.Inversions.SequenceEqual(originalInversions), "opening-keeps-every-original-occurrence-inversion");
            Check((int)Value(Draft(table, "F#", "m7"), "OccurrenceCount")! == 2
                && (int)Value(Draft(table, "Bb", ""), "OccurrenceCount")! == 2, "unicode-accidentals-and-quality-spaces-share-groups");
            Check(new[] { "", "m", "7", "maj7", "mMaj7", "9" }.Select(quality => Draft(table, "C", quality)).Distinct().Count() == 6,
                "major-minor-dominant-and-major-seventh-qualities-remain-distinct");
            Pitches(Draft(table, "C", "9"), [52, 55, 58, 60, 62], "ninth-first-inversion-keeps-all-five-notes");
            bool? accepted = Modal(dialog, async () =>
            {
                var firstRow = await Realize(table, first);
                var combo = Descendants(firstRow).OfType<ComboBox>().Single();
                var image = Descendants(firstRow).OfType<Image>().Single();
                Check(ReferenceEquals(image.Source, Value(first, "PreviewImage")), "cell-image-is-bound-to-its-draft");
                Check(combo.IsTabStop && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(combo)), "inversion-combo-is-keyboard-accessible");
                Check(AutomationProperties.GetName(image) == Value(first, "PreviewDescription") as string
                    || image.ToolTip?.ToString() == Value(first, "PreviewDescription") as string, "image-exposes-complete-note-description");
                Position(first, -1); Position(first, 4); Flush();
                Check(dialog.Inversions.SequenceEqual(originalInversions) && (bool)Value(first, "HasMixedPositions")!,
                    "unselected-and-mixed-sentinel-never-overwrite-saved-positions");
                dialog.Width = 800; dialog.Height = 580; Flush(); Capture(dialog, "diagram-preview-mixed-graphite-800");
                combo.SelectedIndex = 1; Flush();
                Pitches(first, [60, 64, 67], "combo-fundamental-updates-markers");
                Check(image.Source is not null && ReferenceEquals(image.Source, Value(first, "PreviewImage")), "combo-refreshes-rendered-cell-immediately");
                Check(dialog.Inversions[0] == 0 && dialog.Inversions[2] == 0 && !(bool)Value(first, "HasMixedPositions")!,
                    "choosing-a-position-updates-all-repeated-occurrences-immediately");
                Check(dialog.Inversions.Where((_, index) => index is not 0 and not 2).SequenceEqual(originalInversions.Where((_, index) => index is not 0 and not 2)),
                    "choosing-major-chord-leaves-other-groups-and-qualities-intact");
                combo.SelectedIndex = 2; Flush(); Pitches(first, [52, 55, 60], "combo-first-inversion-updates-markers");
                combo.SelectedIndex = 3; Flush(); Pitches(first, [55, 60, 64], "combo-second-inversion-updates-markers");
                var secondInversionImage = image.Source;
                combo.SelectedIndex = 1; Flush(); combo.SelectedIndex = 3; Flush();
                Check(ReferenceEquals(secondInversionImage, image.Source), "equivalent-position-reuses-cached-image");
                combo.SelectedIndex = 0; Flush();
                Check(Value(first, "Inversion") is null && dialog.Inversions[0] is null && dialog.Inversions[2] is null,
                    "choosing-default-clears-overrides-for-the-whole-group");
                Pitches(first, [55, 60, 64], "combo-default-restores-adopted-position");
                Check((int?)Value(Draft(table, "C", "9"), "Inversion") == 1, "group-default-does-not-change-other-quality");
                Click(Button(dialog, "ResetDiagramPositionsButton")); Flush();
                Check(dialog.Inversions.Count == 14 && dialog.Inversions.All(value => value is null)
                    && table.Items.Cast<object>().All(item => !(bool)Value(item, "HasMixedPositions")!), "reset-clears-every-occurrence-and-mixed-group");
                Pitches(first, [55, 60, 64], "reset-refreshes-group-preview");
                foreach (var (root, quality) in new[] { ("H", ""), ("C", "unsupported") })
                {
                    object unsupported = Draft(table, root, quality); var unsupportedRow = await Realize(table, unsupported);
                    Check(Value(unsupported, "PreviewImage") is null && Value(unsupported, "PreviewUnavailableVisibility") is Visibility.Visible
                        && !string.IsNullOrWhiteSpace(Value(unsupported, "PreviewDescription") as string), "unsupported-chord-has-no-fallback-" + root + quality);
                    Check(Descendants(unsupportedRow).OfType<TextBlock>().Any(block => block.IsVisible && block.Text.Contains("indispon", StringComparison.OrdinalIgnoreCase)),
                        "unsupported-chord-shows-unavailable-label-" + root + quality);
                }
                foreach (string theme in new[] { "graphite", "light" })
                {
                    ThemeManager.Apply(theme);
                    foreach (var size in new[] { (Width: 800, Height: 580), (Width: 900, Height: 730) })
                    {
                        dialog.Width = size.Width; dialog.Height = size.Height; Flush(); dialog.UpdateLayout();
                        firstRow = await Realize(table, first); image = Descendants(firstRow).OfType<Image>().Single();
                        combo = Descendants(firstRow).OfType<ComboBox>().Single();
                        Capture(dialog, $"diagram-preview-{theme}-{size.Width}");
                        Check(image.ActualHeight >= 45 && FullyVisible(image, dialog) && FullyVisible(combo, dialog), $"diagram-and-selector-visible-{theme}-{size.Width}");
                        Check(new[] { "ResetDiagramPositionsButton", "SuggestComfortableVoicingsButton", "CancelDiagramPositionsButton", "ApplyDiagramPositionsButton" }
                            .All(name => FullyVisible(Button(dialog, name), dialog)), $"dialog-actions-fit-{theme}-{size.Width}");
                    }
                }
                Check(Snapshot(row) == original, "live-preview-never-edits-original-chords");
                Click(Button(dialog, "CancelDiagramPositionsButton"));
            });
            Check(accepted != true && Snapshot(row) == original, "cancel-preserves-original-inversions-and-music");

            var legacy = Music(("C", "", null), ("G", "", 0), ("C", "", 2), ("D", "", 0), ("D", "", 1),
                ("C#", "m", 0), (" C♯ ", "m", 0), ("Db", "m", 0), ("C", "M", 0), ("C", "m", 1));
            var legacyOriginal = Snapshot(legacy); var legacyInversions = legacy.Chords.Select(chord => chord.DiagramInversion).ToArray();
            var legacyDialog = new DiagramPositionsWindow(legacy); var legacyTable = Table(legacyDialog);
            Check(legacyTable.Items.Count == 7 && !ReferenceEquals(Draft(legacyTable, "C#", "m"), Draft(legacyTable, "Db", "m"))
                && !ReferenceEquals(Draft(legacyTable, "C", "M"), Draft(legacyTable, "C", "m")), "enharmonic-spelling-and-quality-case-do-not-merge");
            Check((bool)Value(Draft(legacyTable, "C", ""), "HasMixedPositions")!
                && (bool)Value(Draft(legacyTable, "D", ""), "HasMixedPositions")!
                && legacyDialog.Inversions.SequenceEqual(legacyInversions), "legacy-default-and-matching-explicit-position-stay-mixed-without-mutation");
            bool? legacyAccepted = Modal(legacyDialog, async () =>
            {
                await InspectSuggestions(legacyDialog, suggestion =>
                {
                    var comparison = (DataGrid)suggestion.FindName("ComfortableVoicingsComparison");
                    Check(comparison.Items.Count == 7 && suggestion.Plan.Suggestions.Count == 10,
                        "comfortable-comparison-groups-labels-but-keeps-all-occurrence-results");
                    Check(suggestion.Plan.Suggestions[0].SuggestedInversion == 2 && suggestion.Plan.Suggestions[2].SuggestedInversion == 2
                        && suggestion.Plan.Suggestions[3].SuggestedInversion == 0 && suggestion.Plan.Suggestions[4].SuggestedInversion == 1,
                        "comfortable-suggestions-preserve-legacy-mixed-choices");
                }, accept: false);
                Check(legacyDialog.Inversions.SequenceEqual(legacyInversions), "canceling-grouped-suggestions-preserves-all-draft-choices");
                Position(Draft(legacyTable, "G", ""), 2); Flush();
                Check(legacyDialog.Inversions[0] is null && legacyDialog.Inversions[2] == 2
                    && legacyDialog.Inversions[3] == 0 && legacyDialog.Inversions[4] == 1,
                    "editing-another-group-does-not-flatten-legacy-mixed-positions");
                var dRow = await Realize(legacyTable, Draft(legacyTable, "D", ""));
                Descendants(dRow).OfType<ComboBox>().Single().SelectedIndex = 1; Flush();
                Check(legacyDialog.Inversions[3] == 0 && legacyDialog.Inversions[4] == 0,
                    "choosing-first-occurrence-position-still-updates-the-other-occurrence");
                var cRow = await Realize(legacyTable, Draft(legacyTable, "C", ""));
                Descendants(cRow).OfType<ComboBox>().Single().SelectedIndex = 0; Flush();
                Check(legacyDialog.Inversions[0] is null && legacyDialog.Inversions[2] is null,
                    "choosing-default-directly-from-mixed-clears-every-occurrence");
                Click(Button(legacyDialog, "ApplyDiagramPositionsButton"));
            });
            Check(legacyAccepted == true && legacyDialog.Inversions.SequenceEqual(new int?[] { null, 1, null, 0, 0, 0, 0, 0, 0, 1 }),
                "apply-returns-every-occurrence-in-original-interleaved-order");
            Check(Snapshot(legacy) == legacyOriginal, "accepted-dialog-defers-source-mutation-to-caller");

            var transposed = Music(("C", "", null), ("F♯", " sus4 ", 1), ("Bbb", "dim", 2), ("G♭", "aug", 0));
            transposed.DefaultDiagramInversion = 0; transposed.DiagramInversion = 1; transposed.TransposeSemitones = -2;
            var shifted = new DiagramPositionsWindow(transposed);
            var shiftedTable = Table(shifted);
            for (int index = 0; index < transposed.Chords.Count; index++)
            {
                var chord = transposed.Chords[index]; KeyboardDiagramVoicing.TryRootPitchClass(chord.Root, out int pc);
                Pitches(shiftedTable.Items[index], KeyboardDiagramVoicing.Pitches(pc - 2, chord.Quality.Trim(), chord.DiagramInversion ?? 1), "transposed-unicode-and-double-accidental-" + index);
                Check((string?)Value(shiftedTable.Items[index], "Symbol") == chord.Root.Trim().Replace('♯', '#').Replace('♭', 'b') + chord.Quality.Trim(),
                    "transposition-keeps-source-key-in-normalized-symbol-" + index);
            }
            Check(Descendants((DependencyObject)shifted.Content).OfType<TextBlock>().Any(block => block.Text.Contains("tom", StringComparison.OrdinalIgnoreCase)
                && block.Text.Contains("origem", StringComparison.OrdinalIgnoreCase)), "transposition-has-explicit-source-and-final-key-explanation");
            Modal(shifted, async () =>
            {
                shifted.Width = 800; shifted.Height = 580; Flush(); shifted.UpdateLayout();
                var realized = await Realize(shiftedTable, shiftedTable.Items[0]);
                Capture(shifted, "diagram-preview-transposed-minimum");
                Check(FullyVisible(Descendants(realized).OfType<Image>().Single(), shifted)
                    && FullyVisible(Button(shifted, "ApplyDiagramPositionsButton"), shifted), "transposed-heading-keeps-diagram-and-apply-visible-at-minimum");
                Click(Button(shifted, "CancelDiagramPositionsButton"));
            });

            var matrix = new MidiRow { FullPath = "Synthetic supported chords.mid", DefaultDiagramInversion = 2 };
            string[] roots = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];
            foreach (string root in roots)
                foreach (string quality in KeyboardDiagramVoicing.Qualities)
                    for (int inversion = 0; inversion < 3; inversion++) matrix.Chords.Add(new ChordRow
                    { Quarter = matrix.Chords.Count, Root = root, Quality = quality, DiagramInversion = inversion });
            var previews = new KeyboardDiagramPreview();
            for (int index = 0; index < matrix.Chords.Count; index++)
            {
                var chord = matrix.Chords[index]; KeyboardDiagramVoicing.TryRootPitchClass(chord.Root, out int pc);
                Check(previews.GetImage(chord.Root, chord.Quality, chord.DiagramInversion!.Value, 0) is DrawingImage drawing
                    && Markers(drawing).SequenceEqual(KeyboardDiagramVoicing.Pitches(pc, chord.Quality, chord.DiagramInversion!.Value)), "rendering-parity-" + index);
            }
            Check(matrix.Chords.Count == 720, "parity-covers-all-720-supported-voicings");
            var matrixDialog = new DiagramPositionsWindow(matrix); var matrixTable = Table(matrixDialog);
            Check(matrixTable.Items.Count == 240 && matrixTable.Items.Cast<object>().All(item => (int)Value(item, "OccurrenceCount")! == 3
                && (bool)Value(item, "HasMixedPositions")!), "large-sequence-has-240-unique-rows-preserving-720-legacy-choices");
            var matrixOriginal = matrix.Chords.Select(chord => chord.DiagramInversion).ToArray();
            Modal(matrixDialog, async () =>
            {
                object last = matrixTable.Items[matrixTable.Items.Count - 1]; var lastRow = await Realize(matrixTable, last);
                Descendants(lastRow).OfType<ComboBox>().Single().SelectedIndex = 3; Flush();
                var lastImage = Descendants(lastRow).OfType<Image>().Single();
                Check(lastImage.Source is DrawingImage && ReferenceEquals(lastImage.Source, Value(last, "PreviewImage")), "virtualized-last-row-has-correct-chord-image");
                Check(matrixTable.EnableRowVirtualization && Descendants(matrixTable).OfType<DataGridRow>().Count() < 40,
                    "large-chord-list-keeps-row-virtualization");
                object start = matrixTable.Items[0]; var startRow = await Realize(matrixTable, start);
                Check((bool)Value(start, "HasMixedPositions")! && Value(start, "PreviewImage") is null
                    && matrixDialog.Inversions.Take(717).SequenceEqual(matrixOriginal.Take(717)), "recycling-rows-does-not-resolve-unedited-mixed-groups");
                Descendants(startRow).OfType<ComboBox>().Single().SelectedIndex = 2; Flush();
                Check(Descendants(startRow).OfType<Image>().Single().Source is DrawingImage
                    && ReferenceEquals(Descendants(startRow).OfType<Image>().Single().Source, Value(start, "PreviewImage")), "recycled-first-row-restores-correct-preview");
                Check(matrixDialog.Inversions.Take(3).All(value => value == 1) && matrixDialog.Inversions.Skip(717).All(value => value == 2),
                    "virtualized-group-edits-reach-the-correct-original-occurrences");
                Click(Button(matrixDialog, "CancelDiagramPositionsButton"));
            });
            Check(matrix.Chords.Select(chord => chord.DiagramInversion).SequenceEqual(matrixOriginal), "large-group-cancel-preserves-original-sequence");

            var comfortable = Music(("C", "", null), ("F", "", null), ("G", "", null), ("C", "", null));
            comfortable.DefaultDiagramInversion = 0; string comfortableOriginal = Snapshot(comfortable);
            var comfortableDialog = new DiagramPositionsWindow(comfortable);
            Modal(comfortableDialog, async () =>
            {
                ComfortableVoicingPlan? expectedPlan = null;
                await InspectSuggestions(comfortableDialog, suggestion =>
                {
                    var comparison = (DataGrid)suggestion.FindName("ComfortableVoicingsComparison");
                    Check(comparison.Items.Count == 3 && suggestion.Plan.Suggestions.Count == 4,
                        "comfortable-repeated-sequence-shows-one-row-per-chord");
                    Check(((string?)Value(comparison.Items[0], "Label"))?.Contains('2') == true,
                        "comfortable-comparison-shows-repeated-chord-occurrence-count");
                    var reach = (ComboBox)suggestion.FindName("ComfortableVoicingsReach");
                    reach.SelectedValue = 9; Flush(); reach.SelectedValue = 12; Flush();
                    expectedPlan = suggestion.Plan;
                    Check(expectedPlan.ChangeCount > 0, "comfortable-fixture-offers-position-changes");
                    Check(expectedPlan.Suggestions[0].SuggestedInversion == expectedPlan.Suggestions[3].SuggestedInversion,
                        "comfortable-recalculation-keeps-repeated-chord-position-consistent");
                    Check(Snapshot(comfortable) == comfortableOriginal, "comfortable-comparison-preserves-source-before-acceptance");
                }, accept: true);
                var comfortableTable = Table(comfortableDialog);
                for (int index = 0; index < comfortableTable.Items.Count; index++)
                    Pitches(comfortableTable.Items[index], expectedPlan!.Suggestions[index].AfterPitches.ToArray(), "comfortable-accepted-preview-updates-" + index);
                Check(comfortableDialog.Inversions.Count == 4 && comfortableDialog.Inversions[0] == comfortableDialog.Inversions[3],
                    "comfortable-acceptance-propagates-to-every-repeated-occurrence");
                Check(Snapshot(comfortable) == comfortableOriginal, "comfortable-preview-still-preserves-source-until-parent-apply");
                Click(Button(comfortableDialog, "CancelDiagramPositionsButton"));
            });
            Check(Snapshot(comfortable) == comfortableOriginal, "cancel-after-suggestions-keeps-original-chords");
            listener.Flush(); Check(string.IsNullOrWhiteSpace(bindingOutput.ToString()), "no-wpf-binding-warnings");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, supportedVoicings = matrix.Chords.Count,
                filesOpened = 0, nativeRendererCalls = 0, audioStarted = false, networkRequests = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            timeout.Stop(); Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = ex.ToString(), bindingDiagnostics = bindingOutput.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            timeout.Stop(); foreach (var window in windows.ToArray()) try { window.Close(); } catch { }
            Shutdown(1);
        }
        finally { timeout.Stop(); trace.Listeners.Remove(listener); trace.Switch.Level = previousLevel; }
    }
}
