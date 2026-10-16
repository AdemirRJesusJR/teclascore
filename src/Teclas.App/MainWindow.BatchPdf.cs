using System.IO;
using System.Windows;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanExportBatchPdf => IsIdle && Files.Count > 0;

    private static BatchPdfSource BatchPdfCandidate(MidiRow row)
    {
        string reason = row.HasPendingScoreProperties ? "Há alterações pendentes. Atualize a partitura antes de exportar." :
            string.IsNullOrWhiteSpace(row.ScorePath) ? "Gere ou importe a partitura primeiro." :
            !File.Exists(row.ScorePath) ? "Arquivo MSCZ indisponível. Confira a pasta da partitura." :
            !Path.GetExtension(row.ScorePath).Equals(".mscz", StringComparison.OrdinalIgnoreCase) ? "Esta música precisa de uma partitura no formato MSCZ." : "";
        return new(row.FullPath, row.ScoreTitle, row.ScorePath, reason, row.ScoreArtist);
    }

    private async void ExportBatchPdfClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportBatchPdf || !CommitEdits()) return;
        var dialog = new BatchPdfSelectionWindow(Files.Select(BatchPdfCandidate).ToArray()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var folder = new OpenFolderDialog { Title = "Escolha a pasta para os PDFs das partituras", Multiselect = false };
        if (Directory.Exists(OutputDirectory)) folder.InitialDirectory = OutputDirectory;
        if (folder.ShowDialog(this) != true) return;
        // Recheck the selected versions after the dialogs; never silently export an older saved version.
        var selection = dialog.SelectedSources.Select(item => Files.FirstOrDefault(row => row.FullPath == item.Id) is { } row
            ? BatchPdfCandidate(row) : item with { BlockedReason = "Esta música não está mais na biblioteca." }).ToArray();
        BatchPdfReport? report = null;
        await RunOperation(async () =>
        {
            var id = operationId;
            var progress = new Progress<BatchPdfProgress>(item =>
            {
                if (!IsBusy || operationsDisposed || operationCancelRequested || operationId != id) return;
                SetOperationItems(item.Completed, item.Total, item.Title); Status = item.Stage;
            });
            SetOperationItems(0, selection.Length, "Preparando exportação");
            var service = new BatchPdfExportService(MuseScorePath, Path.Combine(PlaybackCacheDirectory, "pdf-batches"));
            report = await service.ExportAsync(selection, folder.FolderName, progress, cancellation!.Token);
            Status = report.Summary;
        }, operationTitle: "Exportando partituras em PDF");
        if (report is not null && !operationsDisposed) new BatchPdfResultsWindow(report) { Owner = this }.ShowDialog();
    }
}
