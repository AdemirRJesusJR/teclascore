using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Teclas.Core;

namespace Teclas.App;

// Shares the score's voicings; the drawing uses the same two-octave keyboard as its SVG.
// A dialog-local cache bounds lifetime and avoids drawing repeated chords again.
internal sealed class KeyboardDiagramPreview
{
    private readonly Dictionary<(int Root, string Quality, int Inversion, int Bass), DrawingImage> images = new();
    private static readonly Color BassColor = Color.FromRgb(37, 99, 235);
    private static readonly int[] WhiteNotes = Enumerable.Range(48, 24)
        .Where(pitch => pitch % 12 is 0 or 2 or 4 or 5 or 7 or 9 or 11).ToArray();

    /// <summary>Lowest key of the bass note on the diagram's two octaves (C3–B3), for a slash chord such as G/B.</summary>
    internal static int BassPitch(int bassPitchClass) => 48 + ((bassPitchClass % 12) + 12) % 12;

    internal DrawingImage? GetImage(string root, string quality, int inversion, int transpose, string? bass = null)
    {
        if (!KeyboardDiagramVoicing.TryRootPitchClass(root, out int pitchClass)
            || !KeyboardDiagramVoicing.SupportsQuality(quality.Trim()) || inversion is < 0 or > 2)
            return null;
        int transposedRoot = ((pitchClass + transpose) % 12 + 12) % 12;
        int bassClass = bass is not null && KeyboardDiagramVoicing.TryRootPitchClass(bass, out int b) ? ((b + transpose) % 12 + 12) % 12 : -1;
        var key = (transposedRoot, quality.Trim(), inversion, bassClass);
        if (images.TryGetValue(key, out var image)) return image;
        var pitches = KeyboardDiagramVoicing.Pitches(transposedRoot, key.Item2, inversion);
        var drawing = new DrawingGroup();
        var ink = new SolidColorBrush(Color.FromRgb(17, 17, 17));
        var border = new Pen(ink, 1);
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 282, 84));
            for (int i = 0; i < WhiteNotes.Length; i++)
                dc.DrawRectangle(Brushes.White, border, new Rect(1 + i * 20, 1, 20, 60));
            foreach (int pitch in Enumerable.Range(48, 24).Except(WhiteNotes))
            {
                int precedingWhite = Array.IndexOf(WhiteNotes, pitch - 1);
                dc.DrawRectangle(ink, null, new Rect(1 + (precedingWhite + 1) * 20 - 6, 1, 12, 37));
            }
            foreach (int pitch in pitches)
            {
                int white = Array.IndexOf(WhiteNotes, pitch);
                var center = white >= 0 ? new Point(11 + white * 20, 49)
                    : new Point(1 + (Array.IndexOf(WhiteNotes, pitch - 1) + 1) * 20, 29);
                dc.DrawEllipse(white >= 0 ? ink : Brushes.White, null, center, 4.2, 4.2);
            }
            if (bassClass >= 0)
            {
                // The bass of a slash chord: a blue key mark in the low octave (a ring when the chord already uses that key).
                int pitch = BassPitch(bassClass), white = Array.IndexOf(WhiteNotes, pitch);
                var center = white >= 0 ? new Point(11 + white * 20, 49) : new Point(1 + (Array.IndexOf(WhiteNotes, pitch - 1) + 1) * 20, 29);
                var blue = new SolidColorBrush(BassColor);
                if (pitches.Contains(pitch)) dc.DrawEllipse(null, new Pen(blue, 2), center, 7, 7);
                else dc.DrawRectangle(blue, null, new Rect(center.X - 4.5, center.Y - 4.5, 9, 9));
            }
            var label = new FormattedText("C", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 16, new SolidColorBrush(Color.FromRgb(51, 51, 51)), 1);
            foreach (int center in new[] { 11, 151 })
                dc.DrawText(label, new Point(center - label.Width / 2, 79 - label.Baseline));
        }
        image = new DrawingImage(drawing);
        image.Freeze();
        images.Add(key, image);
        return image;
    }

    internal static string Describe(string root, string quality, int inversion, int transpose, string? bass)
    {
        string text = Describe(root, quality, inversion, transpose);
        if (bass is null || !KeyboardDiagramVoicing.TryRootPitchClass(bass, out int b)) return text;
        string[] names = ["Dó", "Dó♯", "Ré", "Ré♯", "Mi", "Fá", "Fá♯", "Sol", "Sol♯", "Lá", "Lá♯", "Si"];
        int pitch = BassPitch(b + transpose);
        return text.TrimEnd('.') + $". Baixo (azul): {names[pitch % 12]}{pitch / 12 - 1}.";
    }

    internal static string Describe(string root, string quality, int inversion, int transpose)
    {
        if (!KeyboardDiagramVoicing.TryRootPitchClass(root, out int pitchClass)
            || !KeyboardDiagramVoicing.SupportsQuality(quality.Trim()) || inversion is < 0 or > 2)
            return $"Diagrama indisponível para {root}{quality}. Confira a cifra.";
        string[] names = ["Dó", "Dó♯", "Ré", "Ré♯", "Mi", "Fá", "Fá♯", "Sol", "Sol♯", "Lá", "Lá♯", "Si"];
        var pitches = KeyboardDiagramVoicing.Pitches(pitchClass + transpose, quality.Trim(), inversion);
        string notes = string.Join("–", pitches.Select(pitch => names[pitch % 12] + (pitch / 12 - 1)));
        string position = inversion switch { 1 => "1ª inversão", 2 => "2ª inversão", _ => "Posição fundamental" };
        return $"{root}{quality} · {position}. Grave → agudo: {notes}."
            + (transpose != 0 ? " Teclado no tom final da partitura." : "");
    }
}
