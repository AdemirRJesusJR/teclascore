using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Teclas.Core;

public sealed record ScoreLayoutOptions
{
    // Null preserves an existing value; an empty string explicitly removes it.
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Composer { get; init; }
    public string? Artist { get; init; }
    public double MarginMm { get; init; } = 14;
    public double StaffSpaceMm { get; init; } = 1.75;
    public double MinimumSystemDistance { get; init; } = 12;
    public double TitleFontSize { get; init; } = 20;
    public double SubtitleFontSize { get; init; } = 12;
    public double ComposerFontSize { get; init; } = 11;
    public bool RedistributeSystems { get; init; }
}

public sealed record NativeScoreResult(string Path, IReadOnlyList<string> PreviewPages,
    NativeScoreMetadata Metadata, string MusicalFingerprint);

/// <summary>Edits only the presentation of native MuseScore 4 scores and validates a new copy.</summary>
public sealed class ScoreEditingService(string museScorePath, string cacheDirectory)
{
    public async Task<NativeScoreResult> ImportAsync(string path, string outputDirectory,
        IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        progress?.Report("Lendo a partitura MuseScore e conferindo seu conteúdo…");
        byte[] originalHash = await HashAsync(path, token);
        var source = await Task.Run(() => NativeScoreArchive.Read(path), token);
        string fingerprint = await Task.Run(source.MusicalFingerprint, token);
        token.ThrowIfCancellationRequested();
        string job = CreateJob(outputDirectory, path, "prévia");
        string snapshot = Path.Combine(job, ScoreFileName.Stem(source.Metadata.Artist, source.Metadata.Title, path) + ".mscz");
        try
        {
            // The library's first version must survive moving or editing the user's source later.
            progress?.Report("Preservando uma cópia da partitura original na biblioteca…");
            await Task.Run(() => File.Copy(path, snapshot, overwrite: false), token);
            await CheckSourceAsync(snapshot, originalHash, token);
            progress?.Report("Abrindo todas as páginas da partitura MuseScore…");
            var pages = await new ScoreExporter(museScorePath, cacheDirectory).ExportPreviewAsync(snapshot, Path.Combine(job, "pagina.png"), token);
            progress?.Report("Conferindo o arquivo original e finalizando a importação…");
            await CheckSourceAsync(path, originalHash, token);
            return new(snapshot, pages, source.Metadata, fingerprint);
        }
        catch { DeleteTemporary(snapshot); throw; }
    }

    public async Task<NativeScoreResult> ImproveAsync(string path, string outputDirectory, ScoreLayoutOptions options,
        IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateOptions(options);
        path = Path.GetFullPath(path);
        progress?.Report("Lendo a partitura e registrando notas, ritmos, cifras e diagramas…");
        byte[] originalHash = await HashAsync(path, token);
        var source = await Task.Run(() => NativeScoreArchive.Read(path), token);
        if (options.RedistributeSystems && source.Metadata.ImageCount > 0)
            throw new InvalidOperationException("Esta partitura contém diagramas ou imagens. Mantenha as quebras existentes para evitar imagens cortadas; margens, fontes e cabeçalho continuam disponíveis.");
        string fingerprint = await Task.Run(source.MusicalFingerprint, token);
        token.ThrowIfCancellationRequested();
        string job = CreateJob(outputDirectory, path, "revisada");
        string prepared = Path.Combine(job, ".preparada.mscz");
        string rendered = Path.Combine(job, ".validando.mscz");
        try
        {
            progress?.Report("Ajustando margens, fontes e espaçamento da nova cópia…");
            await Task.Run(() =>
            {
                ApplyLayout(source, options);
                token.ThrowIfCancellationRequested();
                if (source.MusicalFingerprint() != fingerprint)
                    throw new InvalidDataException("O ajuste de apresentação modificou conteúdo musical. A operação foi interrompida e o original permanece intacto.");
                token.ThrowIfCancellationRequested();
                source.Save(prepared);
            }, token);
            token.ThrowIfCancellationRequested();
            var exporter = new ScoreExporter(museScorePath, cacheDirectory);
            progress?.Report("Conferindo a cópia no MuseScore e preservando notas, ritmos, cifras e diagramas…");
            await exporter.ExportNativeAsync(prepared, rendered, token);
            progress?.Report("Verificando se a cópia preservou a música e o cabeçalho…");
            var final = await Task.Run(() => NativeScoreArchive.Read(rendered), token);
            string finalFingerprint = await Task.Run(final.MusicalFingerprint, token);
            token.ThrowIfCancellationRequested();
            if (finalFingerprint != fingerprint)
                throw new InvalidDataException("O MuseScore alterou uma estrutura da partitura ao salvá-la. A revisão não foi publicada; o arquivo original foi preservado. Abra e salve uma cópia no MuseScore antes de tentar novamente.");
            ValidateMetadata(final.Metadata, source.Metadata);
            progress?.Report("Gerando todas as páginas da partitura revisada…");
            var pages = await exporter.ExportPreviewAsync(rendered, Path.Combine(job, "pagina.png"), token);
            progress?.Report("Conferindo o arquivo original e salvando a versão revisada…");
            await CheckSourceAsync(path, originalHash, token);
            token.ThrowIfCancellationRequested();
            string destination = Path.Combine(job, ScoreFileName.Stem(final.Metadata.Artist, final.Metadata.Title, path) + ".mscz");
            File.Move(rendered, destination); // New unique destination; no overwrite of any approved version.
            return new(destination, pages, final.Metadata, fingerprint);
        }
        finally { DeleteTemporary(prepared); DeleteTemporary(rendered); }
    }

