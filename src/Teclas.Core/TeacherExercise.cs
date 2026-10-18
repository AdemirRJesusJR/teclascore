using System.Text.Json;
using System.Text.RegularExpressions;

namespace Teclas.Core;

/// <summary>
/// A practice exercise returned by the AI teacher as JSON: steps with the chord in effect, right-hand notes and length in
/// beats. Becomes a MIDI with the right hand on track 1 and the chord of each step (keyboard-diagram voicing) on track 2,
/// so the usual analysis writes the chords and diagrams. Values out of range are clamped or dropped, never trusted.
/// </summary>
public sealed record TeacherExercise(string Title, string Explanation, double Bpm, int BeatsPerBar, IReadOnlyList<WrittenNote> RightHand, IReadOnlyList<WrittenNote> Chords)
{
    public static TeacherExercise Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string title = Text(root, "title", 80);
        if (title.Length == 0) title = "Exercício";
        string explanation = Text(root, "explanation", 600);
        double bpm = root.TryGetProperty("tempo", out var t) && t.TryGetDouble(out double tempo) ? Math.Clamp(tempo, 40, 160) : 70;
        int meter = root.TryGetProperty("beatsPerBar", out var b) && b.TryGetInt32(out int beats) && beats is 2 or 3 or 4 or 6 ? beats : 4;
        var right = new List<WrittenNote>(); var chords = new List<WrittenNote>();
        double at = 0; string? lastChord = null; double chordStart = 0; int[] chordPitches = [];
        void CloseChord(double end) { foreach (int pitch in chordPitches) chords.Add(new(chordStart, end - chordStart, pitch, 70)); }
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            foreach (var step in steps.EnumerateArray().Take(256))
            {
                double length = step.TryGetProperty("beats", out var l) && l.TryGetDouble(out double value) ? Math.Clamp(Math.Round(value * 4) / 4, .25, 8) : 1;
                if (step.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                    foreach (var note in notes.EnumerateArray().Take(6))
                        if (note.TryGetInt32(out int pitch) && pitch is >= 36 and <= 96) right.Add(new(at, length, pitch, 88));
                string chord = Text(step, "chord", 16);
                if (chord != lastChord)
                {
                    CloseChord(at);
                    chordPitches = Voicing(chord); chordStart = at; lastChord = chord;
                }
                at += length;
            }
        CloseChord(at);
        if (right.Count == 0 && chords.Count == 0) throw new InvalidDataException("o exercício veio sem notas.");
        return new(title, explanation, bpm, meter, right, chords);
    }

    /// <summary>Chord tones of a symbol (root + quality, bass ignored) in the octave below middle C; empty when unknown.</summary>
    public static int[] Voicing(string symbol)
    {
        var match = Regex.Match(symbol.Trim(), "^([A-G](?:#|b)?)([^/]*)", RegexOptions.CultureInvariant);
        if (!match.Success || !KeyboardDiagramVoicing.TryRootPitchClass(match.Groups[1].Value, out int root)) return [];
        string quality = match.Groups[2].Value switch { "M" or "maj" => "", "min" or "-" => "m", "°" or "o" => "dim", "ø" => "m7b5", "7M" or "M7" => "maj7", var q => q };
        if (!KeyboardDiagramVoicing.SupportsQuality(quality)) quality = quality.StartsWith('m') ? "m" : "";
        return KeyboardDiagramVoicing.Pitches(root, quality, 0).Select(p => p - 12).ToArray();
    }

    public byte[] ToMidi() => SimpleMidiWriter.Write(Bpm, BeatsPerBar,
    [
        new WrittenTrack("Exercício (mão direita)", 0, 0, RightHand),
        new WrittenTrack("Acordes do exercício", 1, 0, Chords),
    ]);

    private static string Text(JsonElement element, string name, int limit)
    {
        string value = element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
        value = new string(value.Where(c => !char.IsControl(c) || c == '\n').ToArray());
        return value.Length > limit ? value[..limit] : value;
    }
}
