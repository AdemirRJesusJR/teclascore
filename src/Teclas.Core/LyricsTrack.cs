using System.Text.RegularExpressions;

namespace Teclas.Core;

/// <summary>How many syllables of the MIDI's lyrics fall on notes of a source (track, channel, instrument).</summary>
public sealed record LyricsCoverage(SourceKey Source, int Hits, int Syllables)
{
    public double Share => Syllables == 0 ? 0 : (double)Hits / Syllables;
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
    public static IReadOnlyList<long> Syllables(MidiDocument midi)
    {
        bool Usable(MidiTextEvent e) => e.Text.Trim('/', '\\', ' ').Length > 0 && !Credit().IsMatch(e.Text) && !e.Text.TrimStart().StartsWith('@');
        var lyrics = midi.TextEvents.Where(e => e.Type == 5 && Usable(e)).ToArray();
        if (lyrics.Length < 16) lyrics = midi.TextEvents.Where(e => e.Type == 1 && Usable(e)).ToArray();
        return lyrics.Length < 16 ? [] : lyrics.Select(e => e.Tick).Order().ToArray();
    }

    /// <summary>Coverage of every pitched source, best first; empty when the MIDI has no lyrics.</summary>
    public static IReadOnlyList<LyricsCoverage> Coverage(MidiDocument midi)
    {
        var syllables = Syllables(midi);
        if (syllables.Count == 0) return [];
        long tolerance = Math.Max(1, midi.Ppq / 8);
        return midi.Notes.Where(n => n.Channel != 10).GroupBy(n => new SourceKey(n.Track, n.Channel, n.Program))
            .Select(group =>
            {
                var starts = group.Select(n => n.Start).Order().ToArray();
                int hits = syllables.Count(tick =>
                {
                    int index = Array.BinarySearch(starts, tick - tolerance); if (index < 0) index = ~index;
                    return index < starts.Length && Math.Abs(starts[index] - tick) <= tolerance;
                });
                return new LyricsCoverage(group.Key, hits, syllables.Count);
            })
            .OrderByDescending(c => c.Hits).ToArray();
    }
}