    private static void ApplyLayout(NativeScoreArchive document, ScoreLayoutOptions options)
    {
        var style = document.ReadStyle();
        double width = Number(style.Element("pageWidth"), 210 / 25.4);
        double height = Number(style.Element("pageHeight"), 297 / 25.4);
        double margin = options.MarginMm / 25.4;
        if (width - 2 * margin < 2 || height - 2 * margin < 2)
            throw new InvalidOperationException("As margens escolhidas deixam menos de 5 cm de área útil. Reduza-as para este tamanho de página.");
        Set(style, "pagePrintableWidth", width - 2 * margin);
        foreach (string name in new[] { "pageEvenLeftMargin", "pageOddLeftMargin", "pageEvenTopMargin", "pageEvenBottomMargin", "pageOddTopMargin", "pageOddBottomMargin" })
            Set(style, name, margin);
        // MuseScore 4 stores lowercase spatium; old inline styles may contain its legacy spelling.
        Set(style, "spatium", options.StaffSpaceMm);
        if (style.Element("Spatium") is { } oldSpatium) oldSpatium.Remove();
        // Never shrink an existing allowance around unusually high notes or embedded keyboard diagrams.
        Set(style, "minSystemDistance", Math.Max(options.MinimumSystemDistance, Number(style.Element("minSystemDistance"), 0)));
        Set(style, "maxSystemDistance", Math.Max(options.MinimumSystemDistance + 8, Number(style.Element("maxSystemDistance"), 0)));
        foreach (var (name, size) in new[] { ("title", options.TitleFontSize), ("subTitle", options.SubtitleFontSize), ("composer", options.ComposerFontSize) })
        {
            Set(style, name + "FontFace", "Edwin"); Set(style, name + "FontSize", size); Set(style, name + "FontSpatiumDependent", 0);
        }
        document.WriteStyle(style);
        ApplyHeader(document, options, width * 25.4 - options.MarginMm * 2);
        if (options.RedistributeSystems)
        {
            foreach (var lineBreak in document.Score.Descendants("LayoutBreak")
                .Where(e => (string?)e.Element("subtype") is "line" or "page").ToArray()) lineBreak.Remove();
            // An offset composed for the old system can hit the previous staff after reflow.
            // Let MuseScore reserve space for these annotations in the new system instead.
            foreach (var annotation in document.Score.Descendants().Where(element => element.Name.LocalName is
                "StaffText" or "SystemText" or "Tempo" or "Harmony" or "RehearsalMark"))
            {
                annotation.Element("offset")?.Remove();
                Set(annotation, "autoplace", 1);
            }
        }
    }

