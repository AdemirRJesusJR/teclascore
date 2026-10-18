using System.Text;
using System.Text.RegularExpressions;

namespace Teclas.Core;

/// <summary>One chord placed in the music, in quarters from the start of the MIDI.</summary>
public sealed record ChartChord(double Quarter, string Root, string Quality, string? Bass);

/// <summary>
/// The chords of a song as typed text, one measure between bars: "| C | G/B | Am F | % |". Several chords in a measure
/// share it evenly ("Am F" = beats 1 and 3); "." holds a slot ("C . . G" = beats 1 and 4); "%" repeats the previous
/// measure; an empty measure keeps the chord that is sounding. Suffixes may be written in any known notation (7M, ø, °).
/// </summary>
public static partial class ChordChartText
{
    [GeneratedRegex(@"^([A-G](?:#|b)?)([^/]*)(?:/([A-G](?:#|b)?))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Symbol();

    /// <summary>Measure start (in quarters) of the first measure that has notes, like the score generator.</summary>
    public static double Origin(double firstNoteQuarter, double measureQuarters) => Math.Floor(firstNoteQuarter / measureQuarters + 1e-9) * measureQuarters;

    public static string Format(IReadOnlyList<ChartChord> chords, double measureQuarters, double origin, int measuresPerLine = 4)
    {
        if (chords.Count == 0) return "| |";
        int last = (int)Math.Floor((chords.Max(c => c.Quarter) - origin) / measureQuarters + 1e-9);
        var text = new StringBuilder();
        for (int bar = 0; bar <= last; bar++)
        {
            double start = origin + bar * measureQuarters;
            var inside = chords.Where(c => c.Quarter >= start - 1e-6 && c.Quarter < start + measureQuarters - 1e-6).OrderBy(c => c.Quarter).ToList();
            text.Append("| ");
            if (inside.Count > 0)
            {
                // Slots of the smallest spacing used, so "C . G ." keeps positions that are not evenly spread.
                int slots = inside.Count == 1 && Math.Abs(inside[0].Quarter - start) < 1e-6 ? 1 : SlotsFor(inside.Select(c => c.Quarter - start), measureQuarters);
                var words = Enumerable.Repeat(".", slots).ToArray();
                foreach (var chord in inside) words[Math.Clamp((int)Math.Round((chord.Quarter - start) / measureQuarters * slots), 0, slots - 1)] = Name(chord);
                text.Append(string.Join(' ', words)).Append(' ');
            }
            if ((bar + 1) % measuresPerLine == 0 && bar < last) text.Append("|\n");
        }
        return text.Append('|').ToString();
    }

    private static int SlotsFor(IEnumerable<double> offsets, double measure)
    {
        foreach (int slots in new[] { 1, 2, 4, 8 })
            if (offsets.All(o => Math.Abs(o / measure * slots - Math.Round(o / measure * slots)) < 1e-6)) return slots;
        return 8;
    }

    public static string Name(ChartChord chord) => ChordNotation.Root(chord.Root) + ChordNotation.Display(chord.Quality) + (chord.Bass is { } bass ? "/" + ChordNotation.Root(bass) : "");

    /// <summary>Reads typed measures back into chords; returns the problems found (empty when the text is fine).</summary>
    public static (IReadOnlyList<ChartChord> Chords, IReadOnlyList<string> Errors) Parse(string text, double measureQuarters, double origin)
    {
        var chords = new List<ChartChord>(); var errors = new List<string>();
        // A bar closing one line and a bar opening the next are the same bar line.
        string joined = Regex.Replace((text ?? "").Replace("\r", ""), @"\|[ \t]*\n[ \t]*\|", "|");
        var measures = joined.Replace("\n", " ").Split('|').Select(m => m.Trim()).ToList();
        // Leading and trailing bars make empty pieces at the ends.
        if (measures.Count > 0 && measures[0].Length == 0) measures.RemoveAt(0);
        if (measures.Count > 0 && measures[^1].Length == 0) measures.RemoveAt(measures.Count - 1);
        string[] previous = [];
        for (int bar = 0; bar < measures.Count; bar++)
        {
            var words = measures[bar].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 1 && words[0] == "%") words = previous;
            if (words.Length > 8) { errors.Add($"Compasso {bar + 1}: no máximo 8 posições por compasso."); continue; }
            double start = origin + bar * measureQuarters;
            for (int slot = 0; slot < words.Length; slot++)
            {
                if (words[slot] == ".") continue;
                var match = Symbol().Match(words[slot]);
                string quality = match.Success ? ChordNotation.Canonical(match.Groups[2].Value) : "";
                if (!match.Success || !KeyboardDiagramVoicing.SupportsQuality(quality))
                { errors.Add($"Compasso {bar + 1}: \"{words[slot]}\" não é uma cifra conhecida (use, por exemplo, C, Am7, G/B, F7M, Bm7(b5))."); continue; }
                chords.Add(new ChartChord(start + slot * measureQuarters / words.Length, match.Groups[1].Value, quality, match.Groups[3].Success ? match.Groups[3].Value : null));
            }
            previous = words;
        }
        // The same chord again right after itself is only a reminder: keep the first.
        var cleaned = new List<ChartChord>();
        foreach (var chord in chords)
            if (cleaned.Count == 0 || cleaned[^1] with { Quarter = chord.Quarter } != chord) cleaned.Add(chord);
        return (cleaned, errors);
    }
}
