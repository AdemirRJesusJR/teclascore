using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Compact library panel, chords-only and lyrics options, chord sheet → score, slash basses, QR code, phone state and
    // the lyrics screen. No engine or MuseScore runs.
    private async void VerifyV040Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v040-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null; PresentationWindow? stage = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.40 smoke timed out." })); Shutdown(1); };
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
        T Invoke<T>(object target, string name, params object[] args) =>
            (T)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args)!;
        try
        {
            string settingsPath = Path.Combine(work, "settings.json"), output = Path.Combine(work, "saida");
            // An existing (non-executable) file passes the path checks; no engine or MuseScore runs in this smoke.
            string placeholder = Path.Combine(work, "nao-executar.txt"); File.WriteAllText(placeholder, "");
            new Settings { OutputDirectory = output, PythonPath = placeholder, MuseScorePath = placeholder }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();

            // Library panel: the seven actions are compact rows with icons, and the clear button is an icon in the header.
            string[] tools = ["MelodySearchButton", "ChordSheetButton", "MelodyBatchButton", "LibraryBatchPdfButton", "RepertoirePdfButton", "ConversionQueueButton"];
            window.UpdateLayout();
            Check(tools.All(name => window.FindName(name) is Button { ActualHeight: > 0 and <= 31 }), "library-actions-are-compact-rows");
            Check(window.FindName("ClearLibraryButton") is Button { Width: 30 } clear && clear.ToolTip?.ToString()?.StartsWith("Limpar biblioteca") == true, "clear-library-is-an-icon-with-tooltip");
            var panel = (FrameworkElement)((FrameworkElement)window.FindName("LibrarySplitPanel")).Parent;
            double actions = ((Button)window.FindName("ConversionQueueButton")).TranslatePoint(new Point(0, 30), panel).Y;
            Check(actions < 300, $"actions-fit-in-the-top-of-the-panel ({actions:0} px)");
            CaptureElement(panel, Path.Combine(reports, "library-panel.png"));

            // Chords only and lyrics: saved per song, sent to the engine, no melody track needed.
            var midi = Path.Combine(work, "acompanhamento.mid"); File.WriteAllBytes(midi, SyntheticTwoTrackMidi()); window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single(); ((ListBox)window.FindName("Library")).SelectedItem = row; Flush();
            foreach (var track in row.Tracks) track.IsMelody = false;
            Flush();
            Check(!window.CanGenerate, "without-melody-generation-waits");
            window.ChordsOnly = true; window.IncludeLyrics = false; Flush();
            Check(window.CanGenerate && row.HasMelodyChoice, "chords-only-needs-no-melody-track");
            var analyze = Invoke<JsonObject>(window, "AnalyzeOptions", row);
            Check(analyze["chordsOnly"]?.GetValue<bool>() == true && analyze["melodySources"] is JsonArray { Count: 0 }, "chords-only-goes-to-the-analysis");
            var generation = Invoke<JsonObject>(window, "QueueGenerationOptions", row);
            Check(generation["includeLyrics"]?.GetValue<bool>() == false, "lyrics-choice-goes-to-the-generation");
            Check(window.FindName("ChordsOnlyCheckBox") is CheckBox { IsChecked: true } && window.FindName("IncludeLyricsCheckBox") is CheckBox { IsChecked: false }, "checkboxes-follow-the-song");
            window.SaveSessionForSmoke();
            var saved = Settings.Load(settingsPath).Library.Single(s => s.FullPath == row.FullPath);
            Check(saved.ChordsOnly && !saved.IncludeLyrics, "choices-are-saved");

            // Chord sheet: measures with words cut at the chord columns, slash basses kept, 7/9 still a chord type.
            const string sheet = "Intro: C  G/B\n\nC              G7        Am7\nAsa branca que voa no sertão\nD7/9   F°\n";
            var measures = ChordSheetParser.Measures(sheet);
            Check(measures.Select(m => m.Chord.Symbol).SequenceEqual(new[] { "C", "G/B", "C", "G7", "Am7", "D9", "Fdim" }), "sheet-measures-in-order " + string.Join(",", measures.Select(m => m.Chord.Symbol)));
            Check(measures[2].Words == "Asa branca que" && measures[3].Words == "voa no ser-" && measures[4].Words == "tão" && measures[0].Words == "", "words-follow-their-chords " + string.Join("|", measures.Select(m => m.Words)));
            Check(measures.Count(m => m.LineStart) == 3 && measures[2].LineStart && !measures[3].LineStart, "each-chord-line-starts-a-system");
            var request = window.ChordSheetRequest(measures, "Asa Branca", work, output);
            Check(request["mode"]!.GetValue<string>() == "chordSheet" && request["measures"]![1]!["bass"]!.GetValue<string>() == "B" && request["measures"]![2]!["words"]!.GetValue<string>() == "Asa branca que", "sheet-request-carries-bass-and-words");
            string fixture = Path.Combine(work, "cifra-fixture.mscz"); WriteMinimalScore(fixture);
            JsonObject? sent = null;
            window.chordSheetEngine = (json, _) =>
            {
                sent = json; string target = Path.Combine(json["outputDirectory"]!.GetValue<string>(), "Asa Branca.mscz");
                File.Copy(fixture, target); return Task.FromResult(target);
            };
            bool created = await window.CreateChordSheetScoreAsync(measures, "Asa Branca"); Flush();
            Check(created && sent is not null && window.Files.Any(f => f.FullPath.EndsWith("Asa Branca.mscz")), "sheet-score-is-created-and-imported");

            // Slash basses on the chord chart: symbol with the bass and a description naming it.
            var chartRow = new MidiRow { FullPath = "cifra", DefaultDiagramInversion = 2 };
            chartRow.Chords.Add(new ChordRow { Root = "G", Quality = "", Bass = "B" }); chartRow.Chords.Add(new ChordRow { Quarter = 1, Root = "G", Quality = "" });
            var entries = ChordChartWindow.BuildEntries(chartRow);
            Check(entries.Count == 2 && entries[0].Symbol == "G/B" && entries[0].Notes.Contains("Baixo (azul): Si3") && entries[1].Symbol == "G", "slash-bass-on-the-chart");
            var chart = new ChordChartWindow(chartRow) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }; chart.Show(); Flush();
            CaptureFolderWindow(chart, Path.Combine(reports, "slash-bass-chart.png")); chart.Close();

            // QR code of the remote address, and the state the phone shows.
            var qr = QrCode.Encode("http://192.168.0.12:51234/?k=123456");
            int size = qr.GetLength(0);
            bool Finder(int r, int c) => qr[r, c] && qr[r + 6, c + 6] && !qr[r + 1, c + 1] && qr[r + 3, c + 3];
            Check(size == 29 && Finder(0, 0) && Finder(0, size - 7) && Finder(size - 7, 0), "qr-code-has-version-3-finders");
            Check(PresentationWindow.QrImage(qr) is { PixelWidth: (29 + 8) * 4 }, "qr-image-has-quiet-zone");
            using (var server = new RemoteControlServer(_ => { }, 0, "123456") { State = new RemoteState("Asa Branca", 2, 5, 3, 4) })
            {
                var state = server.Answer("/state?k=123456");
                Check(state.Type == "application/json" && state.Body.Contains("\"song\":\"Asa Branca\"") && state.Body.Contains("\"pages\":4"), "phone-reads-song-and-page");
                Check(server.Answer("/state?k=000000").Status.StartsWith("403") && server.Answer("/?k=123456").Body.Contains("/state?k="), "state-needs-the-code-and-page-polls-it");
            }

            // Lyrics per page: syllables joined into words, one line per system, page by page.
            var syllables = new[] { new KaraokeSyllable(1, 0, "Ca", true), new KaraokeSyllable(1, 1, "sa", false), new KaraokeSyllable(2, 0, "de", false), new KaraokeSyllable(3, 0, "sol", false) };
            var regions = new[] { new ScoreMeasureRegion(1, new(1, .1, .1, .4, .1)), new ScoreMeasureRegion(2, new(1, .1, .3, .4, .1)), new ScoreMeasureRegion(3, new(2, .1, .1, .4, .1)) };
            var texts = KaraokeLyrics.PageTexts(syllables, regions);
            Check(texts.SequenceEqual(new[] { "Casa\nde", "sol" }), "lyrics-per-page " + string.Join(" | ", texts));

            // Presentation: QR next to the address, and the lyrics screen following page turns.
            string pages = Path.Combine(work, "paginas"); Directory.CreateDirectory(pages);
            WhitePage(Path.Combine(pages, "pagina-1.png")); WhitePage(Path.Combine(pages, "pagina-2.png"));
            string score = Path.Combine(work, "musica.mscz"); File.WriteAllBytes(score, [1]);
            stage = new PresentationWindow(new PresentationStore(Path.Combine(work, "presentation.json")), [new(midi, score, "Asa Branca", null)],
                (_, _, _) => Task.FromResult(Path.Combine(pages, "pagina-1.png")))
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Owner = window, PageLyrics = (_, _) => Task.FromResult<IReadOnlyList<string>>(["Asa branca", "voa no sertão"]) };
            stage.pedalDevice = () => null;
            stage.Show(); Flush(); stage.AddForSmoke(); Flush();
            await Wait(() => stage.Viewer.PageCount == 2, "presentation pages");
            stage.SetPhoneRemote(true); Flush();
            Check(stage.Remote is { State: { Song: "Asa Branca", Page: 1, Pages: 2 } }, "presentation-publishes-its-state");
            var qrImage = LogicalChildren(stage).OfType<Image>().FirstOrDefault(i => System.Windows.Automation.AutomationProperties.GetName(i).StartsWith("Código QR"));
            Check(qrImage is not null && (qrImage.Source is not null || stage.Remote!.Addresses().Count == 0), "presentation-shows-the-qr-code");
            CaptureFolderWindow(stage, Path.Combine(reports, "presentation-qr.png"));
            stage.SetPhoneRemote(false);
            var lyricsBox = (CheckBox)LogicalChildren(stage).OfType<CheckBox>().First(c => c.Name == "PresentationLyricsScreen");
            lyricsBox.IsChecked = true; lyricsBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Flush();
            await Wait(() => stage.LyricsWindow?.Lyrics == "Asa branca", "lyrics screen page 1");
            stage.LyricsWindow!.Opacity = 0;
            stage.Present(true); Flush(); stage.RemoteCommand("next"); Flush();
            await Wait(() => stage.LyricsWindow?.Lyrics == "voa no sertão", "lyrics screen page 2");
            Check(true, "lyrics-screen-follows-the-page");
            stage.Present(false); stage.Close(); stage = null;

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            stage?.Close(); window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }

    private static void WhitePage(string path)
    {
        var bitmap = new RenderTargetBitmap(600, 849, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var context = visual.RenderOpen()) context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 849));
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    /// <summary>A one-measure MuseScore file, enough to be imported as a native score.</summary>
    private static void WriteMinimalScore(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false));
        writer.Write("<museScore version='4.70'><Score><metaTag name='workTitle'>Asa Branca</metaTag><Part><Staff id='1'><StaffType group='pitched'/></Staff>" +
            "<Instrument id='piano'><Channel><program value='0'/></Channel></Instrument></Part><Staff id='1'><Measure><voice><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>" +
            "<Chord><durationType>whole</durationType><Note><pitch>60</pitch><tpc>14</tpc></Note></Chord></voice></Measure></Staff></Score></museScore>");
    }

    private static void CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        int w = (int)Math.Ceiling(element.ActualWidth), h = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush?)Current.TryFindResource("CardBrush") ?? Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, w, h));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