    private static void ApplyHeader(NativeScoreArchive document, ScoreLayoutOptions options, double printableMm)
    {
        var score = document.Score;
        var originalMetadata = document.Metadata;
        var staff = score.Elements("Staff").FirstOrDefault() ?? throw new InvalidDataException("A partitura não possui uma pauta editável.");
        var frame = staff.Elements("VBox").FirstOrDefault();
        if (frame is null)
        {
            frame = new XElement("VBox"); staff.AddFirst(frame);
        }
        foreach (var (role, metadata, value, size, maximum) in new[]
        {
            ("title", "workTitle", options.Title, options.TitleFontSize, 160),
            ("subtitle", "subtitle", options.Subtitle, options.SubtitleFontSize, 200)
        })
        {
            var texts = frame.Elements("Text").Where(e => (string?)e.Element("style") == role).ToArray();
            if (value is not null)
            {
                string normalized = NormalizeText(value, maximum);
                var tag = score.Elements("metaTag").FirstOrDefault(e => (string?)e.Attribute("name") == metadata);
                if (tag is null) { tag = new XElement("metaTag", new XAttribute("name", metadata)); score.AddFirst(tag); }
                tag.Value = normalized;
                if (normalized.Length == 0) { foreach (var text in texts) text.Remove(); continue; }
                foreach (var duplicate in texts.Skip(1)) duplicate.Remove();
                var target = texts.FirstOrDefault();
                if (target is null) { target = new XElement("Text", new XElement("style", role)); frame.Add(target); }
                target.Element("text")?.Remove();
                int columns = Math.Max(18, (int)(printableMm / (size * 25.4 / 72 * .58)));
                target.Add(new XElement("text", Wrap(normalized, columns)));
                texts = [target];
            }
            foreach (var text in texts) { Set(text, "size", size); Set(text, "family", "Edwin"); }
        }
        if (options.Composer is not null || options.Artist is not null)
        {
            string composerValue = options.Composer is null ? originalMetadata.Composer : NormalizeText(options.Composer, 120);
            string artistValue = options.Artist is null ? originalMetadata.Artist : NormalizeText(options.Artist, 120);
            foreach (var (name, value) in new[] { ("composer", composerValue), ("artist", artistValue) })
            {
                var tag = score.Elements("metaTag").FirstOrDefault(e => (string?)e.Attribute("name") == name);
                if (tag is null) { tag = new XElement("metaTag", new XAttribute("name", name)); score.AddFirst(tag); }
                tag.Value = value;
            }
            foreach (var previous in frame.Elements("Text").Where(e => (string?)e.Element("style") == "composer").ToArray()) previous.Remove();
            int columns = Math.Max(18, (int)(printableMm / (options.ComposerFontSize * 25.4 / 72 * .58)));
            string credits = artistValue.Length == 0 ? Wrap(composerValue, columns)
                : Wrap("Intérprete: " + artistValue, columns) + (composerValue.Length == 0 ? "" : "\n" + Wrap("Autor: " + composerValue, columns));
            if (credits.Length > 0) frame.Add(new XElement("Text", new XElement("style", "composer"), new XElement("text", credits)));
        }
        foreach (var text in frame.Elements("Text").Where(e => (string?)e.Element("style") == "composer"))
        { Set(text, "size", options.ComposerFontSize); Set(text, "family", "Edwin"); }
        var title = frame.Elements("Text").FirstOrDefault(e => (string?)e.Element("style") == "title");
        var subtitle = frame.Elements("Text").FirstOrDefault(e => (string?)e.Element("style") == "subtitle");
        var composer = frame.Elements("Text").FirstOrDefault(e => (string?)e.Element("style") == "composer");
        double titleMm = Lines(title) * options.TitleFontSize * 25.4 / 72 * 1.2;
        double subtitleMm = Lines(subtitle) * options.SubtitleFontSize * 25.4 / 72 * 1.25;
        double composerMm = Lines(composer) * options.ComposerFontSize * 25.4 / 72 * 1.25;
        if (title is not null) { Set(title, "align", "center,top"); SetOffset(title, 0); }
        if (subtitle is not null) { Set(subtitle, "align", "center,top"); SetOffset(subtitle, titleMm + (titleMm > 0 ? 2 : 0)); }
        if (composer is not null) { Set(composer, "align", "right,top"); SetOffset(composer, titleMm + subtitleMm + 4); }
        // Automatic frame sizing ignores the manual offsets of title/subtitle/composer.
        // Pin the required height in staff spaces so the first system cannot overlap them.
        Set(frame, "boxAutoSize", 0);
        Set(frame, "sizeIsSpatiumDependent", 1);
        Set(frame, "height", Math.Max(Number(frame.Element("height"), 0), (titleMm + subtitleMm + composerMm + 7) / options.StaffSpaceMm));
    }

