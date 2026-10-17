namespace Teclas.Core;

/// <summary>
/// Converts between seconds of the rendered score audio and quarters of the written score, measure by measure,
/// so tempo changes and the study speed are respected. Only the first pass is mapped: practice steps follow the
/// written order, so when the audio jumps back for a repeat the practice has already reached the end of that music.
/// </summary>
public sealed class PracticeTimeline
{
    private readonly record struct Span(int Measure, double StartSeconds, double EndSeconds, double StartQuarter, double EndQuarter);
    private readonly Span[] spans;

    /// <summary>Average tempo of the mapped music (quarters per minute), used for timing tolerance.</summary>
    public double Bpm { get; }
    public bool IsEmpty => spans.Length == 0;

    public PracticeTimeline(IEnumerable<ScorePlaybackOccurrence> measureTimeline, IReadOnlyDictionary<int, double> measureStarts)
    {
        ArgumentNullException.ThrowIfNull(measureTimeline); ArgumentNullException.ThrowIfNull(measureStarts);
        var ordered = measureTimeline.Where(o => double.IsFinite(o.StartSeconds) && o.EndSeconds > o.StartSeconds).OrderBy(o => o.StartSeconds).ToArray();
        var list = new List<Span>(); int last = int.MinValue;
        foreach (var occurrence in ordered)
        {
            if (occurrence.Measure <= last) break; // a repeat or jump back: the written order ends here
            if (!measureStarts.TryGetValue(occurrence.Measure, out double start)) continue;
            double end = measureStarts.TryGetValue(occurrence.Measure + 1, out double next) ? next : start + 4;
            if (end <= start) continue;
            list.Add(new(occurrence.Measure, occurrence.StartSeconds, occurrence.EndSeconds, start, end));
            last = occurrence.Measure;
        }
        spans = list.ToArray();
        double seconds = spans.Sum(s => s.EndSeconds - s.StartSeconds), quarters = spans.Sum(s => s.EndQuarter - s.StartQuarter);
        Bpm = seconds > 0 ? Math.Clamp(quarters / seconds * 60, 20, 300) : 80;
    }

    /// <summary>Score position for an audio time; before or after the mapped music it continues at the nearest measure's tempo.</summary>
    public double ToQuarters(double seconds)
    {
        if (spans.Length == 0) return seconds * Bpm / 60;
        var span = seconds <= spans[0].StartSeconds ? spans[0] : seconds >= spans[^1].EndSeconds ? spans[^1]
            : spans.First(s => seconds < s.EndSeconds);
        return span.StartQuarter + (seconds - span.StartSeconds) * (span.EndQuarter - span.StartQuarter) / (span.EndSeconds - span.StartSeconds);
    }

    /// <summary>Audio time for a score position (inverse of <see cref="ToQuarters"/>).</summary>
    public double ToSeconds(double quarters)
    {
        if (spans.Length == 0) return quarters * 60 / Bpm;
        var span = quarters <= spans[0].StartQuarter ? spans[0] : quarters >= spans[^1].EndQuarter ? spans[^1]
            : spans.First(s => quarters < s.EndQuarter);
        return span.StartSeconds + (quarters - span.StartQuarter) * (span.EndSeconds - span.StartSeconds) / (span.EndQuarter - span.StartQuarter);
    }

    /// <summary>Where the audio should start so one measure plays before <paramref name="quarter"/> (a musical count-in).</summary>
    public double LeadInSeconds(double quarter)
    {
        var at = spans.LastOrDefault(s => s.StartQuarter <= quarter + 1e-6);
        if (spans.Length == 0 || at == default) return 0;
        int index = Array.IndexOf(spans, at);
        return index > 0 ? spans[index - 1].StartSeconds : Math.Max(0, ToSeconds(quarter) - (at.EndSeconds - at.StartSeconds));
    }
}
