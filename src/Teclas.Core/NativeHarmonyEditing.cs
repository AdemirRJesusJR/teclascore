using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Teclas.Core;

internal sealed record NativeHarmonyInfo(string Root, string Quality, bool CanEdit, string Restriction,
    bool HasDiagram, int? DiagramInversion);

/// <summary>Edits one evidenced native chord/keyboard pair; never rewrites shared image bytes.</summary>
internal static class NativeHarmonyEditing
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly Dictionary<char, int> NaturalTpc = new()
        { ['C'] = 14, ['D'] = 16, ['E'] = 18, ['F'] = 13, ['G'] = 15, ['A'] = 17, ['B'] = 19 };
    private static readonly HashSet<string> Presentation = new(StringComparer.Ordinal)
    {
        "eid", "offset", "pos", "autoplace", "minDistance", "color", "placement", "visible", "z",
        "fontFace", "fontSize", "fontStyle", "fontSpatiumDependent"
    };

    internal static NativeHarmonyInfo Inspect(NativeScoreArchive archive, XElement harmony)
    {
        if (harmony.Name != "Harmony" || harmony.Parent?.Name != "voice" ||
            !harmony.Ancestors().Any(parent => ReferenceEquals(parent, archive.Score)))
            return Block("", "", "Selecione uma cifra existente na partitura principal.");
        var info = harmony.Element("harmonyInfo") ?? harmony;
        string quality = (string?)info.Element("name") ?? "";
        if (!int.TryParse((string?)info.Element("root"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tpc) ||
            !TryRoot(tpc, out string root))
            return Block("", quality, "Esta cifra textual ou funcional não possui uma fundamental convencional editável.");
        if (harmony.Elements("harmonyInfo").Skip(1).Any() || info.Elements("root").Count() != 1 ||
            info.Elements("name").Count() > 1 || harmony.Descendants().Any(e =>
                e.Name.LocalName is "base" or "degree" or "function" or "text" or "extension" or "rootCase" or "baseCase"))
            return Block(root, quality, "Cifras com baixo alternativo, graus ou estruturas adicionais devem ser editadas no MuseScore.");
        if (!KeyboardDiagramVoicing.SupportsQuality(quality))
            return Block(root, quality, "O tipo desta cifra ainda não é compatível com a edição de diagramas do TeclaScore.");
        if (harmony.Elements().Any(e => e.Name == "harmonyType" && e.Value != "0") ||
            info.Elements().Any(e => e.Name != "root" && e.Name != "name" &&
                !(ReferenceEquals(info, harmony) && (Presentation.Contains(e.Name.LocalName) || e.Name == "harmonyType"))) ||
            (!ReferenceEquals(info, harmony) && harmony.Elements().Any(e => e.Name != "harmonyInfo" &&
                e.Name != "harmonyType" && !Presentation.Contains(e.Name.LocalName))))
            return Block(root, quality, "Esta representação de cifra contém propriedades que precisam ser preservadas no MuseScore.");
        var images = FollowingImages(harmony).ToArray();
        var preceding = harmony.ElementsBeforeSelf().LastOrDefault();
        bool ambiguousBefore = preceding?.Name == "Image" && preceding.ElementsBeforeSelf().LastOrDefault()?.Name != "Harmony";
        var next = harmony.ElementsAfterSelf().FirstOrDefault();
        if (ambiguousBefore || images.Length > 1 || (images.Length == 1 && next?.Name != "Image"))
            return Block(root, quality, "Não foi possível associar esta cifra a uma única imagem de teclado com segurança.", images.Length > 0 || ambiguousBefore);
        if (images.Length == 0) return new(root, quality, true, "", false, null);
        var image = images[0];
        if (!TryReadKeyboard(archive, image, PitchClass(tpc), quality, out int inversion))
            return Block(root, quality, "A imagem junto à cifra não corresponde a um diagrama de teclado reconhecido. Edite este par no MuseScore.", true);
        if (image.Element("offset") is { } offset && (!FiniteAttribute(offset, "x", out _) || !FiniteAttribute(offset, "y", out _)))
            return Block(root, quality, "A posição da imagem não pôde ser interpretada com segurança.", true);
        return new(root, quality, true, "", true, inversion);
    }

    internal static void Apply(NativeScoreArchive archive, XElement harmony, string root, string quality, int? diagramInversion)
    {
        string spelling = (root ?? "").Trim().Replace('♯', '#').Replace('♭', 'b');
        if (!Regex.IsMatch(spelling, "^[A-G](#{1,2}|b{1,2})?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Informe a fundamental como C, F#, Bb ou outra nota entre A e G.", nameof(root));
        quality = (quality ?? "").Trim();
        if (!KeyboardDiagramVoicing.SupportsQuality(quality)) throw new ArgumentException("Tipo de acorde não suportado pelo editor.", nameof(quality));
        if (diagramInversion is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(diagramInversion), "Escolha posição fundamental, primeira ou segunda inversão.");
        var current = Inspect(archive, harmony);
        if (!current.CanEdit) throw new InvalidOperationException(current.Restriction);
        if (spelling == current.Root && quality == current.Quality &&
            (!current.HasDiagram || diagramInversion is null || diagramInversion == current.DiagramInversion)) return;
        int tpc = NaturalTpc[spelling[0]] + 7 * (spelling.Count(c => c == '#') - spelling.Count(c => c == 'b'));
        var info = harmony.Element("harmonyInfo") ?? harmony;
        if (current.HasDiagram)
        {
            var image = harmony.ElementsAfterSelf().First();
            int inversion = diagramInversion ?? current.DiagramInversion!.Value;
            byte[] bytes = NativeScoreArchive.XmlBytes(new XDocument(KeyboardSvg(PitchClass(tpc), quality, inversion)));
            string name = Md4(bytes) + ".svg";
            string entry = "Pictures/" + name;
            if (archive.Entries.TryGetValue(entry, out var existing) && !existing.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("A nova imagem conflita com um recurso existente; a partitura original foi preservada.");
            // The content hash prevents MuseScore from renaming the path when reopening.
            archive.Entries.TryAdd(entry, bytes);
            RegisterAsset(archive, entry);
            image.SetElementValue("path", name);
            int extraCharacters = spelling.Length + quality.Length - current.Root.Length - current.Quality.Length;
            if (extraCharacters > 0)
            {
                var offset = image.Element("offset");
                double x = offset is null ? 4.5 : double.Parse(offset.Attribute("x")!.Value, CultureInfo.InvariantCulture);
                if (offset is null) { offset = new XElement("offset", new XAttribute("x", x), new XAttribute("y", -6.7)); image.Add(offset); }
                offset.SetAttributeValue("x", (x + extraCharacters * .8).ToString("0.######", CultureInfo.InvariantCulture));
            }
        }
        info.SetElementValue("root", tpc);
        if (quality.Length == 0) info.Element("name")?.Remove();
        else
        {
            // MuseScore serializes the suffix before the root. Keep that native order so
            // reopening the edited chord does not change its structural fingerprint.
            var name = info.Element("name");
            if (name is null) name = new XElement("name", quality);
            else { name.Value = quality; name.Remove(); }
            info.Element("root")!.AddBeforeSelf(name);
        }
    }

    private static NativeHarmonyInfo Block(string root, string quality, string reason, bool image = false) =>
        new(root, quality, false, reason, image, null);
    private static IEnumerable<XElement> FollowingImages(XElement harmony)
    {
        foreach (var element in harmony.ElementsAfterSelf())
        {
            if (element.Name.LocalName is "Harmony" or "Chord" or "Rest" or "tick" or "location") yield break;
            if (element.Name == "Image") yield return element;
        }
    }
    private static int PitchClass(int tpc) => ((tpc * 7 - 2) % 12 + 12) % 12;
    private static bool TryRoot(int tpc, out string root)
    {
        if (tpc is < -1 or > 33) { root = ""; return false; }
        foreach (var (letter, natural) in NaturalTpc)
        {
            int delta = tpc - natural;
            if (delta % 7 == 0 && Math.Abs((long)delta / 7) <= 2)
            {
                root = letter + (delta >= 0 ? new string('#', delta / 7) : new string('b', -delta / 7));
                return true;
            }
        }
        root = ""; return false;
    }
    private static bool FiniteAttribute(XElement element, string attribute, out double value) =>
        double.TryParse((string?)element.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static bool TryReadKeyboard(NativeScoreArchive archive, XElement image, int root, string quality, out int inversion)
    {
        inversion = 0;
        string? path = (string?)image.Element("path");
        if (path is null || (!archive.Entries.TryGetValue("Pictures/" + path, out var bytes) && !archive.Entries.TryGetValue(path, out bytes)) || bytes.Length > 128 * 1024)
            return false;
        XDocument document;
        try { document = NativeScoreArchive.ParseXml(bytes); }
        catch (InvalidDataException) { return false; }
        if (document.Root?.Name != Svg + "svg") return false;
        var actual = CanonicalSvg(document.Root);
        for (int candidate = 0; candidate < 3; candidate++)
        {
            if (!XNode.DeepEquals(actual, CanonicalSvg(KeyboardSvg(root, quality, candidate)))) continue;
            inversion = candidate; return true;
        }
        return false;
    }
    private static XElement CanonicalSvg(XElement element) => new(element.Name,
        element.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal),
        element.Nodes().Where(n => n is not XText t || !string.IsNullOrWhiteSpace(t.Value))
            .Select(n => n is XElement child ? child.Name == Svg + "title" || child.Name == Svg + "desc" ? null :
                (object)CanonicalSvg(child) : n is XText text ? (object)new XText(text.Value) : null));

    private static int[] Pitches(int root, string quality, int inversion) =>
        KeyboardDiagramVoicing.Pitches(root, quality, inversion);
    private static XElement KeyboardSvg(int root, string quality, int inversion)
    {
        int[] white = Enumerable.Range(48, 24).Where(p => new[] { 0, 2, 4, 5, 7, 9, 11 }.Contains(p % 12)).ToArray();
        int[] pitches = Pitches(root, quality, inversion);
        var svg = new XElement(Svg + "svg", new XAttribute("width", "282"), new XAttribute("height", "84"), new XAttribute("viewBox", "0 0 282 84"),
            new XElement(Svg + "title", new[] { "Acorde em posição fundamental", "Acorde em primeira inversão", "Acorde em segunda inversão" }[inversion]),
            new XElement(Svg + "desc", new[] { "Fundamental no grave", "Terça ou nota suspensa no grave", "Quinta no grave" }[inversion] + "; notas MIDI: " + string.Join(',', pitches)));
        for (int i = 0; i < white.Length; i++)
            svg.Add(new XElement(Svg + "rect", new XAttribute("x", 1 + i * 20), new XAttribute("y", "1"), new XAttribute("width", "20"), new XAttribute("height", "60"),
                new XAttribute("fill", "white"), new XAttribute("stroke", "#111111"), new XAttribute("stroke-width", "1")));
        foreach (int p in Enumerable.Range(48, 24).Except(white))
            svg.Add(new XElement(Svg + "rect", new XAttribute("x", 1 + (Array.IndexOf(white, p - 1) + 1) * 20 - 6), new XAttribute("y", "1"),
                new XAttribute("width", "12"), new XAttribute("height", "37"), new XAttribute("fill", "#111111")));
        foreach (int p in pitches)
        {
            bool natural = white.Contains(p);
            int x = natural ? 11 + Array.IndexOf(white, p) * 20 : 1 + (Array.IndexOf(white, p - 1) + 1) * 20;
            svg.Add(new XElement(Svg + "circle", new XAttribute("cx", x), new XAttribute("cy", natural ? 49 : 29), new XAttribute("r", "4.2"),
                new XAttribute("fill", natural ? "#111111" : "white"), new XAttribute("data-midi", p)));
        }
        foreach (int x in new[] { 11, 151 })
            svg.Add(new XElement(Svg + "text", new XAttribute("x", x), new XAttribute("y", "79"), new XAttribute("fill", "#333333"),
                new XAttribute("font-family", "Arial"), new XAttribute("font-size", "16"), new XAttribute("text-anchor", "middle"), "C"));
        return svg;
    }
    private static void RegisterAsset(NativeScoreArchive archive, string entry)
    {
        XDocument container;
        if (archive.Entries.TryGetValue("META-INF/container.xml", out var bytes)) container = NativeScoreArchive.ParseXml(bytes);
        else container = new XDocument(new XElement("container", new XElement("rootfiles", new XElement("rootfile", new XAttribute("full-path", archive.MainEntry)))));
        var files = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfiles")
            ?? throw new InvalidDataException("O índice de recursos do MSCZ não foi reconhecido.");
        if (!files.Elements().Any(e => (string?)e.Attribute("full-path") == entry))
            files.Add(new XElement(files.Name.Namespace + "rootfile", new XAttribute("full-path", entry)));
        archive.Entries["META-INF/container.xml"] = NativeScoreArchive.XmlBytes(container);
    }

    // MuseScore uses MD4 as an image content identifier, not as a security boundary.
    private static string Md4(byte[] bytes)
    {
        int padded = checked((bytes.Length + 9 + 63) / 64 * 64);
        byte[] data = new byte[padded]; bytes.CopyTo(data, 0); data[bytes.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(padded - 8), (ulong)bytes.Length * 8);
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;
        int[] shifts1 = [3, 7, 11, 19], shifts2 = [3, 5, 9, 13], shifts3 = [3, 9, 11, 15];
        int[] order3 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
        unchecked
        {
            for (int offset = 0; offset < data.Length; offset += 64)
            {
                uint[] x = new uint[16]; for (int i = 0; i < 16; i++) x[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + i * 4));
                uint aa = a, bb = b, cc = c, dd = d;
                for (int round = 0; round < 3; round++)
                    for (int i = 0; i < 16; i++)
                    {
                        uint f = round == 0 ? (b & c) | (~b & d) : round == 1 ? (b & c) | (b & d) | (c & d) : b ^ c ^ d;
                        int index = round == 0 ? i : round == 1 ? (i % 4) * 4 + i / 4 : order3[i];
                        uint constant = round == 0 ? 0u : round == 1 ? 0x5a827999u : 0x6ed9eba1u;
                        int shift = (round == 0 ? shifts1 : round == 1 ? shifts2 : shifts3)[i % 4];
                        uint next = BitOperations.RotateLeft(a + f + x[index] + constant, shift);
                        a = d; d = c; c = b; b = next;
                    }
                a += aa; b += bb; c += cc; d += dd;
            }
        }
        byte[] digest = new byte[16]; uint[] state = [a, b, c, d];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(i * 4), state[i]);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
