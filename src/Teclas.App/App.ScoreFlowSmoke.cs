using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>The step-by-step locks of a new MIDI, driven through the real window without the Python engine.</summary>
public partial class App
{
    private async void VerifyScoreFlowUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), output = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(output);
        string work = Path.Combine(output, "score-flow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        MainWindow? window = null;
        var checks = new Dictionary<string, bool>();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Score flow smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name + " · " + window?.Status + " · next=" + window?.CanFlowNext + " reach=" + window?.FlowReachable + " gen=" + window?.CanGenerateNow + " idle=" + window?.IsIdle + " edit=" + window?.CanEditMidi + " pending=" + string.Join(" | ", window?.FlowCheckList.Where(c => c.Pending).Select(c => c.Id) ?? [])); checks.Add(name, true); }
        void Flush() { for (int i = 0; i < 3; i++) Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        try
        {
            string midi = Path.Combine(work, "tres-pistas.mid"), settingsPath = Path.Combine(work, "settings.json");
            byte[] Track(params byte[][] events) { var body = events.SelectMany(e => e).Concat(new byte[] { 0, 0xff, 0x2f, 0 }).ToArray();
                return [.. "MTrk"u8, (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body]; }
            byte[] Notes(int channel, int program, params int[] pitches) => [0, (byte)(0xC0 | channel), (byte)program,
                .. pitches.SelectMany(p => new byte[] { 0, (byte)(0x90 | channel), (byte)p, 90, 0x83, 0x60, (byte)(0x80 | channel), (byte)p, 0 })];
            // Piano melody in the middle, a flute line and a synth with notes above the 88-key piano (G8).
            File.WriteAllBytes(midi, [.. "MThd"u8, 0, 0, 0, 6, 0, 1, 0, 3, 1, 224,
                .. Track(Notes(0, 0, 60, 62, 64, 65, 67, 69, 71, 72, 71, 69, 67, 65, 64, 62, 60, 62)),
                .. Track(Notes(1, 73, 72, 74, 76, 77, 79, 77, 76, 74, 72, 74, 76, 77)),
                .. Track(Notes(2, 80, 103, 108, 115, 112, 108, 115))]);
            new Settings { OutputDirectory = Path.Combine(work, "output"), KeyboardKeys = 61 }.Save(settingsPath);
            window = new MainWindow(true, settingsPath, () => new SilentMidiOutput())
                { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1360, Height = 960 };
            window.Show(); Flush();
            window.SetThemeForSmoke("ocean");
            window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single();
            ((ListBox)window.FindName("Library")).SelectedItem = row;
            window.SelectPageForSmoke("library", 0); Flush();
            var tabs = (TabControl)window.FindName("EditorTabs");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Call(string method, params object?[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, args);
            void Click(string method) => Call(method, new Button(), new RoutedEventArgs());
            bool Pending(string id) => window.FlowCheckList.Any(c => c.Id == id && c.Pending);
            void Snapshot(string name)
            {
                window.UpdateLayout(); Flush();
                var content = (FrameworkElement)window.Content;
                int width = (int)content.ActualWidth, height = (int)content.ActualHeight;
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                    context.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
            }

            Check(window.GuidedFlow && tabs.SelectedIndex == 0 && window.FlowCurrentStep == ScoreFlowStep.Melody, "new-midi-starts-on-melodia");
            Check(window.SuggestedTrack is not null && window.ProgressItems.Count == 4, "melodia-suggests-a-track");
            Check(window.FlowSteps.Skip(1).All(s => s.IsLocked) && !window.CanGenerateNow && !window.CanEnqueueNow && !window.CanFlowNext, "later-steps-and-generation-are-locked");
            Check(Pending("melody-none") && window.GenerationButtonsVisibility == Visibility.Collapsed, "no-melody-blocks-and-generate-is-hidden");
            tabs.SelectedIndex = 2; Flush();
            Check(tabs.SelectedIndex == 0, "jumping-to-preview-is-reverted");

            TrackRow piano = row.Tracks[0], synth = row.Tracks[2];
            piano.IsMelody = true; synth.IsMelody = true; Flush();
            Check(window.FlowCheckList.Any(c => c.Id == "melody-outside-piano" && c.Level == ScoreFlowLevel.Block) && !window.CanFlowNext, "notes-beyond-the-piano-block");
            Snapshot("score-flow-1-melodia-bloqueada");
            Click("AnalyzeClick"); Flush();
            Check(window.Status.StartsWith("Antes de analisar", StringComparison.Ordinal) && row.AnalysisPath is null, "analyze-button-respects-the-lock");

            synth.IsMelody = false; Flush();
            Check(!window.FlowCheckList.Any(c => c.Id == "melody-outside-piano") && Pending("melody-listen") && !window.CanFlowNext, "listening-is-required");
            Click("PlayMarkedTracksClick");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (Pending("melody-listen") && clock.Elapsed < TimeSpan.FromSeconds(8)) { await Task.Delay(20); Flush(); }
            Check(!Pending("melody-listen"), "playing-the-marked-tracks-confirms-listening");
            Click("StopTrackAuditionClick"); Flush();
            clock.Restart(); while (!window.IsIdle && clock.Elapsed < TimeSpan.FromSeconds(8)) { await Task.Delay(20); Flush(); }
            foreach (var check in window.FlowCheckList.Where(c => c.Step == ScoreFlowStep.Melody && c.Pending && c.Level == ScoreFlowLevel.Confirm).ToArray())
                Call("ConfirmFlow", row, check.Id);
            Flush();
            Check(window.CanFlowNext && window.FlowReachable == ScoreFlowStep.Analysis && !window.CanGenerateNow, "melody-step-clears-but-analysis-is-next");
            Snapshot("score-flow-2-melodia-pronta");

            // Simulated analysis (the engine is not needed for the locks): a review point, a duplicate chord and a doubtful one.
            string analysis = Path.Combine(work, "analysis.json"); File.WriteAllText(analysis, "{}");
            row.AnalysisPath = analysis; row.KeyFifths = 2; row.KeyMinor = false;
            row.ReviewPoints.Add(new ScoreReviewPoint { Quarter = 4, Measure = 2, Beat = 1, MeasureStartQuarter = 4, MeasureEndQuarter = 8, Kind = "rhythm", Message = "Confira o ritmo", Blocking = true });
            foreach (var (q, root, quality, confidence) in new[] { (0.0, "D", "", .95), (4.0, "A", "", .92), (4.0, "A", "7", .9), (8.0, "G", "", .6), (12.0, "D", "", .97) })
                row.Chords.Add(new ChordRow { Quarter = q, Root = root, Quality = quality, Confidence = confidence });
            Call("NotifyFlow"); Flush();
            Check(Pending("melody-listen") == false && window.FlowAllowedTab == 3 && window.FlowAllowedStep == ScoreFlowStep.Analysis, "analysis-unlocks-the-ritmo-tab");
            tabs.SelectedIndex = 1; Flush();
            Check(tabs.SelectedIndex != 1, "cifras-waits-for-the-ritmo-step");
            tabs.SelectedIndex = 3; Flush();
            Check(tabs.SelectedIndex == 3 && window.FlowCurrentStep == ScoreFlowStep.Analysis && Pending("analysis-review"), "review-points-come-first");
            Check(window.RhythmPoints.Count == 1 && window.RhythmMarkers.Count == 1 && window.RhythmDensities.Count > 0 && !window.CanFlowNext, "ritmo-lists-the-point-on-the-timeline");
            Snapshot("score-flow-3-ritmo");
            Call("RhythmOkClick", new Button { Tag = window.RhythmPoints[0] }, new RoutedEventArgs()); Flush();
            Check(!Pending("analysis-review") && window.RhythmPoints[0].IsChecked && window.CanFlowNext && window.FlowNextLabel == "Continuar para cifras", "esta-certo-confirms-the-point");
            Click("FlowNextClick"); Flush();
            Check(tabs.SelectedIndex == 1 && window.FlowCurrentStep == ScoreFlowStep.Chords && !window.CanFlowNext, "chords-step-still-locked");
            Check(window.FlowCheckList.Any(c => c.Id == "chords-duplicate" && c.Level == ScoreFlowLevel.Block) && Pending("chords-doubtful") && Pending("chords-key"), "chord-locks-are-listed");
            Check(window.ChordTiles.Count == 5 && window.ChordTiles.Any(c => c.IsDoubtful) && window.KeyConfirmVisibility == Visibility.Visible && window.KeyTitle.Contains("Ré maior", StringComparison.Ordinal), "cifras-shows-key-and-tiles");
            Snapshot("score-flow-4-cifras");
            row.Chords.Remove(row.Chords.Single(c => c.Quality == "7")); Flush();
            Call("ConfirmFlow", row, "chords-doubtful"); Click("KeyConfirmClick"); Flush();
            Check(window.CanFlowNext && window.CanGenerateNow && window.KeyConfirmedVisibility == Visibility.Visible, "everything-confirmed-unlocks-generation");
            Click("FlowNextClick"); Flush();
            Check(tabs.SelectedIndex == 2 && window.FlowSteps[3].IsCurrent && window.FlowNextLabel == "Gerar partitura" && window.CanFlowNext && window.FlowEnqueueVisibility == Visibility.Visible && window.GenerateSummaryVisibility == Visibility.Visible, "next-opens-gerar-e-conferir-with-generate-in-the-footer");
            Snapshot("score-flow-5-gerar");
            Check(window.ProgressPanelVisibility == Visibility.Collapsed && window.FooterChecksVisibility == Visibility.Visible, "narrow-window-shows-checks-above-the-footer");
            window.Width = 1600; Flush();
            Check(window.ProgressPanelVisibility == Visibility.Visible && window.FooterChecksVisibility == Visibility.Collapsed && window.NavLabelVisibility == Visibility.Visible && window.ProgressItems.Count(i => i.IsDone) == 3, "wide-window-shows-the-progress-panel");
            var folderButton = (FrameworkElement)window.FindName("MidiFolderButton");
            Check(folderButton.TransformToAncestor(window).Transform(new Point(folderButton.ActualWidth, 0)).X <= ((FrameworkElement)window.Content).ActualWidth + 0.5, "top-bar-fits-with-labels");
            Snapshot("score-flow-6-largo");
            window.Width = 1360; Flush();

            piano.IsMelody = false; window.Files.Single().Tracks[1].IsMelody = true; Flush();
            Check(tabs.SelectedIndex == 0 && !window.CanGenerateNow && Pending("melody-listen"), "changing-the-melody-sends-the-user-back");
            timeout.Stop();
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); Shutdown(0);
        }
        catch (Exception error)
        {
            timeout.Stop();
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            try { window?.Close(); } catch { }
            Shutdown(1);
        }
    }
}
