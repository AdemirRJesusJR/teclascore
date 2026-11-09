using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>
/// The highlighted keyboard diagram ("destacado" in color, "cinza" for black-and-white printing): ten wider keys
/// on a window that follows the chord, the chord's keys filled and named, the bass key darker. Same 282×84 frame
/// as the classic drawing. Mirrors engine/keyboard_svg.py element by element, so a diagram written by the engine
/// is recognized here when a chord is edited or transposed. "pontos" (the default) keeps the keys white and marks them
/// with dots (the bass as a hollow ring), with no letters.
/// </summary>
public static class KeyboardDiagramSvg
{
    public const string Classic = "classico", Highlight = "destacado", Gray = "cinza", Dots = "pontos";
    public static readonly IReadOnlyList<string> Styles = [Dots, Highlight, Gray, Classic];
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private const int WhiteKeys = 10, KeyWidth = 28;
    private static readonly int[] Natural = [0, 2, 4, 5, 7, 9, 11];
    private static readonly string[] Sharp = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly string[] Flat = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];
    private static readonly string[] SolfegeSharp = ["Dó", "Dó#", "Ré", "Ré#", "Mi", "Fá", "Fá#", "Sol", "Sol#", "Lá", "Lá#", "Si"];
    private static readonly string[] SolfegeFlat = ["Dó", "Réb", "Ré", "Mib", "Mi", "Fá", "Solb", "Sol", "Láb", "Lá", "Sib", "Si"];
    private const string Letters = "CDEFGAB";
    private static readonly Dictionary<char, int> LetterPc = new() { ['C'] = 0, ['D'] = 2, ['E'] = 4, ['F'] = 5, ['G'] = 7, ['A'] = 9, ['B'] = 11 };
    private static readonly Dictionary<char, string> SolfegeLetter = new() { ['C'] = "Dó", ['D'] = "Ré", ['E'] = "Mi", ['F'] = "Fá", ['G'] = "Sol", ['A'] = "Lá", ['B'] = "Si" };

    public static string Normalize(string? style) => style is Highlight or Gray or Classic ? style : Dots;

    /// <summary>Lowest white key of the 10-key window: holds every pitch, prefers C or F, then centres the chord.</summary>
    public static int WindowStart(IReadOnlyList<int> pitches)
    {
        int low = pitches.Min(), high = pitches.Max();
        (bool, double, int) best = default; int? chosen = null;
        for (int start = low - 17; start <= low; start++)
        {
            if (!Natural.Contains(Mod12(start))) continue;
            var whites = Whites(start);
            int last = whites[^1];
            if (high > last) continue;
            double centre = (start + last) / 2.0 - (low + high) / 2.0;
            var key = (!(Mod12(start) is 0 or 5), Math.Abs(centre), -start);
            if (chosen is null || Compare(key, best) < 0) { best = key; chosen = start; }
        }
        return chosen!.Value;
    }

    private static int Compare((bool, double, int) a, (bool, double, int) b)
    {
        int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
        c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
        return a.Item3.CompareTo(b.Item3);
    }

    private static int[] Whites(int start) => Enumerable.Range(start, 24).Where(p => Natural.Contains(Mod12(p))).Take(WhiteKeys).ToArray();

    /// <summary>Written name of each chord tone by its degree over the root (Cm → C, Eb, G); null if unspellable.</summary>
    public static Dictionary<int, string>? ToneNames(int rootPc, IReadOnlyList<int> intervals, string? rootSpelling, bool flats, bool solfege)
    {
        var (letter, alter) = RootSpelling(rootPc, rootSpelling, flats);
        bool seventhDim = intervals.Contains(9) && intervals.Contains(3) && intervals.Contains(6) && !intervals.Contains(10) && !intervals.Contains(11);
        bool augmented = intervals.Contains(8) && intervals.Contains(4) && !intervals.Contains(7);
        var result = new Dictionary<int, string>();
        foreach (int interval in intervals)
        {
            int steps = Mod12(interval) switch
            {
                0 => 0, 1 => 1, 2 => 1, 3 => 2, 4 => 2, 5 => 3, 6 => 4, 7 => 4, 8 => augmented ? 4 : 5,
                9 => seventhDim ? 6 : 5, _ => 6
            };
            if (interval == 3 && intervals.Contains(4)) steps = 1;
            if (interval == 6 && intervals.Contains(7)) steps = 3;
            char target = Letters[(Letters.IndexOf(letter) + steps) % 7];
            int accidental = Mod12(rootPc + interval - LetterPc[target]);
            if (accidental > 6) accidental -= 12;
            if (Math.Abs(accidental) > 2) return null;
            if (Math.Abs(accidental) == 2)
            {
                string simple = (accidental < 0 ? Flat : Sharp)[Mod12(rootPc + interval)];
                target = simple[0]; accidental = simple.Contains('#') ? 1 : simple.Length > 1 && simple[1] == 'b' ? -1 : 0;
            }
            string baseName = solfege ? SolfegeLetter[target] : target.ToString();
            result[Mod12(rootPc + interval)] = baseName + (accidental > 0 ? new string('#', accidental) : new string('b', -accidental));
        }
        return result;
    }

    private static (char Letter, int Alter) RootSpelling(int rootPc, string? spelling, bool flats)
    {
        string text = (spelling ?? "").Trim().Replace('♯', '#').Replace('♭', 'b');
        if (text.Length > 0 && LetterPc.ContainsKey(text[0]))
        {
            int alter = text[1..].Count(c => c == '#') - text[1..].Count(c => c == 'b');
            if (Mod12(LetterPc[text[0]] + alter) == Mod12(rootPc)) return (text[0], alter);
        }
        string name = (flats ? Flat : Sharp)[Mod12(rootPc)];
        return (name[0], name.Contains('#') ? 1 : name.Length > 1 && name[1] == 'b' ? -1 : 0);
    }

    /// <summary>The highlighted SVG (title and description in Portuguese), as the engine writes it.</summary>
    public static XElement Build(int rootPc, string quality, int inversion, string style, bool solfege, string? rootSpelling, bool flats = false)
    {
        style = Normalize(style);
        if (style == Classic) throw new ArgumentException("O desenho clássico é gerado à parte.", nameof(style));
        int[] pitches = KeyboardDiagramVoicing.Pitches(rootPc, quality, inversion);
        var intervals = KeyboardDiagramVoicing.IntervalsOf(quality);
        var spelled = ToneNames(Mod12(rootPc), intervals, rootSpelling, flats, solfege);
        int start = WindowStart(pitches);
        int[] whites = Whites(start);
        int[] blacks = Enumerable.Range(start, whites[^1] - start).Where(p => !Natural.Contains(Mod12(p))).ToArray();
        var (whiteFill, blackFill, bassWhite, bassBlack, whiteInk, blackInk) = style == Gray
            ? ("#A6A6A6", "#8C8C8C", "#4D4D4D", "#4D4D4D", "#000000", "#FFFFFF")
            : ("#5B8DEF", "#2F6FEB", "#1D3F99", "#1D3F99", "#FFFFFF", "#FFFFFF");
        string[] table = solfege ? (flats ? SolfegeFlat : SolfegeSharp) : (flats ? Flat : Sharp);
        int bass = pitches.Min();
        if (style == Dots) return BuildDots(pitches, whites, blacks, bass, inversion);
        var svg = new XElement(Svg + "svg", new XAttribute("width", "282"), new XAttribute("height", "84"), new XAttribute("viewBox", "0 0 282 84"),
            new XElement(Svg + "title", new[] { "Acorde em posição fundamental", "Acorde em primeira inversão", "Acorde em segunda inversão" }[inversion]),
            new XElement(Svg + "desc", new[] { "Fundamental no grave", "Terça ou nota suspensa no grave", "Quinta no grave" }[inversion] + "; notas MIDI: " + string.Join(',', pitches)));
        for (int index = 0; index < whites.Length; index++)
        {
            int pitch = whites[index]; bool pressed = pitches.Contains(pitch);
            var rect = new XElement(Svg + "rect", new XAttribute("x", 1 + index * KeyWidth), new XAttribute("y", "1"), new XAttribute("width", KeyWidth), new XAttribute("height", "60"),
                new XAttribute("fill", pressed ? (pitch == bass ? bassWhite : whiteFill) : "white"), new XAttribute("stroke", "#111111"), new XAttribute("stroke-width", "1"));
            if (pressed) rect.Add(new XAttribute("data-midi", pitch));
            svg.Add(rect);
        }
        foreach (int pitch in blacks)
        {
            bool pressed = pitches.Contains(pitch);
            int x = 1 + (Array.IndexOf(whites, pitch - 1) + 1) * KeyWidth - 8;
            var rect = new XElement(Svg + "rect", new XAttribute("x", x), new XAttribute("y", "1"), new XAttribute("width", "16"), new XAttribute("height", "37"),
                new XAttribute("fill", pressed ? (pitch == bass ? bassBlack : blackFill) : "#111111"));
            if (pressed) rect.Add(new XAttribute("stroke", "#111111"), new XAttribute("stroke-width", "1"), new XAttribute("data-midi", pitch));
            svg.Add(rect);
        }
        foreach (int pitch in pitches)
        {
            bool natural = Natural.Contains(Mod12(pitch));
            int x = natural ? 1 + Array.IndexOf(whites, pitch) * KeyWidth + KeyWidth / 2 : 1 + (Array.IndexOf(whites, pitch - 1) + 1) * KeyWidth;
            string name = spelled is not null && spelled.TryGetValue(Mod12(pitch), out var written) && written.Length > 0 ? written : table[Mod12(pitch)];
            svg.Add(new XElement(Svg + "text", new XAttribute("x", x), new XAttribute("y", natural ? "56" : "33"), new XAttribute("fill", natural ? whiteInk : blackInk),
                new XAttribute("font-family", "Arial"), new XAttribute("font-size", natural ? "15" : "11"), new XAttribute("font-weight", "bold"),
                new XAttribute("text-anchor", "middle"), name));
        }
        for (int index = 0; index < whites.Length; index++)
            if (Mod12(whites[index]) == 0)
                svg.Add(new XElement(Svg + "text", new XAttribute("x", 1 + index * KeyWidth + KeyWidth / 2), new XAttribute("y", "79"), new XAttribute("fill", "#555555"),
                    new XAttribute("font-family", "Arial"), new XAttribute("font-size", "15"), new XAttribute("text-anchor", "middle"),
                    table[0] + (whites[index] / 12 - 1)));
        return svg;
    }

    private static XElement BuildDots(int[] pitches, int[] whites, int[] blacks, int bass, int inversion)
    {
        var svg = new XElement(Svg + "svg", new XAttribute("width", "282"), new XAttribute("height", "84"), new XAttribute("viewBox", "0 0 282 84"),
            new XElement(Svg + "title", new[] { "Acorde em posição fundamental", "Acorde em primeira inversão", "Acorde em segunda inversão" }[inversion]),
            new XElement(Svg + "desc", new[] { "Fundamental no grave", "Terça ou nota suspensa no grave", "Quinta no grave" }[inversion] + "; notas MIDI: " + string.Join(',', pitches)));
        for (int index = 0; index < whites.Length; index++)
            svg.Add(new XElement(Svg + "rect", new XAttribute("x", 1 + index * KeyWidth), new XAttribute("y", "1"), new XAttribute("width", KeyWidth), new XAttribute("height", "60"),
                new XAttribute("fill", "white"), new XAttribute("stroke", "#111111"), new XAttribute("stroke-width", "1")));
        foreach (int pitch in blacks)
            svg.Add(new XElement(Svg + "rect", new XAttribute("x", 1 + (Array.IndexOf(whites, pitch - 1) + 1) * KeyWidth - 8), new XAttribute("y", "1"),
                new XAttribute("width", "16"), new XAttribute("height", "37"), new XAttribute("fill", "#111111")));
        foreach (int pitch in pitches.Order())
        {
            bool natural = Natural.Contains(Mod12(pitch));
            int x = natural ? 1 + Array.IndexOf(whites, pitch) * KeyWidth + KeyWidth / 2 : 1 + (Array.IndexOf(whites, pitch - 1) + 1) * KeyWidth;
            string ink = natural ? "#111111" : "#FFFFFF";
            var dot = new XElement(Svg + "circle", new XAttribute("cx", x), new XAttribute("cy", natural ? "50" : "28"));
            if (pitch == bass) dot.Add(new XAttribute("r", natural ? "6" : "5"), new XAttribute("fill", "none"), new XAttribute("stroke", ink), new XAttribute("stroke-width", "2.5"));
            else dot.Add(new XAttribute("r", natural ? "7" : "6"), new XAttribute("fill", ink));
            dot.Add(new XAttribute("data-midi", pitch));
            svg.Add(dot);
        }
        return svg;
    }

    /// <summary>Chord pitches marked on a TeclaScore highlighted diagram, from its data-midi attributes.</summary>
    public static int[] MarkedPitches(XElement svg) => svg.Descendants()
        .Select(e => (string?)e.Attribute("data-midi")).OfType<string>()
        .Select(v => int.TryParse(v, out int p) ? p : -1).Where(p => p >= 0).Order().ToArray();

    private static int Mod12(int value) => (value % 12 + 12) % 12;
}
