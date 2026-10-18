namespace Teclas.Core;

/// <summary>How the user prefers chord symbols written: major seventh, half-diminished, diminished, added ninth, accidentals.</summary>
public sealed record ChordNotationPreference(string Major7 = "maj7", string HalfDiminished = "m7b5", string Diminished = "dim", string Add9 = "add9", string Accidentals = "auto")
{
    public static readonly ChordNotationPreference Default = new();
    public static readonly string[] Major7Options = ["maj7", "7M", "Δ"];
    public static readonly string[] HalfDiminishedOptions = ["m7b5", "m7(b5)", "ø"];
    public static readonly string[] DiminishedOptions = ["dim", "°"];
    public static readonly string[] Add9Options = ["add9", "(9)"];
    public static readonly string[] AccidentalOptions = ["auto", "flats", "sharps"];
    public bool IsDefault => this == Default;

    /// <summary>Keeps only known choices (anything else falls back to the default).</summary>
    public ChordNotationPreference Normalized() => new(
        Major7Options.Contains(Major7) ? Major7 : "maj7", HalfDiminishedOptions.Contains(HalfDiminished) ? HalfDiminished : "m7b5",
        DiminishedOptions.Contains(Diminished) ? Diminished : "dim", Add9Options.Contains(Add9) ? Add9 : "add9", AccidentalOptions.Contains(Accidentals) ? Accidentals : "auto");
}

/// <summary>
/// Chord notation: the TeclaScore keeps one internal name per quality ("maj7", "m7b5"…) and writes the score in the
/// user's notation ("7M", "m7(b5)", "°"…). Reading a score, every known variant is turned back into the internal name,
/// so editing, practice and the text chord sheet keep working whatever notation the score uses.
/// </summary>
public static class ChordNotation
{
    /// <summary>The notation in effect (set by the app from the user's settings).</summary>
    public static ChordNotationPreference Current { get; set; } = ChordNotationPreference.Default;

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["7M"] = "maj7", ["Δ"] = "maj7", ["M7"] = "maj7", ["7+"] = "maj7", ["Maj7"] = "maj7",
        ["7M(9)"] = "maj9", ["Δ9"] = "maj9", ["m7M"] = "mMaj7", ["mΔ"] = "mMaj7", ["m(maj7)"] = "mMaj7",
        ["m7(b5)"] = "m7b5", ["ø"] = "m7b5", ["ø7"] = "m7b5", ["°"] = "dim", ["°7"] = "dim7", ["º"] = "dim", ["º7"] = "dim7",
        ["(9)"] = "add9", ["m(9)"] = "madd9", ["add(9)"] = "add9",
    };

    /// <summary>The internal name of a written suffix ("7M" → "maj7"); unknown suffixes are returned unchanged.</summary>
    public static string Canonical(string written) => Aliases.TryGetValue(written ?? "", out var quality) ? quality : written ?? "";

    /// <summary>The suffix to write for an internal quality in a notation (default: the current one).</summary>
    public static string Display(string quality, ChordNotationPreference? preference = null)
    {
        var p = preference ?? Current;
        return quality switch
        {
            "maj7" => p.Major7,
            "maj9" => p.Major7 switch { "7M" => "7M(9)", "Δ" => "Δ9", _ => "maj9" },
            "mMaj7" => p.Major7 switch { "7M" => "m7M", "Δ" => "mΔ", _ => "mMaj7" },
            "m7b5" => p.HalfDiminished,
            "dim" => p.Diminished,
            "dim7" => p.Diminished == "°" ? "°7" : "dim7",
            "add9" => p.Add9,
            "madd9" => p.Add9 == "(9)" ? "m(9)" : "madd9",
            _ => quality,
        };
    }

    private static readonly string[] Flats = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];
    private static readonly string[] Sharps = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>A root written with flats or sharps as preferred; natural notes and "auto" keep their spelling.</summary>
    public static string Root(string root, ChordNotationPreference? preference = null)
    {
        var p = preference ?? Current;
        if (p.Accidentals == "auto" || root.Length < 2 || !KeyboardDiagramVoicing.TryRootPitchClass(root, out int pc)) return root;
        return (p.Accidentals == "flats" ? Flats : Sharps)[pc];
    }

    /// <summary>The generation request field for the engine (null for the default notation, which writes internal names).</summary>
    public static Dictionary<string, object>? EngineOption(ChordNotationPreference preference)
    {
        if (preference.IsDefault) return null;
        var texts = new Dictionary<string, string>();
        foreach (string quality in new[] { "maj7", "maj9", "mMaj7", "m7b5", "dim", "dim7", "add9", "madd9" })
            if (Display(quality, preference) is var text && text != quality) texts[quality] = text;
        return new() { ["texts"] = texts, ["accidentals"] = preference.Accidentals };
    }
}
