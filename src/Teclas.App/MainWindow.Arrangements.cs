using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanArrangeScore => IsIdle && SelectedScoreExists;

    private async void OpenArrangementClick(object sender, RoutedEventArgs e)
    {
        if (!CanArrangeScore || selectedFile is not { ScorePath: { } path } row || (!row.UsesNativeScoreWorkflow && !CommitEdits())) return;
        if (!File.Exists(MuseScorePath))
        {
            Status = "Localize o MuseScore Studio 4 em Configurações para preparar arranjos por nível.";
            return;
        }
        StopTrackAudition(); ResetPlayback();
        NativeMusicModel? model = null;
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            Status = "Lendo as pautas e vozes da partitura salva…";
            var opened = await Task.Run(() => NativeMusicEditor.Open(path, token), token);
            token.ThrowIfCancellationRequested();
            model = opened;
        }, operationTitle: "Abrindo arranjo por nível", operationItem: row.ScoreTitle);
        if (operationsDisposed || model is null || selectedFile != row || row.ScorePath != path) return;
        string cache = Path.Combine(LocalJobsDirectory, "arrangements");
        var service = new NativeScoreArrangementService(MuseScorePath, cache);
        using var audio = new EditorScorePlayback(MuseScorePath, Path.Combine(cache, "audio"));
        var dialog = new ArrangementWindow(model, row.PreviewPath,
            async (options, progress, token) =>
            {
                progress.Report("Calculando alterações e trechos que precisam ser preservados…");
                return await Task.Run(() => NativeScoreArrangement.CreatePlan(model.SourcePath, options, token), token);
            },
            (plan, progress, token) => service.PrepareAsync(plan, progress, token),
            async (plan, prepared, progress, token) => await service.PublishAsync(plan, prepared, await PrepareOutputDirectoryAsync(token), progress, token),
            audio) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
            ApplyArrangementResult(row, result);
    }

    private void ApplyArrangementResult(MidiRow row, NativeScoreResult result)
    {
        ApplyMusicalEditorResult(row, result);
        row.Status = "Arranjo salvo · " + row.PageCountText;
        PersistPreferences();
        Status = "Arranjo salvo como nova versão. A partitura anterior continua disponível no Histórico.";
    }
}
