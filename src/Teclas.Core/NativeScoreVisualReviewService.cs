using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>Rendered visual review with a private preview and atomic publication.</summary>
public sealed class NativeScoreVisualReviewService(string museScorePath, string pythonPath, string engineDirectory, string cacheDirectory)
{
    private readonly ConcurrentDictionary<string, AnalysisStamp> analyses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PreparedStamp> proposals = new(StringComparer.OrdinalIgnoreCase);

    public async Task<NativeVisualReviewReport> AnalyzeAsync(string path, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        string hash = await Task.Run(() => NativeMusicEditor.HashFile(path, token), token);
        var source = await Task.Run(() => NativeScoreArchive.Read(path), token);
        string fingerprint = await Task.Run(source.MusicalFingerprint, token);
        string job = Job("analise");
        string snapshot = Path.Combine(job, "original.mscz");
        progress?.Report("Preservando a partitura original e gerando todas as páginas para revisão…");
        await CopyNew(path, snapshot, token);
        if (Hash(snapshot, token) != hash) throw new IOException("A partitura mudou durante a leitura. Analise novamente.");
        var pages = await Exporter().ExportPreviewAsync(snapshot, Path.Combine(job, "pagina.png"), token);
        progress?.Report("Medindo cifras, diagramas, textos e limites das páginas…");
        var geometry = await Geometry(snapshot, job, progress, token);
        CheckSource(path, hash, token);
        var original = new NativeScoreResult(snapshot, pages, source.Metadata, fingerprint);
        var report = Report(path, hash, original, geometry);
        analyses[snapshot] = new(report, geometry, Hashes(original, token), Signature(report));
        return report;
    }

