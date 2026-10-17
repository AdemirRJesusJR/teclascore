using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace Teclas.Core;

public sealed record BatchPdfSource(string Id, string Title, string? ScorePath, string BlockedReason = "", string Artist = "");
public enum BatchPdfStatus { Success, Failure, Skipped, Cancelled }
public sealed record BatchPdfItemResult(string Id, string Title, string? SourcePath, BatchPdfStatus Status,
    string? PdfPath, string Message, string? SourceSha256 = null)
{
    public string StatusText => Status switch { BatchPdfStatus.Success => "Salvo", BatchPdfStatus.Failure => "Falha", BatchPdfStatus.Skipped => "Não exportado", _ => "Cancelado" };
    public string PdfFileName => PdfPath is null ? "—" : Path.GetFileName(PdfPath);
}
public sealed record BatchPdfProgress(int Completed, int Total, string Title, string Stage);
public sealed record BatchPdfReport(string? Directory, IReadOnlyList<BatchPdfItemResult> Items, bool Cancelled,
    string? ReportPath, string Warning)
{
    public int SavedCount => Items.Count(i => i.Status == BatchPdfStatus.Success);
    public int FailedCount => Items.Count(i => i.Status == BatchPdfStatus.Failure);
    public int SkippedCount => Items.Count(i => i.Status == BatchPdfStatus.Skipped);
    public int CancelledCount => Items.Count(i => i.Status == BatchPdfStatus.Cancelled);
    public string Summary => $"{SavedCount} PDF(s) salvos · {FailedCount} falha(s) · {SkippedCount} não exportado(s) · {CancelledCount} cancelado(s).";
}

/// <summary>Exports complete individual PDFs sequentially, publishing each only after validation.</summary>
public sealed class BatchPdfExportService
{
    private readonly string cacheDirectory;
    private readonly Func<string, string, IProgress<string>?, CancellationToken, Task> export;
    public BatchPdfExportService(string museScorePath, string cacheDirectory)
        : this(new ScoreExporter(museScorePath, cacheDirectory).ExportPdfAsync, cacheDirectory) { }

