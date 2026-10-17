using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    internal Func<string, Task<string?>>? exportCopyTarget = null;

    private async void ExportCopyClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportPdf || CurrentScorePath is not { } scorePath) return;
        string stem = ScoreFileName.Stem(selectedFile?.ScoreArtist, selectedFile?.ScoreTitle, scorePath);
        string? destination;
        if (exportCopyTarget is { } choose) destination = await choose(stem);
        else
        {
            var dialog = await CreateScoreExportDialogAsync("Exportar partitura", "MusicXML (outros editores)|*.musicxml|Imagens PNG (uma por página)|*.png|PDF para tablet (margens estreitas, página 3:4)|*.pdf", stem + ".musicxml", ".musicxml");
            destination = dialog is not null && dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (destination is null) return;
        await RunOperation(async () =>
        {
            var exporter = new ScoreExporter(MuseScorePath, PlaybackCacheDirectory);
            if (Path.GetExtension(destination).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // Tablet PDF: the same layout with narrow margins on a 3:4 page; the score itself is not changed.
                string style = TabletPageStyle.WriteFor(scorePath, Path.Combine(PlaybackCacheDirectory, "estilos"));
                try { await exporter.ExportPdfAsync(scorePath, destination, CreateOperationProgress(), cancellation!.Token, style); }
                finally { try { File.Delete(style); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                RememberExportDirectory(Path.GetDirectoryName(destination)!);
                Status = "PDF para tablet salvo: " + destination;
                return;
            }
            var saved = await exporter.ExportCopyAsync(scorePath, destination, CreateOperationProgress(), cancellation!.Token);
            RememberExportDirectory(Path.GetDirectoryName(destination)!);
            Status = saved.Count == 1 ? "Arquivo salvo: " + saved[0] : $"{saved.Count} páginas salvas em PNG em {Path.GetDirectoryName(destination)}.";
        }, operationTitle: "Exportando a partitura", operationItem: Path.GetFileNameWithoutExtension(scorePath));
    }
}
