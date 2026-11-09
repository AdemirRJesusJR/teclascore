using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teclas.Core;

/// <summary>Options of <c>Teclas.Partituras.exe --gerar</c>; paths are already absolute.</summary>
public sealed record BatchCommandLineOptions(
    IReadOnlyList<string> Targets, bool Recursive, string? PdfDirectory, string? OutputDirectory, string? ReportPath, string? SettingsPath, bool Simulate = false);

/// <summary>Result of reading the arguments: help, an error message, or the options to run.</summary>
public sealed record BatchCommandLineParse(BatchCommandLineOptions? Options, bool ShowHelp, string? Error);

/// <summary>
/// Command-line generation: imports the given MIDIs, runs the library autopilot on them and reports each file.
/// Exit codes: 0 all ready, 2 something waits for review, 1 error.
/// </summary>
public static class BatchCommandLine
{
    public const int ExitReady = 0, ExitError = 1, ExitReview = 2;
    public const string GenerateOption = "--gerar";
    private static readonly string[] HelpOptions = ["--ajuda", "-h", "--help", "/?", "-?"];

    public const string Usage =
        "TeclaScore · geração pela linha de comando\n" +
        "\n" +
        "Uso:\n" +
        "  Teclas.Partituras.exe --gerar <pasta-ou-arquivos>... [--recursivo] [--pdf <pasta>] [--saida <pasta>]\n" +
        "                        [--relatorio <arquivo.json>] [--configuracoes <settings.json>] [--simular]\n" +
        "  Teclas.Partituras.exe --ajuda\n" +
        "\n" +
        "  --gerar <...>      MIDIs (.mid, .midi, .kar) ou pastas; de uma pasta entram todos os MIDIs dela.\n" +
        "  --recursivo        Inclui também os MIDIs das subpastas.\n" +
        "  --pdf <pasta>      Salva nessa pasta o PDF de cada partitura gerada.\n" +
        "  --saida <pasta>    Pasta das partituras .mscz nesta execução (o padrão das Configurações não muda).\n" +
        "  --relatorio <arq>  Grava um relatório JSON: situação, partitura, PDF e motivo de cada arquivo.\n" +
        "  --configuracoes    Usa outro arquivo de configurações (outra biblioteca) em vez do padrão.\n" +
        "  --simular          Só mostra o que vai acontecer (o que seria gerado, pulado ou precisa de você) e sai sem gerar.\n" +
        "\n" +
        "Antes de gerar, um ponto de restauração da biblioteca é guardado (os 5 mais recentes ficam em Histórico › Pontos de\n" +
        "restauração). Partituras aprovadas na revisão nunca são refeitas pelo lote.\n" +
        "Os MIDIs entram na biblioteca como na importação normal (sem copiar nem repetir arquivos) e passam pelo\n" +
        "Piloto automático: melodias duvidosas e MIDIs sem melodia completa não são adivinhados.\n" +
        "\n" +
        "Código de saída: 0 tudo pronto · 2 algo ficou para revisar ou precisa de você · 1 erro ou falha.";

    /// <summary>True when the arguments ask for the command-line mode (and not for the window or a smoke check).</summary>
    public static bool IsRequest(IReadOnlyList<string> args) =>
        args.Count > 0 && (args.Contains(GenerateOption, StringComparer.OrdinalIgnoreCase) || args.Any(a => HelpOptions.Contains(a, StringComparer.OrdinalIgnoreCase)));

    public static BatchCommandLineParse Parse(IReadOnlyList<string> args, string? workingDirectory = null)
    {
        string baseDirectory = workingDirectory ?? Environment.CurrentDirectory;
        if (args.Any(a => HelpOptions.Contains(a, StringComparer.OrdinalIgnoreCase))) return new(null, true, null);
        var targets = new List<string>();
        bool recursive = false, generate = false, simulate = false;
        string? pdf = null, output = null, report = null, settings = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string option = arg.ToLowerInvariant();
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                return Fail($"Argumento inesperado: {arg}. Os arquivos e pastas vêm logo depois de --gerar.");
            if (option != GenerateOption && !seen.Add(option)) return Fail($"A opção {arg} foi informada mais de uma vez.");
            switch (option)
            {
                case GenerateOption:
                    if (generate) return Fail("Use --gerar uma vez só, seguido de todos os arquivos e pastas.");
                    generate = true;
                    while (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        if (Resolve(args[++i], baseDirectory) is not { } target) return Fail($"Caminho inválido: {args[i]}");
                        if (!targets.Contains(target, StringComparer.OrdinalIgnoreCase)) targets.Add(target);
                    }
                    if (targets.Count == 0) return Fail("Informe depois de --gerar uma pasta ou um ou mais arquivos MIDI.");
                    break;
                case "--recursivo": recursive = true; break;
                case "--simular": simulate = true; break;
                case "--pdf": if (Value(ref i, out pdf) is { } e1) return Fail(e1); break;
                case "--saida": if (Value(ref i, out output) is { } e2) return Fail(e2); break;
                case "--relatorio": if (Value(ref i, out report) is { } e3) return Fail(e3); break;
                case "--configuracoes": if (Value(ref i, out settings) is { } e4) return Fail(e4); break;
                default: return Fail($"Opção desconhecida: {arg}. Use --ajuda para ver as opções.");
            }
        }
        if (!generate) return Fail("Use --gerar seguido de uma pasta ou de arquivos MIDI.");
        if (report is not null && Directory.Exists(report)) return Fail("--relatorio precisa ser um arquivo (por exemplo relatorio.json), não uma pasta.");
        if (settings is not null && !File.Exists(settings)) return Fail("Arquivo de configurações não encontrado: " + settings);
        return new(new(targets, recursive, pdf, output, report, settings, simulate), false, null);

