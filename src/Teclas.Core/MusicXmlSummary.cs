using System.Xml;

namespace Teclas.Core;

/// <summary>What an exported MusicXML file carries, checked after saving: notes, chord symbols and lyric syllables.</summary>
public sealed record MusicXmlSummary(int Notes, int Harmonies, int Lyrics)
{
    public static MusicXmlSummary Read(string path)
    {
        int notes = 0, harmonies = 0, lyrics = 0;
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName) { case "note": notes++; break; case "harmony": harmonies++; break; case "lyric": lyrics++; break; }
        }
        return new(notes, harmonies, lyrics);
    }

    /// <summary>Status line for the user; keyboard diagrams have no MusicXML element, so they are named as staying in the PDF.</summary>
    public string Describe(int expectedHarmonies, int expectedLyrics)
    {
        var parts = new List<string> { $"{Harmonies} cifra(s)" + (expectedHarmonies > 0 && Harmonies < expectedHarmonies ? $" de {expectedHarmonies} — confira" : "") };
        if (Lyrics > 0 || expectedLyrics > 0) parts.Add($"{Lyrics} sílaba(s) de letra" + (expectedLyrics > 0 && Lyrics < expectedLyrics ? $" de {expectedLyrics} — confira" : ""));
        return string.Join(", ", parts) + ". Os diagramas de teclado não existem no formato MusicXML: use o PDF para levá-los.";
    }
}
