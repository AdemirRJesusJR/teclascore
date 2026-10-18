using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanOpenMusicEditor => IsIdle && SelectedScoreExists;
    private async void OpenMusicEditorClick(object sender, RoutedEventArgs e)
    {
        if (!CanOpenMusicEditor || selectedFile is not { ScorePath: { } path } row || (!row.UsesNativeScoreWorkflow && !CommitEdits())) return;
        try
        {
            var pending = persistenceEnabled ? new EditorDraftRecoveryStore(DraftRecoveryDirectory).List().Drafts.FirstOrDefault(d => d.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase)) : null;
            if (pending is not null)
            {
                var choice = MessageBox.Show(this, "Há um rascunho protegido para esta partitura. Deseja recuperá-lo?\nNão abre a partitura salva e mantém o rascunho disponível em Recuperar rascunhos.", "Continuar edição", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes) { await OpenEditorSession(row, pending.SnapshotPath, pending); return; }
            }
            await OpenEditorSession(row, path, null);
        }
        catch (Exception error) { Status = "Não foi possível abrir o editor. " + error.Message; }
    }

    private async Task OpenEditorSession(MidiRow? row, string path, EditorDraftCheckpoint? recovery)
    {
        if (!File.Exists(MuseScorePath)) { Status = "Localize o MuseScore Studio 4 em Configurações para preparar as edições."; return; }
        StopTrackAudition(); ResetPlayback();
        NativeMusicModel? model = null;
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            Status = "Lendo pautas, compassos, notas e cifras para o editor…";
            var openedModel = await Task.Run(() => NativeMusicEditor.Open(path, token), token);
            token.ThrowIfCancellationRequested();
            model = openedModel;
        }, operationTitle: "Abrindo editor musical", operationItem: recovery?.Title ?? row?.ScoreTitle);
        if (operationsDisposed || model is null || (recovery is null && (selectedFile != row || row?.ScorePath != path))) return;
        string cache = Path.Combine(LocalJobsDirectory, "music-editor");
        var service = new NativeMusicEditingService(MuseScorePath, cache);
        using var audio = new EditorScorePlayback(MuseScorePath, Path.Combine(cache, "audio"));
        var dialog = new MusicEditorWindow(model, recovery is null ? row?.PreviewPath : null,
            (edits, progress, token) => service.PrepareAsync(model, edits, progress, token),
            async (prepared, progress, token) => await service.PublishAsync(model, prepared, await PrepareOutputDirectoryAsync(token), progress, token),
            audio, persistenceEnabled ? DraftRecoveryDirectory : null, recovery) { Owner = this, ComfortableDiagramMaximumSpan = settings.ComfortableDiagramMaximumSpan };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            if (row is null) { row = LoadNativeScore(result.Path); Files.Add(row); }
            Library.SelectedItem = row;
            ApplyMusicalEditorResult(row, result);
            if (SaveCurrentSession()) dialog.AcknowledgeRecoveryPublication();
        }
        settings.ComfortableDiagramMaximumSpan = dialog.ComfortableDiagramMaximumSpan;
        PersistPreferences();
        await RefreshDraftRecoveryAvailability();
    }

    private void ApplyMusicalEditorResult(MidiRow row, NativeScoreResult result)
    {
        RememberVersion(row);
        ResetPlayback(); StopTrackAudition();
        row.ScorePath = result.Path;
        row.PreviewPath = result.PreviewPages.FirstOrDefault();
        row.PageCount = result.PreviewPages.Count;
        row.GeneratedAt = DateTimeOffset.Now; row.Approved = false;
        row.ScoreTitle = result.Metadata.Title; row.ScoreSubtitle = result.Metadata.Subtitle;
        row.ScoreArtist = result.Metadata.Artist; row.ScoreComposer = result.Metadata.Composer;
        row.HasMusicalEdits = true;
        row.ArrangementSummary = result.Metadata.ArrangementSummary;
        row.UseReviewedProfile = false;
        row.HasPendingScoreProperties = false;
        row.HasOperationError = false;
        row.Status = "Edição musical salva · " + row.PageCountText;
        row.ReviewPoints.Clear();
        RememberVersion(row);
        CurrentFilter = "all";
        EditorTabs.SelectedIndex = 2;
        RefreshSelection(); PersistPreferences();
        Status = "Nova versão musical salva. Continue pelo Editor musical; as versões anteriores permanecem no Histórico.";
    }
}
