using System.Security.Cryptography;
using System.Text;
using Teclas.Core;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "teclas-batchpdf-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("FAILED: " + label); checks++; }
byte[] fakePdf = Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 150) + "\n%%EOF\n");
string Fixture(string name, string contents = "test MSCZ immutable bytes")
{
    string path = Path.Combine(root, name + ".mscz"); File.WriteAllText(path, contents); return path;
}
string Destination(string name) => Path.Combine(root, name);
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
BatchPdfSource Source(string id, string name, string? path, string blocked = "") => new(id, name, path, blocked);
string first = Fixture("original"), second = Fixture("second", "second score");
string firstHash = Hash(first), secondHash = Hash(second);
int running = 0, maximum = 0, calls = 0;
var snapshots = new List<string>();
var progress = new List<BatchPdfProgress>();
async Task Export(string source, string destination, IProgress<string>? phases, CancellationToken token)
{
    calls++; int count = Interlocked.Increment(ref running); maximum = Math.Max(maximum, count);
    try
    {
        Check(source != first && source != second, "exporter receives private copy");
        Check(Path.GetExtension(source) == ".mscz", "copy retains score extension");
        Check(Path.GetDirectoryName(source) == Path.GetDirectoryName(destination), "native rendering stays inside private cache, independent of output path");
        snapshots.Add(source); phases?.Report("Convertendo todas as páginas…");
        await Task.Delay(2, token); await File.WriteAllBytesAsync(destination, fakePdf, token);
    }
    finally { Interlocked.Decrement(ref running); }
}
var service = new BatchPdfExportService(Export, Destination("cache"));
var duplicateTitles = new[] { "Canção", "canção", "Canção", "CON", "../Nome: música?", new string('L', 240) };
var inputs = duplicateTitles.Select((title, index) => Source(index.ToString(), title, index % 2 == 0 ? first : second)).ToArray();
var complete = await service.ExportAsync(inputs, Destination("complete"), new InlineProgress<BatchPdfProgress>(progress.Add), CancellationToken.None);
Check(complete.SavedCount == inputs.Length && complete.FailedCount == 0 && !complete.Cancelled, "all selected scores exported");
Check(maximum == 1 && calls == inputs.Length, "strict sequential export");
Check(complete.Items.Select(i => Path.GetFileName(i.PdfPath!)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == inputs.Length, "case-insensitive names unique");
Check(Path.GetFileName(complete.Items[1].PdfPath) == "canção (2).pdf", "collision suffix preserves readable title");
Check(Path.GetFileName(complete.Items[3].PdfPath) == "Partitura - CON.pdf", "reserved Windows filename sanitized");
Check(Path.GetFileName(complete.Items[4].PdfPath) == "-Nome- música-.pdf", "unsafe path characters sanitized");
Check(complete.Items.All(i => File.Exists(i.PdfPath) && Path.GetDirectoryName(i.PdfPath) == complete.Directory), "all PDFs remain directly inside exclusive batch");
Check(complete.Items.All(i => i.SourceSha256 == (i.SourcePath == first ? firstHash : secondHash)), "source hash recorded accurately");
Check(Hash(first) == firstHash && Hash(second) == secondHash, "original scores preserved byte for byte");
Check(snapshots.All(path => !File.Exists(path)), "private score copies cleaned");
Check(!Directory.EnumerateDirectories(complete.Directory!).Any(), "temporary output directory removed after completion");
Check(File.Exists(complete.ReportPath) && File.ReadAllText(complete.ReportPath!).Contains("Todas as páginas"), "readable result report persisted");
Check(progress.Count(p => p.Stage == "Convertendo todas as páginas…") == inputs.Length, "per-file exporter stages forwarded");
Check(progress.Last().Completed == inputs.Length && progress.All(p => p.Total == inputs.Length), "progress has correct total and completion");
var again = await service.ExportAsync([Source("repeat", "Canção", first)], Destination("complete"), null, CancellationToken.None);
Check(again.Directory != complete.Directory && File.Exists(complete.Items[0].PdfPath), "repeated batch never overwrites earlier output");

int failureCalls = 0;
var failureService = new BatchPdfExportService(async (_, destination, _, token) =>
{
    failureCalls++;
    if (failureCalls == 1) { await File.WriteAllTextAsync(destination, "%PDF-partial", token); throw new IOException("Falha de teste"); }
    await File.WriteAllBytesAsync(destination, fakePdf, token);
}, Destination("cache-failures"));
var failed = await failureService.ExportAsync([
    Source("bad", "Falha", first), Source("missing", "Ausente", Destination("missing.mscz")),
    Source("pending", "Alterada", first, "Atualize a partitura antes de exportar."), Source("good", "Concluída", second)
], Destination("failed"), null, CancellationToken.None);
Check(failed.FailedCount == 2 && failed.SkippedCount == 1 && failed.SavedCount == 1, "failure and blocked source do not stop subsequent score");
Check(failed.Items[3].Status == BatchPdfStatus.Success && failureCalls == 2, "blocked and missing rows never reach exporter");
Check(Directory.GetFiles(failed.Directory!, "*.pdf").Length == 1, "failed partial PDF never published");
Check(!Directory.EnumerateDirectories(failed.Directory!).Any(), "failure temp files removed");
Check(failed.Items[1].Message.Contains("indisponível"), "missing source has actionable explanation");
Check(failed.Items[2].Message.StartsWith("Atualize"), "pending edits explanation retained");
Check(File.ReadAllText(failed.ReportPath!).Contains("Falha de teste"), "item errors persisted in report");

foreach (var invalidPdf in new[] { new byte[30], Encoding.ASCII.GetBytes(new string('x', 200)), Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 200)) })
{
    var invalid = new BatchPdfExportService((_, destination, _, token) => File.WriteAllBytesAsync(destination, invalidPdf, token), Destination("cache-invalid"));
    var result = await invalid.ExportAsync([Source("badpdf", "Inválido", first)], Destination("invalid"), null, CancellationToken.None);
    Check(result.FailedCount == 1 && Directory.GetFiles(result.Directory!, "*.pdf").Length == 0, "invalid or unfinished PDF rejected");
}

