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
        try
        {
            ThemeManager.Apply("graphite");
            var row = Music(("C", "", null), ("C", "", 1), ("C", "9", 1), ("F♯", "m7", null), ("H", "", null), ("C", "unsupported", null));
            string original = Snapshot(row);
            var dialog = new DiagramPositionsWindow(row);
            var table = Table(dialog); object first = table.Items[0];
            Pitches(first, [55, 60, 64], "default-second-inversion");
            Pitches(table.Items[2], [52, 55, 58, 60, 62], "ninth-first-inversion-keeps-all-five-notes");
            bool? accepted = Modal(dialog, async () =>
            {
                var firstRow = await Realize(table, first);
                var combo = Descendants(firstRow).OfType<ComboBox>().Single();
                var image = Descendants(firstRow).OfType<Image>().Single();
                Check(ReferenceEquals(image.Source, Value(first, "PreviewImage")), "cell-image-is-bound-to-its-draft");
                Check(combo.IsTabStop && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(combo)), "inversion-combo-is-keyboard-accessible");
                Check(AutomationProperties.GetName(image) == Value(first, "PreviewDescription") as string
                    || image.ToolTip?.ToString() == Value(first, "PreviewDescription") as string, "image-exposes-complete-note-description");
                var previousImage = image.Source;
                combo.SelectedIndex = 1; Flush();
                Pitches(first, [60, 64, 67], "combo-fundamental-updates-markers");
                Check(!ReferenceEquals(previousImage, image.Source) && ReferenceEquals(image.Source, Value(first, "PreviewImage")), "combo-refreshes-rendered-cell-immediately");
                combo.SelectedIndex = 2; Flush(); Pitches(first, [52, 55, 60], "combo-first-inversion-updates-markers");
                combo.SelectedIndex = 3; Flush(); Pitches(first, [55, 60, 64], "combo-second-inversion-updates-markers");
                Check(ReferenceEquals(previousImage, image.Source), "equivalent-position-reuses-cached-image");
                combo.SelectedIndex = 0; Flush();
                Check(Value(first, "Inversion") is null, "combo-default-clears-only-draft-override");
                Pitches(first, [55, 60, 64], "combo-default-restores-adopted-position");
                combo.SelectedIndex = 1; Flush(); Click(Button(dialog, "ApplySameChordButton")); Flush();
                Pitches(table.Items[1], [60, 64, 67], "same-chord-updates-other-occurrence-preview");
                Check((int?)Value(table.Items[2], "Inversion") == 1, "same-chord-does-not-change-other-quality");
                Click(Button(dialog, "ResetDiagramPositionsButton")); Flush();
                Check(dialog.Inversions.All(value => value is null), "reset-clears-every-draft-override");
                Pitches(table.Items[1], [55, 60, 64], "reset-refreshes-every-occurrence-preview");
                foreach (int index in new[] { 4, 5 })
                {
                    object unsupported = table.Items[index]; var unsupportedRow = await Realize(table, unsupported);
                    Check(Value(unsupported, "PreviewImage") is null && Value(unsupported, "PreviewUnavailableVisibility") is Visibility.Visible
                        && !string.IsNullOrWhiteSpace(Value(unsupported, "PreviewDescription") as string), "unsupported-chord-has-no-fallback-" + index);
                    Check(Descendants(unsupportedRow).OfType<TextBlock>().Any(block => block.IsVisible && block.Text.Contains("indispon", StringComparison.OrdinalIgnoreCase)),
                        "unsupported-chord-shows-unavailable-label-" + index);
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
                        Check(new[] { "ApplySameChordButton", "ResetDiagramPositionsButton", "SuggestComfortableVoicingsButton", "CancelDiagramPositionsButton", "ApplyDiagramPositionsButton" }
                            .All(name => FullyVisible(Button(dialog, name), dialog)), $"dialog-actions-fit-{theme}-{size.Width}");
                    }
                }
                Check(Snapshot(row) == original, "live-preview-never-edits-original-chords");
                Click(Button(dialog, "CancelDiagramPositionsButton"));
            });
            Check(accepted != true && Snapshot(row) == original, "cancel-preserves-original-inversions-and-music");

            var transposed = Music(("C", "", null), ("F♯", " sus4 ", 1), ("Bbb", "dim", 2), ("G♭", "aug", 0));
            transposed.DefaultDiagramInversion = 0; transposed.DiagramInversion = 1; transposed.TransposeSemitones = -2;
            var shifted = new DiagramPositionsWindow(transposed);
            var shiftedTable = Table(shifted);
            for (int index = 0; index < transposed.Chords.Count; index++)
            {
                var chord = transposed.Chords[index]; KeyboardDiagramVoicing.TryRootPitchClass(chord.Root, out int pc);
                Pitches(shiftedTable.Items[index], KeyboardDiagramVoicing.Pitches(pc - 2, chord.Quality.Trim(), chord.DiagramInversion ?? 1), "transposed-unicode-and-double-accidental-" + index);
                Check((string?)Value(shiftedTable.Items[index], "Symbol") == chord.Root + chord.Quality, "transposition-keeps-source-symbol-" + index);
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
            var matrixDialog = new DiagramPositionsWindow(matrix); var matrixTable = Table(matrixDialog);
            for (int index = 0; index < matrix.Chords.Count; index++)
            {
                var chord = matrix.Chords[index]; KeyboardDiagramVoicing.TryRootPitchClass(chord.Root, out int pc);
                Check(Value(matrixTable.Items[index], "PreviewImage") is DrawingImage drawing
                    && Markers(drawing).SequenceEqual(KeyboardDiagramVoicing.Pitches(pc, chord.Quality, chord.DiagramInversion!.Value)), "rendering-parity-" + index);
            }
            Check(matrix.Chords.Count == 720, "parity-covers-all-720-supported-voicings");
            Modal(matrixDialog, async () =>
            {
                object last = matrixTable.Items[matrixTable.Items.Count - 1]; var lastRow = await Realize(matrixTable, last);
                var lastImage = Descendants(lastRow).OfType<Image>().Single();
                Check(ReferenceEquals(lastImage.Source, Value(last, "PreviewImage")), "virtualized-last-row-has-correct-chord-image");
                Check(matrixTable.EnableRowVirtualization && Descendants(matrixTable).OfType<DataGridRow>().Count() < 40,
                    "large-chord-list-keeps-row-virtualization");
                object start = matrixTable.Items[0]; var startRow = await Realize(matrixTable, start);
                Check(ReferenceEquals(Descendants(startRow).OfType<Image>().Single().Source, Value(start, "PreviewImage")), "recycled-first-row-restores-correct-preview");
                Click(Button(matrixDialog, "CancelDiagramPositionsButton"));
            });

            var comfortable = Music(("C", "", null), ("F", "", null), ("G", "", null), ("C", "", null));
            comfortable.DefaultDiagramInversion = 0; string comfortableOriginal = Snapshot(comfortable);
            var comfortableDialog = new DiagramPositionsWindow(comfortable);
            Modal(comfortableDialog, async () =>
            {
                Exception? suggestionFailure = null; ComfortableVoicingPlan? expectedPlan = null;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    var suggestion = Current.Windows.OfType<ComfortableVoicingsWindow>().Single();
                    try
                    {
                        expectedPlan = suggestion.Plan;
                        Check(expectedPlan.ChangeCount > 0, "comfortable-fixture-offers-position-changes");
                        Check(Snapshot(comfortable) == comfortableOriginal, "comfortable-comparison-preserves-source-before-acceptance");
                        Click(Button(suggestion, "ApplyComfortableVoicingsButton"));
                    }
                    catch (Exception ex) { suggestionFailure = ex; suggestion.Close(); }
                }));
                Click(Button(comfortableDialog, "SuggestComfortableVoicingsButton"));
                await V018Wait(() => expectedPlan is not null || suggestionFailure is not null); Flush();
                if (suggestionFailure is not null) throw suggestionFailure;
                var comfortableTable = Table(comfortableDialog);
                for (int index = 0; index < comfortable.Chords.Count; index++)
                    Pitches(comfortableTable.Items[index], expectedPlan!.Suggestions[index].AfterPitches.ToArray(), "comfortable-accepted-preview-updates-" + index);
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
