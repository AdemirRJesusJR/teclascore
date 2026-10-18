using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Songbook: the text chord sheets of several songs in one PDF, one song per page (or more), with an index.</summary>
public partial class MainWindow
{
    internal Func<string, string?>? songbookTarget = null;
    private const double SongbookDpi = 150, SongbookWidth = 8.27, SongbookHeight = 11.69, SongbookMargin = 0.7;

    private async void SongbookClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportBatchPdf || !CommitEdits()) return;
        var generated = Files.Where(row => !string.IsNullOrWhiteSpace(row.ScorePath)).ToArray();
        var dialog = new BatchPdfSelectionWindow(generated.Select(BatchPdfCandidate).ToArray(), Files.Count - generated.Length, repertoire: true, songbook: true) { Owner = this };
        if (batchPdfSelectionDialog(dialog) != true || dialog.Repertoire is not { } options) return;
        string? target = songbookTarget is { } choose ? choose(options.Title) : null;
        if (songbookTarget is null)
        {
            var save = new SaveFileDialog { Title = "Salvar caderno de cifras em PDF", Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true,
                FileName = ScoreFileName.Stem(null, options.Title) + " - cifras.pdf" };
            if (settings.LastExportDirectory is { Length: > 0 } last && Directory.Exists(last)) save.InitialDirectory = last;
            target = save.ShowDialog(this) == true ? save.FileName : null;
        }
        if (target is null) return;
        var rows = dialog.SelectedSources.Select(source => Files.FirstOrDefault(row => row.FullPath == source.Id)).OfType<MidiRow>()
            .Where(row => BatchPdfCandidate(row).BlockedReason.Length == 0).ToList();
        if (options.Alphabetical) rows = rows.OrderBy(row => row.ScoreTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (rows.Count == 0) { Status = "Nenhuma partitura disponível para o caderno de cifras."; return; }
        await BuildSongbookPdf(rows.Select(row => new RepertoireItem(row.ScorePath!, row.ScoreTitle, row.ScoreArtist ?? "", row)).ToArray(), options.Title, options.Index, target);
    }

    /// <summary>Writes the songbook; used by the library and by the presentation lists.</summary>
    internal async Task BuildSongbookPdf(IReadOnlyList<RepertoireItem> items, string title, bool index, string target)
    {
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            string job = Path.Combine(PlaybackCacheDirectory, "cifras", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            try
            {
                var songs = new List<RepertoireSong>(); int skipped = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    SetOperationItems(i, items.Count, item.Title); Status = $"Escrevendo a cifra de {item.Title}…";
                    string text;
                    try
                    {
                        string heading = item.Artist.Length > 0 ? $"{item.Title} — {item.Artist}" : item.Title;
                        text = await Task.Run(() => ChordSheetWriter.FromScore(NativeMusicEditor.Open(item.ScorePath, token), heading), token);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Xml.XmlException) { skipped++; continue; }
                    songs.Add(new RepertoireSong(item.Title, item.Artist, RenderSongbookPages(text, Path.Combine(job, $"{i + 1:000}"))));
                }
                token.ThrowIfCancellationRequested();
                if (songs.Count == 0) throw new InvalidOperationException("Nenhuma das partituras pôde ser lida para o caderno de cifras.");
                int total = songs.Sum(song => song.Pages.Count);
                string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".teclas-" + Guid.NewGuid().ToString("N") + ".pdf");
                try
                {
                    await Task.Run(() =>
                    {
                        using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                        RepertoirePdfWriter.Write(stream, title, songs, index, LoadGrayPage, null, token);
                    }, token);
                    File.Move(temporary, target, overwrite: true);
                }
                finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                RememberExportDirectory(Path.GetDirectoryName(target)!);
                Status = $"Caderno de cifras salvo: {songs.Count} músicas, {total} páginas" + (skipped > 0 ? $" ({skipped} não puderam ser lidas)" : "") + $" · {target}";
            }
            finally { try { Directory.Delete(job, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }, operationTitle: "Montando o caderno de cifras");
    }

    /// <summary>
    /// Draws a text chord sheet on A4 pages (150 dpi) in a monospaced font: the first line as a bold heading, the font
    /// shrinking (down to 8 pt) so the longest line fits the width, and as many pages as the lines need.
    /// </summary>
    internal static IReadOnlyList<string> RenderSongbookPages(string text, string basePath)
    {
        var lines = text.Replace("\r", "").TrimEnd().Split('\n');
        var mono = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double width = SongbookWidth * 96, height = SongbookHeight * 96, margin = SongbookMargin * 96, usable = width - 2 * margin;
        int longest = Math.Max(1, lines.Skip(1).Select(line => line.Length).DefaultIfEmpty(1).Max());
        double size = Math.Clamp(usable / (longest * 0.55), 8 * 96 / 72.0, 12 * 96 / 72.0), lineHeight = size * 1.3;
        var pages = new List<string>(); int next = 0; double dip = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        while (next < lines.Length || pages.Count == 0)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                double y = margin;
                if (pages.Count == 0 && lines.Length > 0)
                {
                    var heading = new FormattedText(lines[0], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold, 20, Brushes.Black, dip) { MaxTextWidth = usable };
                    dc.DrawText(heading, new Point(margin, y)); y += heading.Height + lineHeight * .6; next = 1;
                }
                while (next < lines.Length && y + lineHeight <= height - margin)
                {
                    var line = new FormattedText(lines[next], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, mono, size, Brushes.Black, dip);
                    dc.DrawText(line, new Point(margin, y)); y += lineHeight; next++;
                }
            }
            double scale = SongbookDpi / 96;
            var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale), SongbookDpi, SongbookDpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            string path = $"{basePath}-{pages.Count + 1}.png";
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            pages.Add(path);
            if (pages.Count > 40) break;
        }
        return pages;
    }
}
