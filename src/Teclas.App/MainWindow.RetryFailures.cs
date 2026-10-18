using System.Windows;

namespace Teclas.App;

/// <summary>
/// Failed generations are tried again with adjustments instead of being investigated one by one: first without folding
/// the repeats (the most common cause of a refused score), then as chords only (when the melody cannot be written).
/// What had to change is reported per song; songs that still fail keep their original options.
/// </summary>
public partial class MainWindow
{
    /// <summary>Test hook: one generation attempt over the rows (instead of the conversion queue).</summary>
    internal Func<IReadOnlyList<MidiRow>, CancellationToken, Task<IReadOnlyDictionary<MidiRow, string>>>? generationAttempt = null;
    /// <summary>What had to change to generate each song in the last run (empty when nothing changed).</summary>
    public IReadOnlyDictionary<MidiRow, string> LastAdjustments { get; private set; } = new Dictionary<MidiRow, string>();

    internal async Task<IReadOnlyDictionary<MidiRow, string>> GenerateWithAdjustmentsAsync(IReadOnlyList<MidiRow> rows, CancellationToken token)
    {
        var attempt = generationAttempt ?? GenerateThroughQueueAsync;
        var failures = new Dictionary<MidiRow, string>(await attempt(rows, token));
        var adjustments = new Dictionary<MidiRow, string>();
        var original = failures.Keys.ToDictionary(r => r, r => (r.FoldRepeats, r.ChordsOnly));
        async Task Retry(Func<MidiRow, bool> applies, Action<MidiRow> change, string description)
        {
            var retry = failures.Keys.Where(r => r.Document is not null && !r.UsesNativeScoreWorkflow && applies(r)).ToList();
            if (retry.Count == 0 || token.IsCancellationRequested) return;
            foreach (var row in retry) { change(row); adjustments[row] = adjustments.TryGetValue(row, out var before) ? before + " e " + description : description; }
            Status = $"Tentando de novo {retry.Count} música(s) {description}…";
            var again = await attempt(retry, token);
            foreach (var row in retry)
                if (again.TryGetValue(row, out var error)) failures[row] = error; else failures.Remove(row);
        }
        await Retry(r => r.FoldRepeats, r => r.FoldRepeats = false, "sem dobrar as repetições");
        await Retry(r => !r.ChordsOnly, r => r.ChordsOnly = true, "só com cifras");
        foreach (var row in failures.Keys.Where(original.ContainsKey))
        {
            // Still failing: the song keeps the options it had.
            (row.FoldRepeats, row.ChordsOnly) = original[row];
            adjustments.Remove(row);
        }
        LastAdjustments = adjustments.Where(a => !failures.ContainsKey(a.Key)).ToDictionary(a => a.Key, a => a.Value);
        return failures;
    }

    public Visibility RetryFailuresVisibility => Files.Any(r => r.HasOperationError && r.Document is not null && !r.UsesNativeScoreWorkflow) ? Visibility.Visible : Visibility.Collapsed;

    private async void RetryFailuresClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var failed = Files.Where(r => r.HasOperationError && r.Document is not null && !r.UsesNativeScoreWorkflow && (r.HasMelodyChoice || r.UseReviewedProfile || r.ChordsOnly)).ToList();
        if (failed.Count == 0) { Status = "Nenhuma música com falha de geração para tentar de novo."; return; }
        await RetryFailuresAsync(failed);
    }

    internal async Task<IReadOnlyDictionary<MidiRow, string>> RetryFailuresAsync(IReadOnlyList<MidiRow> failed)
    {
        IReadOnlyDictionary<MidiRow, string> failures = new Dictionary<MidiRow, string>();
        await RunOperation(async () => failures = await GenerateWithAdjustmentsAsync(failed, cancellation!.Token), operationTitle: "Repetindo falhas com ajuste");
        foreach (var row in failed.Where(r => !failures.ContainsKey(r))) row.HasOperationError = false;
        RefreshSelection(); PersistPreferences(); libraryDirty = true; RefreshDashboard();
        int fixedCount = failed.Count - failures.Count;
        Status = $"Falhas: {fixedCount} de {failed.Count} música(s) geradas." +
                 (LastAdjustments.Count > 0 ? " Ajustes: " + string.Join("; ", LastAdjustments.Select(a => $"{a.Key.Name} {a.Value}")) + "." : "") +
                 (failures.Count > 0 ? $" Continuam falhando: {string.Join("; ", failures.Select(f => f.Key.Name + " — " + f.Value))}." : "");
        return failures;
    }
}
