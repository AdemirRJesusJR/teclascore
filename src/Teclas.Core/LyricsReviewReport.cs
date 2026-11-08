using System.Text.Json;

namespace Teclas.Core;

/// <summary>A point of the guided review placed on a measure of the generated score.</summary>
public sealed record LyricsReviewItem(double Quarter, int Measure, double Beat, double MeasureStartQuarter, double MeasureEndQuarter, string Kind, string Message);

/// <summary>
/// What the engine reported about the lyrics of a generated score (validacao.json › melody_analysis.lyrics): syllables
/// left out, several words on one note, speech over the instrumental part, or a lyric written by lines because it did
/// not fit the chosen melody. Each becomes a non-blocking point of the guided review. Older reports, or reports without
/// these keys, give no points.
/// </summary>
public static class LyricsReviewReport
{
    public const double Div = 960;
    public const int PointsPerKind = 8;

    public static IReadOnlyList<LyricsReviewItem> Points(string reportPath)
    {
        try
        {
            if (!File.Exists(reportPath)) return [];
            using var document = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            return Points(document.RootElement);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    /// <summary>The review folder's report of a score: "&lt;name&gt; - revisao/validacao.json".</summary>
    public static string ReportPath(string scorePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scorePath))!, Path.GetFileNameWithoutExtension(scorePath) + " - revisao", "validacao.json");

    public static IReadOnlyList<LyricsReviewItem> Points(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("bars", out var barsElement) || barsElement.ValueKind != JsonValueKind.Array) return [];
        var bars = new List<(double Start, double End, int Denominator)>();
        foreach (var bar in barsElement.EnumerateArray())
        {
            if (bar.ValueKind != JsonValueKind.Object || Number(bar, "start") is not double start || Number(bar, "end") is not double end || end <= start) return [];
            int denominator = bar.TryGetProperty("meter", out var meter) && meter.ValueKind == JsonValueKind.Array && meter.GetArrayLength() == 2
                && meter[1].ValueKind == JsonValueKind.Number && meter[1].TryGetInt32(out int d) && d > 0 ? d : 4;
            bars.Add((start, end, denominator));
        }
        if (bars.Count == 0) return [];
        double origin = (Number(root, "origin_removed_quarters") ?? 0) * Div;
        if (!root.TryGetProperty("melody_analysis", out var analysis) || analysis.ValueKind != JsonValueKind.Object
            || !analysis.TryGetProperty("lyrics", out var lyrics) || lyrics.ValueKind != JsonValueKind.Object) return [];

        LyricsReviewItem? Locate(double? quarter, int? measure, string kind, string message)
        {
            int index = measure is int m && m >= 1 && m <= bars.Count ? m - 1 : -1;
            if (index < 0 && quarter is double q) index = bars.FindIndex(b => b.Start <= q * Div - origin && q * Div - origin < b.End);
            if (index < 0) return null;
            var bar = bars[index];
            double start = (bar.Start + origin) / Div, end = (bar.End + origin) / Div;
            double at = Math.Clamp(quarter ?? start, start, end - 1e-6);
            double beat = 1 + (at * Div - origin - bar.Start) / Div / (4.0 / bar.Denominator);
            return new LyricsReviewItem(Math.Round(at, 6), index + 1, Math.Round(beat, 4), start, end, kind, message);
        }

        var points = new List<LyricsReviewItem>();
        string mode = Text(lyrics, "mode") ?? "";
        if (mode == "lines")
        {
            double? estimate = Number(lyrics, "match_estimate") ?? Number(lyrics, "share");
            string share = estimate is double e ? $"Só {e:P0} das sílabas casam com a melodia escolhida: " : "A letra não casa com a melodia escolhida: ";
            if (Locate(null, 1, "lyrics_lines", share + "a letra foi escrita por versos, sob o primeiro compasso de cada um. Escolha a pista do canto para ter a letra sob as notas.") is { } point)
                points.Add(point);
        }
        // Each kind: at most one point per measure, the words of that measure listed.
        void Group(IEnumerable<(double? Quarter, int? Measure, string Text)> items, string kind, Func<int, string, string> message)
        {
            var groups = new List<(LyricsReviewItem Place, List<string> Words)>();
            foreach (var (quarter, measure, text) in items)
            {
                if (Locate(quarter, measure, kind, "") is not { } place) continue;
                var existing = groups.FindIndex(g => g.Place.Measure == place.Measure);
                if (existing < 0) groups.Add((place, text.Length > 0 ? [text] : []));
                else if (text.Length > 0) groups[existing].Words.Add(text);
            }
            for (int i = 0; i < groups.Count && i < PointsPerKind; i++)
            {
                var (place, words) = groups[i];
                string listed = string.Join(", ", words.Take(6).Select(w => $"“{w}”"));
                string text = message(Math.Max(1, words.Count), listed);
                if (i == PointsPerKind - 1 && groups.Count > PointsPerKind) text += $" O mesmo acontece em mais {groups.Count - PointsPerKind} compasso(s).";
                points.Add(place with { Message = text });
            }
        }
        if (mode != "lines")
        {
            Group(Items(lyrics, "dropped"), "lyrics_dropped", (n, words) =>
                $"{n} sílaba(s) da letra sem nota próxima ficaram fora{(words.Length > 0 ? ": " + words : "")}. Confira a melodia e a letra neste compasso.");
            var crowded = Items(lyrics, "crowded").ToList();
            if (crowded.Count == 0 && lyrics.TryGetProperty("crowded_notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                crowded = notes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Number).Select(n => ((double?)((n.GetDouble() + origin) / Div), (int?)null, "")).ToList();
            Group(crowded, "lyrics_crowded", (_, words) =>
                $"Três ou mais palavras na mesma nota{(words.Length > 0 ? " (" + words + ")" : "")}. Confira se a melodia é a voz cantada.");
        }
        Group(Items(lyrics, "spoken"), "lyrics_spoken", (_, words) =>
            $"Trecho falado sobre a parte instrumental{(words.Length > 0 ? ": " + words : "")}. Foi escrito como texto, sem notas; confira ouvindo o compasso.");
        return points.OrderBy(p => p.Quarter).ToArray();
    }

    private static IEnumerable<(double? Quarter, int? Measure, string Text)> Items(JsonElement lyrics, string name)
    {
        if (!lyrics.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            int? measure = item.TryGetProperty("measure", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out int value) ? value : null;
            double? quarter = Number(item, "quarter");
            if (measure is null && quarter is null) continue;
            string text = (Text(item, "text") ?? Text(item, "texto") ?? "").Trim();
            if (text.StartsWith("(falado:", StringComparison.OrdinalIgnoreCase)) text = text["(falado:".Length..].TrimEnd(')').Trim();
            yield return (quarter, measure, text.Length > 40 ? text[..40] + "…" : text);
        }
    }

    private static double? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;

    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
