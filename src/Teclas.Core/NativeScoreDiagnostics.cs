using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Teclas.Core;

public enum ScoreDiagnosticSeverity { Information, Review, Error }

public sealed record ScoreDiagnosticIssue(string Code, ScoreDiagnosticSeverity Severity, string Location,
    string Message, string Suggestion)
{
    public int? Staff { get; init; }
    public int? Measure { get; init; }
    public int? Voice { get; init; }
}

public sealed record ScoreDiagnosticReport(string SourceSha256, NativeScoreMetadata Metadata,
    IReadOnlyList<ScoreDiagnosticIssue> Issues, IReadOnlyList<string> Limitations);

/// <summary>
/// Read-only structural checks for MuseScore 4. Unsupported timing structures are explicitly excluded
/// from duration checks; a finding is never permission to rewrite the user's musical decisions.
/// </summary>
public static class NativeScoreDiagnostics
{
    public static ScoreDiagnosticReport Analyze(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // Keep the source open for reading throughout the operation. In addition to the hash check,
        // FileShare.Read prevents concurrent edits on Windows without locking out another reader.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string before = Hash(source, token);
        var archive = NativeScoreArchive.Read(path);
        token.ThrowIfCancellationRequested();
        var analyzer = new Analyzer(archive, token);
        analyzer.Run();
        source.Position = 0;
        if (Hash(source, token) != before)
            throw new IOException("A partitura mudou durante o diagnóstico. Execute a análise novamente sobre a versão atual.");
        return analyzer.Report(before);
    }

