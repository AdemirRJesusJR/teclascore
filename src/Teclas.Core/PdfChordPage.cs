using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>One chord of the song as printed in the score, with its keyboard diagram (SVG) when it has one.</summary>
public sealed record PdfChord(string Label, XElement? Diagram);

/// <summary>
/// Finishes a PDF exported by MuseScore: appends "Acordes desta música" (every chord of the score, in order of
/// first appearance, with its keyboard diagram drawn in vector at a readable size) and fills the document
/// properties (title, performer, subject). It is a PDF incremental update: the pages MuseScore wrote are kept
/// byte for byte, and the new objects, page tree and trailer are appended after them.
/// </summary>
public static class PdfChordPage
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private const double DiagramWidth = 158, Scale = DiagramWidth / 282;

    /// <summary>Chords (and the title and performer) of a native score, read from its .mscx and embedded pictures.</summary>
    public static (string Title, string Artist, IReadOnlyList<PdfChord> Chords) ReadScore(string scorePath)
    {
        using var zip = ZipFile.OpenRead(scorePath);
        var main = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("A partitura não contém o arquivo principal do MuseScore.");
        XDocument document;
        using (var stream = main.Open()) document = XDocument.Load(stream);
        var score = document.Root?.Element("Score") ?? throw new InvalidDataException("Partitura MuseScore sem conteúdo.");
        string Meta(string name) => score.Elements("metaTag").FirstOrDefault(m => (string?)m.Attribute("name") == name)?.Value.Trim() ?? "";
        var chords = new List<PdfChord>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        // Chords and diagrams live on the first staff.
        var staff = score.Elements("Staff").FirstOrDefault();
        foreach (var harmony in staff?.Descendants("Harmony") ?? [])
        {
            var info = harmony.Element("harmonyInfo") ?? harmony;
            if (!int.TryParse((string?)info.Element("root"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tpc) || TpcName(tpc) is not { } root) continue;
            string label = root + ((string?)info.Element("name") ?? "");
            string? bassValue = (string?)info.Element("bass") ?? (string?)info.Element("base");
            if (int.TryParse(bassValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bassTpc) && TpcName(bassTpc) is { } bass) label += "/" + bass;
            var image = harmony.ElementsAfterSelf().TakeWhile(e => e.Name.LocalName is not ("Harmony" or "Chord" or "Rest")).FirstOrDefault(e => e.Name == "Image");
            string? path = (string?)image?.Element("path");
            if (!seen.Add(label + "|" + path)) continue;
            XElement? diagram = null;
            if (path is not null && (zip.GetEntry("Pictures/" + Path.GetFileName(path)) ?? zip.GetEntry(path)) is { } entry && entry.Length < 256 * 1024)
                try { using var svgStream = entry.Open(); var svg = XDocument.Load(svgStream).Root; if (svg?.Name == Svg + "svg") diagram = svg; }
                catch (System.Xml.XmlException) { }
            chords.Add(new(label, diagram));
        }
        return (Meta("workTitle"), Meta("artist"), chords);
    }

    /// <summary>Note name of a MuseScore tonal pitch class (14 = C, 12 = Bb, 20 = F#).</summary>
    public static string? TpcName(int tpc)
    {
        if (tpc is < -1 or > 33) return null;
        int accidentals = (tpc + 1) / 7 - 2;
        return "FCGDAEB"[(tpc + 1) % 7] + (accidentals > 0 ? new string('#', accidentals) : new string('b', -accidentals));
    }

    /// <summary>Appends the chord page(s) and the document properties to <paramref name="pdfPath"/> in place.</summary>
    public static void Append(string pdfPath, string title, string artist, IReadOnlyList<PdfChord> chords, bool chordPage = true)
    {
        byte[] original = File.ReadAllBytes(pdfPath);
        string text = Encoding.Latin1.GetString(original);
        int startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        int trailerAt = text.LastIndexOf("trailer", startxref, StringComparison.Ordinal);
        if (startxref < 0 || trailerAt < 0) throw new InvalidDataException("O PDF não tem a tabela de referências esperada.");
        string trailer = text[trailerAt..startxref];
        long previous = long.Parse(Regex.Match(text[startxref..], @"startxref\s+(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        int size = int.Parse(Regex.Match(trailer, @"/Size\s+(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        int rootId = int.Parse(Regex.Match(trailer, @"/Root\s+(\d+)\s+0\s+R").Groups[1].Value, CultureInfo.InvariantCulture);
        string id = Regex.Match(trailer, @"/ID\s*\[[^\]]*\]").Value;
        string catalog = ObjectBody(text, rootId);
        int pagesId = int.Parse(Regex.Match(catalog, @"/Pages\s+(\d+)\s+0\s+R").Groups[1].Value, CultureInfo.InvariantCulture);
        string pages = ObjectBody(text, pagesId);
        var kids = Regex.Match(pages, @"/Kids\s*\[([^\]]*)\]");
        var count = Regex.Match(pages, @"/Count\s+(\d+)");
        if (!kids.Success || !count.Success) throw new InvalidDataException("O PDF não tem a árvore de páginas esperada.");
        var firstKid = Regex.Match(kids.Groups[1].Value, @"(\d+)\s+0\s+R");
        var media = firstKid.Success ? Regex.Match(ObjectBody(text, int.Parse(firstKid.Groups[1].Value, CultureInfo.InvariantCulture)), @"/MediaBox\s*\[([^\]]*)\]") : Match.Empty;
        double[] box = media.Success ? media.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray() : [0, 0, 595, 842];
        double width = box[2] - box[0], height = box[3] - box[1];

        var output = new MemoryStream(); output.Write(original);
        if (original[^1] != (byte)'\n') output.WriteByte((byte)'\n');
        var offsets = new SortedDictionary<int, long>();
        void Write(int number, string body)
        {
            offsets[number] = output.Position;
            output.Write(Encoding.Latin1.GetBytes($"{number} 0 obj\n{body}\nendobj\n"));
        }
        void WriteStream(int number, byte[] data)
        {
            offsets[number] = output.Position;
            output.Write(Encoding.Latin1.GetBytes($"{number} 0 obj\n<< /Length {data.Length} >>\nstream\n"));
            output.Write(data); output.Write("\nendstream\nendobj\n"u8);
        }
        int next = size;
        var newPages = new List<int>();
        var drawable = chords.Where(c => c.Diagram is not null).ToArray();
        if (chordPage && drawable.Length > 0)
        {
            int regular = next++, bold = next++;
            Write(regular, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            Write(bold, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
            const int columns = 3;
            double marginX = 46, top = height - 60, cell = (width - 2 * marginX) / columns, rowHeight = 92;
            int rows = Math.Max(1, (int)((top - 50) / rowHeight));
            int perPage = rows * columns;
            int pageCount = (drawable.Length + perPage - 1) / perPage;
            for (int page = 0; page < pageCount; page++)
            {
                var content = new StringBuilder();
                string heading = "Acordes desta música" + (title.Length > 0 ? " — " + title : "");
                Text(content, "F2", 15, marginX, height - 40, heading, "0 0 0", center: false);
                if (artist.Length > 0) Text(content, "F1", 10, marginX, height - 54, artist, "0.3 0.3 0.3", center: false);
                var slice = drawable.Skip(page * perPage).Take(perPage).ToArray();
                for (int index = 0; index < slice.Length; index++)
                {
                    double x = marginX + index % columns * cell, y = top - 18 - index / columns * rowHeight;
                    Text(content, "F2", 14, x + 2, y, slice[index].Label, "0 0 0", center: false);
                    DrawDiagram(content, slice[index].Diagram!, x, y - 8);
                }
                Text(content, "F1", 8, width / 2, 28, "Diagramas na posição usada na partitura; a tecla mais escura é a nota do grave.", "0.4 0.4 0.4", center: true);
                int contentId = next++, pageId = next++;
                WriteStream(contentId, Encoding.Latin1.GetBytes(content.ToString()));
                Write(pageId, $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [{Num(box[0])} {Num(box[1])} {Num(box[2])} {Num(box[3])}] /Resources << /Font << /F1 {regular} 0 R /F2 {bold} 0 R >> /ProcSet [/PDF /Text] >> /Contents {contentId} 0 R >>");
                newPages.Add(pageId);
            }
            string updatedPages = pages.Remove(kids.Index, kids.Length).Insert(kids.Index, "/Kids [" + kids.Groups[1].Value.Trim() + " " + string.Join(" ", newPages.Select(p => p + " 0 R")) + "]");
            var updatedCount = Regex.Match(updatedPages, @"/Count\s+(\d+)");
            updatedPages = updatedPages.Remove(updatedCount.Index, updatedCount.Length).Insert(updatedCount.Index, "/Count " + (int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture) + newPages.Count));
            Write(pagesId, updatedPages);
        }
        int infoId = next++;
        var info = new StringBuilder("<< ");
        if (title.Length > 0) info.Append("/Title ").Append(PdfString(title)).Append(' ');
        if (artist.Length > 0) info.Append("/Author ").Append(PdfString(artist)).Append(' ');
        info.Append("/Subject ").Append(PdfString("Melodia, cifras e diagramas de teclado")).Append(' ');
        info.Append("/Creator ").Append(PdfString("TeclaScore com MuseScore Studio")).Append(" >>");
        Write(infoId, info.ToString());

        long xref = output.Position;
        var table = new StringBuilder("xref\n");
        foreach (var run in Runs(offsets.Keys))
        {
            table.Append(run.First).Append(' ').Append(run.Count).Append('\n');
            for (int n = run.First; n < run.First + run.Count; n++) table.Append(offsets[n].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        table.Append($"trailer\n<< /Size {next} /Root {rootId} 0 R /Info {infoId} 0 R /Prev {previous} {id} >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Encoding.Latin1.GetBytes(table.ToString()));
        string temporary = pdfPath + ".acordes";
        File.WriteAllBytes(temporary, output.ToArray());
        File.Move(temporary, pdfPath, true);
    }

    /// <summary>The body of the latest definition of object <paramref name="number"/>.</summary>
    private static string ObjectBody(string text, int number)
    {
        var matches = Regex.Matches(text, $@"(?:^|[\r\n]){number}\s+0\s+obj\b");
        if (matches.Count == 0) throw new InvalidDataException($"Objeto {number} ausente no PDF.");
        int start = matches[^1].Index + matches[^1].Length;
        int end = text.IndexOf("endobj", start, StringComparison.Ordinal);
        return text[start..end].Trim();
    }

    private static IEnumerable<(int First, int Count)> Runs(IEnumerable<int> numbers)
    {
        int? first = null, last = null;
        foreach (int n in numbers)
        {
            if (first is null) { first = last = n; continue; }
            if (n == last + 1) { last = n; continue; }
            yield return (first.Value, last!.Value - first.Value + 1); first = last = n;
        }
        if (first is not null) yield return (first.Value, last!.Value - first.Value + 1);
    }

    /// <summary>Draws the SVG diagram (rect, circle, text) with its top-left corner at (x, y) in PDF points.</summary>
    private static void DrawDiagram(StringBuilder content, XElement svg, double x, double y)
    {
        double X(double value) => x + value * Scale;
        double Y(double value) => y - value * Scale;
        foreach (var element in svg.Elements())
        {
            string? fill = (string?)element.Attribute("fill"), stroke = (string?)element.Attribute("stroke");
            double lineWidth = Attr(element, "stroke-width", stroke is null ? 0 : 1) * Scale;
            switch (element.Name.LocalName)
            {
                case "rect":
                {
                    double rx = Attr(element, "x"), ry = Attr(element, "y"), w = Attr(element, "width"), h = Attr(element, "height");
                    content.Append(Paint(fill, stroke, lineWidth));
                    content.Append($"{Num(X(rx))} {Num(Y(ry + h))} {Num(w * Scale)} {Num(h * Scale)} re {Operator(fill, stroke)}\n");
                    break;
                }
                case "circle":
                {
                    double cx = X(Attr(element, "cx")), cy = Y(Attr(element, "cy")), r = Attr(element, "r") * Scale, k = r * .5523;
                    if (r <= 0) break;
                    content.Append(Paint(fill, stroke, lineWidth));
                    content.Append($"{Num(cx + r)} {Num(cy)} m {Num(cx + r)} {Num(cy + k)} {Num(cx + k)} {Num(cy + r)} {Num(cx)} {Num(cy + r)} c ");
                    content.Append($"{Num(cx - k)} {Num(cy + r)} {Num(cx - r)} {Num(cy + k)} {Num(cx - r)} {Num(cy)} c ");
                    content.Append($"{Num(cx - r)} {Num(cy - k)} {Num(cx - k)} {Num(cy - r)} {Num(cx)} {Num(cy - r)} c ");
                    content.Append($"{Num(cx + k)} {Num(cy - r)} {Num(cx + r)} {Num(cy - k)} {Num(cx + r)} {Num(cy)} c {Operator(fill, stroke)}\n");
                    break;
                }
                case "text":
                {
                    double size = Attr(element, "font-size", 12) * Scale;
                    bool boldText = (string?)element.Attribute("font-weight") == "bold";
                    string anchor = (string?)element.Attribute("text-anchor") ?? "start";
                    double tx = X(Attr(element, "x")), ty = Y(Attr(element, "y"));
                    string color = Rgb(fill ?? "#000000");
                    if (anchor == "middle") Text(content, boldText ? "F2" : "F1", size, tx, ty, element.Value, color, center: true, bold: boldText);
                    else Text(content, boldText ? "F2" : "F1", size, tx, ty, element.Value, color, center: false);
                    break;
                }
            }
        }
    }

    private static string Paint(string? fill, string? stroke, double width)
    {
        var paint = new StringBuilder();
        if (fill is not null and not "none") paint.Append(Rgb(fill)).Append(" rg ");
        if (stroke is not null and not "none") paint.Append(Rgb(stroke)).Append(" RG ").Append(Num(width)).Append(" w ");
        return paint.ToString();
    }

    private static string Operator(string? fill, string? stroke) =>
        (fill is not null and not "none", stroke is not null and not "none") switch { (true, true) => "B", (true, false) => "f", (false, true) => "S", _ => "n" };

    private static void Text(StringBuilder content, string font, double size, double x, double y, string value, string color, bool center, bool bold = false)
    {
        if (center) x -= TextWidth(value, size, font == "F2" || bold) / 2;
        content.Append($"BT {color} rg /{font} {Num(size)} Tf {Num(x)} {Num(y)} Td {PdfLatin1(value)} Tj ET\n");
    }

    /// <summary>Helvetica advance widths (per 1000 em) for the characters chord names and labels use.</summary>
    private static double TextWidth(string value, double size, bool bold)
    {
        double total = 0;
        foreach (char raw in value.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            char c = raw;
            total += c switch
            {
                ' ' => 278, '#' => 556, '/' => 278, '(' or ')' or '-' => 333, '.' or ',' => 278, ':' => 278,
                >= '0' and <= '9' => 556,
                'I' => 278, 'J' => 500, 'L' or 'F' or 'T' or 'Z' => 611, 'M' => 833, 'W' => 944,
                'C' or 'D' or 'H' or 'N' or 'R' or 'U' => 722, 'G' or 'O' or 'Q' => 778,
                >= 'A' and <= 'Z' => 667,
                'i' or 'j' or 'l' => 222, 'f' or 't' => 278, 'r' => 333, 'm' => 833, 'w' => 722,
                'c' or 'k' or 's' or 'v' or 'x' or 'y' or 'z' => 500,
                >= 'a' and <= 'z' => 556,
                _ => 556
            };
        }
        return total / 1000 * size * (bold ? 1.07 : 1);
    }

    private static double Attr(XElement element, string name, double fallback = 0) =>
        double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

    private static string Rgb(string color)
    {
        if (color == "white") return "1 1 1";
        if (color.StartsWith('#') && color.Length == 7)
        {
            int Hex(int start) => int.Parse(color.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return $"{Num(Hex(1) / 255.0)} {Num(Hex(3) / 255.0)} {Num(Hex(5) / 255.0)}";
        }
        return "0 0 0";
    }

    private static string Num(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A WinAnsi literal string for the standard fonts (accents kept; other symbols become ?).</summary>
    private static string PdfLatin1(string value)
    {
        var builder = new StringBuilder("(");
        foreach (char c in value.Replace('—', '-').Replace('→', '>').Replace('♯', '#').Replace('♭', 'b'))
        {
            char safe = c <= 255 ? c : '?';
            if (safe is '(' or ')' or '\\') builder.Append('\\');
            if (safe < 32) { builder.Append(' '); continue; }
            builder.Append(safe);
        }
        return builder.Append(')').ToString();
    }

    /// <summary>A UTF-16BE text string (with BOM) for the document properties.</summary>
    private static string PdfString(string value)
    {
        var builder = new StringBuilder("<FEFF");
        foreach (byte b in Encoding.BigEndianUnicode.GetBytes(value)) builder.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return builder.Append('>').ToString();
    }
}
