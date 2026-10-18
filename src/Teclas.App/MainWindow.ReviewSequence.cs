using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Review in sequence: approve, redo with another melody or edit each generated score, the riskiest first.</summary>
public partial class MainWindow
{
    internal Func<ReviewSequenceWindow, bool?> reviewSequenceDialog = window => window.ShowDialog();
    /// <summary>Test hook: regenerates a row after its melody changed (instead of analysis + generation).</summary>
    internal Func<MidiRow, Task>? reviewRegenerate = null;

    /// <summary>Generated scores not approved yet, lowest rating first.</summary>
    internal IReadOnlyList<MidiRow> ReviewSequenceRows()
    {
        RefreshAllConfidence();
        return Files.Where(r => r.ScorePath is not null && !r.Approved && r.Confidence is not null).OrderBy(r => r.Confidence!.Score).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void ReviewSequenceClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var rows = ReviewSequenceRows();
        if (rows.Count == 0) { Status = "Nada para revisar: todas as partituras geradas já foram aprovadas."; return; }
        var window = new ReviewSequenceWindow(rows, row => { row.Approved = true; PersistPreferences(); }, RedoWithNextMelodyAsync) { Owner = this };
        reviewSequenceDialog(window);
        libraryDirty = true; RefreshDashboard(); PersistPreferences();
        Status = window.Approved > 0 ? $"{window.Approved} partitura(s) aprovada(s) na revisão." : Status;
        if (window.EditRequested is { } edit)
        {
            SetCurrentPage("library"); Library.SelectedItem = edit; Library.ScrollIntoView(edit); RefreshSelection();
            OpenMusicEditorClick(this, new RoutedEventArgs());
        }
    }

    /// <summary>The next melody candidate (by the advisor's ranking) replaces the current choice and the score is made again.</summary>
    internal async Task<string> RedoWithNextMelodyAsync(MidiRow row)
    {
        var current = row.Tracks.Where(t => t.IsMelody).Select(t => t.Source.Key).ToHashSet();
        var ranked = row.Tracks.Where(t => t.MelodySuggestion is not null).OrderBy(t => t.MelodySuggestion!.Rank).ToList();
        if (ranked.Count == 0) ranked = row.Tracks.ToList();
        int at = ranked.FindIndex(t => current.Contains(t.Source.Key));
        var next = ranked.Skip(at + 1).Concat(ranked.Take(Math.Max(0, at))).FirstOrDefault(t => !current.Contains(t.Source.Key));
        if (next is null) throw new InvalidOperationException("Não há outra pista candidata a melodia nesta música. Abra no editor (E) para ajustar.");
        foreach (var track in row.Tracks) track.IsMelody = track == next;
        row.AutoMelody = false; row.Approved = false;
        string name = $"{next.Instrument} (pista {next.Track}, canal {next.Channel})";
        bool ok = false;
        await RunOperation(async () =>
        {
            if (reviewRegenerate is { } regenerate) await regenerate(row);
            else { await Analyze(row); await Generate(row); }
            ok = true;
        }, operationTitle: "Gerando com outra melodia", operationItem: row.Name);
        RefreshConfidence(row); PersistPreferences();
        return ok ? $"Gerada de novo com a melodia em {name}{(row.Confidence is { } c ? $": {c.Score}%" : "")}." : $"Não foi possível gerar com {name}: {Status}";
    }
}