    private static string Hash(Stream source, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int count;
        while ((count = source.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class Analyzer(NativeScoreArchive archive, CancellationToken token)
    {
        private const int MaximumIssues = 200;
        private readonly List<ScoreDiagnosticIssue> issues = [];
        private readonly Dictionary<string, string> limitations = new(StringComparer.Ordinal);
        private int omitted;
        private ScoreDiagnosticSeverity omittedSeverity = ScoreDiagnosticSeverity.Information;
        private int? staff, measure, voice;
        private readonly NativeScoreMetadata metadata = archive.Metadata;

        internal ScoreDiagnosticReport Report(string hash)
        {
            if (omitted > 0)
                issues.Add(new("MORE_ISSUES", omittedSeverity, "Partitura",
                    $"Mais {omitted} ocorrências foram encontradas além das {MaximumIssues} exibidas.",
                    "Revise os pontos apresentados e execute o diagnóstico novamente."));
            return new(hash, metadata, issues.ToArray(), limitations.Values.ToArray());
        }

        internal void Run()
        {
            Limit("scope", "A análise confere a estrutura da partitura principal. Não avalia a intenção musical nem garante ausência de sobreposições na página renderizada.");
            Limit("locations", "Pautas, compassos e vozes são identificados pela ordem no arquivo; a numeração impressa pode ser diferente.");
            if (archive.Entries.Keys.Any(name => name != archive.MainEntry && name.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase)))
                Limit("parts", "Partes individuais incorporadas não foram diagnosticadas separadamente.");
            bool printedTitle = archive.Score.Elements("Staff").Elements("VBox").Elements("Text")
                .Any(text => (string?)text.Element("style") == "title" && !string.IsNullOrWhiteSpace(text.Element("text")?.Value));
            if (string.IsNullOrWhiteSpace(metadata.Title) && !printedTitle)
                Add("TITLE_MISSING", ScoreDiagnosticSeverity.Information, "A partitura não possui título informado.",
                    "Preencha o título nas propriedades, se desejar apresentá-lo na partitura.");
            CheckPageStyle();
            var staves = archive.Score.Elements("Staff").ToArray();
            if (staves.Length == 0)
                Add("STAFF_MISSING", ScoreDiagnosticSeverity.Error, "A partitura principal não contém pautas.",
                    "Abra o arquivo no MuseScore e confira se ele contém a partitura esperada.");
            for (int index = 0; index < staves.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                staff = index + 1;
                var current = staves[index];
                if (!current.Descendants("Note").Any())
                    Add("STAFF_WITHOUT_NOTES", ScoreDiagnosticSeverity.Information, "Esta pauta não contém notas.",
                        "Confirme se a pauta de pausas, cifras ou marcações deve fazer parte desta edição.");
                double? meter = null;
                int measureIndex = 0;
                foreach (var bar in current.Elements("Measure"))
                {
                    token.ThrowIfCancellationRequested();
                    measure = ++measureIndex;
                    CheckMeasure(bar, ref meter);
                }
                measure = null;
            }
            staff = null;
            CheckImages();
        }

        private void CheckMeasure(XElement bar, ref double? meter)
        {
            voice = null;
            var voices = bar.Elements("voice").ToArray();
            var signatures = voices.SelectMany(v => v.Elements("TimeSig")).ToArray();
            bool ambiguousSignature = false;
            if (signatures.Length > 0)
            {
                var parsed = signatures.Select(ParseMeter).ToArray();
                meter = parsed.All(v => v.HasValue) && parsed.Distinct().Count() == 1 ? parsed[0] : null;
                if (meter is null)
                    Limit("time-signature", "Fórmulas de compasso locais, estendidas ou não reconhecidas foram excluídas da conferência de duração.");
                ambiguousSignature = voices.Any(v => v.Elements().TakeWhile(e => e.Name != "TimeSig")
                    .Any(e => e.Name.LocalName is "Chord" or "Rest") && v.Element("TimeSig") is not null);
                if (ambiguousSignature)
                    Limit("internal-signature", "Compassos com mudança interna de fórmula de compasso não tiveram a duração conferida.");
            }
            double? length = meter;
            if (bar.Attribute("len") is { } explicitLength)
            {
                length = PositiveFraction(explicitLength.Value);
                if (length is null)
                    Limit("measure-length", "Há duração de compasso explícita que não pôde ser interpretada com segurança.");
            }
            else if (IsTrue(bar.Element("irregular")))
            {
                length = null;
                Limit("irregular", "Compassos irregulares sem duração explícita não tiveram a duração conferida.");
            }
            if (ambiguousSignature) length = null;
            if (meter is null && bar.Attribute("len") is null)
                Limit("missing-meter", "Compassos sem fórmula ou duração explícita conhecida não tiveram a duração conferida.");
            for (int index = 0; index < voices.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                voice = index + 1;
                CheckVoice(voices[index], length);
            }
            voice = null;
        }

        private void CheckVoice(XElement current, double? length)
        {
            foreach (var chord in current.Descendants("Chord")) CheckNotes(chord);
            // A location inside a Tie/Slur is an endpoint, not a voice cursor. Only direct voice
            // locations make the sequential timeline ambiguous.
            bool safe = true;
            if (current.Descendants().Any(e => e.Name.LocalName is "Tuplet" or "endTuplet" or "tuplet"))
            {
                safe = false;
                Limit("tuplets", "Vozes com quiálteras não tiveram a duração nem a posição das cifras conferidas nesta análise.");
            }
            if (current.Elements().Any(e => e.Name.LocalName is "location" or "tick"))
            {
                safe = false;
                Limit("cursor", "Vozes com deslocamentos explícitos de posição não tiveram a duração nem a posição das cifras conferidas.");
            }
            if (current.Descendants().Any(e => e.Name.LocalName is "Tremolo" or "TremoloTwoChord" or "timeStretch"))
            {
                safe = false;
                Limit("special-rhythm", "Vozes com tremolos ou alterações explícitas da duração foram reservadas para conferência no MuseScore.");
            }
            double position = 0;
            var harmonies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in current.Elements())
            {
                token.ThrowIfCancellationRequested();
                string kind = item.Name.LocalName;
                if (kind is "Chord" or "Rest")
                {
                    if (kind == "Chord" && IsGrace(item)) continue;
                    double? duration = Duration(item, length);
                    if (duration is null)
                    {
                        safe = false;
                        Limit("duration", "Vozes com durações não reconhecidas não tiveram o preenchimento do compasso conferido.");
                    }
                    else
                    {
                        position += duration.Value;
                        harmonies.Clear();
                    }
                }
                else if (kind == "Harmony" && safe)
                {
                    string identity = HarmonyIdentity(item);
                    if (identity.Length > 0 && !harmonies.Add(identity))
                        Add("HARMONY_DUPLICATED", ScoreDiagnosticSeverity.Review,
                            "A mesma cifra aparece mais de uma vez na mesma posição desta voz.",
                            "Confira se há uma cifra duplicada antes de remover qualquer indicação.");
                }
                else if (!NonRhythmicElements.Contains(kind))
                {
                    safe = false;
                    Limit("unknown-voice-element", "Vozes com estruturas adicionais não reconhecidas não tiveram a duração nem a posição das cifras conferidas.");
                }
            }
            if (safe && length is { } expected && position > expected + 0.000000001)
                Add("VOICE_TOO_LONG", ScoreDiagnosticSeverity.Review,
                    $"A soma das durações desta voz ({Beats(position)} semínimas) excede a duração do compasso ({Beats(expected)} semínimas).",
                    "Confira notas, pausas e a duração do compasso no MuseScore; nenhuma nota foi removida ou encurtada.");
        }

        private void CheckNotes(XElement chord)
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            bool duplicate = false;
            foreach (var note in chord.Elements("Note"))
            {
                token.ThrowIfCancellationRequested();
                if (!int.TryParse((string?)note.Element("pitch"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pitch) || pitch is < 0 or > 127)
                {
                    Add("NOTE_PITCH_INVALID", ScoreDiagnosticSeverity.Error, "Uma nota possui altura ausente ou fora do intervalo nativo de 0 a 127.",
                        "Abra este compasso no MuseScore e confira a nota antes de reproduzir ou editar a partitura.");
                    continue;
                }
                // Preserve enharmonic spellings and microtonal differences: equal MIDI pitches alone
                // are not enough to call two noteheads redundant.
                string identity = string.Join('|', pitch.ToString(CultureInfo.InvariantCulture),
                    (string?)note.Element("tpc") ?? "", (string?)note.Element("tpc2") ?? "", (string?)note.Element("tuning") ?? "0");
                if (!unique.Add(identity)) duplicate = true;
            }
            if (duplicate)
                Add("CHORD_NOTE_DUPLICATED", ScoreDiagnosticSeverity.Review, "Um acorde contém notas repetidas com a mesma altura, grafia e afinação.",
                    "Confira se a duplicação é intencional. Notas iguais em vozes diferentes são mantidas.");
        }

        private void CheckImages()
        {
            foreach (var image in archive.Score.Descendants("Image"))
            {
                token.ThrowIfCancellationRequested();
                var containingStaff = image.Ancestors("Staff").FirstOrDefault(s => s.Parent == archive.Score);
                var containingMeasure = image.Ancestors("Measure").FirstOrDefault();
                var containingVoice = image.Ancestors("voice").FirstOrDefault();
                staff = containingStaff is null ? null : containingStaff.ElementsBeforeSelf("Staff").Count() + 1;
                measure = containingMeasure is null ? null : containingMeasure.ElementsBeforeSelf("Measure").Count() + 1;
                voice = containingVoice is null ? null : containingVoice.ElementsBeforeSelf("voice").Count() + 1;
                string? path = (string?)image.Element("path");
                if (string.IsNullOrWhiteSpace(path) ||
                    !archive.Entries.ContainsKey(path) && !archive.Entries.ContainsKey("Pictures/" + path))
                    Add("IMAGE_MISSING", ScoreDiagnosticSeverity.Error, "Uma imagem ou diagrama referenciado não está incorporado neste MSCZ.",
                        "Incorpore a imagem no MuseScore e salve uma nova cópia. O diagnóstico não acessa arquivos externos.");
            }
            staff = measure = voice = null;
        }

        private void CheckPageStyle()
        {
            var style = archive.ReadStyle();
            double? width = Number(style.Element("pageWidth"));
            double? height = Number(style.Element("pageHeight"));
            foreach (string name in new[] { "pageWidth", "pageHeight", "pagePrintableWidth", "spatium", "Spatium" })
                if (style.Element(name) is { } value && (Number(value) is not { } number || number <= 0))
                    Add("PAGE_STYLE_INVALID", ScoreDiagnosticSeverity.Error, "Uma dimensão de página ou pauta tem valor inválido.",
                        "Revise o tamanho da página, a área útil e o tamanho da pauta no MuseScore.");
            double? printable = Number(style.Element("pagePrintableWidth"));
            if (width is > 0 && printable is > 0 && printable > width + 0.000001)
                Add("PAGE_WIDTH_OVERFLOW", ScoreDiagnosticSeverity.Review, "A largura útil configurada excede a largura do papel.",
                    "Ajuste as margens e confira a prévia de todas as páginas.");
            foreach (string parity in new[] { "Odd", "Even" })
            {
                double? top = Number(style.Element($"page{parity}TopMargin"));
                double? bottom = Number(style.Element($"page{parity}BottomMargin"));
                if (height is > 0 && top is >= 0 && bottom is >= 0 && top + bottom >= height)
                    Add("PAGE_MARGINS_OVERFLOW", ScoreDiagnosticSeverity.Review, "As margens verticais configuradas não deixam altura útil para a música.",
                        "Reduza as margens e confira a prévia antes de exportar a partitura.");
            }
        }

        private void Add(string code, ScoreDiagnosticSeverity severity, string message, string suggestion)
        {
            if (issues.Count >= MaximumIssues)
            {
                omitted++;
                if (severity > omittedSeverity) omittedSeverity = severity;
                return;
            }
            string location = staff is null ? "Partitura" : $"Pauta {staff}";
            if (measure is not null) location += $", compasso {measure}";
            if (voice is not null) location += $", voz {voice}";
            issues.Add(new(code, severity, location, message, suggestion) { Staff = staff, Measure = measure, Voice = voice });
        }

        private void Limit(string key, string message) => limitations.TryAdd(key, message);
    }

    private static readonly HashSet<string> NonRhythmicElements = new(StringComparer.Ordinal)
    {
        "eid", "Clef", "KeySig", "TimeSig", "Tempo", "Harmony", "Image", "BarLine", "Segment",
        "StaffText", "SystemText", "RehearsalMark", "Dynamic", "HairPin", "Hairpin", "TextLine",
        "Ottava", "Pedal", "Slur", "Tie", "Spanner", "endSpanner", "Fermata", "Breath", "Articulation",
        "Beam", "FretDiagram", "FiguredBass", "Symbol", "TremoloBar", "StaffTypeChange", "InstrumentChange"
    };

    private static bool IsGrace(XElement chord) => chord.Elements().Any(e =>
        e.Name.LocalName is "acciaccatura" or "appoggiatura" || e.Name.LocalName.StartsWith("grace", StringComparison.Ordinal));

    private static double? Duration(XElement item, double? measureLength)
    {
        string kind = (string?)item.Element("durationType") ?? "";
        double? explicitDuration = item.Element("duration") is { } value ? PositiveFraction(value.Value) : null;
        if (kind == "measure" && item.Name == "Rest")
            return item.Element("duration") is not null ? explicitDuration : measureLength;
        double? basis = kind switch
        {
            "long" or "longa" => 4, "breve" => 2, "whole" => 1, "half" => .5, "quarter" => .25,
            "eighth" => .125, "16th" => 1d / 16, "32nd" => 1d / 32, "64th" => 1d / 64,
            "128th" => 1d / 128, "256th" => 1d / 256, "512th" => 1d / 512, "1024th" => 1d / 1024,
            _ => null
        };
        if (basis is null) return null;
        int dots = 0;
        if (item.Element("dots") is { } dotted && (!int.TryParse(dotted.Value, out dots) || dots is < 0 or > 4)) return null;
        double duration = basis.Value * (2 - Math.Pow(.5, dots));
        if (item.Element("duration") is not null && (explicitDuration is null || Math.Abs(explicitDuration.Value - duration) > .000000001)) return null;
        return duration;
    }

    private static double? ParseMeter(XElement time)
    {
        if (time.Element("stretch") is { } stretch && PositiveFraction(stretch.Value) != 1) return null;
        if (!int.TryParse((string?)time.Element("sigN"), out int numerator) || numerator is < 1 or > 1024 ||
            !int.TryParse((string?)time.Element("sigD"), out int denominator) || denominator is < 1 or > 1024) return null;
        return (double)numerator / denominator;
    }

    private static double? PositiveFraction(string text)
    {
        string[] pieces = text.Split('/');
        if (pieces.Length != 2 || !long.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long numerator) ||
            !long.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long denominator) ||
            numerator is <= 0 or > 1000000 || denominator is <= 0 or > 1000000) return null;
        return (double)numerator / denominator;
    }

    private static bool IsTrue(XElement? value) => value?.Value is "1" or "true";
    private static double? Number(XElement? value) => double.TryParse(value?.Value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
    private static string Beats(double wholeNotes) => (wholeNotes * 4).ToString("0.####", CultureInfo.GetCultureInfo("pt-BR"));

    private static string HarmonyIdentity(XElement harmony)
    {
        XElement Canonical(XElement element) => new(element.Name,
            element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal),
            element.Nodes().Select(node => node is XElement child
                ? child.Name.LocalName is "eid" or "offset" or "pos" or "autoplace" or "minDistance" or "color" or "placement" or "visible" or "z"
                    ? null : (object)Canonical(child)
                : node is XText text ? (object)new XText(text.Value) : null));
        var identity = Canonical(harmony);
        return identity.HasElements ? identity.ToString(SaveOptions.DisableFormatting) : "";
    }
}