using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    var result = await service.ExportAsync([Source("c", "Cancelada", first)], Destination("pre-cancelled"), null, cancelled.Token);
    Check(result.Cancelled && result.CancelledCount == 1 && result.Directory is null, "pre-cancel returns explicit result");
    Check(!Directory.Exists(Destination("pre-cancelled")), "pre-cancel does not create output");
}
using (var cancellation = new CancellationTokenSource())
{
    int cancelCalls = 0;
    var cancelService = new BatchPdfExportService(async (_, destination, _, token) =>
    {
        cancelCalls++;
        if (cancelCalls == 1) { await File.WriteAllBytesAsync(destination, fakePdf, token); return; }
        await File.WriteAllTextAsync(destination, "%PDF-incomplete", token);
        cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token);
    }, Destination("cache-cancel"));
    var result = await cancelService.ExportAsync([Source("a", "Concluída", first), Source("b", "Em andamento", second), Source("c", "Aguardando", first)], Destination("cancelled"), null, cancellation.Token);
    Check(result.Cancelled && result.SavedCount == 1 && result.CancelledCount == 2, "cancel marks active and pending scores");
    Check(cancelCalls == 2, "cancelled batch does not start next item");
    Check(Directory.GetFiles(result.Directory!, "*.pdf").Length == 1 && File.Exists(result.Items[0].PdfPath), "completed PDF preserved, partial unpublished");
    Check(!Directory.EnumerateDirectories(result.Directory!).Any(), "cancel cleans private temp output");
    Check(File.ReadAllText(result.ReportPath!).Contains("Operação cancelada"), "cancellation report persisted");
}
using (var cancellation = new CancellationTokenSource())
{
    var result = await service.ExportAsync([Source("copy-cancel", "Cancelar cópia", first)], Destination("copy-cancelled"),
        new InlineProgress<BatchPdfProgress>(p => { if (p.Stage.StartsWith("Copiando o PDF")) cancellation.Cancel(); }), cancellation.Token);
    Check(result.Cancelled && result.CancelledCount == 1 && result.SavedCount == 0, "cancellation between rendering and destination copy is honored");
    Check(!Directory.GetFiles(result.Directory!, "*.pdf").Any() && !Directory.GetDirectories(result.Directory!).Any(), "cancelled copy does not publish PDF or leave staging files");
}

