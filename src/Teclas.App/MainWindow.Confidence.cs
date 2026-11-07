using System.ComponentModel;
using System.IO;
using Teclas.Core;

namespace Teclas.App;

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
        return ScoreConfidence.Rate(new ConfidenceInput(
            row.Chords.Select(c => c.Confidence).ToArray(), row.Chords.Count(c => !string.IsNullOrWhiteSpace(c.Alternatives)),
            row.ReviewPoints.Count, row.ReviewPoints.Count(p => p.Blocking),
            row.AutoMelody, row.AutoMelodyConfident, lyrics, row.IncludeLyrics, row.HasOperationError, row.PageCount,
            row.CatalogLacksMelody && !row.ChordsOnly && !row.UseReviewedProfile));
    }

    internal void RefreshConfidence(MidiRow row) => row.Confidence = RateScore(row);

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
