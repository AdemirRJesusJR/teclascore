using System.Text.RegularExpressions;

namespace Teclas.Core;

public sealed record SheetChord(string Root, string Quality, string Written, string? Bass = null)
{
    /// <summary>Symbol as the app writes it (Brazilian spellings normalized), with the bass after a slash: G/B.</summary>
    public string Symbol => Root + Quality + (Bass is null ? "" : "/" + Bass);
}
public sealed record ChordSheet(IReadOnlyList<SheetChord> Sequence, IReadOnlyList<SheetChord> Distinct, IReadOnlyList<string> Unknown);
/// <summary>One measure of the score made from a chord sheet: the chord, the words under it, and whether a sheet line starts here.</summary>
public sealed record SheetMeasure(SheetChord Chord, string Words, bool LineStart);

/// <summary>
/// Reads chords from a chord sheet pasted as text (chord lines over lyrics, as on chord websites). A line counts as chords
/// when most of its words are chord symbols; Brazilian spellings are understood (7M, m7(b5), °, 4, (9), 7/9) and a slash
/// bass is kept apart (C/E: chord C, bass E), so the diagram shows the chord and marks the bass note.
/// </summary>
public static partial class ChordSheetParser
{
    [GeneratedRegex(@"^([A-G])(#|b|♯|♭)?(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Symbol();
    [GeneratedRegex(@"^([A-G])(#|b)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Bass();

    public static ChordSheet Parse(string text)
    {
        var sequence = new List<SheetChord>(); var unknown = new List<string>();
        foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var words = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('|', '[', ']', ',', ';')).Where(w => w.Length > 0).ToArray();
            if (words.Length == 0) continue;
            var read = words.Select(word => (word, chord: TryParse(word))).ToArray();
            int chords = read.Count(r => r.chord is not null), lookalikes = read.Count(r => r.chord is null && Symbol().IsMatch(r.word) && r.word.Length <= 8);
            // Lyrics lines have few chord-like words; a chord line is mostly symbols.
            if (chords == 0 || chords < Math.Ceiling(words.Length * .6)) continue;
            foreach (var (word, chord) in read)
                if (chord is not null) sequence.Add(chord);
                else if (lookalikes > 0 && Symbol().IsMatch(word)) unknown.Add(word);
        }
        var distinct = sequence.GroupBy(c => c.Symbol).Select(g => g.First()).ToArray();
        return new(sequence, distinct, unknown.Distinct().ToArray());
    }

    /// <summary>
    /// Measures for a score: one per chord, in order, with the words of the lyric line right below the chord line, cut at the
    /// chord columns (a word split by the next chord gets a hyphen). Each chord line starts a new system.
    /// </summary>
    public static IReadOnlyList<SheetMeasure> Measures(string text)
    {
        var lines = (text ?? "").Replace("\r", "").Replace("\t", "    ").Split('\n');
        var result = new List<SheetMeasure>();
        for (int i = 0; i < lines.Length; i++)
        {
            var chords = ChordColumns(lines[i]);
            if (chords is null) continue;
            string lyric = i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && ChordColumns(lines[i + 1]) is null ? lines[i + 1] : "";
            for (int k = 0; k < chords.Count; k++)
            {
                int from = k == 0 ? 0 : chords[k].Column, to = k + 1 < chords.Count ? chords[k + 1].Column : int.MaxValue;
                result.Add(new(chords[k].Chord, Words(lyric, from, to), k == 0));
            }
        }
        return result;
    }

    private static string Words(string lyric, int from, int to)
    {
        if (from >= lyric.Length) return "";
        int end = Math.Min(to, lyric.Length);
        string words = lyric[from..end].Trim();
        if (words.Length > 0 && end < lyric.Length && !char.IsWhiteSpace(lyric[end]) && !char.IsWhiteSpace(lyric[end - 1])) words += "-";
        return words;
    }

    /// <summary>Chords of a chord line with their text columns; null when the line is lyrics (or empty).</summary>
    private static List<(SheetChord Chord, int Column)>? ChordColumns(string line)
    {
        var words = Regex.Matches(line, @"\S+").Select(m => (Text: m.Value.Trim('|', '[', ']', ',', ';'), m.Index)).Where(w => w.Text.Length > 0).ToArray();
        if (words.Length == 0) return null;
        var read = words.Select(w => (w.Index, Chord: TryParse(w.Text))).ToArray();
        int chords = read.Count(r => r.Chord is not null);
        if (chords == 0 || chords < Math.Ceiling(words.Length * .6)) return null;
        return read.Where(r => r.Chord is not null).Select(r => (r.Chord!, r.Index)).ToList();
    }

    public static SheetChord? TryParse(string word)
    {
        string written = word.Trim();
        string clean = written.Replace('♯', '#').Replace('♭', 'b');
        string? bass = null;
        int slash = clean.LastIndexOf('/');
        // A note after the slash is the bass (G/B); digits belong to the chord type (7/9, 7/4).
        if (slash > 0 && Bass().Match(clean[(slash + 1)..]) is { Success: true } note)
        {
            bass = note.Groups[1].Value + note.Groups[2].Value; clean = clean[..slash];
        }
        var match = Symbol().Match(clean);
        if (!match.Success) return null;
        string root = match.Groups[1].Value + match.Groups[2].Value;
        string quality = Normalize(match.Groups[3].Value);
        return KeyboardDiagramVoicing.SupportsQuality(quality) ? new(root, quality, written, bass) : null;
    }

    /// <summary>Common spellings to the app's chord types; anything else is left unknown.</summary>
    private static string Normalize(string q)
    {
        q = q.Replace("(", "").Replace(")", "").Replace("º", "°").Replace("ø", "m7b5").Replace("+", "aug").Trim();
        return q switch
        {
            "" or "M" or "maj" => "",
            "m" or "min" or "-" => "m",
            "7" => "7", "7M" or "maj7" or "M7" or "7+" => "maj7",
            "m7" or "min7" or "-7" => "m7", "m7M" or "mMaj7" or "m(7M)" => "mMaj7",
            "°" or "dim" or "o" => "dim", "°7" or "dim7" or "o7" => "dim7",
            "m7b5" or "m7-5" => "m7b5",
            "aug" or "5+" => "aug", "sus2" or "2" => "sus2", "sus4" or "4" or "sus" => "sus4",
            "6" => "6", "m6" => "m6", "9" or "add9" => "add9", "m9" or "madd9" => "madd9",
            "79" or "7/9" or "7(9)" => "9", "7M9" or "maj9" => "maj9", "m79" or "m7/9" => "m9", "74" or "7sus4" or "7/4" => "7sus4",
            _ => "?"
        };
    }
}
