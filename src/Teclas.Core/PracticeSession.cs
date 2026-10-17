namespace Teclas.Core;

/// <summary>One moment of the score to play: every pitch that starts together (all staves).</summary>
public sealed record PracticeStep(int Measure, double Quarter, IReadOnlyList<int> Pitches);
public enum PracticeResult { Ignored, Correct, StepComplete, Finished, Wrong }

/// <summary>
/// Wait-mode practice: the next step stays until every expected key is played. Timing is free,
/// so the player can learn the notes before the tempo. Wrong keys are counted but never skip ahead.
/// </summary>
public sealed class PracticeSession
{
    private readonly HashSet<int> played = [];
    public IReadOnlyList<PracticeStep> Steps { get; }
    public bool AnyOctave { get; set; }
    public int Index { get; private set; }
    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public bool IsFinished => Index >= Steps.Count;
    public PracticeStep? Current => IsFinished ? null : Steps[Index];
    /// <summary>Expected keys of the current step not played yet.</summary>
    public IReadOnlyList<int> Remaining => Current is { } step ? step.Pitches.Where(pitch => !played.Contains(pitch)).ToArray() : [];
    public double Accuracy => Hits + Misses == 0 ? 1 : Hits / (double)(Hits + Misses);

    public PracticeSession(IReadOnlyList<PracticeStep> steps, bool anyOctave = true)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Steps = steps; AnyOctave = anyOctave;
    }

    public PracticeResult Press(int pitch)
    {
        if (Current is not { } step || pitch is < 0 or > 127) return PracticeResult.Ignored;
        int? match = step.Pitches.Where(expected => !played.Contains(expected))
            .Select(expected => (int?)expected).FirstOrDefault(expected => expected == pitch || AnyOctave && expected % 12 == pitch % 12);
        if (match is null) { Misses++; return PracticeResult.Wrong; }
        played.Add(match.Value); Hits++;
        if (played.Count < step.Pitches.Count) return PracticeResult.Correct;
        Advance(1);
        return IsFinished ? PracticeResult.Finished : PracticeResult.StepComplete;
    }

    /// <summary>Moves by whole steps (negative goes back); clears keys already played in the step.</summary>
    public void Advance(int steps) { Index = Math.Clamp(Index + steps, 0, Steps.Count); played.Clear(); }
    public void Restart() { Index = 0; Hits = 0; Misses = 0; played.Clear(); }

    /// <summary>Steps from a saved score: notes starting together on any staff form one step; rests and grace notes are skipped.</summary>
    public static IReadOnlyList<PracticeStep> FromScore(NativeMusicModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Measures
            .SelectMany(measure => measure.Events.Where(e => e.Quarter is not null && e.Notes.Count > 0)
                .Select(e => (measure.Number, Quarter: Math.Round(e.Quarter!.Value, 6), e.Notes)))
            .GroupBy(item => (item.Number, item.Quarter))
            .OrderBy(group => group.Key.Number).ThenBy(group => group.Key.Quarter)
            .Select(group => new PracticeStep(group.Key.Number, group.Key.Quarter,
                group.SelectMany(item => item.Notes.Select(note => note.Pitch)).Distinct().Order().ToArray()))
            .ToArray();
    }

    /// <summary>Portuguese note name with octave (Dó4 is middle C).</summary>
    public static string NoteName(int pitch) =>
        new[] { "Dó", "Dó♯", "Ré", "Mi♭", "Mi", "Fá", "Fá♯", "Sol", "Sol♯", "Lá", "Si♭", "Si" }[pitch % 12] + (pitch / 12 - 1);
}
