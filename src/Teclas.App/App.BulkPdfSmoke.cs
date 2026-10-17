using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // MuseScore export is replaced by a synthetic PDF writer; files go to an isolated folder.
    private async void VerifyBulkPdfUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "bulk-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        MainWindow? window = null;
        var checks = new Dictionary<string, bool>();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Bulk PDF smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException(what + " " + window?.Status);
            Flush();
        }
        try
        {
            string settingsPath = Path.Combine(work, "settings.json"), output = Path.Combine(work, "Meus PDFs");
            new Settings { OutputDirectory = Path.Combine(work, "output") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            window.Show(); Flush();
            Check(window.FindName("LibraryBatchPdfButton") is Button { IsEnabled: false }, "bulk-pdf-disabled-without-generated-scores");
            MidiRow Card(string name, bool generated)
            {
                string midi = Path.Combine(work, name + ".mid"); File.WriteAllBytes(midi, TinyMidi());
                window!.ImportForSmoke(midi);
                var row = window.Files.Single(f => f.FullPath == Path.GetFullPath(midi));
                row.ScoreTitle = name; row.ScoreArtist = "Banda";
                if (generated) { string score = Path.Combine(work, name + ".mscz"); File.WriteAllText(score, "synthetic score " + name); row.ScorePath = score; }
                return row;
            }
            Card("Primeira", true); Card("Segunda", true); Card("Sem partitura", false);
            Flush();
            Check(window.CanExportBatchPdf && window.FindName("LibraryBatchPdfButton") is Button { IsEnabled: true }, "library-offers-bulk-pdf");
            Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "Banda - Primeira.pdf"), "existing");

            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            int offered = -1; string? hint = null;
            typeof(MainWindow).GetField("batchPdfSelectionDialog", hidden)!.SetValue(window, new Func<BatchPdfSelectionWindow, bool?>(dialog =>
            {
                offered = dialog.Rows.Count;
                IEnumerable<object> Logical(object node)
                {
                    yield return node;
                    if (node is DependencyObject element) foreach (var child in LogicalTreeHelper.GetChildren(element)) foreach (var item in Logical(child)) yield return item;
                }
                hint = string.Join(" ", Logical(dialog).OfType<TextBlock>().Select(t => t.Text));
                return dialog.SelectedSources.Count == 2;
            }));
            typeof(MainWindow).GetField("batchPdfFolderOverride", hidden)!.SetValue(window, new Func<Task<string?>>(() => Task.FromResult<string?>(output)));
            byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 150) + "\n%%EOF\n");
            typeof(MainWindow).GetField("batchPdfExport", hidden)!.SetValue(window,
                new Func<string, string, IProgress<string>?, CancellationToken, Task>((_, destination, _, token) => File.WriteAllBytesAsync(destination, pdf, token)));
            ((Button)window.FindName("LibraryBatchPdfButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Wait(() => window.IsIdle && window.Status.Contains("PDF(s) salvos"), "bulk export did not finish");
            Check(offered == 2 && hint!.Contains("1 música ainda sem partitura"), "only-generated-cards-are-listed-and-preselected");
            var files = Directory.GetFiles(output).Select(Path.GetFileName).Order().ToArray();
            Check(files.SequenceEqual(new[] { "Banda - Primeira (2).pdf", "Banda - Primeira.pdf", "Banda - Segunda.pdf" }.Order()), "pdfs-saved-directly-in-the-chosen-folder");
            Check(File.ReadAllText(Path.Combine(output, "Banda - Primeira.pdf")) == "existing", "existing-pdf-is-not-replaced");
            Check(Directory.GetDirectories(output).Length == 0, "no-batch-subfolder-or-staging-left");
            Check(window.Status.Contains(output), "status-tells-where-the-pdfs-are");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, museScoreRuns = 0 }, new JsonSerializerOptions { WriteIndented = true }));
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
