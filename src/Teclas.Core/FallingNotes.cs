namespace Teclas.Core;

/// <summary>A note block of the falling-notes view: start and length in quarters (written order), pitch, and whether it is a chord note.</summary>
public sealed record FallingNote(double Start, double Length, int Pitch, bool Chord, string Label = "");

/// <summary>
/// The score as falling blocks: melody notes from the top staff and, separately, the keyboard-diagram notes of each chord
/// held until the next one. Positions follow the written measures (repeats are not unfolded); a tied note shows as
/// consecutive blocks.
/// </summary>
public static class FallingNotes
{
    public static IReadOnlyList<FallingNote> FromScore(NativeMusicModel model, int defaultInversion = 2)
    {
        ArgumentNullException.ThrowIfNull(model);
        var starts = PracticeSession.MeasureStarts(model);
        int top = model.Measures.Count == 0 ? 1 : model.Measures.Min(m => m.Staff);
        var melody = model.Measures.Where(m => m.Staff == top).SelectMany(m => m.Events)
            .Where(e => e.Quarter is not null && e.DurationQuarter is > 0 && starts.ContainsKey(e.Measure))
            .SelectMany(e => e.Notes.Select(n => new FallingNote(starts[e.Measure] + e.Quarter!.Value, e.DurationQuarter!.Value, n.Pitch, false)))
            .OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        var joined = melody;
        var chords = PracticeSession.ChordsFromScore(model, defaultInversion);
        double end = joined.Count == 0 ? starts.Values.DefaultIfEmpty(0).Max() + 4 : joined.Max(n => n.Start + n.Length);
        for (int i = 0; i < chords.Count; i++)
        {
            double length = (i + 1 < chords.Count ? chords[i + 1].Start : Math.Max(end, chords[i].Start + 4)) - chords[i].Start;
            if (length <= 0) continue;
            joined.AddRange(chords[i].Pitches.Select(p => new FallingNote(chords[i].Start, length, p, true, chords[i].Label)));
        }
        return joined.OrderBy(n => n.Start).ThenBy(n => n.Chord).ThenBy(n => n.Pitch).ToArray();
    }

    /// <summary>Lowest and highest keys to draw: whole octaves around the notes, at least two octaves. A larger
    /// <paramref name="minimumOctaves"/> adds octaves above and below in turn, so the notes stay in the middle.</summary>
    public static (int Low, int High) KeyRange(IReadOnlyList<FallingNote> notes, int minimumOctaves = 2)
    {
        if (notes.Count == 0) return Widen(48, 71, minimumOctaves);
        int low = notes.Min(n => n.Pitch) / 12 * 12, high = notes.Max(n => n.Pitch) / 12 * 12 + 11;
        if (high - low < 23) high = low + 23;
        return Widen(low, high, minimumOctaves);
    }

    private static (int Low, int High) Widen(int low, int high, int octaves)
    {
        for (bool above = true; (high - low + 1) / 12 < octaves; above = !above)
            if (above && high + 12 <= 119 || low < 12) high += 12; else low -= 12;
        return (low, high);
    }
}
