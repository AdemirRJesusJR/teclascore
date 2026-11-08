using System.IO;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// After generating, the engine's report on the lyrics (syllables left out, crowded words, speech, lyric by lines) joins
/// the guided review as non-blocking points; a point the analysis already gave for the same measure is not repeated.
/// </summary>
public partial class MainWindow
{
    internal int AddLyricsReviewPoints(MidiRow row)
    {
        if (row.UsesNativeScoreWorkflow || row.ScorePath is not { } score || !File.Exists(score)) return 0;
        int added = 0;
        foreach (var item in LyricsReviewReport.Points(LyricsReviewReport.ReportPath(score)))
        {
            if (row.ReviewPoints.Any(p => p.Measure == item.Measure && (p.Kind == item.Kind || p.Kind == "lyrics_dropped" && item.Kind == "lyrics_lines"))) continue;
            row.ReviewPoints.Add(new ScoreReviewPoint
            {
                Quarter = item.Quarter, Measure = item.Measure, Beat = item.Beat, MeasureStartQuarter = item.MeasureStartQuarter,
                MeasureEndQuarter = item.MeasureEndQuarter, Kind = item.Kind, Message = item.Message, Blocking = false
            });
            added++;
        }
        if (added > 0 && ReferenceEquals(row, selectedFile)) { Notify(nameof(ReviewPoints)); Notify(nameof(CanReviewScore)); }
        return added;
    }
}
