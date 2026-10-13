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
        }, operationTitle: "Abrindo editor musical", operationItem: row.ScoreTitle);
        if (operationsDisposed || model is null || selectedFile != row || row.ScorePath != path) return;
        string cache = Path.Combine(LocalJobsDirectory, "music-editor");
        var service = new NativeMusicEditingService(MuseScorePath, cache);
        using var audio = new EditorScorePlayback(MuseScorePath, Path.Combine(cache, "audio"));
        var dialog = new MusicEditorWindow(model, row.PreviewPath,
            (edits, progress, token) => service.PrepareAsync(model, edits, progress, token),
            (prepared, progress, token) => service.PublishAsync(model, prepared, OutputDirectory, progress, token),
            audio) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
            ApplyMusicalEditorResult(row, result);
    }

    private void ApplyMusicalEditorResult(MidiRow row, NativeScoreResult result)
    {
        RememberVersion(row);
        ResetPlayback(); StopTrackAudition();
        row.ScorePath = result.Path;
        row.PreviewPath = result.PreviewPages.FirstOrDefault();
        row.PageCount = result.PreviewPages.Count;
        row.GeneratedAt = DateTimeOffset.Now;
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
