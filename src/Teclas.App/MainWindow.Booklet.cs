using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Booklet: two pages side by side on A4 landscape, in booklet order, to print on both sides, fold and staple.</summary>
public partial class MainWindow
{
    internal Func<string, string?>? bookletTarget = null;
    internal Func<string, string, CancellationToken, Task<IReadOnlyList<string>>>? bookletPages = null;
    private const double BookletDpi = 150;

    public bool CanExportBooklet => CanExportPdf;

    private async void BookletClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportBooklet || CurrentScorePath is not { } score || selectedFile is not { } row) return;
        string title = string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle;
        string? target = bookletTarget is { } choose ? choose(title) : null;
        if (bookletTarget is null)
        {
            var save = new SaveFileDialog { Title = "Salvar livreto em PDF", Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true, FileName = ScoreFileName.Stem(null, title) + " - livreto.pdf" };
            if (settings.LastExportDirectory is { Length: > 0 } last && Directory.Exists(last)) save.InitialDirectory = last;
            target = save.ShowDialog(this) == true ? save.FileName : null;
        }
        if (target is null) return;
        await BuildBookletPdf(score, title, target);
    }

    internal async Task BuildBookletPdf(string score, string title, string target)
    {
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            string job = Path.Combine(PlaybackCacheDirectory, "livreto", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            try
            {
                Status = "Desenhando as páginas da partitura…";
                string pngBase = Path.Combine(job, "pagina.png");
                var pages = bookletPages is { } render ? await render(score, pngBase, token)
                    : await new ScoreExporter(MuseScorePath, job).ExportPagesAsync(score, pngBase, (int)BookletDpi, null, token);
                var sides = BookletImposition.Sides(pages.Count);
                var images = new List<string>();
                for (int i = 0; i < sides.Count; i++)
                {
                    string path = Path.Combine(job, $"folha-{i + 1:000}.png");
                    ComposeBookletSide(sides[i].Left is int left ? pages[left] : null, sides[i].Right is int right ? pages[right] : null, path);
                    images.Add(path);
                }
                string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".teclas-" + Guid.NewGuid().ToString("N") + ".pdf");
                try
                {
                    await Task.Run(() =>
                    {
                        using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                        RepertoirePdfWriter.Write(stream, title, [new RepertoireSong(title, "", images)], false, LoadGrayPage, null, token);
                    }, token);
                    File.Move(temporary, target, overwrite: true);
                }
                finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                RememberExportDirectory(Path.GetDirectoryName(target)!);
                Status = $"Livreto salvo: {pages.Count} páginas em {sides.Count / 2} folha(s). Imprima frente e verso virando pela borda curta, dobre ao meio e grampeie. {target}";
            }
            finally { try { Directory.Delete(job, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }, operationTitle: "Montando o livreto");
    }

    /// <summary>One side of a sheet: A4 landscape at 150 dpi, each page scaled into its half on white (blank halves stay white).</summary>
    internal static void ComposeBookletSide(string? left, string? right, string path)
    {
        double width = 11.69 * 96, height = 8.27 * 96, half = width / 2, margin = 0.15 * 96;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            foreach (var (page, x) in new[] { (left, 0.0), (right, half) })
            {
                if (page is null) continue;
                var image = BitmapFrame.Create(new Uri(page), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                double scale = Math.Min((half - 2 * margin) / image.Width, (height - 2 * margin) / image.Height);
                double w = image.Width * scale, h = image.Height * scale;
                dc.DrawImage(image, new Rect(x + (half - w) / 2, (height - h) / 2, w, h));
            }
        }
        double factor = BookletDpi / 96;
        var bitmap = new RenderTargetBitmap((int)Math.Round(width * factor), (int)Math.Round(height * factor), BookletDpi, BookletDpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
