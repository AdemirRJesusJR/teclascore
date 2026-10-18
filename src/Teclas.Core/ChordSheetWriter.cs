using System.Text;

namespace Teclas.Core;

/// <summary>
/// A score as a text chord sheet, the way chord websites print it: each chord above the syllable where it starts, a few
/// measures per line. Lines without words show the chords measure by measure ("C  G7 | F | C"). Verse 2 of a repeat
/// follows as its own block. Monospaced fonts keep the chords aligned. ToChordPro writes the same sheet in the ChordPro
/// format (chords in brackets inside the words) that apps such as OnSong and SongBook import.
/// </summary>
public static class ChordSheetWriter
{
    public const int MeasuresPerLine = 4;

    public static string FromScore(NativeMusicModel model, string title) => Build(model, title, "", false);

    public static string ToChordPro(NativeMusicModel model, string title, string artist) => Build(model, title, artist, true);

    private static string Build(NativeMusicModel model, string title, string artist, bool pro)
    {
        ArgumentNullException.ThrowIfNull(model);
        var syllables = KaraokeLyrics.FromScore(model);
        int top = model.Measures.Count == 0 ? 1 : model.Measures.Min(m => m.Staff);
        var harmonies = model.Measures.Where(m => m.Staff == top).SelectMany(m => m.Harmonies)
            .Where(h => h.Quarter is not null).OrderBy(h => h.Measure).ThenBy(h => h.Quarter).ToArray();
        int last = model.Measures.Count == 0 ? 0 : model.Measures.Max(m => m.Number);
        var text = new StringBuilder();
        if (pro)
        {
            if (!string.IsNullOrWhiteSpace(title)) text.AppendLine("{title: " + title.Trim() + "}");
            if (!string.IsNullOrWhiteSpace(artist)) text.AppendLine("{artist: " + artist.Trim() + "}");
            text.AppendLine();
        }
        else if (!string.IsNullOrWhiteSpace(title)) text.AppendLine(title.Trim()).AppendLine();
        AppendVerse(text, harmonies, syllables.Where(s => s.Verse == 1).ToArray(), 1, last, pro);
        var second = syllables.Where(s => s.Verse == 2).ToArray();
        if (second.Length > 0)
        {
            // Only the repeated measures have a second verse; their chords are the same.
            int from = second.Min(s => s.Measure), to = second.Max(s => s.Measure);
            text.AppendLine().AppendLine(pro ? "{comment: 2ª vez}" : "2ª vez:");
            AppendVerse(text, harmonies.Where(h => h.Measure >= from && h.Measure <= to).ToArray(), second, from, to, pro);
        }
        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendVerse(StringBuilder text, IReadOnlyList<MusicHarmony> harmonies, IReadOnlyList<KaraokeSyllable> syllables, int first, int last, bool pro)
    {
        // Sung stretches and instrumental stretches (no words) get lines of their own, up to four measures each.
        var sung = syllables.Select(s => s.Measure).ToHashSet();
        for (int start = first; start <= last;)
        {
            bool words = sung.Contains(start);
            int end = start;
            while (end < last && end - start + 1 < MeasuresPerLine && sung.Contains(end + 1) == words) end++;
            var chords = harmonies.Where(h => h.Measure >= start && h.Measure <= end).ToArray();
            if (!words) { if (chords.Length > 0) text.AppendLine(pro ? ChordsOnly(chords, start, end, name => "[" + name + "]") : ChordsOnly(chords, start, end)); }
            else if (pro) text.AppendLine(Inline(chords, syllables.Where(s => s.Measure >= start && s.Measure <= end).ToArray()));
            else
            {
                var (chordLine, lyricLine) = Aligned(chords, syllables.Where(s => s.Measure >= start && s.Measure <= end).ToArray());
                if (chordLine.Trim().Length > 0) text.AppendLine(chordLine.TrimEnd());
                text.AppendLine(lyricLine.TrimEnd());
            }
            start = end + 1;
        }
    }

    private static string Name(MusicHarmony harmony) => harmony.Root + harmony.Quality + (harmony.Bass is { } bass ? "/" + bass : "");

    private static string ChordsOnly(IReadOnlyList<MusicHarmony> chords, int start, int end, Func<string, string>? format = null) =>
        string.Join(" | ", Enumerable.Range(start, end - start + 1)
            .Select(measure => string.Join("  ", chords.Where(c => c.Measure == measure).Select(c => format is null ? Name(c) : format(Name(c)))))
            .Select(measure => measure.Length == 0 ? "%" : measure));

    /// <summary>ChordPro line: each chord in brackets right before the syllable where it starts.</summary>
    private static string Inline(IReadOnlyList<MusicHarmony> chords, IReadOnlyList<KaraokeSyllable> words)
    {
        var line = new StringBuilder(); int next = 0; bool joined = false;
        foreach (var word in words)
        {
            if (line.Length > 0 && !joined) line.Append(' ');
            while (next < chords.Count && (chords[next].Measure < word.Measure || chords[next].Measure == word.Measure && chords[next].Quarter <= word.Quarter + 1e-6))
                line.Append('[').Append(Name(chords[next++])).Append(']');
            line.Append(word.Text); joined = word.Continues;
        }
        while (next < chords.Count) line.Append(" [").Append(Name(chords[next++])).Append(']');
        return line.ToString();
    }

    /// <summary>Chord line and lyric line: each chord over the first syllable at or after it, never overlapping.</summary>
    private static (string Chords, string Lyrics) Aligned(IReadOnlyList<MusicHarmony> chords, IReadOnlyList<KaraokeSyllable> words)
    {
        var chordLine = new StringBuilder(); var lyricLine = new StringBuilder();
        var pending = new List<string>(); int next = 0; bool joined = false;
        void Place(int column)
        {
            foreach (string name in pending)
            {
                int at = Math.Max(column, chordLine.Length == 0 ? 0 : chordLine.Length + 1);
                chordLine.Append(' ', at - chordLine.Length).Append(name);
                column = at + name.Length + 1;
            }
            pending.Clear();
        }
        foreach (var word in words)
        {
            while (next < chords.Count && (chords[next].Measure < word.Measure || chords[next].Measure == word.Measure && chords[next].Quarter <= word.Quarter + 1e-6))
                pending.Add(Name(chords[next++]));
            if (lyricLine.Length > 0 && !joined) lyricLine.Append(' ');
            if (pending.Count > 0)
            {
                // Make room in the words when the chords before would overlap this one.
                int needed = chordLine.Length == 0 ? 0 : chordLine.Length + 1;
                if (needed > lyricLine.Length) lyricLine.Append(joined ? '-' : ' ', needed - lyricLine.Length);
                Place(lyricLine.Length);
            }
            lyricLine.Append(word.Text); joined = word.Continues;
        }
        while (next < chords.Count) pending.Add(Name(chords[next++]));
        if (pending.Count > 0) Place(Math.Max(lyricLine.Length + 1, chordLine.Length + 1));
        return (chordLine.ToString(), lyricLine.ToString());
    }
}
