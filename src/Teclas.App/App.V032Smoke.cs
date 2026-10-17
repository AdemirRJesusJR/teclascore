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
    private sealed class RecordingMidiOutput : IMidiOutputTransport
    {
        public readonly List<uint> Messages = [];
        public void Send(uint message) => Messages.Add(message);
        public void Reset() { }
        public void Dispose() { }
    }

    // Chord alternatives, saved repertoires with PDF, MIDI pedal and half-page turns, MusicXML check and dark dialogs.
    private async void VerifyV032Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v032-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null; PresentationWindow? stage = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.32 smoke timed out." })); Shutdown(1); };
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
        string Page(string path, int height)
        {
            var bitmap = new RenderTargetBitmap(600, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual(); using (var context = visual.RenderOpen()) context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, height));
            bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path); encoder.Save(file); return path;
        }
        try
        {
            // Alternatives from the analysis text.
            var parsed = ChordAlternative.Parse("Am7, C6, Xz, F#m, Bb7");
            Check(parsed.Select(a => a.Label).SequenceEqual(new[] { "Am7", "C6", "F#m", "Bb7" }), "alternatives-are-parsed");

            // MusicXML summary.
            string xml = Path.Combine(work, "teste.musicxml");
            File.WriteAllText(xml, "<score-partwise><part><measure><harmony/><note><lyric><text>a</text></lyric></note><note/><harmony/></measure></part></score-partwise>");
            var summary = MusicXmlSummary.Read(xml);
            Check(summary is { Notes: 2, Harmonies: 2, Lyrics: 1 } && summary.Describe(3, 0).Contains("de 3") && summary.Describe(2, 0).Contains("diagramas"), "musicxml-summary-counts-and-explains");

            // Main window in the dark theme.
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), ThemeId = "graphite" }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1200, Height = 800 };
            window.Show(); Flush();
            ThemeManager.Apply("graphite"); Flush();
            string midi = Path.Combine(work, "cifras.mid"); File.WriteAllBytes(midi, SyntheticTwoTrackMidi());
            window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single(); ((ListBox)window.FindName("Library")).SelectedItem = row; Flush();
            window.SelectPageForSmoke("library", 1); Flush();
            var chord = new ChordRow { Root = "A", Quality = "m", Alternatives = "Am7, C6" }; row.Chords.Add(chord); Flush();
            var output = new RecordingMidiOutput();
            typeof(MainWindow).GetField("chordPreviewOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new Func<IMidiOutputTransport?>(() => output));
            ((DataGrid)window.FindName("ChordGrid")).SelectedItem = chord; Flush();
            var panel = (WrapPanel)window.FindName("ChordAlternativesPanel");
            Check(panel.Children.Count == 5, "alternatives-become-buttons");
            ((Button)panel.Children[1]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var expected = KeyboardDiagramVoicing.Pitches(9, "m7", row.EffectiveDiagramInversion);
            Check(window.LastChordPreview.SequenceEqual(expected) && output.Messages.Count(m => (m & 0xF0) == 0x90) == expected.Length, "alternative-is-heard-in-diagram-position");
            ((Button)panel.Children[2]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(chord is { Root: "A", Quality: "m7" } && window.Status.Contains("Am7"), "alternative-can-be-used");

            // Saved repertoires: create, switch, PDF of the list, delete (file kept aside).
            string pages = Path.Combine(work, "paginas"); Directory.CreateDirectory(pages);
            Page(Path.Combine(pages, "pagina-1.png"), 1600); Page(Path.Combine(pages, "pagina-2.png"), 1600);
            string score = Path.Combine(work, "musica.mscz"); File.WriteAllBytes(score, [1]);
            string presentation = Path.Combine(work, "presentation.json");
            var catalog = new PresentationCatalog(presentation);
            (IReadOnlyList<PresentationEntry> Songs, string Title, string Target, bool Tablet)? exported = null;
            stage = new PresentationWindow(new PresentationStore(presentation), [new(midi, score, "Asa Branca", null)],
                (_, _, _) => Task.FromResult(Path.Combine(pages, "pagina-1.png")), catalog,
                (songs, title, target, tablet) => { exported = (songs, title, target, tablet); return Task.FromResult("PDF salvo"); })
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Owner = window };
            stage.pdfTarget = _ => Path.Combine(work, "lista.pdf"); stage.pedalDevice = () => null;
            stage.Show(); Flush();
            stage.AddForSmoke(); Flush();
            await Wait(() => stage.Viewer.PageCount == 2, "presentation pages did not load");
            stage.CreateRepertoire("Festa junina"); Flush();
            Check(stage.Repertoires.Count == 2 && stage.RepertoireTitle == "Festa junina" && stage.Entries.Count == 0, "new-repertoire-opens-empty");
            stage.AddForSmoke(); Flush();
            await stage.ExportPdf();
            Check(exported is { Title: "Festa junina", Songs.Count: 1, Tablet: false } && exported.Value.Target.EndsWith("lista.pdf"), "repertoire-list-goes-to-pdf");
            stage.SwitchTo(presentation); Flush();
            Check(stage.RepertoireTitle == PresentationDocument.Empty.Title && stage.Entries.Count == 1, "switching-keeps-each-list");
            string junina = catalog.List().Single(item => !item.IsDefault).Path;
            stage.SwitchTo(junina); Flush(); stage.DeleteRepertoire(confirmed: true); Flush();
            Check(stage.Repertoires.Count == 1 && !File.Exists(junina) && Directory.EnumerateFiles(catalog.Folder, "*.excluido").Any(), "deleted-repertoire-is-kept-aside");

            // MIDI pedal while presenting: sustain forward, soft pedal back, only on the press.
            await Wait(() => !stage.IsPreparing && stage.Viewer.PageCount == 2, "pages after switching");
            stage.Present(true); Flush();
            Check(stage.IsPresenting, "presentation-starts");
            stage.Pedal(64, 127); Flush();
            Check(stage.CurrentPage == 2, "sustain-pedal-turns-forward");
            stage.Pedal(64, 100); Flush();
            Check(stage.CurrentPage == 2, "held-pedal-turns-once");
            stage.Pedal(64, 0); stage.Pedal(67, 127); Flush();
            Check(stage.CurrentPage == 1, "soft-pedal-turns-back");
            stage.Present(false); Flush();
            // Half page: a page taller than the screen scrolls first.
            stage.Viewer.Zoom = 3; Flush(); stage.Viewer.UpdateLayout();
            bool down = stage.Viewer.ScrollHalf(1); Flush(); stage.Viewer.UpdateLayout(); Flush();
            Check(down && stage.Viewer.ScrollHalf(-1), "half-page-scrolls-inside-the-page");

            // Dark theme: dialogs built in code take the theme (backgrounds dark, text light).
            var problems = new List<string>();
            void Inspect(Window dialog)
            {
                dialog.ShowActivated = false; dialog.ShowInTaskbar = false; dialog.Opacity = 0; dialog.Show(); Flush(); dialog.UpdateLayout(); Flush();
                CaptureFolderWindow(dialog, Path.Combine(reports, "dark-" + dialog.GetType().Name + ".png"));
                // Mean brightness of the rendered window: the dark theme must stay dark (keyboard keys excluded by their small share).
                int width = Math.Max(1, (int)dialog.ActualWidth), height = Math.Max(1, (int)dialog.ActualHeight);
                var shot = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); shot.Render(dialog);
                var pixels = new byte[width * height * 4]; shot.CopyPixels(pixels, width * 4, 0);
                double sum = 0; for (int i = 0; i < pixels.Length; i += 4) sum += .299 * pixels[i + 2] + .587 * pixels[i + 1] + .114 * pixels[i];
                double mean = sum / (width * height) / 255;
                if (mean > (dialog is PracticeWindow ? .5 : .35)) problems.Add($"{dialog.GetType().Name}: brilho médio {mean:0.00}");
                dialog.Close();
            }
            var history = new List<StudyRecord> { new() { Song = "a", Title = "Asa Branca", Date = DateTime.Now, Score = 60, Hits = 6, Misses = 4 } };
            Inspect(new StudyHistoryWindow(history));
            Inspect(new BatchPdfSelectionWindow([new("a", "Asa Branca", score)], 0, repertoire: true));
            Inspect(new PracticeWindow("Asa Branca", [new PracticeStep(1, 0, [60]) { Start = 0 }], [], true, 80) { clickOutput = () => null });
            Inspect(new MelodyBatchWindow(MelodyBatchWindow.Build(window.Files)));
            Inspect(new TextPromptWindow("Novo repertório", "Nome", "Festa"));
            Check(problems.Count == 0, "dark-theme-dialogs " + string.Join(" | ", problems.Distinct().Take(12)));
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            stage.Close(); stage = null; window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            stage?.Close(); window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
