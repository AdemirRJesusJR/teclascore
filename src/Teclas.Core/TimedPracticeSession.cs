namespace Teclas.Core;

public enum TimedPracticeResult { Ignored, OnTime, Early, Late, Wrong }

/// <summary>
/// In-time practice: the score runs at a chosen tempo and each expected key must be played inside a window
/// around its moment. Keys not played before the window closes count as missed; extra keys count as wrong.
/// Time is measured in quarters from the start of the score, so the tempo only changes the clock.
/// </summary>
public sealed class TimedPracticeSession
{
    private readonly List<(int Step, int Pitch)> pending = [];
    private readonly List<MeasureMark> log = [];
    /// <summary>Every key judged, by measure, for the report after the attempt.</summary>
    public IReadOnlyList<MeasureMark> Log => log;
    private int nextStep;
    public IReadOnlyList<PracticeStep> Steps { get; }
    public double Bpm { get; }
    public bool AnyOctave { get; set; }
    /// <summary>Half the window, in quarters. Default: a quarter of a second at the chosen tempo, never under 0.15 quarter.</summary>
    public double Tolerance { get; }
    public int OnTime { get; private set; }
    public int OffTime { get; private set; }
    public int Missed { get; private set; }
    public int Wrong { get; private set; }
    public int Hits => OnTime + OffTime;
    public double Position { get; private set; }
    public bool IsFinished => Steps.Count == 0 || Position > Steps[^1].Start + Tolerance;
    /// <summary>Points from 0 to 100: on time counts 1, early or late ½, missed and wrong 0.</summary>
    public double Score
    {
        get
        {
            int total = Hits + Missed + Wrong;
            return total == 0 ? 0 : Math.Round(100 * (OnTime + OffTime * .5) / total, 1);
        }
    }
    /// <summary>The step being played or coming next.</summary>
    public PracticeStep? Current => Steps.FirstOrDefault(step => step.Start + Tolerance >= Position);
    /// <summary>Keys inside the open window still to play (for highlighting).</summary>
    public IReadOnlyList<int> Waiting => pending.Select(note => note.Pitch).Distinct().ToArray();

    public TimedPracticeSession(IReadOnlyList<PracticeStep> steps, double bpm, bool anyOctave = true, double? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (bpm is < 20 or > 300) throw new ArgumentOutOfRangeException(nameof(bpm));
        Steps = steps.OrderBy(step => step.Start).ToArray(); Bpm = bpm; AnyOctave = anyOctave;
        Tolerance = tolerance ?? Math.Max(.15, .25 * bpm / 60);
    }

    public double SecondsToQuarters(double seconds) => seconds * Bpm / 60;

    /// <summary>Moves the clock forward; notes whose window closed become missed.</summary>
    public void Advance(double position)
    {
        if (position < Position) return;
        Position = position;
        while (nextStep < Steps.Count && Steps[nextStep].Start - Tolerance <= position)
        {
            int step = nextStep++;
            pending.AddRange(Steps[step].Pitches.Select(pitch => (step, pitch)));
        }
        foreach (var note in pending.Where(note => Steps[note.Step].Start + Tolerance < position)) log.Add(new(Steps[note.Step].Measure, MeasureMarkKind.Missed));
        Missed += pending.RemoveAll(note => Steps[note.Step].Start + Tolerance < position);
    }

    public TimedPracticeResult Press(int pitch, double position)
    {
        if (pitch is < 0 or > 127 || Steps.Count == 0) return TimedPracticeResult.Ignored;
        Advance(position);
        var candidates = pending.Where(note => note.Pitch == pitch || AnyOctave && note.Pitch % 12 == pitch % 12)
            .OrderBy(note => Math.Abs(Steps[note.Step].Start - Position)).ToArray();
        if (candidates.Length == 0) { Wrong++; if (Current is { } at) log.Add(new(at.Measure, MeasureMarkKind.Wrong)); return TimedPracticeResult.Wrong; }
        var hit = candidates[0]; pending.Remove(hit);
        double offset = Position - Steps[hit.Step].Start;
        if (Math.Abs(offset) <= Tolerance / 2) { OnTime++; log.Add(new(Steps[hit.Step].Measure, MeasureMarkKind.OnTime)); return TimedPracticeResult.OnTime; }
        OffTime++; log.Add(new(Steps[hit.Step].Measure, MeasureMarkKind.OffTime));
        return offset < 0 ? TimedPracticeResult.Early : TimedPracticeResult.Late;
    }
}
