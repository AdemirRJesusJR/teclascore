using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Crash log, labels and smart lists, MusicXML import, chord sheets and the phone remote. No engine or MuseScore runs.
    private async void VerifyV039Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v039-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.39 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            // Crash log: anonymized files, only the newest 20 kept.
            string crashes = Path.Combine(work, "falhas"); string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string? first = WriteCrashLog(crashes, "teste", new InvalidOperationException("falhou em " + profile + @"\Documents"));
            Check(first is not null && File.ReadAllText(first).Contains("%USERPROFILE%") && !File.ReadAllText(first).Contains(profile, StringComparison.OrdinalIgnoreCase), "crash-log-is-anonymized");
            for (int i = 0; i < 24; i++) { WriteCrashLog(crashes, "teste", new Exception("x" + i)); await Task.Delay(2); }
            Check(Directory.GetFiles(crashes, "falha-*.txt").Length == KeptCrashLogs && SupportReport.FindDiagnostics([crashes]).Count == SupportReport.MaximumDiagnostics, "crash-logs-are-trimmed-and-reported");

            // Chord sheet text: chord lines found, lyrics ignored, Brazilian spellings read, slash basses kept apart (v0.40).
            var sheet = ChordSheetParser.Parse("Intro: C  G/B  Am7  F7M\n\nC              G\nAsa branca que voa no sertão\nAm7          D7(9)    F°   Bb4\nFoi embora Xyz9");
            Check(sheet.Distinct.Select(c => c.Symbol).SequenceEqual(new[] { "C", "G/B", "Am7", "Fmaj7", "G", "D9", "Fdim", "Bbsus4" }), "chord-sheet-reads-chords " + string.Join(",", sheet.Distinct.Select(c => c.Symbol)));
            Check(sheet.Sequence.Count == 10, "chord-sheet-keeps-the-sequence");

            // Phone remote: only the code-protected page and commands answer.
            var commands = new List<string>();
            using (var server = new RemoteControlServer(commands.Add, 0, "123456"))
            {
                Check(server.Answer("/?k=000000").Status.StartsWith("403") && server.Answer("/cmd?a=next&k=999999").Status.StartsWith("403"), "remote-refuses-a-wrong-code");
                Check(server.Answer("/cmd?a=format&k=123456").Status.StartsWith("404") && server.Answer("/?k=123456").Body.Contains("Página"), "remote-answers-only-its-page-and-commands");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var answer = await http.GetAsync($"http://127.0.0.1:{server.Port}/cmd?a=next&k=123456");
                Check(answer.IsSuccessStatusCode && commands.SequenceEqual(new[] { "next" }), "remote-command-arrives-over-http");
            }

            // Library: labels (#busca) and smart lists.
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            for (int i = 0; i < 3; i++)
            {
                var bytes = SyntheticTwoTrackMidi(); bytes[^10] = (byte)(i + 1);
                string midi = Path.Combine(work, $"musica{i}.mid"); File.WriteAllBytes(midi, bytes); window.ImportForSmoke(midi); Flush();
            }
            var rows = window.Files.ToArray();
            rows[0].Tags = "lentas, missa"; rows[1].Tags = "festa";
            rows[1].Chords.Add(new ChordRow { Root = "A", Quality = "m" }); rows[1].KeyMinor = true;
            rows[2].Chords.Add(new ChordRow { Root = "C", Quality = "", Confidence = .3 }); rows[2].KeyMinor = false;
            var cards = (ListBox)window.FindName("Library");
            window.SearchText = "#Lentas"; Flush();
            Check(cards.Items.Count == 1 && cards.Items[0] == rows[0], "label-search-finds-the-card");
            window.SearchText = ""; window.SmartList = "menor"; Flush();
            Check(cards.Items.Count == 1 && cards.Items[0] == rows[1], "smart-list-minor-key");
            window.SmartList = "cifras-duvida"; Flush();
            Check(cards.Items.Count == 1 && cards.Items[0] == rows[2], "smart-list-doubtful-chords");
            window.SmartList = "nao-estudadas"; Flush();
            Check(cards.Items.Count == 3, "smart-list-not-studied");
            window.SmartList = ""; window.SaveSessionForSmoke();
            Check(Settings.Load(settingsPath).Library.Single(s => s.FullPath == rows[0].FullPath).Tags == "lentas, missa", "labels-are-saved");

            // MusicXML: converted into a new MuseScore file in the output folder (original kept).
            string xml = Path.Combine(work, "de outro editor.musicxml"); File.WriteAllText(xml, "<score-partwise/>");
            typeof(MainWindow).GetField("musicXmlConverter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window,
                new Func<string, string, CancellationToken, Task>((input, target, _) => { File.WriteAllBytes(target, [80, 75]); return Task.CompletedTask; }));
            var converted = await window.ConvertMusicXmlAsync([xml]);
            Check(converted.Count == 1 && converted[0].EndsWith("de outro editor.mscz") && File.Exists(xml) && Path.GetDirectoryName(converted[0]) == Path.Combine(work, "saida"), "musicxml-is-converted-to-mscz");

            // Optional native check: a real MusicXML converted by MuseScore opens as a score.
            if (Environment.GetEnvironmentVariable("TECLAS_TEST_MUSESCORE") is { Length: > 0 } museScore && File.Exists(museScore)
                && Environment.GetEnvironmentVariable("TECLAS_TEST_MUSICXML") is { Length: > 0 } realXml && File.Exists(realXml))
            {
                string nativeTarget = Path.Combine(work, "convertida.mscz");
                await new ScoreExporter(museScore, work).ExportNativeAsync(realXml, nativeTarget, CancellationToken.None);
                var metadata = NativeScoreDocument.Inspect(nativeTarget);
                Check(metadata.MeasureCount > 0 && metadata.HarmonyCount > 0, $"native-musicxml-conversion ({metadata.MeasureCount} compassos, {metadata.HarmonyCount} cifras)");
            }

            // Chord sheet window: distinct chords become a chart, with remembered positions.
            ChordChartWindow? chart = null;
            var sheetWindow = new ChordSheetWindow(2, (root, quality) => root == "C" ? 0 : null) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            sheetWindow.chartDialog = w => { chart = w; return true; };
            sheetWindow.Show(); Flush();
            sheetWindow.SetText("C   G   Am   F\nletra da música"); Flush();
            var chartRow = sheetWindow.ChartRow();
            Check(chartRow.Chords.Count == 4 && chartRow.Chords[0].DiagramInversion == 0 && chartRow.Chords[1].DiagramInversion is null, "chord-sheet-chart-uses-remembered-positions");
            CaptureFolderWindow(sheetWindow, Path.Combine(reports, "chord-sheet.png"));
            ((Button)LogicalChildren(sheetWindow).OfType<Button>().First(b => (b.Content as string) == "Ver acordes com diagramas")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(chart is { Entries.Count: 4 }, "chord-sheet-opens-the-chart");
            sheetWindow.Close();
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
