using System.Text.RegularExpressions;

namespace Teclas.Core;

/// <summary>The exact closed voicings used by native keyboard diagram assets.</summary>
public static class KeyboardDiagramVoicing
{
    private static readonly Dictionary<string, int[]> Intervals = new(StringComparer.Ordinal)
    {
        [""] = [0, 4, 7], ["m"] = [0, 3, 7], ["7"] = [0, 4, 7, 10], ["maj7"] = [0, 4, 7, 11],
        ["m7"] = [0, 3, 7, 10], ["mMaj7"] = [0, 3, 7, 11], ["dim"] = [0, 3, 6], ["dim7"] = [0, 3, 6, 9],
        ["m7b5"] = [0, 3, 6, 10], ["aug"] = [0, 4, 8], ["sus2"] = [0, 2, 7], ["sus4"] = [0, 5, 7],
        ["6"] = [0, 4, 7, 9], ["m6"] = [0, 3, 7, 9], ["add9"] = [0, 2, 4, 7], ["madd9"] = [0, 2, 3, 7],
        ["9"] = [0, 2, 4, 7, 10], ["maj9"] = [0, 2, 4, 7, 11], ["m9"] = [0, 2, 3, 7, 10], ["7sus4"] = [0, 5, 7, 10]
    };
    public static IReadOnlyList<string> Qualities => Intervals.Keys.ToArray();
    public static bool SupportsQuality(string quality) => quality is not null && Intervals.ContainsKey(quality);
    public static int[] Pitches(int rootPitchClass, string quality, int inversion)
    {
        if (inversion is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(inversion));
        if (!Intervals.TryGetValue(quality, out var intervals)) throw new ArgumentException("Tipo de acorde não suportado.", nameof(quality));
        int root = Mod12(rootPitchClass);
        int bassInterval = inversion == 0 ? 0 : (inversion == 1 ? new[] { 3, 4, 2, 5 } : [7, 6, 8]).First(intervals.Contains);
        int bass = 53 + Mod12(root + bassInterval - 53);
        int[] pitches = intervals.Select(interval => bass + Mod12(root + interval - bass)).Order().ToArray();
        if (pitches[^1] > 71) pitches = pitches.Select(p => p - 12).ToArray();
        return pitches;
    }
    public static bool TryRootPitchClass(string root, out int pitchClass)
    {
        pitchClass = 0;
        string value = (root ?? "").Trim().Replace('♯', '#').Replace('♭', 'b');
        if (!Regex.IsMatch(value, "^[A-G](#{1,2}|b{1,2})?$", RegexOptions.CultureInvariant)) return false;
        int natural = value[0] switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, _ => 11 };
        pitchClass = Mod12(natural + value.Count(c => c == '#') - value.Count(c => c == 'b'));
        return true;
    }
    private static int Mod12(int value) => (value % 12 + 12) % 12;
}
