using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Options chosen in the repertoire dialog.</summary>
public sealed record RepertoireOptions(string Title, bool Index, bool Chords, bool Tablet, bool Alphabetical);

public partial class MainWindow
{
    internal Func<string, string?>? repertoireTarget = null;
    internal Func<string, string, CancellationToken, Task<IReadOnlyList<string>>>? repertoirePages = null;
    private const int RepertoireDpi = 200;

    private async void RepertoirePdfClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportBatchPdf || !CommitEdits()) return;
        var generated = Files.Where(row => !string.IsNullOrWhiteSpace(row.ScorePath)).ToArray();
        var dialog = new BatchPdfSelectionWindow(generated.Select(BatchPdfCandidate).ToArray(), Files.Count - generated.Length, repertoire: true) { Owner = this };
        if (batchPdfSelectionDialog(dialog) != true || dialog.Repertoire is not { } options) return;
        string? target;
        if (repertoireTarget is { } choose) target = choose(options.Title);
        else
        {
            var save = new SaveFileDialog { Title = "Salvar repertório em PDF", Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true,
                FileName = ScoreFileName.Stem(null, options.Title) + (options.Tablet ? " - tablet" : "") + ".pdf" };
            if (settings.LastExportDirectory is { Length: > 0 } last && Directory.Exists(last)) save.InitialDirectory = last;
            target = save.ShowDialog(this) == true ? save.FileName : null;
        }
        if (target is null) return;
        var rows = dialog.SelectedSources.Select(source => Files.FirstOrDefault(row => row.FullPath == source.Id)).OfType<MidiRow>()
            .Where(row => BatchPdfCandidate(row).BlockedReason.Length == 0).ToList();
        if (options.Alphabetical) rows = rows.OrderBy(row => row.ScoreTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (rows.Count == 0) { Status = "Nenhuma partitura disponível para o repertório."; return; }
        await BuildRepertoirePdf(rows.Select(row => new RepertoireItem(row.ScorePath!, row.ScoreTitle, row.ScoreArtist ?? "", row)).ToArray(), options, target);
    }

    /// <summary>One song of a repertoire PDF; the library card (when known) supplies the chord chart.</summary>
    public sealed record RepertoireItem(string ScorePath, string Title, string Artist, MidiRow? Row);

    /// <summary>Renders each score (and chord chart) and writes the single PDF; used by the library and the presentation lists.</summary>
    internal async Task BuildRepertoirePdf(IReadOnlyList<RepertoireItem> rows, RepertoireOptions options, string target)
    {
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            string job = Path.Combine(PlaybackCacheDirectory, "repertorio", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            try
            {
                var songs = new List<RepertoireSong>();
                for (int i = 0; i < rows.Count; i++)
                {
                    var item = rows[i]; string score = item.ScorePath;
                    SetOperationItems(i, rows.Count, item.Title); Status = $"Desenhando as páginas de {item.Title}…";
                    string pngBase = Path.Combine(job, $"{i + 1:000}.png");
                    IReadOnlyList<string> pages;
                    if (repertoirePages is { } render) pages = await render(score, pngBase, token);
                    else
                    {
                        string? style = options.Tablet ? TabletPageStyle.WriteFor(score, job) : null;
                        pages = await new ScoreExporter(MuseScorePath, job).ExportPagesAsync(score, pngBase, RepertoireDpi, style, token);
                    }
                    var all = pages.ToList();
                    if (options.Chords && item.Row is { } row && ChordChartWindow.BuildEntries(row).Count > 0)
                    {
                        var chart = new ChordChartWindow(row);
                        string chartPath = Path.Combine(job, $"{i + 1:000}-acordes.png");
                        if (chart.SavePng(chartPath) is not null) all.Add(chartPath);
                        chart.Close();
                    }
                    songs.Add(new RepertoireSong(item.Title, item.Artist, all));
                }
                token.ThrowIfCancellationRequested();
                int total = songs.Sum(song => song.Pages.Count);
                SetOperationItems(0, total, "Montando o PDF");
                string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".teclas-" + Guid.NewGuid().ToString("N") + ".pdf");
                try
                {
                    var id = operationId;
                    var progress = new Progress<int>(done => { if (operationId == id && IsBusy) { SetOperationItems(done, total, "Montando o PDF"); Status = $"Montando o PDF: página {done} de {total}…"; } });
                    await Task.Run(() =>
                    {
                        using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                        RepertoirePdfWriter.Write(stream, options.Title, songs, options.Index, LoadGrayPage, progress, token);
                    }, token);
                    File.Move(temporary, target, overwrite: true);
                }
                finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                RememberExportDirectory(Path.GetDirectoryName(target)!);
                Status = $"Repertório salvo: {songs.Count} músicas, {total + (options.Index ? (songs.Count + 31) / 32 : 0)} páginas · {target}";
            }
            finally { try { Directory.Delete(job, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }, operationTitle: "Montando o repertório em PDF");
    }

    /// <summary>Decodes a rendered page and flattens it on white as 8-bit gray (MuseScore pages have a transparent background).</summary>
    internal static PdfPageImage LoadGrayPage(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        double dpiX = frame.DpiX is > 30 and < 2400 ? frame.DpiX : RepertoireDpi, dpiY = frame.DpiY is > 30 and < 2400 ? frame.DpiY : RepertoireDpi;
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        var gray = new byte[width * height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            int alpha = pixels[p + 3];
            int luminance = (pixels[p + 2] * 299 + pixels[p + 1] * 587 + pixels[p] * 114) / 1000;
            gray[i] = (byte)((luminance * alpha + 255 * (255 - alpha)) / 255);
        }
        return new PdfPageImage(width, height, gray, width * 72.0 / dpiX, height * 72.0 / dpiY);
    }
}