        string? Value(ref int index, out string? value)
        {
            value = null;
            string name = args[index];
            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal)) return $"Falta o valor de {name}.";
            value = Resolve(args[++index], baseDirectory);
            return value is null ? $"Caminho inválido em {name}: {args[index]}" : null;
        }
        static BatchCommandLineParse Fail(string message) => new(null, false, message);
    }

    private static string? Resolve(string value, string baseDirectory)
    {
        string trimmed = value.Trim().Trim('"');
        if (trimmed.Length == 0 || trimmed.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c))) return null;
        try { return Path.GetFullPath(trimmed, baseDirectory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The MIDIs of the targets (a folder gives its visible MIDIs), without repeats; missing or other files are reported.</summary>
    public static IReadOnlyList<string> CollectMidis(BatchCommandLineOptions options, List<BatchFileReport> problems)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string target in options.Targets)
        {
            if (Directory.Exists(target))
            {
                try
                {
                    var scan = MidiFolderScanner.Scan(target, options.Recursive);
                    if (scan.Entries.Count == 0) problems.Add(BatchFileReport.Failure(target, "Nenhum MIDI (.mid, .midi, .kar) nesta pasta" + (options.Recursive ? " nem nas subpastas." : ". Use --recursivo para incluir as subpastas.")));
                    if (scan.Truncated) problems.Add(BatchFileReport.Failure(target, $"A pasta tem mais de {MidiFolderScanner.MaximumEntries} MIDIs; só os primeiros foram incluídos."));
                    foreach (var entry in scan.Entries) if (seen.Add(entry.FullPath)) files.Add(entry.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { problems.Add(BatchFileReport.Failure(target, "Não foi possível ler a pasta: " + ex.Message)); }
            }
            else if (File.Exists(target))
            {
                if (!MidiFolderScanner.IsMidi(target)) problems.Add(BatchFileReport.Failure(target, "Não é um arquivo MIDI (.mid, .midi, .kar)."));
                else if (seen.Add(target)) files.Add(target);
            }
            else problems.Add(BatchFileReport.Failure(target, "Arquivo ou pasta não encontrado."));
        }
        return files;
    }

    /// <summary>0 when every file is ready, 1 when anything failed (or the run itself), 2 when something waits for review.</summary>
    public static int ExitCode(IEnumerable<BatchFileReport> files, string? runError = null)
    {
        var list = files.ToList();
        if (runError is not null || list.Count == 0 || list.Any(f => f.Situacao == BatchFileReport.Failed || f.PdfErro is not null)) return ExitError;
        return list.Any(f => f.Situacao is BatchFileReport.Review or BatchFileReport.NeedsYou) ? ExitReview : ExitReady;
    }

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ToJson(BatchRunReport report) => JsonSerializer.Serialize(report, ReportJson);

    public static void WriteReport(BatchRunReport report, string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, ToJson(report), new System.Text.UTF8Encoding(false)); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>One file of the command-line report (JSON names in Portuguese, like the rest of the app's files).</summary>
public sealed record BatchFileReport(string Arquivo, string Situacao, string? Titulo = null, string? Partitura = null, string? Pdf = null, string? Motivo = null, string? PdfErro = null)
{
    public const string Ready = "pronta", Review = "para-revisar", NeedsYou = "precisa-de-voce", Failed = "falhou", AlreadyGenerated = "ja-gerada", Simulated = "simulada";
    public static BatchFileReport Failure(string path, string reason) => new(path, Failed, Motivo: reason);
}

public sealed record BatchRunSummary(int Prontas, int ParaRevisar, int PrecisamDeVoce, int Falharam, int JaGeradas, int Pdfs);

public sealed record BatchRunReport(string Versao, DateTimeOffset Inicio, DateTimeOffset Fim, int CodigoSaida, string? Erro, BatchRunSummary Resumo, IReadOnlyList<BatchFileReport> Arquivos)
{
    public static BatchRunReport Create(string version, DateTimeOffset start, IReadOnlyList<BatchFileReport> files, string? error = null) =>
        new(version, start, DateTimeOffset.Now, BatchCommandLine.ExitCode(files, error), error,
            new(files.Count(f => f.Situacao is BatchFileReport.Ready or BatchFileReport.Simulated), files.Count(f => f.Situacao == BatchFileReport.Review),
                files.Count(f => f.Situacao == BatchFileReport.NeedsYou), files.Count(f => f.Situacao == BatchFileReport.Failed),
                files.Count(f => f.Situacao == BatchFileReport.AlreadyGenerated), files.Count(f => f.Pdf is not null)),
            files);
}
