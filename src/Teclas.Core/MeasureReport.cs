namespace Teclas.Core;

public enum MeasureMarkKind { OnTime, OffTime, Missed, Wrong }
public sealed record MeasureMark(int Measure, MeasureMarkKind Kind);

/// <summary>One measure of an attempt: right keys (on time or not), missed and wrong, and points from 0 to 100.</summary>
public sealed record MeasureResult(int Measure, int OnTime, int OffTime, int Missed, int Wrong)
{
    public double Score => OnTime + OffTime + Missed + Wrong == 0 ? 100 : Math.Round(100 * (OnTime + OffTime * .5) / (OnTime + OffTime + Missed + Wrong), 1);
    public string ScoreText => $"{Score:0}";
    public bool Clean => Missed == 0 && Wrong == 0 && OffTime == 0;
}

/// <summary>Turns the keys judged in an attempt into a measure-by-measure report and the measures worth practicing.</summary>
public static class MeasureReport
{
    public static IReadOnlyList<MeasureResult> Build(IEnumerable<MeasureMark> marks) => marks
        .GroupBy(mark => mark.Measure).OrderBy(group => group.Key)
        .Select(group => new MeasureResult(group.Key, group.Count(m => m.Kind == MeasureMarkKind.OnTime), group.Count(m => m.Kind == MeasureMarkKind.OffTime),
            group.Count(m => m.Kind == MeasureMarkKind.Missed), group.Count(m => m.Kind == MeasureMarkKind.Wrong))).ToArray();

    /// <summary>The weakest measures (below 90 points), worst first.</summary>
    public static IReadOnlyList<MeasureResult> Hardest(IReadOnlyList<MeasureResult> results, int count = 3) =>
        results.Where(r => r.Score < 90).OrderBy(r => r.Score).ThenBy(r => r.Measure).Take(count).ToArray();
}
