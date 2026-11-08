using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Editar › Letra…: corrects, moves or pastes the syllables and saves a new version, like the other native edits.</summary>
public partial class MainWindow
{
    public bool CanEditLyrics => CanOpenMusicEditor;
    internal Func<LyricsEditorWindow, bool?> lyricsEditorDialog = window => window.ShowDialog();
    /// <summary>Prepares (MuseScore reopens and renders the draft) and publishes the new version; replaced in the smoke.</summary>
    internal Func<NativeMusicModel, NativeMusicEdits, IProgress<string>, CancellationToken, Task<NativeScoreResult>>? lyricsSaver = null;

    private async void LyricsEditorClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditLyrics || selectedFile is not { ScorePath: { } path } row || (!row.UsesNativeScoreWorkflow && !CommitEdits())) return;
        if (lyricsSaver is null && !File.Exists(MuseScorePath)) { Status = "Localize o MuseScore Studio 4 em Configurações para salvar a letra."; return; }
        NativeMusicModel model;
        try { model = await Task.Run(() => NativeMusicEditor.Open(path, CancellationToken.None)); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Xml.XmlException)
        { Status = "Não foi possível abrir a letra da partitura: " + error.Message; return; }
        if (selectedFile != row || row.ScorePath != path) return;
        var dialog = new LyricsEditorWindow(LyricsEditorDocument.FromModel(model), string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle) { Owner = this };
        if (lyricsEditorDialog(dialog) != true || !dialog.Document.HasChanges) return;
        var edits = dialog.Document.ToEdits();
        var problems = NativeMusicEditor.Validate(model, edits);
        if (problems.Count > 0) { Status = string.Join(" ", problems); return; }
        StopTrackAudition(); ResetPlayback();
        bool saved = false;
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            var progress = CreateOperationProgress();
            progress.Report("Conferindo a letra no MuseScore…");
            NativeScoreResult result;
            if (lyricsSaver is { } saver) result = await saver(model, edits, progress, token);
            else
            {
                var service = new NativeMusicEditingService(MuseScorePath, Path.Combine(LocalJobsDirectory, "music-editor"));
                var prepared = await service.PrepareAsync(model, edits, progress, token);
                result = await service.PublishAsync(model, prepared, await PrepareOutputDirectoryAsync(token), progress, token);
            }
            ApplyMusicalEditorResult(row, result);
            row.Status = "Letra editada · " + row.PageCountText;
            PersistPreferences();
            saved = true;
        }, operationTitle: "Salvando a letra", operationItem: row.ScoreTitle);
        // After the operation, so a late progress message never covers the result.
        if (saved) Status = $"Letra salva numa nova versão ({edits.Lyrics.Count} sílaba(s) alterada(s)). As notas não mudaram; a versão anterior está no Histórico.";
    }
}
