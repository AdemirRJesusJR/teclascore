using System.Text;

namespace Teclas.Core;

/// <summary>One syllable of a pasted lyric, with where its word, line and stanza start.</summary>
public sealed record LyricSyllableText(string Text, bool WordStart, bool LineStart, bool StanzaStart)
{
    /// <summary>The word continues after this syllable (printed with a hyphen under the notes).</summary>
    public bool Continues { get; init; }
}

/// <summary>
/// Simple Portuguese syllable division by rules, enough to spread a pasted lyric over the melody notes: the digraphs
/// ch, lh, nh, qu and gu stay together; rr, ss, sc, sç, xc and cc split; a consonant followed by l or r starts the next
/// syllable with it (pra-to, a-bra-ço); the falling diphthongs (ai, ei, oi, ui, au, eu, ou, iu, ão, ãe, õe) stay in one
/// syllable and the other vowel meetings are hiatus (di-a, sa-ú-de). It does not know stress exceptions: the editor lets
/// the user join or split syllables afterwards.
/// </summary>
public static class PortugueseSyllables
{
    private const string Vowels = "aeiouáéíóúâêôãõàüy";
    private static readonly HashSet<string> Inseparable = new(StringComparer.Ordinal) { "ch", "lh", "nh" };
    private static readonly HashSet<char> Liquids = ['l', 'r'];
    private static readonly HashSet<char> ClusterStarts = ['b', 'c', 'd', 'f', 'g', 'k', 'p', 't', 'v'];

    private static bool IsVowel(char c) => Vowels.Contains(char.ToLowerInvariant(c));

    /// <summary>Syllables of one word; letters keep their case and any punctuation stays attached at the edges.</summary>
    public static IReadOnlyList<string> Split(string word)
    {
        if (string.IsNullOrEmpty(word)) return [];
        int first = 0, last = word.Length - 1;
        while (first <= last && !char.IsLetter(word[first])) first++;
        while (last >= first && !char.IsLetter(word[last])) last--;
        if (first > last) return [word];
        string core = word[first..(last + 1)], lower = core.ToLowerInvariant();
        // Positions where a new syllable starts.
        var nuclei = new List<(int Start, int End)>();
        for (int i = 0; i < lower.Length;)
        {
            if (!IsVowel(lower[i]) || IsSemivowelU(lower, i)) { i++; continue; }
            int end = i + 1;
            while (end < lower.Length && IsVowel(lower[end]) && Diphthong(lower, end - 1, end)) end++;
            nuclei.Add((i, end));
            i = end;
        }
        if (nuclei.Count <= 1) return [word];
        var starts = new List<int> { 0 };
        for (int k = 1; k < nuclei.Count; k++)
        {
            int from = nuclei[k - 1].End, to = nuclei[k].Start; // consonants between two nuclei: [from, to)
            string between = lower[from..to];
            int cut = between.Length switch
            {
                0 => to,
                1 => from,
                _ => from + Onset(between)
            };
            starts.Add(cut);
        }
        var syllables = new List<string>();
        for (int k = 0; k < starts.Count; k++)
        {
            int start = starts[k], end = k + 1 < starts.Count ? starts[k + 1] : core.Length;
            syllables.Add(core[start..end]);
        }
        syllables[0] = word[..first] + syllables[0];
        syllables[^1] += word[(last + 1)..];
        return syllables;
    }

    /// <summary>Where the consonants between two vowels split: the index (in the cluster) where the next syllable starts.</summary>
    private static int Onset(string cluster)
    {
        // The last two consonants start the next syllable when they are a digraph or a consonant + l/r.
        string tail = cluster[^2..];
        bool onset = Inseparable.Contains(tail) || ClusterStarts.Contains(tail[0]) && Liquids.Contains(tail[1])
            || tail is "qu" or "gu";
        if (cluster.Length == 2) return onset ? 0 : 1;
        return onset ? cluster.Length - 2 : cluster.Length - 1;
    }

    /// <summary>The u of qu/gu before e or i (and before a/o in qua, quo, gua, guo) belongs to the consonant.</summary>
    private static bool IsSemivowelU(string word, int index) =>
        word[index] is 'u' or 'ü' && index > 0 && word[index - 1] is 'q' or 'g' && index + 1 < word.Length && IsVowel(word[index + 1]);

    /// <summary>A falling diphthong: a vowel followed by an unstressed i or u (or the nasal ão, ãe, õe).</summary>
    private static bool Diphthong(string word, int first, int second)
    {
        char a = word[first], b = word[second];
        if (a is 'ã' && b is 'o' or 'e' || a is 'õ' && b is 'e') return true;
        if (b is not ('i' or 'u') || a is 'i' && b is 'i' || a is 'u' && b is 'u') return false;
        // "ra-i-nha", "ca-ir", "ju-iz", "ru-im": the i/u before nh, or before a final r, l, z, m, n, is a vowel of its own.
        if (word.AsSpan(second + 1).StartsWith("nh")) return false;
        if (second + 2 == word.Length && word[second + 1] is 'r' or 'l' or 'z' or 'm' or 'n') return false;
        // "sa-ir-mos" style (i/u + r/l + consonant) also starts its own syllable.
        if (second + 2 < word.Length && word[second + 1] is 'r' or 'l' && !IsVowel(word[second + 2]) && word[second + 2] is not 'h') return false;
        return true;
    }

    /// <summary>
    /// The syllables of a whole lyric in singing order: lines are verses, blank lines separate stanzas, and words are
    /// split into syllables. Hyphenated words ("sexta-feira") continue across the hyphen.
    /// </summary>
    public static IReadOnlyList<LyricSyllableText> FromLyrics(string text)
    {
        var result = new List<LyricSyllableText>();
        bool stanza = false, firstLine = true;
        foreach (string rawLine in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) { stanza = !firstLine; continue; }
            bool lineStart = true;
            foreach (string word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = word.Split('-', StringSplitOptions.RemoveEmptyEntries);
                bool wordStart = true;
                for (int p = 0; p < parts.Length; p++)
                {
                    var syllables = Split(parts[p]);
                    for (int s = 0; s < syllables.Count; s++)
                    {
                        bool continues = s < syllables.Count - 1 || p < parts.Length - 1;
                        result.Add(new LyricSyllableText(syllables[s], wordStart, lineStart, lineStart && stanza) { Continues = continues });
                        wordStart = false; lineStart = false; stanza = false;
                    }
                }
            }
            firstLine = false;
        }
        return result;
    }

    /// <summary>The word made of syllables (for the editor's verse list): "Ca", "sa" → "Casa".</summary>
    public static string Join(IEnumerable<string> syllables)
    {
        var text = new StringBuilder();
        foreach (string syllable in syllables) text.Append(syllable);
        return text.ToString();
    }
}
