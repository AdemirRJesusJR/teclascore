using System.Text.RegularExpressions;

namespace Teclas.Core;

/// <summary>How many syllables of the MIDI's lyrics fall on notes of a source (track, channel, instrument).</summary>
public sealed record LyricsCoverage(SourceKey Source, int Hits, int Syllables)
{
    public double Share => Syllables == 0 ? 0 : (double)Hits / Syllables;
    /// <summary>
    /// Of the source's attacks while the lyrics are sung, the share that falls on a syllable. The sung line is close to
    /// one attack per syllable; an accompaniment that plays on every eighth also covers the words, but with many attacks
    /// between them.
    /// </summary>
    public double Precision { get; init; }
    /// <summary>Mean distance, in quarters, between a covered syllable and its attack: the sung line starts with the word.</summary>
    public double Offset { get; init; }
}

/// <summary>
/// Finds the part that carries the sung melody in a karaoke MIDI: the source whose note attacks coincide with the most
/// syllables (within a 32nd note). Credits such as e-mails or web addresses in the text events are not syllables.
/// </summary>
public static partial class LyricsTrack
{
    [GeneratedRegex(@"@|https?:|www\.|\.com\b|\.br\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Credit();

    /// <summary>Ticks of the syllables: lyric events (type 5), else karaoke text events (type 1, "@" headers left out).</summary>
    public static IReadOnlyList<long> Syllables(MidiDocument midi) => SyllableEvents(midi).Select(e => e.Tick).Order().ToArray();

    /// <summary>The text events that hold the syllables, in time order (same choice as <see cref="Syllables"/>).</summary>
    public static IReadOnlyList<MidiTextEvent> SyllableEvents(MidiDocument midi)
    {
        bool Usable(MidiTextEvent e) => e.Text.Trim('/', '\\', ' ').Length > 0 && !Credit().IsMatch(e.Text) && !e.Text.TrimStart().StartsWith('@');
        var lyrics = midi.TextEvents.Where(e => e.Type == 5 && Usable(e)).ToArray();
        if (lyrics.Length < 16) lyrics = midi.TextEvents.Where(e => e.Type == 1 && Usable(e)).ToArray();
        return lyrics.Length < 16 ? [] : lyrics.OrderBy(e => e.Tick).ToArray();
    }

    /// <summary>Coverage of every pitched source, best first; empty when the MIDI has no lyrics.</summary>
    public static IReadOnlyList<LyricsCoverage> Coverage(MidiDocument midi)
    {
        var syllables = Syllables(midi).ToArray();
        if (syllables.Length == 0) return [];
        return midi.Notes.Where(n => n.Channel != 10).GroupBy(n => new SourceKey(n.Track, n.Channel, n.Program))
            .Select(group => Measure(midi.Ppq, syllables, group.Key, group.Select(n => n.Start)))
            .OrderByDescending(c => c.Hits).ToArray();
    }

    /// <summary>Coverage of several sources played together (the melody chosen from more than one track); null without lyrics.</summary>
    public static LyricsCoverage? CoverageOf(MidiDocument midi, IEnumerable<SourceKey> sources)
    {
        var keys = sources.ToHashSet();
        var syllables = Syllables(midi).ToArray();
        if (syllables.Length == 0 || keys.Count == 0) return null;
        var starts = midi.Notes.Where(n => keys.Contains(new SourceKey(n.Track, n.Channel, n.Program))).Select(n => n.Start);
        return Measure(midi.Ppq, syllables, keys.First(), starts);
    }

    private static LyricsCoverage Measure(int ppq, long[] syllables, SourceKey key, IEnumerable<long> noteStarts)
    {
        long tolerance = Math.Max(1, ppq / 8);
        var starts = noteStarts.Distinct().Order().ToArray();
        long Distance(long[] sorted, long tick)
        {
            int index = Array.BinarySearch(sorted, tick - tolerance); if (index < 0) index = ~index;
            return index < sorted.Length ? Math.Abs(sorted[index] - tick) : long.MaxValue;
        }
        bool Near(long[] sorted, long tick) => Distance(sorted, tick) <= tolerance;
        var distances = syllables.Select(tick => Distance(starts, tick)).Where(d => d <= tolerance).ToArray();
        int hits = distances.Length;
        // Notes started within a 64th of each other are one attack (a chord, or a humanized double).
        var attacks = new List<long>();
        foreach (long start in starts) if (attacks.Count == 0 || start - attacks[^1] > Math.Max(1, ppq / 16)) attacks.Add(start);
        long first = syllables[0] - ppq, last = syllables[^1] + ppq;
        var sung = attacks.Where(a => a >= first && a <= last).ToArray();
        double precision = sung.Length == 0 ? 0 : sung.Count(a => Near(syllables, a)) / (double)sung.Length;
        return new LyricsCoverage(key, hits, syllables.Length) { Precision = precision, Offset = hits == 0 ? 0 : distances.Average() / ppq };
    }
}
