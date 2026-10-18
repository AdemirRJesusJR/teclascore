namespace Teclas.Core;

public sealed record KaraokeSyllable(int Measure, double Quarter, string Text, bool Continues, int Verse = 1);
/// <summary>A piece of the karaoke line: already sung, being sung now, or still to come.</summary>
public sealed record KaraokePiece(string Text, KaraokeState State);
public enum KaraokeState { Sung, Current, Coming }

/// <summary>
/// Lyrics of a score in singing order (verse 1, top staff) and the line to show at a playback position: the syllables of
/// the current measure and the next one, with the one being sung marked.
/// </summary>
public static class KaraokeLyrics
{
    public static IReadOnlyList<KaraokeSyllable> FromScore(NativeMusicModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var events = model.Measures.SelectMany(m => m.Events).Where(e => (e.Lyric.Length > 0 || e.Lyric2.Length > 0) && e.Quarter is not null).ToArray();
        if (events.Length == 0) return [];
        int top = events.Min(e => e.Staff);
        var ordered = events.Where(e => e.Staff == top).OrderBy(e => e.Measure).ThenBy(e => e.Quarter).ToArray();
        return ordered.Where(e => e.Lyric.Length > 0).Select(e => new KaraokeSyllable(e.Measure, Math.Round(e.Quarter!.Value, 4), e.Lyric, e.LyricContinues))
            .Concat(ordered.Where(e => e.Lyric2.Length > 0).Select(e => new KaraokeSyllable(e.Measure, Math.Round(e.Quarter!.Value, 4), e.Lyric2, e.Lyric2Continues, 2)))
            .OrderBy(s => s.Verse).ThenBy(s => s.Measure).ThenBy(s => s.Quarter).ToArray();
    }

    /// <summary>
    /// The syllables sung on a given pass: measures that have a verse 2 use it from the second time on; every other measure
    /// keeps verse 1.
    /// </summary>
    public static IReadOnlyList<KaraokeSyllable> ForPass(IReadOnlyList<KaraokeSyllable> syllables, int pass)
    {
        var second = syllables.Where(s => s.Verse == 2).Select(s => s.Measure).ToHashSet();
        int verse = pass >= 2 ? 2 : 1;
        return syllables.Where(s => s.Verse == (second.Contains(s.Measure) ? verse : 1)).OrderBy(s => s.Measure).ThenBy(s => s.Quarter).ToArray();
    }

    /// <summary>
    /// The lyrics printed on each page, for a lyrics screen: syllables joined into words, one text line per system of the
    /// score (measures at the same height on a page). Index 0 is page 1; a page without lyrics is an empty string.
    /// </summary>
    public static IReadOnlyList<string> PageTexts(IReadOnlyList<KaraokeSyllable> syllables, IReadOnlyList<ScoreMeasureRegion> measures)
    {
        var where = measures.GroupBy(m => m.Measure).ToDictionary(g => g.Key, g => g.First().Bounds);
        int pages = measures.Count == 0 ? 0 : measures.Max(m => m.Bounds.Page);
        var first = Texts(syllables.Where(s => s.Verse == 1).ToArray(), where, pages);
        var second = Texts(syllables.Where(s => s.Verse == 2).ToArray(), where, pages);
        // A second verse on the page follows the first, after a blank line.
        return first.Zip(second, (a, b) => b.Length == 0 ? a : a.Length == 0 ? b : a + "\n\n" + b).ToArray();
    }

    private static IReadOnlyList<string> Texts(IReadOnlyList<KaraokeSyllable> syllables, Dictionary<int, ScorePageRegion> where, int pages)
    {
        var texts = Enumerable.Range(0, pages).Select(_ => new System.Text.StringBuilder()).ToArray();
        ScorePageRegion? previous = null; bool joined = false;
        foreach (var s in syllables)
        {
            if (!where.TryGetValue(s.Measure, out var bounds) || bounds.Page < 1 || bounds.Page > pages) continue;
            var text = texts[bounds.Page - 1];
            bool newLine = previous is not null && previous.Page == bounds.Page && Math.Abs(previous.Y - bounds.Y) > .01;
            if (newLine) { text.Append(joined ? "-\n" : "\n"); joined = false; }
            else if (text.Length > 0 && !joined) text.Append(' ');
            text.Append(s.Text); joined = s.Continues; previous = bounds;
        }
        return texts.Select(t => t.ToString().Replace(" \n", "\n").Trim()).ToArray();
    }

    /// <summary>Index of the syllable being sung at a position (the last one starting at or before it), or -1 before the first.</summary>
    public static int CurrentIndex(IReadOnlyList<KaraokeSyllable> syllables, int measure, double quarter)
    {
        int found = -1;
        for (int i = 0; i < syllables.Count; i++)
        {
            var s = syllables[i];
            if (s.Measure < measure || s.Measure == measure && s.Quarter <= quarter + 1e-6) found = i; else break;
        }
        return found;
    }

    /// <summary>The line around a position: syllables of the current and next measure that have lyrics, joined into words.</summary>
    public static IReadOnlyList<KaraokePiece> Line(IReadOnlyList<KaraokeSyllable> syllables, int measure, double quarter)
    {
        if (syllables.Count == 0) return [];
        int current = CurrentIndex(syllables, measure, quarter);
        int anchor = current >= 0 ? syllables[current].Measure : syllables[0].Measure;
        var measures = syllables.Select(s => s.Measure).Where(m => m >= anchor).Distinct().Take(2).ToHashSet();
        var pieces = new List<KaraokePiece>();
        for (int i = 0; i < syllables.Count; i++)
        {
            if (!measures.Contains(syllables[i].Measure)) continue;
            var state = i < current ? KaraokeState.Sung : i == current ? KaraokeState.Current : KaraokeState.Coming;
            pieces.Add(new(syllables[i].Text + (syllables[i].Continues ? "-" : " "), state));
        }
        return pieces;
    }
}
