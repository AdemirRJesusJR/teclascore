using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>
/// Page style for reading on a tablet: the same printable width (so systems, chords and diagrams keep the
/// layout checked on A4), narrow margins and a 3:4 portrait page that fills the screen. On a 4:3 tablet the
/// music shows about 17% larger than an A4 page. Applied to the export only (MuseScore -S); the score is not changed.
/// </summary>
public static class TabletPageStyle
{
    public const double SideMargin = 0.16, TopMargin = 0.2, DefaultPrintableWidth = 7.16535; // inches
    public const double Ratio = 4.0 / 3.0;

    /// <summary>Printable width in inches from the score's style; the MuseScore default when absent or unreadable.</summary>
    public static double PrintableWidth(string scorePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(scorePath);
            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".mss", StringComparison.OrdinalIgnoreCase))
                .Concat(archive.Entries.Where(e => e.FullName.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'))))
            {
                using var stream = entry.Open();
                var value = XDocument.Load(stream).Descendants("pagePrintableWidth").FirstOrDefault()?.Value;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double width) && width is > 2 and < 30) return width;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException) { }
        return DefaultPrintableWidth;
    }

    public static (double Width, double Height) Page(double printableWidth)
    {
        double width = printableWidth + 2 * SideMargin;
        return (width, width * Ratio);
    }

    /// <summary>MuseScore style file with only the page settings.</summary>
    public static string Create(double printableWidth)
    {
        var (width, height) = Page(printableWidth);
        string V(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
        var style = new XElement("Style",
            new XElement("pageWidth", V(width)), new XElement("pageHeight", V(height)), new XElement("pagePrintableWidth", V(printableWidth)),
            new XElement("pageEvenLeftMargin", V(SideMargin)), new XElement("pageOddLeftMargin", V(SideMargin)),
            new XElement("pageEvenTopMargin", V(TopMargin)), new XElement("pageEvenBottomMargin", V(TopMargin)),
            new XElement("pageOddTopMargin", V(TopMargin)), new XElement("pageOddBottomMargin", V(TopMargin)),
            new XElement("pageTwosided", "0"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + new XElement("museScore", new XAttribute("version", "4.70"), style);
    }

    /// <summary>Writes the style for this score next to the export job and returns its path.</summary>
    public static string WriteFor(string scorePath, string folder)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "tablet-" + Guid.NewGuid().ToString("N") + ".mss");
        File.WriteAllText(path, Create(PrintableWidth(scorePath)));
        return path;
    }
}
