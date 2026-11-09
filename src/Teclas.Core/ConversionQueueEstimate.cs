namespace Teclas.Core;

/// <summary>
/// "Termina em ~M min" for the conversion queue: the median of the generation times already recorded in the local
/// performance diagnostics (engine analysis + generation of one song), spread over the simultaneous conversions.
/// It is an estimate for planning a batch, never a promise; without history it falls back to a typical duration.
/// </summary>
public static class ConversionQueueEstimate
{
    /// <summary>Used when no generation was measured yet on this computer (about a minute and a half per song).</summary>
    public const double DefaultSecondsPerSong = 90;

    /// <summary>Seconds one song takes from the recorded history: median generation plus median analysis (each when present).</summary>
    public static double SecondsPerSong(IEnumerable<PerformanceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = records.Where(r => r.Outcome == PerformanceOutcome.Success && r.ProcessKind == PerformanceProcessKind.Worker).ToArray();
        double? generate = Median(list.Where(r => r.Operation == PerformanceOperation.EngineGenerate).Select(r => r.DurationMilliseconds / 1000));
        double? analyze = Median(list.Where(r => r.Operation == PerformanceOperation.EngineAnalyze).Select(r => r.DurationMilliseconds / 1000));
        if (generate is null) return DefaultSecondsPerSong;
        return Math.Clamp(generate.Value + (analyze ?? 0), 1, 3600);
    }

    /// <summary>Seconds until the queue ends: the remaining songs (waiting and running) over the lanes, times the median per song.</summary>
    public static double RemainingSeconds(double secondsPerSong, int remaining, int parallel)
    {
        if (remaining <= 0) return 0;
        int lanes = Math.Max(1, parallel);
        return Math.Ceiling(remaining / (double)lanes) * Math.Max(1, secondsPerSong);
    }

    /// <summary>"termina em ~3 min", "termina em menos de 1 min" or "termina em ~1 h 20 min".</summary>
    public static string Describe(double remainingSeconds)
    {
        if (!double.IsFinite(remainingSeconds) || remainingSeconds <= 0) return "";
        if (remainingSeconds < 60) return "termina em menos de 1 min";
        int minutes = (int)Math.Ceiling(remainingSeconds / 60);
        if (minutes < 60) return $"termina em ~{minutes} min";
        int hours = minutes / 60, rest = minutes % 60;
        return rest == 0 ? $"termina em ~{hours} h" : $"termina em ~{hours} h {rest} min";
    }

    /// <summary>"3 em andamento · 12 de 40 · termina em ~9 min" (the parts that apply).</summary>
    public static string Strip(int running, int done, int total, double remainingSeconds)
    {
        var parts = new List<string>();
        if (running > 0) parts.Add(running == 1 ? "1 em andamento" : $"{running} em andamento");
        if (total > 0) parts.Add($"{done} de {total}");
        string eta = Describe(remainingSeconds);
        if (eta.Length > 0 && done < total) parts.Add(eta);
        return string.Join(" · ", parts);
    }

    internal static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Where(v => v >= 0).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return null;
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
