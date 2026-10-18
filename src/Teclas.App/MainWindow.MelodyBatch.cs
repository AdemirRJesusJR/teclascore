using System.Windows;

namespace Teclas.App;

public partial class MainWindow
{
    internal Func<MelodyBatchWindow, bool?> melodyBatchDialog = window => window.ShowDialog();
    internal Func<MidiRow, CancellationToken, Task>? melodyBatchEnqueue = null;

    private async void MelodyBatchClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var rows = MelodyBatchWindow.Build(Files);
        if (rows.Count == 0) { Status = "Todos os MIDIs já têm melodia escolhida ou partitura."; return; }
        var dialog = new MelodyBatchWindow(rows) { Owner = this };
        if (melodyBatchDialog(dialog) != true) return;
        var chosen = dialog.SelectedRows;
        // The proposal becomes an ordinary melody choice: it stays visible and editable on each card.
        foreach (var item in chosen) { item.Track!.IsMelody = true; item.Row.AutoMelody = true; item.Row.AutoMelodyConfident = item.Proposal.Confident; }
        PersistPreferences();
        int added = 0; var failed = new List<string>();
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            foreach (var item in chosen)
            {
                token.ThrowIfCancellationRequested();
                SetOperationItems(added + failed.Count, chosen.Count, item.Song);
                try { await (melodyBatchEnqueue?.Invoke(item.Row, token) ?? EnqueueConversion(item.Row, token)); added++; }
                catch (Exception error) when (error is not OperationCanceledException) { failed.Add(item.Song + ": " + error.Message); }
            }
            Status = $"{added} músicas com melodia escolhida foram para a fila." + (failed.Count > 0 ? $" {failed.Count} não entraram: " + string.Join(" · ", failed) : "");
        }, operationTitle: "Enfileirando as melodias escolhidas");
        if (added > 0 && melodyBatchEnqueue is null) ShowConversionQueue();
    }
}