    public BatchPdfExportService(Func<string, string, IProgress<string>?, CancellationToken, Task> export, string cacheDirectory)
    {
        this.export = export ?? throw new ArgumentNullException(nameof(export));
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    /// <param name="directlyIntoDestination">True saves the PDFs in the chosen folder itself (no batch subfolder or report file);
    /// existing files there are never replaced: repeated names receive a number.</param>
    public async Task<BatchPdfReport> ExportAsync(IReadOnlyList<BatchPdfSource> sources, string destinationDirectory,
        IProgress<BatchPdfProgress>? progress, CancellationToken token, bool directlyIntoDestination = false)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || sources.Count > 10000) throw new ArgumentException("Selecione entre 1 e 10.000 partituras.", nameof(sources));
        if (sources.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)) || sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new ArgumentException("A seleção contém identificações inválidas ou repetidas.", nameof(sources));
        if (!Path.IsPathFullyQualified(destinationDirectory)) throw new ArgumentException("Escolha uma pasta de destino com caminho completo.", nameof(destinationDirectory));
        var items = sources.ToArray();
        if (token.IsCancellationRequested)
            return new(null, items.Select(CancelledItem).ToArray(), true, null, "");

        string output;
        if (directlyIntoDestination) { output = Path.GetFullPath(destinationDirectory); Directory.CreateDirectory(output); }
        else output = CreateExclusiveDirectory(Path.GetFullPath(destinationDirectory), "PDFs TeclaScore - " + DateTime.Now.ToString("yyyy-MM-dd HHmmss"));
        string cache = CreateExclusiveDirectory(cacheDirectory, "p");
        // Staging PDFs on the destination volume makes publication an atomic rename, even when
        // the user's output folder and application cache are on different drives.
        string pdfStaging = directlyIntoDestination ? CreateExclusiveDirectory(output, ".teclascore-em-preparacao") : Path.Combine(output, ".em-preparacao");
        Directory.CreateDirectory(pdfStaging);
        var results = new List<BatchPdfItemResult>();
        // Names already in a chosen folder are reserved, so an existing PDF is never replaced.
        var names = new HashSet<string>(directlyIntoDestination ? Directory.EnumerateFileSystemEntries(output).Select(Path.GetFileName).OfType<string>() : [],
            StringComparer.OrdinalIgnoreCase);
        bool cancelled = false;
        try
        {
            for (int index = 0; index < items.Length; index++)
            {
                var item = items[index];
                if (token.IsCancellationRequested)
                {
                    cancelled = true; results.AddRange(items.Skip(index).Select(CancelledItem)); break;
                }
                if (!string.IsNullOrWhiteSpace(item.BlockedReason))
                {
                    results.Add(new(item.Id, item.Title, item.ScorePath, BatchPdfStatus.Skipped, null, item.BlockedReason));
                    progress?.Report(new(index + 1, items.Length, item.Title, item.BlockedReason)); continue;
                }
                string snapshot = Path.Combine(cache, $"{index + 1:00000}.mscz"), renderedPdf = Path.Combine(cache, $"{index + 1:00000}.pdf"),
                    temporaryPdf = Path.Combine(pdfStaging, $"{index + 1:00000}.pdf");
                string? sourceHash = null;
                var fileProgress = new InlineProgress<string>(stage => progress?.Report(new(index, items.Length, item.Title, stage)));
                try
                {
                    if (string.IsNullOrWhiteSpace(item.ScorePath) || !Path.IsPathFullyQualified(item.ScorePath) ||
                        !Path.GetExtension(item.ScorePath).Equals(".mscz", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Gere ou importe uma partitura MuseScore antes de exportar esta música.");
                    fileProgress.Report("Conferindo e protegendo a partitura para exportação…");
                    sourceHash = await CopyVerifiedAsync(item.ScorePath, snapshot, token).ConfigureAwait(false);
                    // MuseScore's native process can reject a long output working directory.
                    // Render in the app cache, then use managed I/O for the user's destination.
                    await export(snapshot, renderedPdf, fileProgress, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    fileProgress.Report("Conferindo o PDF e a versão da partitura…");
                    await ValidatePdfAsync(renderedPdf, token).ConfigureAwait(false);
                    fileProgress.Report("Copiando o PDF completo para a pasta escolhida…");
                    await CopyVerifiedAsync(renderedPdf, temporaryPdf, token, "PDF").ConfigureAwait(false);
                    // Keep the original read-locked until publication, so the checked version
                    // cannot change between its final hash and the completed PDF's rename.
                    await using var checkedSource = new FileStream(item.ScorePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(checkedSource, token).ConfigureAwait(false)) != sourceHash)
                        throw new IOException("A partitura mudou durante a exportação. O PDF não foi salvo; exporte novamente a versão atual.");
                    string filename = ScoreFileName.Unique(ScoreFileName.Stem(item.Artist, item.Title, item.ScorePath), ".pdf", names);
                    string target = Path.Combine(output, filename);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporaryPdf, target, overwrite: false);
                    results.Add(new(item.Id, item.Title, item.ScorePath, BatchPdfStatus.Success, target, "Todas as páginas salvas neste PDF.", sourceHash));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled = true; results.Add(CancelledItem(item));
                    results.AddRange(items.Skip(index + 1).Select(CancelledItem)); break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                    InvalidOperationException or ArgumentException or TimeoutException or Win32Exception or System.Xml.XmlException)
                {
                    results.Add(new(item.Id, item.Title, item.ScorePath, BatchPdfStatus.Failure, null, FriendlyError(error), sourceHash));
                }
                finally { TryDelete(snapshot); TryDelete(renderedPdf); TryDelete(temporaryPdf); }
                progress?.Report(new(index + 1, items.Length, item.Title, results[^1].Message));
            }
        }
        finally
        {
            // Remove only files explicitly owned by this operation. Unknown files are preserved.
            TryDeleteEmptyDirectory(cache);
            TryDeleteEmptyDirectory(pdfStaging);
        }
        var report = new BatchPdfReport(output, results, cancelled, null, "");
        if (directlyIntoDestination) return report;
        try
        {
            string reportPath = Path.Combine(output, "Relatório da exportação.txt");
            var text = new StringBuilder("TeclaScore — exportação de PDFs\n\n").AppendLine(report.Summary)
                .AppendLine(cancelled ? "Operação cancelada. Os PDFs concluídos foram preservados." : "Exportação concluída.").AppendLine();
            foreach (var result in results)
            {
                text.AppendLine($"{result.StatusText} — {result.Title}").AppendLine(result.Message);
                if (result.PdfPath is not null) text.AppendLine(Path.GetFileName(result.PdfPath));
                text.AppendLine();
            }
            await File.WriteAllTextAsync(reportPath, text.ToString(), new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
            report = report with { ReportPath = reportPath };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { report = report with { Warning = "Os resultados estão nesta janela, mas não foi possível gravar o relatório na pasta: " + error.Message }; }
        return report;
    }

    private static BatchPdfItemResult CancelledItem(BatchPdfSource item) => new(item.Id, item.Title, item.ScorePath,
        BatchPdfStatus.Cancelled, null, "Exportação cancelada; nenhum PDF incompleto foi publicado.");
    private static string FriendlyError(Exception error)
    {
        if (error is FileNotFoundException or DirectoryNotFoundException) return "Arquivo indisponível. Confira se a partitura e o MuseScore continuam na pasta configurada.";
        string message = error.Message.Trim();
        return message.Length <= 1000 ? message : message[..1000] + "…";
    }
    private static async Task<string> CopyVerifiedAsync(string source, string destination, CancellationToken token, string kind = "partitura")
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length == 0 || input.Length > 512L * 1024 * 1024) throw new InvalidDataException($"O arquivo de {kind} está vazio ou excede o tamanho suportado para exportação (512 MB).");
        string originalHash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
        input.Position = 0;
        await using (var copied = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        { await input.CopyToAsync(copied, token).ConfigureAwait(false); await copied.FlushAsync(token).ConfigureAwait(false); }
        if (await HashFileAsync(destination, token).ConfigureAwait(false) != originalHash) throw new IOException($"A cópia de {kind} não passou na conferência; o original foi preservado.");
        return originalHash;
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
    }
    private static async Task ValidatePdfAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        byte[] header = new byte[5];
        if (stream.Length < 100) throw new InvalidDataException("O PDF ficou incompleto. Nenhum arquivo foi publicado para esta música.");
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (!header.AsSpan().SequenceEqual("%PDF-"u8)) throw new InvalidDataException("O arquivo produzido não é um PDF válido.");
        stream.Position = Math.Max(0, stream.Length - 2048);
        byte[] tail = new byte[(int)(stream.Length - stream.Position)];
        await stream.ReadExactlyAsync(tail, token).ConfigureAwait(false);
        if (!Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal)) throw new InvalidDataException("O PDF ficou incompleto. Nenhum arquivo foi publicado para esta música.");
    }
    private static string CreateExclusiveDirectory(string parent, string prefix)
    {
        Directory.CreateDirectory(parent);
        while (true)
        {
            string path = Path.Combine(parent, prefix + " - " + Guid.NewGuid().ToString("N")[..12]);
            if (Directory.Exists(path) || File.Exists(path)) continue;
            Directory.CreateDirectory(path); return path;
        }
    }
    private static void TryDelete(string path)
    { try { if (File.Exists(path)) File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    private static void TryDeleteEmptyDirectory(string path)
    { try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
