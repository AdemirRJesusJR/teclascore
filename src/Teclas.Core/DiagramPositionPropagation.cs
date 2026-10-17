namespace Teclas.Core;

/// <summary>The chord as played on the keyboard: final pitch class after transposition and its quality (G#m and Abm match).</summary>
public readonly record struct ChordShape(int PitchClass, string Quality);
/// <summary>A position chosen for every occurrence of one chord shape. Null means "use each song's default".</summary>
public sealed record ChordPositionChange(ChordShape Shape, int? Inversion);
public sealed record ChordPositionEntry(string Root, string? Quality, int? Inversion);

/// <summary>Carries diagram positions chosen in one song to other songs with the same chords, without touching other chords.</summary>
public static class DiagramPositionPropagation
{
    public static ChordShape? Shape(string root, string? quality, int transposeSemitones) =>
        KeyboardDiagramVoicing.TryRootPitchClass(root, out int pitchClass)
            ? new ChordShape(((pitchClass + transposeSemitones) % 12 + 12) % 12, (quality ?? "").Trim())
            : null;

    /// <summary>Shapes the user changed, where every occurrence now shares one position. Mixed or unchanged shapes are not carried.</summary>
    public static IReadOnlyList<ChordPositionChange> Changes(IReadOnlyList<ChordPositionEntry> before, IReadOnlyList<int?> after, int transposeSemitones)
    {
        if (before.Count != after.Count) throw new ArgumentException("Cada cifra precisa de uma posição.", nameof(after));
        var changes = new List<ChordPositionChange>();
        foreach (var group in before.Select((chord, index) => (Shape: Shape(chord.Root, chord.Quality, transposeSemitones), Index: index))
                     .Where(item => item.Shape is not null).GroupBy(item => item.Shape!.Value))
        {
            var values = group.Select(item => after[item.Index]).Distinct().ToArray();
            if (values.Length == 1 && group.Any(item => before[item.Index].Inversion != after[item.Index]))
                changes.Add(new(group.Key, values[0]));
        }
        return changes;
    }

    /// <summary>New positions for another song's chords, by index; only matching chords with a different position appear.</summary>
    public static IReadOnlyDictionary<int, int?> Plan(IReadOnlyList<ChordPositionEntry> chords, int transposeSemitones, IReadOnlyList<ChordPositionChange> changes)
    {
        var wanted = changes.ToDictionary(change => change.Shape, change => change.Inversion);
        var plan = new Dictionary<int, int?>();
        for (int index = 0; index < chords.Count; index++)
            if (Shape(chords[index].Root, chords[index].Quality, transposeSemitones) is { } shape &&
                wanted.TryGetValue(shape, out var inversion) && chords[index].Inversion != inversion)
                plan[index] = inversion;
        return plan;
    }

    public static bool SharesAnyShape(IReadOnlyList<ChordPositionEntry> first, int firstTranspose, IReadOnlyList<ChordPositionEntry> second, int secondTranspose)
    {
        var shapes = first.Select(chord => Shape(chord.Root, chord.Quality, firstTranspose)).OfType<ChordShape>().ToHashSet();
        return second.Any(chord => Shape(chord.Root, chord.Quality, secondTranspose) is { } shape && shapes.Contains(shape));
    }
}
