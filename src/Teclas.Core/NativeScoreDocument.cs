using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Teclas.Core;

public sealed record NativeScoreMetadata(string Title, string Subtitle, string Composer, int StaffCount,
    int MeasureCount, int NoteCount, int HarmonyCount, int ImageCount, double PageWidthMm,
    double PageHeightMm, string MuseScoreVersion)
{
    public string Artist { get; init; } = "";
    public string ArrangementSummary { get; init; } = "";
}

/// <summary>Reads a native archive in memory, without extracting user-supplied ZIP paths.</summary>
public static class NativeScoreDocument
{
    public static NativeScoreMetadata Inspect(string path) => NativeScoreArchive.Read(path).Metadata;
    public static string MusicalFingerprint(string path) => NativeScoreArchive.Read(path).MusicalFingerprint();
}

internal sealed class NativeScoreArchive
{
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumXmlBytes = 32L * 1024 * 1024;
    internal Dictionary<string, byte[]> Entries { get; } = new(StringComparer.Ordinal);
    internal required string MainEntry { get; init; }
    internal required XDocument MainDocument { get; init; }
    internal XElement Score => MainDocument.Root!.Element("Score")!;

    internal static NativeScoreArchive Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".mscz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Selecione uma partitura MuseScore no formato .mscz.");
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("A partitura contém arquivos demais para ser aberta com segurança.");
        long total = 0;
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            total = checked(total + entry.Length);
            if (total > MaximumArchiveBytes || entry.Length > MaximumArchiveBytes)
                throw new InvalidDataException("A partitura ultrapassa o limite de 256 MB descompactados.");
            if (!entries.TryAdd(entry.FullName, [])) throw new InvalidDataException("A partitura contém entradas duplicadas no arquivo MSCZ.");
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            byte[] block = new byte[81920];
            int read;
            while ((read = input.Read(block)) != 0)
            {
                if (buffer.Length + read > entry.Length || buffer.Length + read > MaximumArchiveBytes)
                    throw new InvalidDataException("Uma entrada do MSCZ excede o tamanho declarado.");
                buffer.Write(block, 0, read);
            }
            if (buffer.Length != entry.Length) throw new InvalidDataException("O arquivo MSCZ está incompleto.");
            entries[entry.FullName] = buffer.ToArray();
        }
        string? main = null;
        if (entries.TryGetValue("META-INF/container.xml", out var container))
        {
            main = ParseXml(container).Descendants().Where(e => e.Name.LocalName == "rootfile")
                .Select(e => (string?)e.Attribute("full-path"))
                .FirstOrDefault(name => name?.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase) == true);
        }
        main ??= entries.Keys.FirstOrDefault(name => !name.Contains('/') && name.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase));
        if (main is null || !entries.TryGetValue(main, out var data))
            throw new InvalidDataException("O MSCZ não contém a partitura principal indicada pelo MuseScore.");
        var document = ParseXml(data);
        if (document.Root?.Name != "museScore" || document.Root.Element("Score") is null)
            throw new InvalidDataException("O conteúdo não é uma partitura MuseScore válida.");
        string version = (string?)document.Root.Attribute("version") ?? "";
        if (!version.StartsWith("4.", StringComparison.Ordinal))
            throw new InvalidDataException("Abra e salve uma cópia desta partitura no MuseScore Studio 4 antes de importá-la. Esta edição preserva o formato nativo da versão 4.");
        var result = new NativeScoreArchive { MainEntry = main, MainDocument = document };
        foreach (var entry in entries) result.Entries.Add(entry.Key, entry.Value);
        return result;
    }

    internal static XDocument ParseXml(byte[] bytes)
    {
        if (bytes.Length > MaximumXmlBytes) throw new InvalidDataException("O XML da partitura ultrapassa o limite de 32 MB.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            IgnoreWhitespace = true, MaxCharactersInDocument = MaximumXmlBytes
        });
        try { return XDocument.Load(reader); }
        catch (XmlException error) { throw new InvalidDataException("O XML da partitura MuseScore está inválido ou incompleto.", error); }
    }

    internal NativeScoreMetadata Metadata
    {
        get
        {
            var score = Score;
            string Meta(string name) => score.Elements("metaTag").FirstOrDefault(e => (string?)e.Attribute("name") == name)?.Value ?? "";
            string Header(string style, bool preserveLineBreaks = false)
            {
                var text = score.Elements("Staff").FirstOrDefault()?.Elements("VBox")
                    .Elements("Text").FirstOrDefault(e => (string?)e.Element("style") == style)?.Element("text");
                if (text is null) return "";
                var copy = new XElement(text);
                foreach (var lineBreak in copy.Descendants("br").ToArray()) lineBreak.ReplaceWith(new XText(preserveLineBreaks ? "\n" : " "));
                return copy.Value;
            }
            string credit = Header("composer", preserveLineBreaks: true);
            bool labeledCredit = credit.StartsWith("Intérprete: ", StringComparison.Ordinal);
            string CreditValue(string label)
            {
                var values = new List<string>(); bool collecting = false;
                foreach (string line in credit.Split('\n'))
                {
                    if (line.StartsWith(label, StringComparison.Ordinal)) { values.Add(line[label.Length..].Trim()); collecting = true; }
                    else if (line.StartsWith("Autor: ", StringComparison.Ordinal) || line.StartsWith("Intérprete: ", StringComparison.Ordinal)) collecting = false;
                    else if (collecting) values.Add(line.Trim());
                }
                return string.Join(' ', values);
            }
            string Composer() => Nonempty(Meta("composer"), labeledCredit ? CreditValue("Autor: ") : Header("composer"));
            var style = ReadStyle();
            double Dimension(string name, double fallback) => double.TryParse((string?)style.Element(name), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0 ? Math.Round(value * 25.4, 2) : fallback;
            return new(Nonempty(Meta("workTitle"), Header("title")), Nonempty(Meta("subtitle"), Header("subtitle")),
                Composer(), score.Elements("Staff").Count(),
                score.Elements("Staff").Select(s => s.Elements("Measure").Count()).DefaultIfEmpty().Max(),
                score.Elements("Staff").Descendants("Note").Count(), score.Elements("Staff").Descendants("Harmony").Count(),
                score.Descendants("Image").Count(), Dimension("pageWidth", 210), Dimension("pageHeight", 297),
                (string?)MainDocument.Root?.Element("programVersion") ?? (string?)MainDocument.Root?.Attribute("version") ?? "4")
            {
                Artist = Nonempty(Meta("artist"), labeledCredit ? CreditValue("Intérprete: ") : ""),
                ArrangementSummary = Meta("teclasArrangementSummary")[..Math.Min(200, Meta("teclasArrangementSummary").Length)]
            };
        }
    }

    internal XElement ReadStyle() => Entries.TryGetValue("score_style.mss", out var style)
        ? ParseXml(style).Root?.Element("Style") ?? new XElement("Style")
        : Score.Element("Style") is { } inline ? new XElement(inline) : new XElement("Style");

    internal void WriteStyle(XElement style)
    {
        if (Entries.ContainsKey("score_style.mss"))
        {
            var document = ParseXml(Entries["score_style.mss"]);
            if (document.Root?.Element("Style") is { } previous) previous.ReplaceWith(style);
            else document.Root!.Add(style);
            Entries["score_style.mss"] = XmlBytes(document);
        }
        else if (Score.Element("Style") is { } previous) previous.ReplaceWith(style);
        else Score.AddFirst(style);
    }

    internal void Save(string path)
    {
        Entries[MainEntry] = XmlBytes(MainDocument);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var entry in Entries)
        {
            var target = archive.CreateEntry(entry.Key, CompressionLevel.Optimal);
            using var output = target.Open();
            output.Write(entry.Value);
        }
    }

    /// <summary>
    /// Conservatively compares all native score structure, dropping only known presentation fields.
    /// Unknown musical elements remain in the comparison, so a changed unsupported feature fails closed.
    /// Embedded images are checked separately by content as well as by their place in the score.
    /// </summary>
    internal string MusicalFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var documents = new[] { MainEntry }.Concat(Entries.Keys.Where(name => name != MainEntry &&
            name.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal));
        foreach (string name in documents)
        {
            var document = name == MainEntry ? MainDocument : ParseXml(Entries[name]);
            var score = document.Root?.Element("Score") ?? throw new InvalidDataException("Parte MuseScore sem conteúdo musical.");
            hash.AppendData(Encoding.UTF8.GetBytes(Canonical(score).ToString(SaveOptions.DisableFormatting)));
            hash.AppendData([0]);
            foreach (var image in score.Descendants("Image"))
            {
                string? imagePath = (string?)image.Element("path");
                if (imagePath is null) throw new InvalidDataException("Há uma imagem sem referência na partitura.");
                if (!Entries.TryGetValue("Pictures/" + imagePath, out var bytes) && !Entries.TryGetValue(imagePath, out bytes))
                    throw new InvalidDataException("A partitura possui imagens externas. Incorpore-as no MuseScore antes de continuar.");
                hash.AppendData(SHA256.HashData(bytes));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static readonly HashSet<string> PresentationElements = new(StringComparer.Ordinal)
    {
        "eid", "Style", "metaTag", "showInvisible", "showUnprintable", "showFrames", "showMargins", "open",
        "layoutMode", "pageNumberOffset", "offset", "pos", "autoplace", "minDistance", "leadingSpace", "color"
    };

    private static XElement Canonical(XElement source)
    {
        var result = new XElement(source.Name, source.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal)
            .Select(a => new XAttribute(a)));
        foreach (var node in source.Nodes())
        {
            if (node is XElement element)
            {
                if (PresentationElements.Contains(element.Name.LocalName)) continue;
                if (element.Name == "LayoutBreak" && (string?)element.Element("subtype") is "line" or "page") continue;
                if (element.Name == "Text" && element.Parent?.Name == "VBox" &&
                    (string?)element.Element("style") is "title" or "subtitle" or "composer") continue;
                if (source.Name.LocalName is "VBox" or "HBox" or "TBox" or "FBox" &&
                    element.Name.LocalName is "height" or "width" or "topGap" or "bottomGap" or "leftMargin" or "rightMargin" or "topMargin" or "bottomMargin" or "boxAutoSize" or "sizeIsSpatiumDependent") continue;
                var child = Canonical(element);
                if (child.Name == "VBox" && !child.HasElements && string.IsNullOrWhiteSpace(child.Value)) continue;
                // MuseScore may remove a segment whose only property was visual spacing.
                if (child.Name == "Segment" && !child.HasElements && string.IsNullOrWhiteSpace(child.Value)) continue;
                result.Add(child);
            }
            else if (node is XText text) result.Add(new XText(text.Value));
        }
        return result;
    }

    internal static byte[] XmlBytes(XDocument document)
    {
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            document.Save(writer);
        return buffer.ToArray();
    }

    private static string Nonempty(string first, string second) => string.IsNullOrEmpty(first) ? second : first;
}
