using System.ComponentModel;
using System.Text.Json;

namespace Teclas.App;

public partial class MainWindow
{
    private readonly HashSet<ChordRow> observedDraftChords = [];

    private void StopObservingScoreDraft()
    {
        if (selectedFile is { } previous)
        {
            previous.PropertyChanged -= SelectedScoreDraftChanged;
            previous.Chords.CollectionChanged -= SelectedChordsChanged;
        }
        foreach (var chord in observedDraftChords) chord.PropertyChanged -= SelectedChordDraftChanged;
        observedDraftChords.Clear();
    }
    private void ObserveScoreDraft()
    {
        if (selectedFile is not { } row) return;
        row.PropertyChanged += SelectedScoreDraftChanged;
        row.Chords.CollectionChanged += SelectedChordsChanged;
        SynchronizeDraftChordObservers();
    }
    private void SynchronizeDraftChordObservers()
    {
        foreach (var chord in observedDraftChords) chord.PropertyChanged -= SelectedChordDraftChanged;
        observedDraftChords.Clear();
        if (selectedFile is not { } row) return;
        foreach (var chord in row.Chords)
            if (observedDraftChords.Add(chord)) chord.PropertyChanged += SelectedChordDraftChanged;
    }
    private void SelectedScoreDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MidiRow.KeyFifths) or nameof(MidiRow.KeyMinor)) MarkManualScoreDraftChanged();
    }
    private void SelectedChordDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChordRow.Quarter) or nameof(ChordRow.Root) or nameof(ChordRow.Quality) or nameof(ChordRow.DiagramInversion))
            MarkManualScoreDraftChanged();
    }
    private void MarkManualScoreDraftChanged()
    {
        // Loading, generation and native-editor publication set their own final state.
        if (loading || restoringSession || IsBusy || selectedFile is not { UsesNativeScoreWorkflow: false, ScorePath: not null } row) return;
        if (!row.HasPendingScoreProperties) ResetPlayback();
        row.HasPendingScoreProperties = true;
        row.Status = "Cifras ou armadura alteradas · gere para atualizar a partitura";
        NotifyPlaybackState(); Notify(nameof(PreviewMessage)); Notify(nameof(ScorePropertiesSummary));
        foreach (string name in new[] { nameof(CanTransposeScore), nameof(CanOpenMusicEditor), nameof(CanArrangeScore), nameof(CanDiagnoseScore), nameof(CanPrepareScoreSynchronization), nameof(CanReviewVisualScore) }) Notify(name);
    }
    private static string ScoreDraftMusicalFingerprint(MidiRow row) => JsonSerializer.Serialize(new
    {
        row.KeyFifths, row.KeyMinor,
        Chords = row.Chords.Select(chord => new { chord.Quarter, chord.Root, chord.Quality, chord.DiagramInversion }).ToArray()
    });
}
