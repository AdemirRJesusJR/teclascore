using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Repertoire PDF, tablet page style and library sync. MuseScore is replaced by synthetic pages; no engine runs.
    private async void VerifyV030Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v030-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        MainWindow? window = null, other = null;
        var checks = new Dictionary<string, bool>();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.30 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(MainWindow target, Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException(what + " " + target.Status);
            Flush();
        }
        string Png(string path, int width, int height, bool transparent)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                if (!transparent) context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                context.DrawRectangle(Brushes.Black, null, new Rect(width * .1, height * .1, width * .8, 4));
            }
            var bitmap = new RenderTargetBitmap(width, height, 200, 200, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path); encoder.Save(file); return path;
        }
        try
        {
            // Tablet style: same printable width, 3:4 page, read from the score style.
            string score = Path.Combine(work, "estilo.mscz");
            using (var zip = ZipFile.Open(score, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("score_style.mss").Open()))
                writer.Write("<museScore version=\"4.70\"><Style><pagePrintableWidth>7.0</pagePrintableWidth></Style></museScore>");
            Check(TabletPageStyle.PrintableWidth(score) == 7.0 && TabletPageStyle.PrintableWidth(Path.Combine(work, "nada.mscz")) == TabletPageStyle.DefaultPrintableWidth, "tablet-reads-printable-width");
            var style = System.Xml.Linq.XDocument.Parse(TabletPageStyle.Create(7.0)).Root!.Element("Style")!;
            Check(style.Element("pagePrintableWidth")!.Value == "7" && style.Element("pageWidth")!.Value == "7.32" && Math.Abs(double.Parse(style.Element("pageHeight")!.Value, System.Globalization.CultureInfo.InvariantCulture) - 9.76) < .001, "tablet-style-keeps-width-and-fills-3-by-4");

            // PDF writer: index, bookmarks, links and lossless gray pages.
            var pageA = Png(Path.Combine(work, "a-1.png"), 400, 566, transparent: true);
            var pageB = Png(Path.Combine(work, "b-1.png"), 400, 566, transparent: false);
            var gray = Teclas.App.MainWindow.LoadGrayPage(pageA);
            Check(gray.Width == 400 && gray.Gray[0] == 255 && gray.Gray[^1] == 255 && gray.Gray.Min() < 30 && Math.Abs(gray.WidthPoints - 144) < .5, "transparent-pages-flatten-on-white");
            string pdfPath = Path.Combine(work, "teste.pdf");
            using (var stream = File.Create(pdfPath))
                RepertoirePdfWriter.Write(stream, "Repertório de São João", [new("Asa Branca", "Luiz Gonzaga", [pageA, pageA]), new("Xote (das meninas)", "", [pageB])], true, Teclas.App.MainWindow.LoadGrayPage);
            string pdf = Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));
            Check(pdf.StartsWith("%PDF-1.4") && pdf.TrimEnd().EndsWith("%%EOF") && pdf.Split("/Type /Page ").Length - 1 == 4, "pdf-has-index-and-three-pages");
            Check(pdf.Contains("/Type /Outlines") && pdf.Contains("/Count 2") && pdf.Split("/Subtype /Link").Length - 1 == 2 && pdf.Contains("(2. Xote \\(das meninas\\))"), "pdf-has-bookmarks-links-and-escaped-text");
            Check(XrefIsValid(pdf), "pdf-cross-reference-is-valid");
            // Optional native run: a real score rendered by MuseScore with the tablet style, saved with the reports.
            if (Environment.GetEnvironmentVariable("TECLAS_TEST_MUSESCORE") is { Length: > 0 } museScore && File.Exists(museScore)
                && Environment.GetEnvironmentVariable("TECLAS_TEST_REPERTOIRE_SCORE") is { Length: > 0 } real && File.Exists(real))
            {
                string nativeJob = Path.Combine(work, "nativo"); Directory.CreateDirectory(nativeJob);
                string copy = Path.Combine(nativeJob, "partitura.mscz"); File.Copy(real, copy);
                var exporter = new ScoreExporter(museScore, nativeJob);
                var a4 = await exporter.ExportPagesAsync(copy, Path.Combine(nativeJob, "a4.png"), 200, null, CancellationToken.None);
                var tablet = await exporter.ExportPagesAsync(copy, Path.Combine(nativeJob, "tablet.png"), 200, TabletPageStyle.WriteFor(copy, nativeJob), CancellationToken.None);
                var a4Page = Teclas.App.MainWindow.LoadGrayPage(a4[0]); var tabletPage = Teclas.App.MainWindow.LoadGrayPage(tablet[0]);
                Check(Math.Abs(tabletPage.HeightPoints / tabletPage.WidthPoints - 4.0 / 3) < .01 && Math.Abs(a4Page.HeightPoints / a4Page.WidthPoints - 297.0 / 210) < .01, "native-tablet-page-is-3-by-4");
                string nativePdf = Path.Combine(reports, "repertorio-nativo.pdf");
                using (var stream = File.Create(nativePdf))
                    RepertoirePdfWriter.Write(stream, "Repertório (teste nativo)", [new("A4", "", a4), new("Tablet", "", tablet)], true, Teclas.App.MainWindow.LoadGrayPage);
                Check(XrefIsValid(Encoding.Latin1.GetString(File.ReadAllBytes(nativePdf))), "native-repertoire-pdf-is-valid");
            }

            // App flow: two generated cards become one PDF, alphabetical, with chord chart pages.
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "output") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            window.Show(); Flush();
            foreach (var name in new[] { "Xote.mid", "Asa.mid" })
            {
                // Different content per song: the library identifies songs by the MIDI itself.
                var bytes = SyntheticTwoTrackMidi(); if (name == "Asa.mid") bytes[Array.LastIndexOf(bytes, (byte)90)] = 91;
                string midi = Path.Combine(work, name); File.WriteAllBytes(midi, bytes);
                window.ImportForSmoke(midi); Flush();
            }
            foreach (var row in window.Files)
            {
                string fake = Path.Combine(work, Path.GetFileNameWithoutExtension(row.FullPath) + ".mscz"); File.Copy(score, fake, true);
                row.ScoreTitle = Path.GetFileNameWithoutExtension(row.FullPath); row.ScorePath = fake; row.HasPendingScoreProperties = false;
                row.Chords.Add(new ChordRow { Root = "A", Quality = "m" }); row.Chords.Add(new ChordRow { Root = "E", Quality = "7" });
                row.HasPendingScoreProperties = false;
            }
            Flush();
            foreach (var row in window.Files) row.HasPendingScoreProperties = false;
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            BatchPdfSelectionWindow? shown = null;
            typeof(MainWindow).GetField("batchPdfSelectionDialog", hidden)!.SetValue(window, new Func<BatchPdfSelectionWindow, bool?>(dialog =>
            {
                shown = dialog;
                if (dialog.Rows.Any(r => !r.CanSelect)) throw new InvalidOperationException("blocked: " + string.Join("; ", dialog.Rows.Select(r => r.Title + "=" + r.Explanation)));
                ((TextBox)dialog.FindName("RepertoireTitleBox")).Text = "Forró";
                ((CheckBox)dialog.FindName("RepertoireChordsCheckBox")).IsChecked = true;
                ((CheckBox)dialog.FindName("RepertoireAlphabeticalCheckBox")).IsChecked = true;
                dialog.ShowActivated = false; dialog.Opacity = 0; dialog.Show(); Flush();
                CaptureFolderWindow(dialog, Path.Combine(reports, "repertoire-dialog.png"));
                bool accepted = dialog.Confirm(); dialog.Close();
                return accepted && dialog.Repertoire is not null;
            }));
            string target = Path.Combine(work, "saida", "Forró.pdf"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var rendered = new List<string>();
            typeof(MainWindow).GetField("repertoireTarget", hidden)!.SetValue(window, new Func<string, string?>(_ => target));
            typeof(MainWindow).GetField("repertoirePages", hidden)!.SetValue(window, new Func<string, string, CancellationToken, Task<IReadOnlyList<string>>>((scorePath, png, _) =>
            {
                rendered.Add(Path.GetFileNameWithoutExtension(scorePath));
                string first = Path.Combine(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(png) + "-1.png");
                File.Copy(pageB, first); return Task.FromResult<IReadOnlyList<string>>([first]);
            }));
            typeof(MainWindow).GetMethod("RepertoirePdfClick", hidden)!.Invoke(window, [window, new RoutedEventArgs()]);
            await Wait(window, () => window.IsIdle && File.Exists(target), "repertoire was not saved");
            string made = Encoding.Latin1.GetString(File.ReadAllBytes(target));
            Check(shown?.Repertoire is { Title: "Forró", Chords: true, Alphabetical: true, Index: true, Tablet: false }, "repertoire-options-are-read");
            Check(rendered.SequenceEqual(new[] { "Asa", "Xote" }), "repertoire-follows-alphabetical-order " + string.Join(",", rendered) + " | " + string.Join(",", window.Files.Select(r => r.ScoreTitle)));
            Check(made.Split("/Type /Page ").Length - 1 == 5 && XrefIsValid(made), "repertoire-has-index-scores-and-chord-pages");
            Check(window.Status.StartsWith("Repertório salvo: 2 músicas, 5 páginas", StringComparison.Ordinal), "repertoire-reports-the-result");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(target)!, ".teclas-*").Any(), "no-temporary-files-left");

            // Library sync: computer A sends; computer B is offered the library and adds only new songs.
            string sync = Path.Combine(work, "nuvem"); Directory.CreateDirectory(sync);
            window.MachineName = "PC-SALA";
            window.LibrarySyncFolder = sync; Flush();
            Check(await window.SendLibraryToSyncFolder(force: false) && File.Exists(Path.Combine(sync, Teclas.App.MainWindow.SyncPackageName)), "library-is-sent-to-the-synced-folder");
            var marker = Teclas.App.MainWindow.ReadSyncMarker(sync);
            Check(marker is { Machine: "PC-SALA", Songs: 2 } && window.LibrarySyncStatus.Contains("este computador"), "marker-says-who-sent");
            var before = File.GetLastWriteTimeUtc(Path.Combine(sync, Teclas.App.MainWindow.SyncPackageName));
            Check(await window.SendLibraryToSyncFolder(force: false) && File.GetLastWriteTimeUtc(Path.Combine(sync, Teclas.App.MainWindow.SyncPackageName)) == before, "unchanged-library-is-not-sent-again");

            string otherSettings = Path.Combine(work, "outro.json");
            new Settings { OutputDirectory = Path.Combine(work, "outro-saida"), LibrarySyncFolder = sync, LibrarySyncRestoreRoot = Path.Combine(work, "recebidas") }.Save(otherSettings);
            other = new MainWindow(true, otherSettings) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            other.MachineName = "PC-QUARTO";
            string? asked = null;
            typeof(MainWindow).GetField("librarySyncOffer", hidden)!.SetValue(other, new Func<string, bool>(question => { asked = question; return true; }));
            other.Show(); Flush();
            // The offer comes by itself when the window opens.
            await Wait(other, () => other.IsIdle && other.Files.Count == 2, "synced library was not received at startup");
            Check(asked!.Contains("PC-SALA") && other.Files.All(row => row.FullPath.StartsWith(Path.Combine(work, "recebidas"), StringComparison.OrdinalIgnoreCase)), "other-computer-receives-the-songs-at-startup");
            asked = null;
            Check(!await other.OfferSyncedLibrary() && asked is null, "same-library-is-offered-once");
            Check(await other.OfferSyncedLibrary(force: true) && other.Files.Count == 2, "songs-already-here-are-not-duplicated");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            other.Close(); other = null; window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            other?.Close(); window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }

    /// <summary>Every xref offset must point at "N 0 obj" for its own object number.</summary>
    private static bool XrefIsValid(string pdf)
    {
        int start = int.Parse(pdf[(pdf.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0]);
        var lines = pdf[start..].Split('\n');
        int count = int.Parse(lines[1].Split(' ')[1]);
        for (int id = 1; id < count; id++)
        {
            int offset = int.Parse(lines[2 + id][..10]);
            if (!pdf.AsSpan(offset).StartsWith($"{id} 0 obj")) return false;
        }
        return true;
    }
}
