using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    // Library-wide memory: the position last chosen for each keyboard chord (pitch class + quality).
    private readonly Dictionary<ChordShape, int> chordPreferences = [];

    private void InitializeChordPreferences()
    {
        foreach (var saved in settings.ChordPositionPreferences)
            if (saved.PitchClass is >= 0 and < 12 && saved.Inversion is >= 0 and <= 2)
                chordPreferences[new ChordShape(saved.PitchClass, (saved.Quality ?? "").Trim())] = saved.Inversion;
        if (settings.ChordPreferencesSeeded) return;
        // First run with this feature: learn from choices already made, then fill songs not generated yet.
        var counts = Files.Where(row => !row.UsesNativeScoreWorkflow)
            .SelectMany(row => row.Chords.Where(chord => chord.DiagramInversion is not null)
                .Select(chord => (Shape: DiagramPositionPropagation.Shape(chord.Root, chord.Quality, row.TransposeSemitones), Inversion: chord.DiagramInversion!.Value)))
            .Where(item => item.Shape is not null)
            .GroupBy(item => item.Shape!.Value);
        foreach (var group in counts)
            if (!chordPreferences.ContainsKey(group.Key))
                chordPreferences[group.Key] = group.GroupBy(item => item.Inversion).OrderByDescending(choice => choice.Count()).First().Key;
        foreach (var row in Files.Where(row => !row.UsesNativeScoreWorkflow && row.ScorePath is null))
            ApplyChordPreferences(row);
        settings.ChordPreferencesSeeded = true;
        SaveChordPreferences();
    }

    /// <summary>Fills chords still on the song default with the library's remembered position. Returns how many changed.</summary>
    private int ApplyChordPreferences(MidiRow row)
    {
        int changed = 0;
        foreach (var chord in row.Chords)
            if (chord.DiagramInversion is null && DiagramPositionPropagation.Shape(chord.Root, chord.Quality, row.TransposeSemitones) is { } shape
                && chordPreferences.TryGetValue(shape, out int inversion))
            { chord.DiagramInversion = inversion; changed++; }
        return changed;
    }

    /// <summary>Positions the dialog should start with: the song's own choices, else the library's remembered ones.</summary>
    private int?[] PreferredInversions(MidiRow row) => row.Chords.Select(chord =>
        chord.DiagramInversion ?? (DiagramPositionPropagation.Shape(chord.Root, chord.Quality, row.TransposeSemitones) is { } shape
            && chordPreferences.TryGetValue(shape, out int inversion) ? inversion : null)).ToArray();

    private void RememberChordPreferences(IReadOnlyList<ChordPositionChange> changes)
    {
        foreach (var change in changes)
            if (change.Inversion is { } inversion) chordPreferences[change.Shape] = inversion;
            else chordPreferences.Remove(change.Shape);
        SaveChordPreferences();
    }

    private void SaveChordPreferences()
    {
        settings.ChordPositionPreferences = chordPreferences.OrderBy(item => item.Key.PitchClass).ThenBy(item => item.Key.Quality, StringComparer.Ordinal)
            .Select(item => new SavedChordPreference { PitchClass = item.Key.PitchClass, Quality = item.Key.Quality, Inversion = item.Value }).ToList();
        PersistPreferences();
    }
}
