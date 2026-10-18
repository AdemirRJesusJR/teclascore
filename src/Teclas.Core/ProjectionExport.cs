using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>
/// Lyrics for church projection software, made from a score's ChordPro sheet: an OpenLyrics file (the open format OpenLP
/// imports, chords kept inline) and a plain text file for Holyrics (lyrics only, one slide per stanza of up to four
/// lines). Instrumental lines are left out; "2ª vez" and other comments start a new slide.
/// </summary>
public static partial class ProjectionExport
{
    public const int LinesPerSlide = 4;
    private static readonly XNamespace Ns = "http://openlyrics.info/namespace/2009/song";

    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex ChordMark();

    /// <summary>Stanzas of sung lines (chords still inline as [C]); empty when the score has no words.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Stanzas(string chordPro)
    {
        var stanzas = new List<IReadOnlyList<string>>(); var current = new List<string>();
        void Close() { if (current.Count > 0) { stanzas.Add(current); current = []; } }
        foreach (var raw in (chordPro ?? "").Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith('{')) { if (line.StartsWith("{comment", StringComparison.OrdinalIgnoreCase)) Close(); continue; }
            if (line.Trim().Length == 0) { Close(); continue; }
            string words = ChordMark().Replace(line, "").Replace("|", "").Trim();
            if (!words.Any(char.IsLetter)) continue; // chords only: instrumental
            current.Add(line.Trim());
            if (current.Count == LinesPerSlide) Close();
        }
        Close();
        return stanzas;
    }

    public static string PlainLine(string line) => Regex.Replace(ChordMark().Replace(line, ""), @"\s{2,}", " ").Trim();

    /// <summary>Holyrics text: the lyrics, a blank line between slides.</summary>
    public static string Holyrics(IReadOnlyList<IReadOnlyList<string>> stanzas) =>
        string.Join("\r\n\r\n", stanzas.Select(s => string.Join("\r\n", s.Select(PlainLine)))) + "\r\n";

    /// <summary>OpenLyrics 0.9 XML for OpenLP, with the chords inline.</summary>
    public static string OpenLyrics(IReadOnlyList<IReadOnlyList<string>> stanzas, string title, string artist)
    {
        var lyrics = new XElement(Ns + "lyrics");
        for (int i = 0; i < stanzas.Count; i++)
        {
            var lines = new XElement(Ns + "lines");
            for (int l = 0; l < stanzas[i].Count; l++)
            {
                if (l > 0) lines.Add(new XElement(Ns + "br"));
                string line = stanzas[i][l]; int at = 0;
                foreach (Match match in ChordMark().Matches(line))
                {
                    if (match.Index > at) lines.Add(new XText(line[at..match.Index]));
                    if (match.Groups[1].Value.Trim().Length > 0) lines.Add(new XElement(Ns + "chord", new XAttribute("name", match.Groups[1].Value.Trim())));
                    at = match.Index + match.Length;
                }
                if (at < line.Length) lines.Add(new XText(line[at..]));
            }
            lyrics.Add(new XElement(Ns + "verse", new XAttribute("name", "v" + (i + 1)), lines));
        }
        var properties = new XElement(Ns + "properties", new XElement(Ns + "titles", new XElement(Ns + "title", title)));
        if (!string.IsNullOrWhiteSpace(artist)) properties.Add(new XElement(Ns + "authors", new XElement(Ns + "author", artist)));
        var song = new XElement(Ns + "song", new XAttribute("version", "0.9"), new XAttribute("createdIn", "TeclaScore"),
            new XAttribute("modifiedIn", "TeclaScore"), new XAttribute("modifiedDate", DateTime.Now.ToString("s")), properties, lyrics);
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), song);
        var builder = new StringBuilder();
        using (var writer = new Utf8StringWriter(builder)) document.Save(writer);
        return builder.ToString();
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder) { public override Encoding Encoding => Encoding.UTF8; }
}