    private static int Lines(XElement? text) => text?.Element("text") is { } content
        ? content.Value.Split('\n').Length + content.Descendants("br").Count() : 0;
    private static void SetOffset(XElement text, double y)
    {
        text.Element("offset")?.Remove();
        text.Add(new XElement("offset", new XAttribute("x", "0"), new XAttribute("y", y.ToString("0.#####", CultureInfo.InvariantCulture))));
    }

    private static string NormalizeText(string text, int maximum)
    {
        string result = Regex.Replace(text, @"\s+", " ").Trim();
        if (result.Length > maximum) throw new InvalidOperationException($"O cabeçalho ultrapassa o limite de {maximum} caracteres.");
        if (result.Any(c => char.IsControl(c))) throw new InvalidOperationException("O cabeçalho contém caracteres de controle inválidos.");
        return result;
    }

    private static string Wrap(string text, int columns)
    {
        var lines = new List<string>(); string current = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + word.Length + 1 > columns) { lines.Add(current); current = ""; }
            string rest = word;
            while (rest.Length > columns) { if (current.Length > 0) { lines.Add(current); current = ""; } lines.Add(rest[..columns]); rest = rest[columns..]; }
            current = current.Length == 0 ? rest : current + " " + rest;
        }
        if (current.Length > 0) lines.Add(current);
        return string.Join('\n', lines);
    }

    private static void ValidateOptions(ScoreLayoutOptions options)
    {
        static void Range(double value, double minimum, double maximum, string label)
        { if (!double.IsFinite(value) || value < minimum || value > maximum) throw new ArgumentOutOfRangeException(label, $"{label}: use um valor entre {minimum} e {maximum}."); }
        Range(options.MarginMm, 5, 40, "Margens (mm)"); Range(options.StaffSpaceMm, 1.2, 2.5, "Tamanho da pauta (mm)");
        Range(options.MinimumSystemDistance, 6, 30, "Distância entre sistemas");
        Range(options.TitleFontSize, 12, 32, "Tamanho do título"); Range(options.SubtitleFontSize, 8, 20, "Tamanho do subtítulo");
        Range(options.ComposerFontSize, 8, 18, "Tamanho do autor");
        if (options.Title is not null) NormalizeText(options.Title, 160);
        if (options.Subtitle is not null) NormalizeText(options.Subtitle, 200);
        if (options.Composer is not null) NormalizeText(options.Composer, 120);
        if (options.Artist is not null) NormalizeText(options.Artist, 120);
    }

    private static void ValidateMetadata(NativeScoreMetadata actual, NativeScoreMetadata expected)
    {
        if (actual.Title != expected.Title || actual.Subtitle != expected.Subtitle || actual.Composer != expected.Composer || actual.Artist != expected.Artist ||
            actual.ArrangementSummary != expected.ArrangementSummary)
            throw new InvalidDataException("O MuseScore não preservou o cabeçalho solicitado. A revisão não foi publicada.");
    }

    private static string CreateJob(string directory, string source, string suffix)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidOperationException("Escolha uma pasta de saída com caminho completo.");
        string path = Path.Combine(directory, SafeName(source) + " - " + suffix + " - " + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path); return path;
    }
    private static string SafeName(string source)
    {
        string name = Path.GetFileNameWithoutExtension(source);
        foreach (char character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        return string.IsNullOrWhiteSpace(name) ? "Partitura" : name[..Math.Min(90, name.Length)].TrimEnd('.', ' ');
    }
    private static void Set(XElement parent, string name, object value)
    {
        string formatted = value is double number ? number.ToString("0.########", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var element = parent.Element(name); if (element is null) parent.Add(new XElement(name, formatted)); else element.Value = formatted;
    }
    private static double Number(XElement? element, double fallback) => double.TryParse(element?.Value,
        NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;
    private static async Task<byte[]> HashAsync(string path, CancellationToken token)
    { await using var stream = File.OpenRead(path); return await SHA256.HashDataAsync(stream, token); }
    private static async Task CheckSourceAsync(string path, byte[] hash, CancellationToken token)
    {
        if (!(await HashAsync(path, token)).SequenceEqual(hash))
            throw new IOException("A partitura original foi alterada por outro aplicativo durante a operação. Importe-a novamente.");
    }
    private static void DeleteTemporary(string path)
    { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
