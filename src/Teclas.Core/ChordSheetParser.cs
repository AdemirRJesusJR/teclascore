using System.Text.RegularExpressions;

namespace Teclas.Core;

public sealed record SheetChord(string Root, string Quality, string Written);
public sealed record ChordSheet(IReadOnlyList<SheetChord> Sequence, IReadOnlyList<SheetChord> Distinct, IReadOnlyList<string> Unknown);

/// <summary>
/// Reads chords from a chord sheet pasted as text (chord lines over lyrics, as on chord websites). A line counts as chords
/// when most of its words are chord symbols; Brazilian spellings are understood (7M, m7(b5), °, 4, (9)) and slash basses
/// are dropped (C/E → C), since the keyboard diagram shows the chord itself.
/// </summary>
public static partial class ChordSheetParser
{
    [GeneratedRegex(@"^([A-G])(#|b|♯|♭)?(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Symbol();

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
        var distinct = sequence.GroupBy(c => c.Root + c.Quality).Select(g => g.First()).ToArray();
        return new(sequence, distinct, unknown.Distinct().ToArray());
    }

    public static SheetChord? TryParse(string word)
    {
        string written = word.Trim();
        string clean = written.Replace('♯', '#').Replace('♭', 'b');
        int slash = clean.IndexOf('/');
        if (slash > 0) clean = clean[..slash];
        var match = Symbol().Match(clean);
        if (!match.Success) return null;
        string root = match.Groups[1].Value + match.Groups[2].Value;
        string quality = Normalize(match.Groups[3].Value);
        return KeyboardDiagramVoicing.SupportsQuality(quality) ? new(root, quality, written) : null;
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
