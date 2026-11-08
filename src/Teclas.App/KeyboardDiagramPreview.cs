using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Teclas.Core;

namespace Teclas.App;

// Shares the score's voicings; the drawing uses the same two-octave keyboard as its SVG.
// A dialog-local cache bounds lifetime and avoids drawing repeated chords again.
internal sealed class KeyboardDiagramPreview
{
    /// <summary>Name under the C keys: "C", or "Dó" with solfège note names (Configurações).</summary>
    internal static string CLabel { get; set; } = "C";
    /// <summary>Drawing of the diagrams (Configurações): "destacado", "cinza" or "classico".</summary>
    internal static string Style { get; set; } = KeyboardDiagramSvg.Highlight;
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
        var key = (transposedRoot, quality.Trim() + "|" + CLabel + "|" + Style + "|" + root, inversion, bassClass);
        if (images.TryGetValue(key, out var image)) return image;
        if (Style != KeyboardDiagramSvg.Classic)
        {
            string spelling = transpose == 0 ? root : "";
            image = new DrawingImage(DrawSvg(KeyboardDiagramSvg.Build(transposedRoot, quality.Trim(), inversion, Style, CLabel == "Dó", spelling, root.Contains('b')), bassClass));
            image.Freeze(); images.Add(key, image);
            return image;
        }
        var pitches = KeyboardDiagramVoicing.Pitches(transposedRoot, quality.Trim(), inversion);
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
            var label = new FormattedText(CLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 16, new SolidColorBrush(Color.FromRgb(51, 51, 51)), 1);
            foreach (int center in new[] { 11, 151 })
                dc.DrawText(label, new Point(center - label.Width / 2, 79 - label.Baseline));
        }
        image = new DrawingImage(drawing);
        image.Freeze();
        images.Add(key, image);
        return image;
    }

    /// <summary>Draws the engine's highlighted SVG (rects and texts only) so the app shows exactly what the score prints.</summary>
    private static DrawingGroup DrawSvg(System.Xml.Linq.XElement svg, int bassClass)
    {
        var drawing = new DrawingGroup();
        double Number(System.Xml.Linq.XElement e, string name) => double.Parse((string?)e.Attribute(name) ?? "0", CultureInfo.InvariantCulture);
        Brush Paint(string? value) => value is null or "none" ? Brushes.Transparent
            : value == "white" ? Brushes.White : new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 282, 84));
            var bassMarks = new List<Rect>();
            foreach (var element in svg.Elements())
            {
                if (element.Name.LocalName == "rect")
                {
                    var rect = new Rect(Number(element, "x"), Number(element, "y"), Number(element, "width"), Number(element, "height"));
                    var stroke = element.Attribute("stroke") is { } s ? new Pen(Paint(s.Value), Number(element, "stroke-width")) : null;
                    dc.DrawRectangle(Paint((string?)element.Attribute("fill")), stroke, rect);
                }
                else if (element.Name.LocalName == "text")
                {
                    double size = Number(element, "font-size");
                    var text = new FormattedText(element.Value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Arial"), FontStyles.Normal, (string?)element.Attribute("font-weight") == "bold" ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
                        size, Paint((string?)element.Attribute("fill")), 1);
                    dc.DrawText(text, new Point(Number(element, "x") - text.Width / 2, Number(element, "y") - text.Baseline));
                }
            }
            if (bassClass >= 0)
            {
                // The bass of a slash chord: a blue ring at the foot of the lowest key with that name in the window.
                var keys = svg.Elements().Where(e => e.Name.LocalName == "rect").ToArray();
                var whites = keys.Where(e => Number(e, "height") == 60).ToArray();
                int start = KeyboardDiagramSvg.WindowStart(KeyboardDiagramSvg.MarkedPitches(svg));
                for (int pitch = start; pitch < start + 18; pitch++)
                {
                    if (((pitch % 12) + 12) % 12 != bassClass) continue;
                    int[] natural = [0, 2, 4, 5, 7, 9, 11];
                    int whiteIndex = Enumerable.Range(start, pitch - start + 1).Count(p => natural.Contains(p % 12)) - 1;
                    bool isWhite = natural.Contains(pitch % 12);
                    if (whiteIndex >= whites.Length) break;
                    var centre = isWhite ? new Point(1 + whiteIndex * 28 + 14, 44) : new Point(1 + (whiteIndex + 1) * 28, 22);
                    dc.DrawEllipse(null, new Pen(new SolidColorBrush(BassColor), 2.5), centre, 7, 7);
                    break;
                }
            }
        }
        return drawing;
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
