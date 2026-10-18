namespace Teclas.Core;

/// <summary>One moment of the score to play: every pitch that starts together (all staves).</summary>
public sealed record PracticeStep(int Measure, double Quarter, IReadOnlyList<int> Pitches)
{
    /// <summary>Position from the start of the score in quarters, as written (repeats are not unfolded).</summary>
    public double Start { get; init; }
    /// <summary>Chord symbol for chord practice; empty for melody steps.</summary>
    public string Label { get; init; } = "";
    /// <summary>Bass of a slash chord (G/B → "B"); null when the bass is the root.</summary>
    public string? Bass { get; init; }
}
public enum PracticeResult { Ignored, Correct, StepComplete, Finished, Wrong }

/// <summary>
/// Wait-mode practice: the next step stays until every expected key is played. Timing is free,
/// so the player can learn the notes before the tempo. Wrong keys are counted but never skip ahead.
/// </summary>
public sealed class PracticeSession
{
    private readonly HashSet<int> played = [];
    private readonly List<MeasureMark> log = [];
    public IReadOnlyList<MeasureMark> Log => log;
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
        if (match is null) { Misses++; log.Add(new(step.Measure, MeasureMarkKind.Wrong)); return PracticeResult.Wrong; }
        played.Add(match.Value); Hits++; log.Add(new(step.Measure, MeasureMarkKind.OnTime));
        if (played.Count < step.Pitches.Count) return PracticeResult.Correct;
        Advance(1);
        return IsFinished ? PracticeResult.Finished : PracticeResult.StepComplete;
    }

    /// <summary>Moves by whole steps (negative goes back); clears keys already played in the step.</summary>
    public void Advance(int steps) { Index = Math.Clamp(Index + steps, 0, Steps.Count); played.Clear(); }
    public void Restart() { Index = 0; Hits = 0; Misses = 0; played.Clear(); log.Clear(); }

    /// <summary>Steps from a saved score: notes starting together on any staff form one step; rests and grace notes are skipped.</summary>
    public static IReadOnlyList<PracticeStep> FromScore(NativeMusicModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var starts = MeasureStarts(model);
        return model.Measures
            .SelectMany(measure => measure.Events.Where(e => e.Quarter is not null && e.Notes.Count > 0)
                .Select(e => (measure.Number, Quarter: Math.Round(e.Quarter!.Value, 6), e.Notes)))
            .GroupBy(item => (item.Number, item.Quarter))
            .OrderBy(group => group.Key.Number).ThenBy(group => group.Key.Quarter)
            .Select(group => new PracticeStep(group.Key.Number, group.Key.Quarter,
                group.SelectMany(item => item.Notes.Select(note => note.Pitch)).Distinct().Order().ToArray())
                { Start = starts.GetValueOrDefault(group.Key.Number) + group.Key.Quarter })
            .ToArray();
    }

    /// <summary>
    /// Chord steps from the score's chord symbols, voiced like the keyboard diagrams (the diagram's own position,
    /// else <paramref name="defaultInversion"/>). A repeated symbol stays one step; unsupported symbols are skipped.
    /// </summary>
    public static IReadOnlyList<PracticeStep> ChordsFromScore(NativeMusicModel model, int defaultInversion = 2)
    {
        ArgumentNullException.ThrowIfNull(model);
        var starts = MeasureStarts(model);
        var steps = new List<PracticeStep>();
        foreach (var harmony in model.Measures.SelectMany(measure => measure.Harmonies)
            .Where(h => h.Quarter is not null).OrderBy(h => h.Measure).ThenBy(h => h.Quarter).ThenBy(h => h.Staff))
        {
            if (!KeyboardDiagramVoicing.TryRootPitchClass(harmony.Root, out int root) || !KeyboardDiagramVoicing.SupportsQuality(harmony.Quality)) continue;
            string label = harmony.Root + harmony.Quality;
            double quarter = Math.Round(harmony.Quarter!.Value, 6);
            if (steps.Count > 0 && (steps[^1].Label == label || steps[^1].Measure == harmony.Measure && steps[^1].Quarter == quarter)) continue;
            int inversion = harmony.DiagramInversion is >= 0 and <= 2 ? harmony.DiagramInversion.Value : Math.Clamp(defaultInversion, 0, 2);
            steps.Add(new PracticeStep(harmony.Measure, quarter, KeyboardDiagramVoicing.Pitches(root, harmony.Quality, inversion))
                { Start = starts.GetValueOrDefault(harmony.Measure) + quarter, Label = label, Bass = harmony.Bass });
        }
        return steps;
    }

    /// <summary>
    /// Both hands: the chord of the cipher joins the melody step that starts with it (a chord without a melody attack
    /// becomes its own step). Later melody steps under the same chord are played by the right hand alone.
    /// </summary>
    public static IReadOnlyList<PracticeStep> Combine(IReadOnlyList<PracticeStep> melody, IReadOnlyList<PracticeStep> chords)
    {
        var byStart = melody.GroupBy(step => Math.Round(step.Start, 3)).ToDictionary(group => group.Key, group => group.First());
        var result = new List<PracticeStep>(melody);
        foreach (var chord in chords)
        {
            double at = Math.Round(chord.Start, 3);
            if (byStart.TryGetValue(at, out var step))
            {
                var merged = step with { Pitches = step.Pitches.Concat(chord.Pitches).Distinct().Order().ToArray(), Label = chord.Label };
                result[result.IndexOf(step)] = merged; byStart[at] = merged;
            }
            else result.Add(chord);
        }
        return result.OrderBy(step => step.Start).ToArray();
    }

    /// <summary>Start of each measure in quarters: the longest staff content of the earlier measures (4 when empty).</summary>
    public static IReadOnlyDictionary<int, double> MeasureStarts(NativeMusicModel model)
    {
        var lengths = model.Measures.GroupBy(measure => measure.Number).OrderBy(group => group.Key)
            .Select(group => (Number: group.Key, Length: group.SelectMany(measure => measure.Events)
                .Where(e => e.Quarter is not null && e.DurationQuarter is not null)
                .Select(e => e.Quarter!.Value + e.DurationQuarter!.Value).DefaultIfEmpty(4).Max()));
        var starts = new Dictionary<int, double>(); double at = 0;
        foreach (var (number, length) in lengths) { starts[number] = at; at += length > 0 ? length : 4; }
        return starts;
    }

    /// <summary>Portuguese note name with octave (Dó4 is middle C).</summary>
    public static string NoteName(int pitch) =>
        new[] { "Dó", "Dó♯", "Ré", "Mi♭", "Mi", "Fá", "Fá♯", "Sol", "Sol♯", "Lá", "Si♭", "Si" }[pitch % 12] + (pitch / 12 - 1);
}