    public async Task<NativeVisualReviewPrepared> PrepareAsync(NativeVisualReviewReport report,
        IProgress<string>? progress, CancellationToken token)
    {
        var analysis = CheckAnalysis(report, token);
        if (!report.CanPrepare) throw new InvalidOperationException("Não há uma correção automática comprovada para os achados. Confira os trechos indicados no MuseScore.");
        string job = Job("proposta");
        string input = report.Original.Path;
        var geometry = analysis.Geometry;
        var changes = new HashSet<string>(StringComparer.Ordinal);
        string? rendered = null;
        NativeScoreArchive? final = null;
        bool completed = false;
        try
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"Ajustando espaço e conferindo novamente — etapa {attempt + 1} de até 6…");
                string pass = Path.Combine(job, "passo-" + attempt); Directory.CreateDirectory(pass);
                var candidate = await Task.Run(() => NativeScoreArchive.Read(input), token);
                if (!ApplyHints(candidate, geometry, changes))
                    throw new InvalidOperationException("Os trechos restantes não têm ajuste automático seguro. Use os locais indicados para conferir no MuseScore; nenhuma nova versão foi salva.");
                if (candidate.MusicalFingerprint() != report.Original.MusicalFingerprint)
                    throw new InvalidDataException("O ajuste visual alterou conteúdo musical. A proposta foi interrompida.");
                string draft = Path.Combine(pass, "ajuste.mscz");
                await Task.Run(() => candidate.Save(draft), token);
                rendered = Path.Combine(pass, "revisada.mscz");
                await Exporter().ExportNativeAsync(draft, rendered, token);
                final = await Task.Run(() => NativeScoreArchive.Read(rendered), token);
                if (final.MusicalFingerprint() != report.Original.MusicalFingerprint || !SameMetadata(final.Metadata, report.Metadata))
                    throw new InvalidDataException("O MuseScore modificou estrutura musical ou cabeçalho. A proposta não foi salva; o original permanece intacto.");
                geometry = await Geometry(rendered, pass, progress, token);
                var issues = geometry["issues"]!.AsArray();
                if (!issues.Any(issue => issue?["severity"]?.GetValue<string>() != "suggestion")) break;
                input = rendered;
            }
            if (geometry["issues"]!.AsArray().Any(issue => issue?["severity"]?.GetValue<string>() != "suggestion"))
                throw new InvalidOperationException("A prévia ainda apresenta sobreposições ou cortes. Não foi possível concluir uma proposta segura; confira os trechos no MuseScore.");
            if (rendered is null || final is null) throw new InvalidOperationException("A proposta visual não foi produzida.");
            progress?.Report("Gerando a prévia corrigida para comparar com o original…");
            var pages = await Exporter().ExportPreviewAsync(rendered, Path.Combine(job, "pagina.png"), token);
            CheckAnalysis(report, token);
            var result = new NativeScoreResult(rendered, pages, final.Metadata, report.Original.MusicalFingerprint);
            var after = Report(report.SourcePath, report.SourceSha256, result, geometry);
            var proposal = new NativeVisualReviewPrepared(result, after, changes.ToArray());
            proposals[result.Path] = new(report, proposal, Signature(report), Signature(proposal), Hashes(result, token));
            completed = true;
            return proposal;
        }
        finally
        {
            // These are private, newly created jobs only. No source path is removed.
            if (!completed) TryDeleteJob(job);
        }
    }

    public async Task<NativeScoreResult> PublishAsync(NativeVisualReviewReport report, NativeVisualReviewPrepared prepared,
        string outputDirectory, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(outputDirectory)) throw new ArgumentException("Escolha uma pasta de saída com caminho completo.", nameof(outputDirectory));
        if (!proposals.TryGetValue(prepared.Result.Path, out var stamp) || stamp.ReportSignature != Signature(report) || stamp.ProposalSignature != Signature(prepared))
            throw new InvalidOperationException("Prepare e confira uma nova prévia antes de salvar a revisão visual.");
        await stamp.Gate.WaitAsync(token);
        string? pending = null; bool published = false;
        try
        {
            if (stamp.Published) throw new InvalidOperationException("Esta prévia já foi salva. Abra a nova versão para continuar.");
            progress?.Report("Conferindo o original, a prévia aprovada e todas as páginas…");
            CheckAnalysis(report, token); CheckHashes(stamp.Hashes, token);
            if (NativeScoreDocument.MusicalFingerprint(prepared.Result.Path) != report.Original.MusicalFingerprint)
                throw new IOException("A música da prévia foi alterada. Prepare a proposta novamente.");
            Directory.CreateDirectory(outputDirectory);
            string id = Guid.NewGuid().ToString("N");
            pending = Path.Combine(outputDirectory, ".teclas-visual-" + id); Directory.CreateDirectory(pending);
            string basename = ScoreFileName.Stem(prepared.Result.Metadata.Artist, prepared.Result.Metadata.Title, report.SourcePath);
            string score = Path.Combine(pending, basename + ".mscz");
            await CopyNew(prepared.Result.Path, score, token);
            if (Hash(score, token) != stamp.Hashes[prepared.Result.Path]) throw new IOException("A cópia divergiu da prévia aprovada.");
            var pages = new List<string>();
            for (int i = 0; i < prepared.Result.PreviewPages.Count; i++)
            {
                string page = Path.Combine(pending, $"pagina-{i + 1}.png");
                await CopyNew(prepared.Result.PreviewPages[i], page, token);
                if (Hash(page, token) != stamp.Hashes[prepared.Result.PreviewPages[i]]) throw new IOException("Uma página mudou durante a publicação.");
                pages.Add(page);
            }
            CheckAnalysis(report, token); CheckHashes(stamp.Hashes, token);
            token.ThrowIfCancellationRequested();
            string destination = Path.Combine(outputDirectory, basename + " - " + id[..10]);
            Directory.Move(pending, destination); published = true; stamp.Published = true;
            return new(Path.Combine(destination, Path.GetFileName(score)), pages.Select(p => Path.Combine(destination, Path.GetFileName(p))).ToArray(), prepared.Result.Metadata, prepared.Result.MusicalFingerprint);
        }
        finally { if (!published && pending is not null) TryDeleteJob(pending); stamp.Gate.Release(); }
    }

    private async Task<JsonObject> Geometry(string score, string folder, IProgress<string>? progress, CancellationToken token)
    {
        string svg = Path.Combine(folder, "geometria.svg");
        await Run(museScorePath, ["-o", svg, score], folder, token);
        var pages = Directory.GetFiles(folder, "geometria*.svg").OrderBy(path => PageNumber(path)).ToArray();
        if (pages.Length == 0) throw new InvalidDataException("O MuseScore não gerou a geometria das páginas.");
        string output = Path.Combine(folder, "revisao-visual.json");
        var args = new List<string> { Path.Combine(engineDirectory, "native_visual_review.py"), "--score", score, "--svg" };
        args.AddRange(pages); args.AddRange(["--output", output]);
        progress?.Report("Localizando possíveis colisões e cortes nas páginas renderizadas…");
        await Run(pythonPath, args, folder, token);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(output, token))?.AsObject() ?? throw new InvalidDataException("A análise visual não retornou um relatório válido.");
        if (json["schemaVersion"]?.GetValue<int>() != 1 || json["issues"] is not JsonArray || json["hints"] is not JsonArray)
            throw new InvalidDataException("O relatório visual está em formato incompatível.");
        // A rectangle pattern in SVG alone does not establish ownership of an
        // image. Verify the embedded keyboard template, notes and chord identity.
        var native = await Task.Run(() => NativeScoreArchive.Read(score), token);
        if (json["coverage"]?["mappedPairs"]?.GetValue<bool>() == true &&
            native.Score.Elements("Staff").Descendants("Harmony").Any(h => NativeHarmonyEditing.Inspect(native, h) is not { CanEdit: true, HasDiagram: true }))
        {
            json["coverage"]!["mappedPairs"] = false;
            foreach (var hint in json["hints"]!.AsArray().Where(h => h?["kind"]?.GetValue<string>() is "imageOffset" or "leadingSpace").ToArray()) json["hints"]!.AsArray().Remove(hint);
            foreach (var issue in json["issues"]!.AsArray().Where(i => i?["kind"]?.GetValue<string>() is "chord_diagram_collision" or "diagram_margin"))
            { issue!["canAutoFix"] = false; issue["staff"] = null; issue["measure"] = null; }
            json["limitations"]!.AsArray().Add("Uma imagem ou cifra não corresponde ao modelo de teclado reconhecido. Esses elementos precisam de revisão manual no MuseScore.");
        }
        return json;
    }

    private static NativeVisualReviewReport Report(string source, string hash, NativeScoreResult original, JsonObject json)
    {
        var pages = json["pages"]!.AsArray();
        var issues = json["issues"]!.AsArray().Select(node =>
        {
            int page = node!["page"]!.GetValue<int>();
            var bounds = pages[page - 1]!["bounds"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
            var box = node["bounds"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
            int? Optional(string name) => node[name]?.GetValue<int>();
            return new NativeVisualReviewIssue(node["id"]!.GetValue<string>(), node["kind"]!.GetValue<string>(), node["severity"]!.GetValue<string>(), page,
                Optional("system"), Optional("staff"), Optional("measure"), new((box[0]-bounds[0])/bounds[2], (box[1]-bounds[1])/bounds[3], (box[2]-box[0])/bounds[2], (box[3]-box[1])/bounds[3]),
                node["message"]!.GetValue<string>(), node["canAutoFix"]!.GetValue<bool>());
        }).ToArray();
        return new(source, hash, original.Metadata, original, issues, json["limitations"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray(),
            issues.Length > 0 && json["hints"]!.AsArray().Count > 0);
    }

    private static bool ApplyHints(NativeScoreArchive archive, JsonObject geometry, ISet<string> changes)
    {
        bool changed = false;
        var images = archive.Score.Elements("Staff").Descendants("Image").ToArray();
        bool mapped = geometry["coverage"]?["mappedPairs"]?.GetValue<bool>() == true;
        var hints = geometry["hints"]!.AsArray().GroupBy(n => (Kind: n!["kind"]!.GetValue<string>(), Index: n["index"]!.GetValue<int>()));
        foreach (var group in hints)
        {
            double amount = group.Max(n => n!["amount"]!.GetValue<double>());
            if (!double.IsFinite(amount) || Math.Abs(amount) > 32) continue;
            switch (group.Key.Kind)
            {
                case "imageOffset" when mapped && group.Key.Index >= 0 && group.Key.Index < images.Length:
                    var offset = images[group.Key.Index].Element("offset");
                    if (offset is null) { offset = new XElement("offset", new XAttribute("x", "0"), new XAttribute("y", "0")); images[group.Key.Index].Add(offset); }
                    double x = Number((string?)offset.Attribute("x"));
                    offset.SetAttributeValue("x", (x + amount).ToString("0.#####", CultureInfo.InvariantCulture));
                    changes.Add("Reposicionar diagramas com associação comprovada à cifra, mantendo suas dimensões."); changed = true; break;
                case "leadingSpace" when mapped && group.Key.Index >= 0 && group.Key.Index < images.Length && amount > 0:
                    var image = images[group.Key.Index];
                    // Native MuseScore serializes Segment settings AFTER its rhythmic
                    // element. Inserting before Chord can be overwritten by that
                    // chord's existing Segment, silently discarding the adjustment.
                    var anchor = image.ElementsAfterSelf().TakeWhile(e => e.Name != "Harmony").FirstOrDefault(e => e.Name.LocalName is "Chord" or "Rest");
                    if (anchor is null) break;
                    var segment = anchor.ElementsAfterSelf().FirstOrDefault();
                    if (segment?.Name != "Segment") { segment = new XElement("Segment"); anchor.AddAfterSelf(segment); }
                    double existing = Number((string?)segment.Element("leadingSpace"));
                    segment.SetElementValue("leadingSpace", (existing + amount).ToString("0.#####", CultureInfo.InvariantCulture));
                    changes.Add("Reservar espaço horizontal antes dos ataques que têm cifras e diagramas próximos."); changed = true; break;
                case "autoplaceTexts":
                    foreach (var text in archive.Score.Descendants().Where(e => e.Name.LocalName is "StaffText" or "SystemText" or "Tempo" or "RehearsalMark"))
                    {
                        text.Element("offset")?.Remove(); text.SetElementValue("autoplace", "1"); text.SetElementValue("minDistance", "1.2");
                    }
                    var style = archive.ReadStyle();
                    style.SetElementValue("minSystemDistance", Math.Max(14, Number((string?)style.Element("minSystemDistance"))).ToString(CultureInfo.InvariantCulture));
                    archive.WriteStyle(style);
                    changes.Add("Reativar o posicionamento automático de textos e reservar separação vertical entre sistemas."); changed = true; break;
                case "pageBreaksToLines":
                    foreach (var subtype in archive.Score.Descendants("LayoutBreak").Elements("subtype").Where(e => e.Value == "page")) { subtype.Value = "line"; changed = true; }
                    changes.Add("Liberar quebras de página mantendo as quebras de sistema existentes."); break;
            }
        }
        return changed;
    }

    private AnalysisStamp CheckAnalysis(NativeVisualReviewReport report, CancellationToken token)
    {
        if (!analyses.TryGetValue(report.Original.Path, out var stamp) || stamp.Signature != Signature(report))
            throw new InvalidOperationException("Analise novamente a versão atual antes de preparar a proposta visual.");
        CheckSource(report.SourcePath, report.SourceSha256, token); CheckHashes(stamp.Hashes, token); return stamp;
    }
    private static void CheckSource(string path, string hash, CancellationToken token)
    { if (Hash(path, token) != hash) throw new IOException("A partitura original mudou. Reabra a versão atual e analise novamente."); }
    private static Dictionary<string, string> Hashes(NativeScoreResult result, CancellationToken token) =>
        new[] { result.Path }.Concat(result.PreviewPages).ToDictionary(path => path, path => Hash(path, token), StringComparer.OrdinalIgnoreCase);
    private static void CheckHashes(IReadOnlyDictionary<string, string> hashes, CancellationToken token)
    { foreach (var pair in hashes) if (Hash(pair.Key, token) != pair.Value) throw new IOException("Um arquivo da prévia mudou. Prepare uma nova revisão."); }
    private static string Hash(string path, CancellationToken token) => NativeMusicEditor.HashFile(path, token);
    private static string Signature<T>(T value) => JsonSerializer.Serialize(value);
    private static bool SameMetadata(NativeScoreMetadata a, NativeScoreMetadata b) => a.Title == b.Title && a.Subtitle == b.Subtitle && a.Composer == b.Composer && a.Artist == b.Artist && a.ArrangementSummary == b.ArrangementSummary && a.StaffCount == b.StaffCount && a.MeasureCount == b.MeasureCount && a.NoteCount == b.NoteCount && a.HarmonyCount == b.HarmonyCount && a.ImageCount == b.ImageCount;
    private static double Number(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : 0;
    private static int PageNumber(string path) => int.TryParse(Path.GetFileNameWithoutExtension(path).Split('-').Last(), out int n) ? n : 0;
    private string Job(string kind) { string path = Path.Combine(Path.GetFullPath(cacheDirectory), "visual-" + kind + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private ScoreExporter Exporter() => new(museScorePath, cacheDirectory);
    private static async Task CopyNew(string source, string destination, CancellationToken token)
    { await using var input = File.OpenRead(source); await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); await input.CopyToAsync(output, token); await output.FlushAsync(token); }
    private static void TryDeleteJob(string path) { try { Directory.Delete(path, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    private static async Task Run(string executable, IEnumerable<string> arguments, string folder, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar a análise visual.");
        using var registration = linked.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException) { try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { } token.ThrowIfCancellationRequested(); throw new TimeoutException("A análise visual excedeu três minutos. Tente novamente ou confira o arquivo no MuseScore."); }
        string details = (await stdout + "\n" + await stderr).Trim();
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException("Falha ao analisar a geometria da partitura. " + details[..Math.Min(details.Length, 1200)]);
    }
    private sealed record AnalysisStamp(NativeVisualReviewReport Report, JsonObject Geometry, IReadOnlyDictionary<string, string> Hashes, string Signature);
    private sealed class PreparedStamp(NativeVisualReviewReport report, NativeVisualReviewPrepared proposal, string reportSignature, string proposalSignature, IReadOnlyDictionary<string, string> hashes)
    {
        internal NativeVisualReviewReport Report { get; } = report;
        internal NativeVisualReviewPrepared Proposal { get; } = proposal;
        internal string ReportSignature { get; } = reportSignature;
        internal string ProposalSignature { get; } = proposalSignature;
        internal IReadOnlyDictionary<string, string> Hashes { get; } = hashes;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal bool Published { get; set; }
    }
}
