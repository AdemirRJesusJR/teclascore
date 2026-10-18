using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Large library timing, score look settings and the problem report. No engine or MuseScore runs.
    private async void VerifyV036Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v036-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        var timings = new Dictionary<string, double>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, timings, error = "v0.36 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            // A 300-song library: opening restores every card; searching filters them while typing.
            string library = Path.Combine(work, "midis"); Directory.CreateDirectory(library);
            var saved = new List<SavedMidi>();
            for (int i = 0; i < 300; i++)
            {
                var bytes = SyntheticTwoTrackMidi(); bytes[^10] = (byte)(i % 100); // distinct files
                string path = Path.Combine(library, $"Música {i:000} - Artista {i % 17}.mid"); File.WriteAllBytes(path, bytes);
                saved.Add(new SavedMidi { FullPath = path, Collection = i % 3 == 0 ? "Forró" : "Xote" });
            }
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), Library = saved, CurrentPage = "library" }.Save(settingsPath);
            var clock = Stopwatch.StartNew();
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1200, Height = 800 };
            window.Show(); Flush();
            timings["open-300-songs-ms"] = clock.Elapsed.TotalMilliseconds;
            Check(window.Files.Count == 300, "library-of-300-opens");
            var cards = (ListBox)window.FindName("Library");
            clock.Restart(); window.SearchText = "Artista 3"; Flush();
            timings["search-ms"] = clock.Elapsed.TotalMilliseconds;
            Check(cards.Items.Count is > 0 and < 300, "search-filters-the-cards");
            clock.Restart(); window.SearchText = ""; Flush();
            timings["clear-search-ms"] = clock.Elapsed.TotalMilliseconds;
            Check(timings["open-300-songs-ms"] < 20000 && timings["search-ms"] < 1500, $"large-library-stays-responsive ({timings["open-300-songs-ms"]:0} ms, busca {timings["search-ms"]:0} ms)");

            // Score look: font and size choices are saved and sent to the engine; defaults send nothing.
            Check(window.ScoreStyleRequest() is null, "default-look-sends-no-style");
            window.ScoreMusicFont = "Bravura"; window.ScoreSize = "grande";
            Check(window.ScoreStyleRequest() is { } style && (string?)style["musicFont"] == "Bravura" && (string?)style["size"] == "grande"
                && Settings.Load(settingsPath) is { ScoreMusicFont: "Bravura", ScoreSize: "grande" }, "look-is-saved-and-sent");
            var bad = Settings.Load(settingsPath); bad.ScoreMusicFont = "Comic"; bad.ScoreSize = "enorme"; bad.Save(settingsPath);
            Check(Settings.Load(settingsPath) is { ScoreMusicFont: "Leland", ScoreSize: "normal" }, "unknown-look-falls-back");

            // Problem report: summary without personal folders, recent messages, diagnostics only when asked.
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Check(SupportReport.Anonymize(profile + @"\Documents\x.mid e fulano@exemplo.com").StartsWith("%USERPROFILE%") && SupportReport.Anonymize("fulano@exemplo.com") == "<email>", "report-anonymizes");
            string data = Path.Combine(work, "diag"); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "request-1-error-abc.json"), "{\"error\":\"falhou em " + profile.Replace("\\", "\\\\") + "\"}");
            string zip = Path.Combine(work, "relatorio.zip");
            SupportReport.Create(zip, new SupportReportInput("0.36.0", [("Biblioteca", "300 músicas")], [(DateTime.Now, "Não foi possível exportar " + profile)], [data], true));
            using (var archive = ZipFile.OpenRead(zip))
            {
                string summary = new StreamReader(archive.GetEntry("resumo.txt")!.Open()).ReadToEnd();
                string diagnostic = new StreamReader(archive.Entries.Single(e => e.FullName.StartsWith("diagnosticos/")).Open()).ReadToEnd();
                Check(summary.Contains("300 músicas") && summary.Contains("%USERPROFILE%") && !summary.Contains(profile, StringComparison.OrdinalIgnoreCase)
                    && !diagnostic.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "report-zip-is-anonymized");
            }
            string zip2 = Path.Combine(work, "sem.zip");
            SupportReport.Create(zip2, new SupportReportInput("0.36.0", [], [], [data], false));
            using (var archive = ZipFile.OpenRead(zip2)) Check(archive.Entries.Count == 1, "diagnostics-only-when-asked");
            string? made = null;
            typeof(MainWindow).GetField("supportReportTarget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(window, new Func<string, string?>(_ => made = Path.Combine(work, "app.zip")));
            window.CreateSupportReport(false);
            Check(made is not null && File.Exists(made) && window.RecentMessages.Count > 0, "app-creates-the-report");

            // Optional: the user's real library (a copy of the settings, opened read-only for timing).
            if (Environment.GetEnvironmentVariable("TECLAS_TEST_REAL_SETTINGS") is { Length: > 0 } real && File.Exists(real))
            {
                window.Close(); window = null;
                string copy = Path.Combine(work, "real-settings.json"); File.Copy(real, copy);
                clock.Restart();
                var realWindow = new MainWindow(true, copy) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1200, Height = 800 };
                realWindow.Show(); Flush();
                timings["open-real-library-ms"] = clock.Elapsed.TotalMilliseconds; timings["real-library-songs"] = realWindow.Files.Count;
                clock.Restart(); realWindow.SearchText = "forró"; Flush(); timings["real-search-ms"] = clock.Elapsed.TotalMilliseconds;
                realWindow.Close();
            }
            // Optional: every MIDI of a real folder imported into an empty library (timing only; files are read, never changed).
            if (Environment.GetEnvironmentVariable("TECLAS_TEST_REAL_FOLDER") is { Length: > 0 } folder && Directory.Exists(folder))
            {
                window?.Close(); window = null;
                string empty = Path.Combine(work, "vazia.json"); new Settings { OutputDirectory = Path.Combine(work, "saida2") }.Save(empty);
                var importer = new MainWindow(true, empty) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1200, Height = 800 };
                importer.Show(); Flush();
                var midis = Directory.EnumerateFiles(folder, "*.mid*", SearchOption.AllDirectories).ToArray();
                clock.Restart();
                foreach (var midi in midis) importer.ImportForSmoke(midi);
                Flush(); timings["import-real-folder-ms"] = clock.Elapsed.TotalMilliseconds; timings["real-folder-midis"] = midis.Length; timings["real-folder-cards"] = importer.Files.Count;
                clock.Restart(); importer.SearchText = "forró"; Flush(); timings["real-folder-search-ms"] = clock.Elapsed.TotalMilliseconds;
                importer.SearchText = ""; importer.Close();
                // Reopening that library: the cards are read on several cores before the window appears.
                clock.Restart();
                var reopened = new MainWindow(true, empty) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1200, Height = 800 };
                reopened.Show(); Flush();
                timings["reopen-real-folder-ms"] = clock.Elapsed.TotalMilliseconds; timings["reopen-real-folder-cards"] = reopened.Files.Count;
                reopened.Close();
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, timings, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, timings, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
