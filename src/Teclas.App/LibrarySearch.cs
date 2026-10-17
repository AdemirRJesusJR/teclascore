using System.Text.RegularExpressions;

namespace Teclas.App;

/// <summary>Search helpers for chord symbols and keys in the library.</summary>
internal static partial class LibrarySearch
{
    private static readonly string[] Major = ["Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#"];
    private static readonly string[] Minor = ["Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#", "G#", "D#", "A#"];
    private static readonly Dictionary<char, string> Solfege = new() { ['C'] = "Dó", ['D'] = "Ré", ['E'] = "Mi", ['F'] = "Fá", ['G'] = "Sol", ['A'] = "Lá", ['B'] = "Si" };

    [GeneratedRegex(@"^[A-Ga-g](#|b|♯|♭)?(m|maj7|m7|7|dim|dim7|m7b5|aug|sus2|sus4|6|m6|9|maj9|m9|add9|madd9|mMaj7|7sus4)?$")]
    private static partial Regex ChordPattern();

    /// <summary>True for words such as Dm, F#dim or Am7; plain words like "forró" are text searches.</summary>
    internal static bool LooksLikeChord(string word) => ChordPattern().IsMatch(word.Replace('♯', '#').Replace('♭', 'b'))
        && char.IsUpper(word[0]);

    /// <summary>Symbol and Portuguese names of a key, e.g. Dm, "Ré menor"; or E, "Mi maior".</summary>
    internal static string[] KeyNames(int fifths, bool minor)
    {
        if (fifths is < -7 or > 7) return [];
        string root = (minor ? Minor : Major)[fifths + 7];
        string name = Solfege[root[0]] + (root.Length > 1 ? (root[1] == '#' ? "♯" : "♭") : "");
        return [root + (minor ? "m" : ""), name + (minor ? " menor" : " maior"), "tom " + root + (minor ? "m" : "")];
    }
}
