using System.ComponentModel;
using System.IO;
using Teclas.Core;

namespace Teclas.App;

internal sealed record NotationQuality(int Score, IReadOnlyList<string> Issues, IReadOnlyList<string>? Notes = null);

/// <summary>The confidence rating of each generated score, and the library order that puts the riskiest first.</summary>
public partial class MainWindow
{
    /// <summary>Rates one song from its chords, review points, melody choice, lyrics fit and errors.</summary>
    internal ConfidenceRating? RateScore(MidiRow row)
    {
        if (row.ScorePath is null || !File.Exists(row.ScorePath)) return null;
        double? lyrics = null;
        if (row.IncludeLyrics && row.Document is not null)
        {
            var coverage = LyricsCoverageOf(row);
            var chosen = row.Tracks.Where(t => t.IsMelody).Select(t => t.Source.Key).ToHashSet();
            if (coverage.Count > 0) lyrics = coverage.Where(c => chosen.Contains(c.Source)).Select(c => c.Share).DefaultIfEmpty(0).Max();
        }
        var notation = NotationQualityOf(row.ScorePath);
        return ScoreConfidence.Rate(new ConfidenceInput(
            row.Chords.Select(c => c.Confidence).ToArray(), row.Chords.Count(c => !string.IsNullOrWhiteSpace(c.Alternatives)),
            row.ReviewPoints.Count, row.ReviewPoints.Count(p => p.Blocking),
            row.AutoMelody, row.AutoMelodyConfident, lyrics, row.IncludeLyrics, row.HasOperationError, row.PageCount,
            row.CatalogLacksMelody && !row.ChordsOnly && !row.UseReviewedProfile,
            notation?.Score, notation?.Issues, row.MelodyFromRecording, notation?.Notes));
    }

    /// <summary>The last rating of each card and the state it was computed from; it is reused until the song changes.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MidiRow, Tuple<string, ConfidenceRating?>> confidenceCache = new();
    internal int ConfidenceComputations { get; private set; }

    /// <summary>Everything the rating reads from a card (score, chords, review points, melody, lyrics, errors).</summary>
    private static string ConfidenceKey(MidiRow row)
    {
        var hash = new HashCode();
        foreach (var chord in row.Chords) { hash.Add(chord.Confidence); hash.Add(string.IsNullOrWhiteSpace(chord.Alternatives)); }
        foreach (var point in row.ReviewPoints) hash.Add(point.Blocking);
        foreach (var track in row.Tracks) if (track.IsMelody) hash.Add(track.Source.Key);
        string? score = row.ScorePath;
        return string.Join("|", score, score is not null && File.Exists(score), row.GeneratedAt?.UtcTicks, row.PageCount, row.Chords.Count, row.ReviewPoints.Count,
            row.AutoMelody, row.AutoMelodyConfident, row.IncludeLyrics, row.HasOperationError, row.CatalogLacksMelody, row.ChordsOnly,
            row.UseReviewedProfile, row.MelodyFromRecording, row.Document?.Sha256, hash.ToHashCode());
    }

    private readonly Dictionary<string, (DateTime Stamp, NotationQuality? Quality)> notationQuality = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The engine's notation check saved with the score (its review folder's validacao.json); null for older scores.</summary>
    internal NotationQuality? NotationQualityOf(string scorePath)
    {
        try
        {
            string report = Path.Combine(Path.GetDirectoryName(scorePath)!, Path.GetFileNameWithoutExtension(scorePath) + " - revisao", "validacao.json");
            if (!File.Exists(report)) return null;
            var stamp = File.GetLastWriteTimeUtc(report);
            if (notationQuality.TryGetValue(report, out var cached) && cached.Stamp == stamp) return cached.Quality;
            NotationQuality? quality = null;
            using (var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(report)))
                if (document.RootElement.TryGetProperty("quality", out var element) && element.TryGetProperty("score", out var score) && score.TryGetInt32(out int value))
                    quality = new(value, Strings(element, "issues"), Strings(element, "notes"));
            notationQuality[report] = (stamp, quality);
            return quality;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { return null; }
    }

    private static string[] Strings(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array
            ? items.EnumerateArray().Select(i => i.GetString() ?? "").Where(i => i.Length > 0).ToArray() : [];

    internal void RefreshConfidence(MidiRow row)
    {
        string key = ConfidenceKey(row);
        if (confidenceCache.TryGetValue(row, out var cached) && cached.Item1 == key) { row.Confidence = cached.Item2; return; }
        var rating = RateScore(row); ConfidenceComputations++;
        confidenceCache.AddOrUpdate(row, Tuple.Create(key, rating));
        row.Confidence = rating;
    }

    internal void RefreshAllConfidence() { foreach (var row in Files) RefreshConfidence(row); }

    /// <summary>"Revisar primeiro" sorts the cards by rating (lowest first); every other list keeps the library order.</summary>
    private bool reviewSortActive;

    private void ApplyReviewSort()
    {
        if (LibraryView is null) return;
        bool review = settings.SmartList == "revisar-primeiro";
        if (review == reviewSortActive) return;
        reviewSortActive = review;
        if (review) RefreshAllConfidence();
        LibraryView.Refresh();
    }

    /// <summary>Status groups in order; inside a group, the library order (or the rating, lowest first, for "Revisar primeiro").</summary>
    private sealed class LibraryOrder(MainWindow window) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not MidiRow a || y is not MidiRow b) return 0;
            int order = a.StatusGroupOrder.CompareTo(b.StatusGroupOrder);
            if (order != 0) return order;
            if (window.reviewSortActive && a.ReviewOrder != b.ReviewOrder) return a.ReviewOrder.CompareTo(b.ReviewOrder);
            return window.Files.IndexOf(a).CompareTo(window.Files.IndexOf(b));
        }
    }
}
