using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;

namespace Teclas.App;

/// <summary>What Ctrl+Z restores on a card: its chords and diagram positions, transposition and simplified chords.</summary>
public sealed record CardSnapshot(IReadOnlyList<(double Quarter, string Root, string Quality, int? Inversion)> Chords,
    int DefaultInversion, int? Inversion, int Transpose, bool Simplified, bool Pending)
{
    public static CardSnapshot Of(MidiRow row) => new(
        row.Chords.Select(c => (c.Quarter, c.Root, c.Quality, c.DiagramInversion)).ToArray(),
        row.DefaultDiagramInversion, row.DiagramInversion, row.TransposeSemitones, row.SimplifiedChords, row.HasPendingScoreProperties);

    public bool SameContent(CardSnapshot other) => Chords.SequenceEqual(other.Chords) && DefaultInversion == other.DefaultInversion &&
        Inversion == other.Inversion && Transpose == other.Transpose && Simplified == other.Simplified;
}

/// <summary>
/// Undo and redo for the selected card. Changes arriving close together (a dialog applying several chords) form one step;
/// the state before the first change is what Ctrl+Z brings back. Each card keeps its own history while the app is open.
/// </summary>
public partial class MainWindow
{
    private sealed class CardHistory { public readonly Stack<CardSnapshot> Undo = new(), Redo = new(); public CardSnapshot? Stable; }
    private readonly Dictionary<MidiRow, CardHistory> histories = [];
    private readonly DispatcherTimer undoSettle = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private MidiRow? trackedRow;
    private bool restoringSnapshot, undoStepOpen;
    public const int UndoLimit = 100;

    private void InitializeUndo()
    {
        undoSettle.Tick += (_, _) => SettleUndoStep();
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectedFile)) TrackUndo(selectedFile); };
    }

    public bool CanUndo => trackedRow is { } row && histories.TryGetValue(row, out var h) && (h.Undo.Count > 0 || undoStepOpen);
    public bool CanRedo => trackedRow is { } row && histories.TryGetValue(row, out var h) && h.Redo.Count > 0;

    private void TrackUndo(MidiRow? row)
    {
        if (ReferenceEquals(row, trackedRow)) return;
        SettleUndoStep();
        if (trackedRow is { } old)
        {
            old.PropertyChanged -= CardChanged; old.Chords.CollectionChanged -= ChordsChanged;
            foreach (var chord in old.Chords) chord.PropertyChanged -= CardChanged;
        }
        trackedRow = row;
        if (row is null || row.UsesNativeScoreWorkflow) { trackedRow = null; return; }
        if (!histories.TryGetValue(row, out var history)) histories[row] = history = new CardHistory();
        history.Stable = CardSnapshot.Of(row);
        row.PropertyChanged += CardChanged; row.Chords.CollectionChanged += ChordsChanged;
        foreach (var chord in row.Chords) chord.PropertyChanged += CardChanged;
    }

    private void ChordsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var chord in e.OldItems?.OfType<ChordRow>() ?? []) chord.PropertyChanged -= CardChanged;
        foreach (var chord in e.NewItems?.OfType<ChordRow>() ?? []) chord.PropertyChanged += CardChanged;
        OpenUndoStep();
    }

    private void CardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChordRow.Root) or nameof(ChordRow.Quality) or nameof(ChordRow.DiagramInversion) or nameof(ChordRow.Quarter)
            or nameof(MidiRow.DefaultDiagramInversion) or nameof(MidiRow.DiagramInversion) or nameof(MidiRow.TransposeSemitones) or nameof(MidiRow.SimplifiedChords))
            OpenUndoStep();
    }

    private void OpenUndoStep()
    {
        if (restoringSnapshot || loading || trackedRow is null || !histories.TryGetValue(trackedRow, out var history) || history.Stable is null) return;
        if (!undoStepOpen)
        {
            undoStepOpen = true;
            history.Undo.Push(history.Stable);
            history.Redo.Clear();
            if (history.Undo.Count > UndoLimit) { var kept = history.Undo.Take(UndoLimit).Reverse().ToArray(); history.Undo.Clear(); foreach (var item in kept) history.Undo.Push(item); }
        }
        undoSettle.Stop(); undoSettle.Start();
        Notify(nameof(CanUndo)); Notify(nameof(CanRedo));
    }

    /// <summary>Closes the open step; a step that ended where it began (e.g. typed and reverted) leaves no trace.</summary>
    private void SettleUndoStep()
    {
        undoSettle.Stop();
        if (!undoStepOpen || trackedRow is null || !histories.TryGetValue(trackedRow, out var history)) { undoStepOpen = false; return; }
        undoStepOpen = false;
        var now = CardSnapshot.Of(trackedRow);
        if (history.Undo.Count > 0 && history.Undo.Peek().SameContent(now)) history.Undo.Pop();
        history.Stable = now;
        Notify(nameof(CanUndo)); Notify(nameof(CanRedo));
    }

    internal bool Undo() => Step(redo: false);
    internal bool Redo() => Step(redo: true);

    private bool Step(bool redo)
    {
        SettleUndoStep();
        if (!IsIdle || trackedRow is not { } row || !histories.TryGetValue(row, out var history)) return false;
        var from = redo ? history.Redo : history.Undo;
        if (from.Count == 0) { Status = redo ? "Nada para refazer." : "Nada para desfazer."; return false; }
        var target = from.Pop();
        (redo ? history.Undo : history.Redo).Push(CardSnapshot.Of(row));
        Apply(row, target);
        history.Stable = CardSnapshot.Of(row);
        Notify(nameof(CanUndo)); Notify(nameof(CanRedo));
        Status = (redo ? "Refeito" : "Desfeito") + $" (Ctrl+{(redo ? "Y" : "Z")}): cifras, posições e transposição como antes." + (row.HasPendingScoreProperties ? " Gere de novo para atualizar a partitura." : "");
        return true;
    }

    private void Apply(MidiRow row, CardSnapshot snapshot)
    {
        restoringSnapshot = true;
        try
        {
            if (row.Chords.Count == snapshot.Chords.Count)
                for (int i = 0; i < snapshot.Chords.Count; i++)
                {
                    var (quarter, root, quality, inversion) = snapshot.Chords[i]; var chord = row.Chords[i];
                    if (chord.Quarter != quarter) chord.Quarter = quarter;
                    if (chord.Root != root) chord.Root = root;
                    if (chord.Quality != quality) chord.Quality = quality;
                    chord.DiagramInversion = inversion;
                }
            else
            {
                row.Chords.Clear();
                foreach (var (quarter, root, quality, inversion) in snapshot.Chords)
                    row.Chords.Add(new ChordRow { Quarter = quarter, Root = root, Quality = quality, DiagramInversion = inversion });
            }
            row.DefaultDiagramInversion = snapshot.DefaultInversion; row.DiagramInversion = snapshot.Inversion;
            row.TransposeSemitones = snapshot.Transpose; row.SimplifiedChords = snapshot.Simplified;
            row.HasPendingScoreProperties = snapshot.Pending;
        }
        finally { restoringSnapshot = false; }
        RefreshSelection(); PersistPreferences(); RefreshChordAlternatives();
        Notify(nameof(SimplifiedChords));
    }
}
