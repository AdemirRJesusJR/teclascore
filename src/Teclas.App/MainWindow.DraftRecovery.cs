using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private string DraftRecoveryDirectory => Path.Combine(persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!, "editor-drafts");
    public bool CanRecoverDrafts => IsIdle;
    private int pendingDrafts;
    public string DraftRecoveryLabel => pendingDrafts > 0 ? $"Rascunhos ({pendingDrafts})" : "Rascunhos";
    private async Task RefreshDraftRecoveryAvailability(bool announce = false)
    {
        if (!persistenceEnabled) return;
        try
        {
            var index = await Task.Run(() => new EditorDraftRecoveryStore(DraftRecoveryDirectory).List());
            if (operationsDisposed) return;
            pendingDrafts = index.Drafts.Count; Notify(nameof(DraftRecoveryLabel));
            if (announce && IsIdle && (pendingDrafts > 0 || index.Warnings.Count > 0))
                Status = pendingDrafts > 0 ? $"Há {pendingDrafts} rascunho(s) protegido(s). Abra Rascunhos para continuar a edição." : index.Warnings[0];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { if (announce && IsIdle) Status = "Não foi possível conferir os rascunhos protegidos. " + error.Message; }
    }
    private async void OpenDraftRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (!CanRecoverDrafts) return;
        if (!persistenceEnabled) { Status = "Recuperação de rascunhos indisponível nesta sessão de demonstração."; return; }
        try
        {
            var dialog = new DraftRecoveryWindow(new(DraftRecoveryDirectory)) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedDraft is not { } draft) return;
            var row = Files.FirstOrDefault(f => string.Equals(f.ScorePath, draft.SourcePath, StringComparison.OrdinalIgnoreCase) || string.Equals(f.FullPath, draft.SourcePath, StringComparison.OrdinalIgnoreCase));
            await OpenEditorSession(row, draft.SnapshotPath, draft);
        }
        catch (Exception error) { Status = "Não foi possível recuperar o rascunho. Os arquivos foram preservados. " + error.Message; }
        finally { await RefreshDraftRecoveryAvailability(); }
    }
}