string longDestination = Destination("destination-" + new string('a', 60));
while (longDestination.Length < 310) longDestination = Path.Combine(longDestination, "subfolder-" + new string('b', 40));
var longResult = await service.ExportAsync([Source("long", "Pasta longa", first)], longDestination, null, CancellationToken.None);
Check(longResult.SavedCount == 1 && File.Exists(longResult.Items[0].PdfPath), "managed destination copy supports paths beyond 260 characters");

string changed = Fixture("externally-changed");
var changedService = new BatchPdfExportService(async (_, destination, _, token) =>
{
    await File.WriteAllBytesAsync(destination, fakePdf, token); File.AppendAllText(changed, "external update");
}, Destination("cache-changed"));
var changedResult = await changedService.ExportAsync([Source("changed", "Alterada fora do aplicativo", changed)], Destination("changed"), null, CancellationToken.None);
Check(changedResult.FailedCount == 1 && changedResult.Items[0].Message.Contains("mudou durante"), "source mutation prevents stale publication");
Check(Directory.GetFiles(changedResult.Directory!, "*.pdf").Length == 0, "mutated score has no final PDF");
Check(File.ReadAllText(changed).EndsWith("external update"), "external modification never overwritten");
Check(Hash(first) == firstHash && Hash(second) == secondHash, "all other originals preserved across errors and cancellation");

foreach (var invalid in new IReadOnlyList<BatchPdfSource>[] { [], [Source("same", "A", first), Source("same", "B", second)], [Source("", "A", first)] })
{
    bool rejected = false; try { await service.ExportAsync(invalid, Destination("invalid-input"), null, CancellationToken.None); } catch (ArgumentException) { rejected = true; }
    Check(rejected && !Directory.Exists(Destination("invalid-input")), "invalid request rejected before creating output");
}
if (args.Length >= 3)
{
    string realScore = Path.GetFullPath(args[1]), museScore = args[2], realHash = Hash(realScore);
    var real = new BatchPdfExportService(museScore, Destination("real-cache"));
    var exported = await real.ExportAsync([Source("real-a", "Partitura completa", realScore), Source("real-b", "Partitura completa", realScore)], longDestination, null, CancellationToken.None);
    Check(exported.SavedCount == 2 && exported.FailedCount == 0, "MuseScore exports two complete separate PDFs");
    Check(exported.Items.All(item => item.PdfPath!.Length > 300), "real MuseScore batch supports long destination via cache rendering");
    Check(Hash(realScore) == realHash, "real MSCZ preserved");
    await File.WriteAllTextAsync(Path.Combine(root, "real-report.json"), System.Text.Json.JsonSerializer.Serialize(exported, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
var named = await service.ExportAsync([
    new("artist-a", "Luz divina", first, Artist: "Roberto Carlos"),
    new("artist-b", "Luz divina", second, Artist: "Outro intérprete"),
    new("artist-repeat", "Luz divina", first, Artist: "Roberto Carlos")
], Destination("property-names"), null, CancellationToken.None);
Check(named.SavedCount == 3 && named.Items.Select(item => item.PdfFileName).SequenceEqual([
    "Roberto Carlos - Luz divina.pdf", "Outro intérprete - Luz divina.pdf", "Roberto Carlos - Luz divina (2).pdf"
]), "Batch PDF uses performer and title, distinguishes performers and numbers repeated names");
Check(Hash(first) == firstHash && Hash(second) == secondHash, "Renaming exported copies preserves original MSCZ files");
await File.WriteAllTextAsync(Path.Combine(root, "checks.txt"), $"PASS {checks} batch PDF checks\n");
Console.WriteLine($"PASS {checks} batch PDF checks. Results: {root}");

sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
