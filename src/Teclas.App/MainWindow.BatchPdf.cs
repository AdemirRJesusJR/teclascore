using System.IO;
using System.Windows;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanExportBatchPdf => IsIdle && Files.Any(row => !string.IsNullOrWhiteSpace(row.ScorePath));
    internal Func<BatchPdfSelectionWindow, bool?> batchPdfSelectionDialog = window => window.ShowDialog();
    internal Func<Task<string?>>? batchPdfFolderOverride = null;
    internal Func<string, string, IProgress<string>?, CancellationToken, Task>? batchPdfExport = null;

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
        // Only cards with a generated score are offered; pending or missing ones stay visible with their reason.
        var generated = Files.Where(row => !string.IsNullOrWhiteSpace(row.ScorePath)).ToArray();
        var dialog = new BatchPdfSelectionWindow(generated.Select(BatchPdfCandidate).ToArray(), Files.Count - generated.Length) { Owner = this };
        if (batchPdfSelectionDialog(dialog) != true) return;
        string? folderName;
        if (batchPdfFolderOverride is { } chooseFolder) folderName = await chooseFolder();
        else
        {
            var folder = await CreateBatchPdfFolderDialogAsync();
            folderName = folder is not null && folder.ShowDialog(this) == true ? folder.FolderName : null;
        }
        if (folderName is null) return;
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
            var cacheFolder = Path.Combine(PlaybackCacheDirectory, "pdf-batches");
            var service = batchPdfExport is { } export ? new BatchPdfExportService(export, cacheFolder) : new BatchPdfExportService(MuseScorePath, cacheFolder);
            report = await service.ExportAsync(selection, folderName, progress, cancellation!.Token, directlyIntoDestination: true);
            if (report.SavedCount > 0) RememberExportDirectory(folderName);
            Status = report.SavedCount == report.Items.Count ? $"{report.SavedCount} PDF(s) salvos em {folderName}." : report.Summary;
        }, operationTitle: "Exportando partituras em PDF");
        // Everything saved: the footer status is enough. Otherwise show what was not exported and why.
        if (report is not null && !operationsDisposed && report.SavedCount != report.Items.Count)
            new BatchPdfResultsWindow(report) { Owner = this }.ShowDialog();
    }
}
