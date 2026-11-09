using System.Text.Json;

namespace Teclas.Core;

/// <summary>
/// What the engine saved next to a generated score (its "- revisao/validacao.json") that the review needs: harmonic
/// warnings, the notation quality issues and the measures where the melody is silent. Older scores have none of it.
/// </summary>
public sealed record ScoreValidationReport(IReadOnlyList<string> HarmonicWarnings, int? QualityScore, IReadOnlyList<string> QualityIssues,
    IReadOnlyList<int> EmptyMeasures, int MeasureCount)
{
    public static ScoreValidationReport Empty { get; } = new([], null, [], [], 0);
    /// <summary>The engine's tick division (quarters × 960) used by "bars" and "notes".</summary>
    public const double Division = 960;
    /// <summary>Quarters removed from the start of the MIDI (empty measures of preparation): score position = MIDI quarter − this.</summary>
    public double OriginQuarters { get; init; }
    /// <summary>Each score measure as (start, end) in quarters from the score start.</summary>
    public IReadOnlyList<(double Start, double End)> Bars { get; init; } = [];

    /// <summary>The score measure (1-based) that contains a position given in MIDI quarters (chords of the analysis); null outside the grid.</summary>
    public int? MeasureAtMidiQuarter(double midiQuarter)
    {
        if (!double.IsFinite(midiQuarter) || Bars.Count == 0) return null;
        double position = midiQuarter - OriginQuarters;
        for (int i = 0; i < Bars.Count; i++) if (Bars[i].Start <= position && position < Bars[i].End) return i + 1;
        return position >= Bars[^1].End ? Bars.Count : null;
    }

    /// <summary>The report folder of a score: "Nome - revisao" beside the .mscz.</summary>
    public static string ReportPath(string scorePath) =>
        Path.Combine(Path.GetDirectoryName(scorePath) ?? "", Path.GetFileNameWithoutExtension(scorePath) + " - revisao", "validacao.json");

    /// <summary>Reads the report of a score; missing or unreadable files give <see cref="Empty"/>.</summary>
    public static ScoreValidationReport Read(string? scorePath)
    {
        if (string.IsNullOrWhiteSpace(scorePath)) return Empty;
        string path = ReportPath(scorePath);
        if (!File.Exists(path)) return Empty;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return Parse(document.RootElement);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return Empty; }
    }

    public static ScoreValidationReport Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return Empty;
        var warnings = Strings(root, "harmonic_warnings");
        int? score = null; IReadOnlyList<string> issues = [];
        if (root.TryGetProperty("quality", out var quality) && quality.ValueKind == JsonValueKind.Object)
        {
            if (quality.TryGetProperty("score", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) score = number;
            issues = Strings(quality, "issues");
        }
        // Measures (1-based) where no melody note sounds: "bars" give the grid, "notes" the melody line.
        var empty = new List<int>(); int measures = 0; var quarters = new List<(double Start, double End)>();
        double origin = Number(root, "origin_removed_quarters"); if (!double.IsFinite(origin)) origin = 0;
        if (root.TryGetProperty("bars", out var bars) && bars.ValueKind == JsonValueKind.Array)
        {
            var grid = bars.EnumerateArray().Select(bar => (Start: Number(bar, "start"), End: Number(bar, "end"))).Where(bar => bar.End > bar.Start).ToArray();
            measures = grid.Length;
            quarters.AddRange(grid.Select(bar => (bar.Start / Division, bar.End / Division)));
            var sounding = new bool[grid.Length];
            if (root.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                foreach (var note in notes.EnumerateArray())
                {
                    double start = Number(note, "start"), end = Number(note, "end");
                    if (end <= start) continue;
                    for (int i = 0; i < grid.Length; i++) if (grid[i].Start < end && start < grid[i].End) sounding[i] = true;
                }
            for (int i = 0; i < grid.Length; i++) if (!sounding[i]) empty.Add(i + 1);
        }
        return new(warnings, score, issues, empty, measures) { OriginQuarters = origin, Bars = quarters };
    }

    /// <summary>Consecutive empty measures as ranges, leaving out the intro and the ending (which are usually instrumental).</summary>
    public IReadOnlyList<(int From, int To)> EmptyRanges()
    {
        var ranges = new List<(int From, int To)>();
        int? from = null, previous = null;
        foreach (int measure in EmptyMeasures.OrderBy(m => m))
        {
            if (from is null) { from = previous = measure; continue; }
            if (measure == previous + 1) { previous = measure; continue; }
            ranges.Add((from.Value, previous!.Value)); from = previous = measure;
        }
        if (from is not null) ranges.Add((from.Value, previous!.Value));
        return ranges.Where(range => range.From > 1 && (MeasureCount == 0 || range.To < MeasureCount)).ToArray();
    }

    private static IReadOnlyList<string> Strings(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").Where(text => text.Length > 0).ToArray()
            : [];

    private static double Number(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : double.NaN;
}
