using System.Text.Json.Nodes;

namespace Teclas.Core;

/// <summary>Beginner chord symbols: sevenths, sixths and extensions become their basic triad (Am7 → Am, G7 → G, F#m7b5 → F#dim).</summary>
public static class ChordSimplifier
{
    public static string Simplify(string? quality) => (quality ?? "").Trim() switch
    {
        "" or "6" or "7" or "maj7" or "9" or "maj9" or "add9" => "",
        "m" or "m6" or "m7" or "m9" or "madd9" or "mMaj7" => "m",
        "dim" or "dim7" or "m7b5" => "dim",
        "7sus4" or "sus4" => "sus4",
        var other => other
    };

    /// <summary>Simplifies JSON chords {root, quality, ...}; a chord equal to the previous one after simplifying is dropped.</summary>
    public static JsonArray SimplifyAll(JsonArray chords)
    {
        var result = new JsonArray();
        (string? Root, string? Quality) previous = (null, null);
        foreach (var node in chords.OfType<JsonObject>())
        {
            var chord = (JsonObject)node.DeepClone();
            string quality = Simplify((string?)chord["quality"]);
            chord["quality"] = quality;
            var current = (((string?)chord["root"] ?? "").Trim(), quality);
            if (current == previous) continue;
            result.Add(chord); previous = current;
        }
        return result;
    }
}
