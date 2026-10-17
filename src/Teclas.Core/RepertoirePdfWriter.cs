using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Teclas.Core;

/// <summary>An 8-bit grayscale page image (white = 255) and its printed size.</summary>
public sealed record PdfPageImage(int Width, int Height, byte[] Gray, double WidthPoints, double HeightPoints);
/// <summary>One song in the repertoire: its title line and the page images (score pages, then the optional chord chart).</summary>
public sealed record RepertoireSong(string Title, string Artist, IReadOnlyList<string> Pages);

/// <summary>
/// Writes a repertoire as one PDF: an index page that links to each song, then every page as an image.
/// Each song is also a bookmark, so tablet readers can jump between songs. Images are compressed losslessly
/// (Flate), so the music keeps the sharpness of the rendering.
/// </summary>
public static class RepertoirePdfWriter
{
    private const int IndexRowsPerPage = 32;

    public static void Write(Stream output, string title, IReadOnlyList<RepertoireSong> songs, bool index,
        Func<string, PdfPageImage> loadPage, IProgress<int>? pageWritten = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(songs); ArgumentNullException.ThrowIfNull(loadPage);
        if (songs.Count == 0 || songs.Any(song => song.Pages.Count == 0)) throw new ArgumentException("Cada música precisa de pelo menos uma página.", nameof(songs));
        var pdf = new Writer(output);
        int catalog = pdf.Reserve(), pages = pdf.Reserve(), font = pdf.Reserve(), bold = pdf.Reserve(), outlines = pdf.Reserve();
        int indexPages = index ? (songs.Count + IndexRowsPerPage - 1) / IndexRowsPerPage : 0;
        var indexIds = Enumerable.Range(0, indexPages).Select(_ => pdf.Reserve()).ToArray();
        var songPageIds = songs.Select(song => song.Pages.Select(_ => pdf.Reserve()).ToArray()).ToArray();
        var outlineIds = songs.Select(_ => pdf.Reserve()).ToArray();
        var kids = indexIds.Concat(songPageIds.SelectMany(ids => ids)).ToArray();
        // Page numbers as printed in the index: the first page of each song, counting the index pages.
        var firstPage = new int[songs.Count]; int number = indexPages + 1;
        for (int i = 0; i < songs.Count; i++) { firstPage[i] = number; number += songs[i].Pages.Count; }

        pdf.Object(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /Outlines {outlines} 0 R /PageMode /UseOutlines >>");
        pdf.Object(pages, $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(id => $"{id} 0 R"))}] /Count {kids.Length} >>");
        pdf.Object(font, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        pdf.Object(bold, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        pdf.Object(outlines, $"<< /Type /Outlines /First {outlineIds[0]} 0 R /Last {outlineIds[^1]} 0 R /Count {songs.Count} >>");
        for (int i = 0; i < songs.Count; i++)
        {
            string links = (i > 0 ? $" /Prev {outlineIds[i - 1]} 0 R" : "") + (i + 1 < songs.Count ? $" /Next {outlineIds[i + 1]} 0 R" : "");
            pdf.Object(outlineIds[i], $"<< /Title {Unicode(Line(songs[i]))} /Parent {outlines} 0 R /Dest [{songPageIds[i][0]} 0 R /Fit]{links} >>");
        }

        for (int p = 0; p < indexPages; p++)
        {
            const double width = 595.28, height = 841.89, left = 56, right = width - 56, rowHeight = 22;
            var content = new StringBuilder();
            var annotations = new List<string>();
            double y = height - 72;
            if (p == 0)
            {
                content.Append(Text(bold, 22, left, y, title)); y -= 22;
                content.Append(Text(font, 10, left, y, $"{songs.Count} músicas · {DateTime.Now:dd/MM/yyyy}")); y -= 34;
            }
            else y -= 10;
            for (int i = p * IndexRowsPerPage; i < Math.Min(songs.Count, (p + 1) * IndexRowsPerPage); i++)
            {
                string label = Fit($"{i + 1}. {Line(songs[i])}", 11, right - left - 40);
                content.Append(Text(font, 11, left, y, label));
                string page = firstPage[i].ToString(CultureInfo.InvariantCulture);
                content.Append(Text(font, 11, right - Measure(page, 11), y, page));
                content.Append(FormattableString.Invariant($"0.85 g {left:0.##} {y - 6:0.##} m {right:0.##} {y - 6:0.##} l 0.4 w S 0 g\n"));
                annotations.Add(FormattableString.Invariant($"<< /Type /Annot /Subtype /Link /Rect [{left:0.##} {y - 6:0.##} {right:0.##} {y + 14:0.##}] /Border [0 0 0] /Dest [{songPageIds[i][0]} 0 R /Fit] >>"));
                y -= rowHeight;
            }
            int stream = pdf.Stream("", Encoding.Latin1.GetBytes(content.ToString()), compress: false);
            pdf.Object(indexIds[p], FormattableString.Invariant($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {width:0.##} {height:0.##}] /Resources << /Font << /F{font} {font} 0 R /F{bold} {bold} 0 R >> >> /Contents {stream} 0 R /Annots [{string.Join(" ", annotations)}] >>"));
        }

        int written = 0;
        for (int s = 0; s < songs.Count; s++)
            for (int p = 0; p < songs[s].Pages.Count; p++)
            {
                token.ThrowIfCancellationRequested();
                var image = loadPage(songs[s].Pages[p]);
                if (image.Width <= 0 || image.Height <= 0 || image.Gray.Length != image.Width * image.Height) throw new InvalidDataException("Página com tamanho inválido.");
                int xobject = pdf.Stream($"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8", image.Gray, compress: true);
                string draw = FormattableString.Invariant($"q {image.WidthPoints:0.###} 0 0 {image.HeightPoints:0.###} 0 0 cm /Im{xobject} Do Q\n");
                int stream = pdf.Stream("", Encoding.ASCII.GetBytes(draw), compress: false);
                pdf.Object(songPageIds[s][p], FormattableString.Invariant($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {image.WidthPoints:0.###} {image.HeightPoints:0.###}] /Resources << /XObject << /Im{xobject} {xobject} 0 R >> >> /Contents {stream} 0 R >>"));
                pageWritten?.Report(++written);
            }
        pdf.Finish(catalog, Unicode(title));
    }

    public static string Line(RepertoireSong song) => song.Artist.Length > 0 ? $"{song.Title} — {song.Artist}" : song.Title;

    /// <summary>Helvetica text (WinAnsi): accents kept, symbols outside Latin-1 replaced.</summary>
    private static string Text(int font, double size, double x, double y, string text) =>
        FormattableString.Invariant($"BT /F{font} {size:0.##} Tf {x:0.##} {y:0.##} Td ({Escape(text)}) Tj ET\n");

    internal static string Plain(string text) => new(text.Replace("—", "-").Replace("–", "-").Replace("…", "...").Replace("♯", "#").Replace("♭", "b")
        .Replace("“", "\"").Replace("”", "\"").Replace("‘", "'").Replace("’", "'").Select(c => c <= 0xFF && !char.IsControl(c) ? c : '?').ToArray());

    private static string Escape(string text) => Plain(text).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>Approximate Helvetica width (average glyph 0.52 em) to keep long titles inside the line.</summary>
    private static double Measure(string text, double size) => Plain(text).Length * size * .52;
    private static string Fit(string text, double size, double width)
    {
        if (Measure(text, size) <= width) return text;
        int keep = Math.Max(4, (int)(width / (size * .52)) - 1);
        return text[..Math.Min(text.Length, keep)].TrimEnd() + "…";
    }

    /// <summary>PDF text string in UTF-16BE, for bookmarks and the document title.</summary>
    private static string Unicode(string text) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    private sealed class Writer(Stream output)
    {
        private readonly List<long> offsets = [0];
        private long position;

        public int Reserve() { offsets.Add(-1); return offsets.Count - 1; }

        private void Raw(string text) => Raw(Encoding.Latin1.GetBytes(text));
        private void Raw(byte[] bytes) { if (position == 0) Header(); output.Write(bytes); position += bytes.Length; }
        private void Header()
        {
            byte[] header = [.. "%PDF-1.4\n%"u8, 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n'];
            output.Write(header); position = header.Length;
        }

        public void Object(int id, string body)
        {
            if (position == 0) Header();
            offsets[id] = position;
            Raw($"{id} 0 obj\n{body}\nendobj\n");
        }

        public int Stream(string dictionary, byte[] data, bool compress)
        {
            if (position == 0) Header();
            byte[] payload = data;
            if (compress)
            {
                using var buffer = new MemoryStream();
                using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
                payload = buffer.ToArray();
            }
            int id = Reserve(); offsets[id] = position;
            Raw($"{id} 0 obj\n<< {dictionary}{(dictionary.Length > 0 ? " " : "")}{(compress ? "/Filter /FlateDecode " : "")}/Length {payload.Length} >>\nstream\n");
            Raw(payload);
            Raw("\nendstream\nendobj\n");
            return id;
        }

        public void Finish(int catalog, string title)
        {
            int info = Reserve(); offsets[info] = position;
            Raw($"{info} 0 obj\n<< /Title {title} /Producer (TeclaScore) >>\nendobj\n");
            if (offsets.Skip(1).Any(offset => offset < 0)) throw new InvalidOperationException("Objeto do PDF reservado e não escrito.");
            long xref = position;
            var table = new StringBuilder($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
            foreach (long offset in offsets.Skip(1)) table.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            table.Append($"trailer\n<< /Size {offsets.Count} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Raw(table.ToString());
            output.Flush();
        }
    }
}
