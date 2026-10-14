using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanReviewVisualScore => IsIdle && SelectedScoreExists;

    private async void OpenVisualReviewClick(object sender, RoutedEventArgs e)
    {
        if (!CanReviewVisualScore || selectedFile is not { ScorePath: { } path } row || (!row.UsesNativeScoreWorkflow && !CommitEdits())) return;
        if (!File.Exists(MuseScorePath) || !File.Exists(PythonPath))
        {
            Status = "Confira os caminhos do MuseScore e do Python em Configurações para revisar a apresentação.";
            return;
        }
        StopTrackAudition(); ResetPlayback();
        var service = new NativeScoreVisualReviewService(MuseScorePath, PythonPath, engineDirectory, Path.Combine(LocalJobsDirectory, "visual-review"));
        NativeVisualReviewReport? report = null;
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            var analyzed = await service.AnalyzeAsync(path, CreateOperationProgress(), token);
            token.ThrowIfCancellationRequested(); report = analyzed;
        }, operationTitle: "Revisando a apresentação", operationItem: row.ScoreTitle);
        if (operationsDisposed || report is null || selectedFile != row || row.ScorePath != path) return;
        var dialog = new NativeVisualReviewWindow(report,
            (progress, token) => service.PrepareAsync(report, progress, token),
            (proposal, progress, token) => service.PublishAsync(report, proposal, OutputDirectory, progress, token)) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            ApplyMusicalEditorResult(row, result);
            row.Status = "Revisão visual salva · " + row.PageCountText;
            PersistPreferences();
            Status = "Apresentação salva como nova versão, com a música preservada. A versão anterior continua no Histórico.";
        }
    }
}
