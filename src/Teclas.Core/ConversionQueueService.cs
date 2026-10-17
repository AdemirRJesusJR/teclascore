using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Teclas.Core;

/// <summary>Manually started durable queue. Receipts recover committed copies without regenerating them.</summary>
public sealed class ConversionQueueService : IDisposable
{
    private const string PublicationReceipt = ".teclas-queue-receipt.json";
    private readonly string root;
    private readonly IConversionQueueWorker worker;
    private readonly FileStream lease;
    private readonly SemaphoreSlim gate = new(1, 1), runGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly List<Job> jobs = [];
    private bool disposed;
    private int operations;
    public event EventHandler? Changed;
    public string? RecoveryMessage { get; private set; }
    public bool IsRunning { get; private set; }
    public IReadOnlyList<ConversionQueueItem> Items { get { lock (sync) return jobs.Select(Snapshot).ToArray(); } }

    public ConversionQueueService(string queueDirectory, IConversionQueueWorker? worker = null)
    {
        if (!Path.IsPathFullyQualified(queueDirectory)) throw new ArgumentException("Escolha uma pasta completa para guardar a fila.", nameof(queueDirectory));
        root = Path.GetFullPath(queueDirectory); Directory.CreateDirectory(root); NoLinks(root);
        this.worker = worker ?? new ProcessWorker();
        try { lease = new(Path.Combine(root, "queue.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("A fila está aberta em outra instância do TeclaScore. Feche-a antes de retomar aqui.", error); }
        try { Load(); } catch { lease.Dispose(); throw; }
    }

    public async Task<ConversionQueueItem> EnqueueAsync(ConversionQueueRecipe recipe, CancellationToken token = default)
    {
        using var operation = Enter(token); token = operation.Token;
        ArgumentNullException.ThrowIfNull(recipe); ValidateRecipe(recipe); recipe = Clone(recipe);
        string id = Guid.NewGuid().ToString("N"), workspace = JobPath(id);
        Directory.CreateDirectory(Path.Combine(workspace, "source"));
        string snapshot = Path.Combine(workspace, "source", Path.GetFileName(recipe.SourcePath));
        await CopyNew(recipe.SourcePath, snapshot, token).ConfigureAwait(false);
        string hash = await Hash(snapshot, token).ConfigureAwait(false);
        if (recipe.SourceSha256 is { Length: > 0 } known && !SameHash(hash, known))
            throw new IOException("O MIDI mudou desde a importação. Importe sua versão atual antes de adicioná-lo à fila.");
        recipe = recipe with { SourceSha256 = hash };
        string? analysisHash = await CopyExistingAnalysis(recipe, snapshot, workspace, hash, token).ConfigureAwait(false);
        string recipePath = Path.Combine(workspace, "recipe.json");
        await WriteNew(recipePath, JsonSerializer.SerializeToUtf8Bytes(recipe), token).ConfigureAwait(false);
        var job = new Job
        {
            Id = id, DisplayName = string.IsNullOrWhiteSpace(recipe.DisplayName) ? Path.GetFileName(recipe.SourcePath) : recipe.DisplayName,
            Recipe = recipe, SourceSha256 = hash, RecipeSha256 = await Hash(recipePath, token).ConfigureAwait(false),
            AnalysisSha256 = analysisHash, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Status = ConversionQueueStatus.Pending, Stage = "Aguardando início"
        };
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            lock (sync) jobs.Add(job);
            try { Save(); } catch { lock (sync) jobs.Remove(job); throw; }
        }
        finally { gate.Release(); }
        Notify(); return Snapshot(job);
    }

    public async Task ResumeAsync(string id, CancellationToken token = default)
    {
        using var operation = Enter(token);
        await gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            var job = Find(id);
            if (job.Status is not (ConversionQueueStatus.Paused or ConversionQueueStatus.Failed))
                throw new InvalidOperationException("Somente itens pausados ou com falha podem voltar para a espera.");
            var previous = (job.Status, job.Stage, job.Error, job.UpdatedAt);
            lock (sync) { job.Status = ConversionQueueStatus.Pending; job.Stage = "Aguardando retomada"; job.Error = null; job.UpdatedAt = DateTimeOffset.UtcNow; }
            try { Save(); }
            catch { lock (sync) (job.Status, job.Stage, job.Error, job.UpdatedAt) = previous; throw; }
        }
        finally { gate.Release(); }
        Notify();
    }

    public async Task RemoveAsync(string id, CancellationToken token = default)
    {
        using var operation = Enter(token);
        await gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            var job = Find(id);
            if (job.Status == ConversionQueueStatus.Running) throw new InvalidOperationException("Pause a conversão antes de retirá-la da fila.");
            int index; lock (sync) { index = jobs.IndexOf(job); jobs.Remove(job); }
            try { Save(); } catch { lock (sync) jobs.Insert(index, job); throw; }
        }
        finally { gate.Release(); }
        Notify();
    }

    public async Task RunAsync(IProgress<ConversionQueueProgress>? progress = null, CancellationToken token = default)
    {
        using var operation = Enter(token); token = operation.Token;
        if (!await runGate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("A fila já está em execução.");
        IsRunning = true; Notify();
        try
        {
            while (!token.IsCancellationRequested)
            {
                Job? job;
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    lock (sync) job = jobs.FirstOrDefault(item => item.Status == ConversionQueueStatus.Pending);
                    if (job is null) break;
                    lock (sync) { job.Status = ConversionQueueStatus.Running; job.Attempts++; job.Error = null; job.Stage = "Conferindo a retomada"; job.UpdatedAt = DateTimeOffset.UtcNow; }
                    Save();
                }
                finally { gate.Release(); }
                Report(job, progress);
                bool committed = false;
                try
                {
                    var result = await Execute(job, progress, token).ConfigureAwait(false);
                    committed = true;
                    // Publication is already atomic and complete: ignore cancellation after the final rename.
                    await SetState(job, ConversionQueueStatus.Completed, "Concluída", null, result).ConfigureAwait(false);
                    Report(job, progress);
                }
                catch (OperationCanceledException) when (!committed)
                {
                    await SetState(job, ConversionQueueStatus.Paused, "Pausada · pronta para retomar", null, null).ConfigureAwait(false);
                    Report(job, progress); break;
                }
                catch (Exception error) when (!committed && error is not OutOfMemoryException)
                {
                    await SetState(job, ConversionQueueStatus.Failed, "Falha · pode tentar novamente", error.Message, null).ConfigureAwait(false);
                    Report(job, progress);
                }
            }
        }
        finally { IsRunning = false; runGate.Release(); Notify(); }
    }

    private async Task<ConversionQueueResult> Execute(Job job, IProgress<ConversionQueueProgress>? progress, CancellationToken token)
    {
        string workspace = JobPath(job.Id), localOutput = Path.Combine(workspace, "output"), final = FinalPath(job);
        int attempt = job.Attempts;
        NoLinks(workspace); NoLinks(job.Recipe.OutputDirectory);
        if (Directory.Exists(final)) return await RecoverPublished(job, final, token).ConfigureAwait(false);
        await VerifyFrozen(job, token).ConfigureAwait(false);
        string receipt = Path.Combine(workspace, "queue-receipt.json");
        if (File.Exists(receipt))
            return await Publish(job, await ReadEngineReceipt(job, receipt, localOutput, token).ConfigureAwait(false), progress, token).ConfigureAwait(false);
        string analysisPath = Path.Combine(workspace, "analysis.json");
        if (job.AnalysisSha256 is { } checkpoint)
        {
            if (!File.Exists(analysisPath) || !SameHash(await Hash(analysisPath, token).ConfigureAwait(false), checkpoint))
                throw new IOException("A análise guardada para esta fila foi alterada. Retire o item e adicione uma nova receita.");
        }
        else
        {
            var request = (JsonObject)job.Recipe.AnalyzeOptions.DeepClone(); SetControlled(request, job, "analyze");
            var result = await worker.RunAsync(Clone(job.Recipe), request, workspace, new StageProgress(message => Stage(job, message, progress, attempt, token)), token).ConfigureAwait(false);
            string returnedPath = RequiredString(result, "analysisPath"); RequireInside(returnedPath, workspace);
            if (!SamePath(returnedPath, analysisPath)) throw new InvalidDataException("O motor retornou uma análise fora do checkpoint esperado.");
            var analysis = await ReadObject(analysisPath, token).ConfigureAwait(false);
            if (!AnalysisMatches(analysis, job.Recipe, job.SourceSha256) || !SamePath(RequiredString(analysis, "source"), SnapshotPath(job)))
                throw new InvalidDataException("A análise retornada não corresponde à receita de melodia congelada.");
            string analysisHash = await Hash(analysisPath, token).ConfigureAwait(false);
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { lock (sync) job.AnalysisSha256 = analysisHash; Save(); } finally { gate.Release(); }
        }
        token.ThrowIfCancellationRequested();
        if (Directory.Exists(localOutput))
        {
            NoLinks(localOutput);
            // Incomplete attempts remain private evidence, never overwriting user output.
            Directory.Move(localOutput, Path.Combine(workspace, "incomplete-" + Guid.NewGuid().ToString("N")));
        }
        Directory.CreateDirectory(localOutput);
        var generate = (JsonObject)job.Recipe.GenerateOptions.DeepClone(); SetControlled(generate, job, "generate");
        var analysisDefaults = await ReadObject(analysisPath, token).ConfigureAwait(false);
        var key = analysisDefaults["harmony"]?["key"];
        if (!generate.ContainsKey("keyFifths")) generate["keyFifths"] = analysisDefaults["keyFifths"]?.DeepClone() ?? key?["fifths"]?.DeepClone() ?? JsonValue.Create(0);
        if (!generate.ContainsKey("keyMinor")) generate["keyMinor"] = analysisDefaults["keyMinor"]?.DeepClone() ?? key?["minor"]?.DeepClone() ?? JsonValue.Create(false);
        if (!generate.ContainsKey("chords") && analysisDefaults["chords"] is JsonArray chords)
        {
            var analyzed = (JsonArray)chords.DeepClone();
            // Chords analyzed here receive the library's remembered diagram positions, like the editor does.
            if (generate["chordPreferences"] is JsonArray preferences)
                DiagramPositionPropagation.ApplyPreferences(analyzed, preferences, (int?)generate["transposeSemitones"] ?? 0);
            if ((bool?)generate["simplifyChords"] == true) analyzed = ChordSimplifier.SimplifyAll(analyzed);
            generate["chords"] = analyzed;
        }
        generate.Remove("chordPreferences"); generate.Remove("simplifyChords");
        generate["analysisPath"] = analysisPath; generate["musescorePath"] = job.Recipe.MuseScorePath; generate["outputDirectory"] = localOutput;
        generate["queueReceipt"] = receipt; generate["queueJobId"] = job.Id; generate["queueRecipeSha256"] = job.RecipeSha256;
        await worker.RunAsync(Clone(job.Recipe), generate, workspace, new StageProgress(message => Stage(job, message, progress, attempt, token)), token).ConfigureAwait(false);
        if (!File.Exists(receipt)) throw new InvalidDataException("O motor terminou sem o comprovante de conversão da fila. Atualize o motor antes de retomar.");
        return await Publish(job, await ReadEngineReceipt(job, receipt, localOutput, token).ConfigureAwait(false), progress, token).ConfigureAwait(false);
    }

    private async Task<ConversionQueueResult> Publish(Job job, Manifest manifest, IProgress<ConversionQueueProgress>? progress, CancellationToken token)
    {
        string output = Path.GetFullPath(job.Recipe.OutputDirectory), final = FinalPath(job);
        Directory.CreateDirectory(output); NoLinks(output);
        if (Directory.Exists(final)) return await RecoverPublished(job, final, token).ConfigureAwait(false);
        string pending = Path.Combine(output, ".teclas-fila-" + job.Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pending);
        var copied = new List<string>(); bool published = false;
        try
        {
            Stage(job, "Publicando a partitura e conferindo todas as páginas…", progress, job.Attempts, token);
            foreach (var file in manifest.Files)
            {
                string destination = Path.Combine(pending, file.RelativePath); RequireInside(destination, pending);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); copied.Add(destination);
                await CopyNew(file.Path, destination, token).ConfigureAwait(false);
                if (!SameHash(await Hash(destination, token).ConfigureAwait(false), file.Sha256)) throw new IOException("Uma cópia não corresponde ao comprovante de conversão.");
            }
            var publication = new Publication
            {
                SchemaVersion = 1, JobId = job.Id, SourceSha256 = job.SourceSha256, RecipeSha256 = job.RecipeSha256,
                Result = manifest.Result, Files = manifest.Files.Select(file => file with { Path = file.RelativePath }).ToArray()
            };
            string receipt = Path.Combine(pending, PublicationReceipt); copied.Add(receipt);
            await WriteNew(receipt, JsonSerializer.SerializeToUtf8Bytes(publication), token).ConfigureAwait(false);
            await VerifyFrozen(job, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Move(pending, final); published = true;
            return RebaseResult(manifest.Result, final, job);
        }
        finally { if (!published) { foreach (string path in copied) TryDelete(path); RemoveEmptyDirectories(pending); } }
    }

    private async Task<ConversionQueueResult> RecoverPublished(Job job, string directory, CancellationToken token)
    {
        NoLinks(directory);
        string receiptPath = Path.Combine(directory, PublicationReceipt);
        if (!File.Exists(receiptPath)) throw new IOException("A pasta final desta conversão já existe sem um comprovante válido. Ela foi preservada; confira-a antes de tentar novamente.");
        var receipt = JsonSerializer.Deserialize<Publication>(await ReadBounded(receiptPath, token).ConfigureAwait(false)) ?? throw new InvalidDataException("Comprovante de publicação vazio.");
        if (receipt.SchemaVersion != 1 || receipt.JobId != job.Id || !SameHash(receipt.SourceSha256, job.SourceSha256) || !SameHash(receipt.RecipeSha256, job.RecipeSha256))
            throw new InvalidDataException("O comprovante da pasta final pertence a outra receita. Nenhum arquivo foi sobrescrito.");
        await ValidateArtifacts(receipt.Files, directory, token, relative: true).ConfigureAwait(false);
        ValidateResult(receipt.Result, receipt.Files);
        return RebaseResult(receipt.Result, directory, job);
    }

    private async Task<Manifest> ReadEngineReceipt(Job job, string receiptPath, string directory, CancellationToken token)
    {
        RequireInside(receiptPath, JobPath(job.Id));
        var receipt = await ReadObject(receiptPath, token).ConfigureAwait(false);
        if (receipt["schemaVersion"]?.GetValue<int>() != 1 || RequiredString(receipt, "jobId") != job.Id ||
            !SameHash(RequiredString(receipt, "recipeSha256"), job.RecipeSha256) || !SameHash(RequiredString(receipt, "sourceSha256"), job.SourceSha256))
            throw new InvalidDataException("O comprovante de geração não corresponde à receita congelada.");
        var result = receipt["result"]?.AsObject() ?? throw new InvalidDataException("Comprovante sem resultado.");
        var artifacts = receipt["artifacts"]?.AsArray() ?? throw new InvalidDataException("Comprovante sem arquivos.");
        var files = artifacts.Select(item => new Artifact(RequiredString(item!.AsObject(), "path"), RequiredString(item.AsObject(), "sha256"),
            Path.GetRelativePath(directory, RequiredString(item.AsObject(), "path")))).ToArray();
        await ValidateArtifacts(files, directory, token, relative: false).ConfigureAwait(false);
        var relativeResult = (JsonObject)result.DeepClone();
        foreach (string key in new[] { "scorePath", "previewPath", "reviewDirectory" })
        { string path = RequiredString(result, key); RequireInside(path, directory); relativeResult[key] = Path.GetRelativePath(directory, path); }
        ValidateResult(relativeResult, files);
        return new(relativeResult, files);
    }

    private static async Task ValidateArtifacts(IReadOnlyList<Artifact> files, string root, CancellationToken token, bool relative)
    {
        if (files.Count is < 2 or > 10000) throw new InvalidDataException("O comprovante contém uma quantidade inválida de arquivos.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string path = relative ? Path.Combine(root, file.Path) : file.Path; RequireInside(path, root);
            if (!paths.Add(Path.GetFullPath(path)) || !ValidHash(file.Sha256) || !File.Exists(path) || !SameHash(await Hash(path, token).ConfigureAwait(false), file.Sha256))
                throw new InvalidDataException("Um arquivo da conversão está ausente, repetido ou diferente do comprovante. Os resultados existentes foram preservados.");
            if (!string.Equals(Path.GetRelativePath(root, path), file.RelativePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Um caminho do comprovante não corresponde à pasta de saída.");
        }
        foreach (string path in SafeFiles(root))
            if (Path.GetFileName(path) != PublicationReceipt && !paths.Contains(Path.GetFullPath(path)))
                throw new InvalidDataException("A pasta de conversão contém arquivos sem comprovação. Ela foi preservada para conferência.");
    }
    private static void ValidateResult(JsonObject result, IReadOnlyList<Artifact> files)
    {
        string score = RequiredString(result, "scorePath"), preview = RequiredString(result, "previewPath"), review = RequiredString(result, "reviewDirectory");
        int pages = result["pages"]?.GetValue<int>() ?? 0;
        bool Has(string path) => files.Any(file => string.Equals(file.RelativePath, path, StringComparison.OrdinalIgnoreCase));
        if (!Has(score) || !Has(preview) || !score.EndsWith(".mscz", StringComparison.OrdinalIgnoreCase) || pages is < 1 or > 10000 ||
            files.Count(file => string.Equals(Path.GetDirectoryName(file.RelativePath), review, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(file.RelativePath).StartsWith("pagina-", StringComparison.OrdinalIgnoreCase) && file.RelativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) != pages)
            throw new InvalidDataException("O resultado não comprova a partitura e todas as páginas esperadas.");
    }
    private ConversionQueueResult RebaseResult(JsonObject result, string directory, Job job)
    {
        var copy = (JsonObject)result.DeepClone();
        foreach (string key in new[] { "scorePath", "previewPath", "reviewDirectory" })
        { string path = Path.Combine(directory, RequiredString(result, key)); RequireInside(path, directory); copy[key] = path; }
        return new(RequiredString(copy, "scorePath"), RequiredString(copy, "previewPath"), RequiredString(copy, "reviewDirectory"),
            copy["pages"]!.GetValue<int>(), copy["message"]?.GetValue<string>() ?? "Partitura gerada. Confira a prévia.")
            { Data = copy, AnalysisPath = Path.Combine(JobPath(job.Id), "analysis.json") };
    }

    private async Task VerifyFrozen(Job job, CancellationToken token)
    {
        if (!SameHash(await Hash(SnapshotPath(job), token).ConfigureAwait(false), job.SourceSha256) ||
            !SameHash(await Hash(Path.Combine(JobPath(job.Id), "recipe.json"), token).ConfigureAwait(false), job.RecipeSha256) ||
            !SameHash(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(job.Recipe))), job.RecipeSha256))
            throw new IOException("O MIDI ou a receita privada desta fila foi alterado. Retire o item e adicione uma nova receita.");
    }
    private static async Task<string?> CopyExistingAnalysis(ConversionQueueRecipe recipe, string snapshot, string workspace, string sourceHash, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(recipe.ExistingAnalysisPath) || !File.Exists(recipe.ExistingAnalysisPath)) return null;
        try
        {
            var analysis = await ReadObject(recipe.ExistingAnalysisPath, token).ConfigureAwait(false);
            if (!AnalysisMatches(analysis, recipe, sourceHash)) return null;
            analysis["source"] = snapshot;
            var config = analysis["config"]!.AsObject(); config["source_path"] = snapshot; config["filename"] = Path.GetFileName(snapshot);
            string path = Path.Combine(workspace, "analysis.json");
            await WriteNew(path, Encoding.UTF8.GetBytes(analysis.ToJsonString()), token).ConfigureAwait(false);
            return await Hash(path, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or IOException) { return null; }
    }
    private static bool AnalysisMatches(JsonObject analysis, ConversionQueueRecipe recipe, string sourceHash)
    {
        if (!SameHash(analysis["sha256"]?.GetValue<string>() ?? "", sourceHash) || analysis["config"] is not JsonObject config || analysis["harmony"] is not JsonObject) return false;
        bool reviewed = analysis["reviewedProfile"]?.GetValue<bool>() ?? false;
        if ((recipe.AnalyzeOptions["useReviewedProfile"]?.GetValue<bool>() ?? false) != reviewed) return false;
        if (recipe.AnalyzeOptions["melodySources"] is not JsonArray selected || config["melody_sources"] is not JsonArray actual) return false;
        static string[] Keys(JsonArray array) => array.Select(node => node!.AsObject()).Select(node => $"{node["track"]?.GetValue<int>()}/{node["channel"]?.GetValue<int>()}/{node["program"]?.GetValue<int>()}").ToArray();
        if (reviewed) return Keys(selected).Order(StringComparer.Ordinal).SequenceEqual(Keys(actual).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        if (!JsonNode.DeepEquals(config["melody_sections"] ?? new JsonArray(), recipe.AnalyzeOptions["melodySections"] ?? new JsonArray())) return false;
        return (selected.Count < 2 || config["merge_mode"]?.GetValue<string>() == "priority") && Keys(selected).SequenceEqual(Keys(actual), StringComparer.Ordinal);
    }
    private void SetControlled(JsonObject request, Job job, string mode)
    {
        foreach (string key in new[] { "queueReceipt", "queueJobId", "queueRecipeSha256", "analysisPath", "outputDirectory", "musescorePath" }) request.Remove(key);
        request["mode"] = mode; request["workspace"] = JobPath(job.Id); request["inputMidi"] = SnapshotPath(job);
    }
    private async Task SetState(Job job, ConversionQueueStatus status, string stage, string? error, ConversionQueueResult? result)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { lock (sync) { job.Status = status; job.Stage = stage; job.Error = error; job.Result = result; job.UpdatedAt = DateTimeOffset.UtcNow; } Save(); }
        finally { gate.Release(); }
        Notify();
    }
    private void Stage(Job job, string stage, IProgress<ConversionQueueProgress>? progress, int attempt, CancellationToken token)
    { lock (sync) { if (token.IsCancellationRequested || job.Status != ConversionQueueStatus.Running || job.Attempts != attempt) return; job.Stage = stage; job.UpdatedAt = DateTimeOffset.UtcNow; } Report(job, progress); }
    private void Report(Job job, IProgress<ConversionQueueProgress>? progress)
    { ConversionQueueProgress value; lock (sync) value = new(job.Id, job.Stage, jobs.Count(item => item.Status == ConversionQueueStatus.Completed), jobs.Count); progress?.Report(value); Notify(); }
    private void Notify() { try { Changed?.Invoke(this, EventArgs.Empty); } catch (Exception error) when (error is not OutOfMemoryException) { /* UI listeners cannot invalidate a committed queue. */ } }

    private void Load()
    {
        string path = Path.Combine(root, "queue.json"), backup = path + ".bak";
        if (!File.Exists(path) && !File.Exists(backup)) return;
        QueueState state; bool recovered = false;
        try { state = ReadState(path); }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            try { state = ReadState(backup); }
            catch (Exception backupError) when (backupError is IOException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
            { throw new InvalidDataException("A fila e sua cópia de segurança não puderam ser lidas. Os arquivos foram preservados para recuperação.", new AggregateException(error, backupError)); }
            recovered = true; RecoveryMessage = "A fila foi recuperada da cópia de segurança. Confira os itens antes de retomá-los.";
            if (File.Exists(path)) File.Move(path, Path.Combine(root, "queue-corrupt-" + Guid.NewGuid().ToString("N") + ".json"));
        }
        jobs.AddRange(state.Jobs);
        foreach (var job in jobs)
            if (job.Status == ConversionQueueStatus.Running)
            { job.Status = ConversionQueueStatus.Paused; job.Stage = "Interrompida · retome quando desejar"; job.UpdatedAt = DateTimeOffset.UtcNow; recovered = true; }
        if (recovered) Save();
    }
    private QueueState ReadState(string path)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(ReadBounded(path, CancellationToken.None).GetAwaiter().GetResult()) ?? throw new InvalidDataException("Fila vazia.");
        if (envelope.SchemaVersion != 1 || !SameHash(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload))), envelope.Sha256))
            throw new InvalidDataException("A integridade da fila não foi confirmada.");
        var state = JsonSerializer.Deserialize<QueueState>(envelope.Payload) ?? throw new InvalidDataException("Conteúdo da fila vazio.");
        if (state.Jobs is null || state.Jobs.Length > 10000) throw new InvalidDataException("Quantidade de itens inválida.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in state.Jobs)
        {
            if (job is null || job.Recipe is null || !Guid.TryParseExact(job.Id, "N", out _) || !ids.Add(job.Id) || !Enum.IsDefined(job.Status) || job.Attempts < 0 ||
                !ValidHash(job.SourceSha256) || !ValidHash(job.RecipeSha256)) throw new InvalidDataException("Um item da fila é inválido.");
            ValidateRecipe(job.Recipe);
        }
        return state;
    }
    private void Save()
    {
        byte[] bytes;
        lock (sync)
        {
            string payload = JsonSerializer.Serialize(new QueueState { Jobs = jobs.ToArray() });
            bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))));
        }
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("A fila atingiu seu limite de tamanho. Retire itens antigos antes de adicionar outros.");
        string path = Path.Combine(root, "queue.json"), temp = Path.Combine(root, ".queue-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
        finally { TryDelete(temp); }
    }
    private static void ValidateRecipe(ConversionQueueRecipe recipe)
    {
        if (recipe.AnalyzeOptions is null || recipe.GenerateOptions is null ||
            new[] { recipe.SourcePath, recipe.PythonPath, recipe.EngineDirectory, recipe.MuseScorePath, recipe.OutputDirectory }.Any(path => !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("A receita precisa de opções e caminhos completos para fonte, ferramentas e saída.");
        if (Path.GetExtension(recipe.SourcePath).ToLowerInvariant() is not (".mid" or ".midi" or ".kar")) throw new ArgumentException("Esta fila recebe MIDIs. Partituras MuseScore usam o editor musical.");
        if (recipe.SourceSha256 is { Length: > 0 } hash && !ValidHash(hash)) throw new ArgumentException("A identificação do MIDI está inválida.");
    }
    private Job Find(string id) { lock (sync) return jobs.FirstOrDefault(job => job.Id == id) ?? throw new KeyNotFoundException("O item não está mais na fila."); }
    private string JobPath(string id) { if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Identificador de conversão inválido."); return Path.Combine(root, "jobs", id); }
    private string SnapshotPath(Job job) => Path.Combine(JobPath(job.Id), "source", Path.GetFileName(job.Recipe.SourcePath));
    private static string FinalPath(Job job)
    {
        string name = Path.GetFileNameWithoutExtension(job.Recipe.SourcePath);
        foreach (char value in Path.GetInvalidFileNameChars()) name = name.Replace(value, '_');
        name = name[..Math.Min(name.Length, 70)].TrimEnd('.', ' ');
        return Path.Combine(job.Recipe.OutputDirectory, (name.Length == 0 ? "Partitura" : name) + " - fila-" + job.Id);
    }
    private ConversionQueueItem Snapshot(Job job) => new(job.Id, job.DisplayName, job.Recipe.SourcePath, SnapshotPath(job), job.SourceSha256, JobPath(job.Id),
        job.Status, job.Attempts, job.Stage, job.Error, job.CreatedAt, job.UpdatedAt,
        job.Result is { } result ? result with { Data = (JsonObject)result.Data.DeepClone() } : null, Clone(job.Recipe));
    private static ConversionQueueRecipe Clone(ConversionQueueRecipe recipe) => recipe with
        { AnalyzeOptions = (JsonObject)recipe.AnalyzeOptions.DeepClone(), GenerateOptions = (JsonObject)recipe.GenerateOptions.DeepClone() };
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool SameHash(string first, string second) => ValidHash(first) && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static string RequiredString(JsonObject document, string name) => document[name]?.GetValue<string>() is { Length: > 0 } value ? value : throw new InvalidDataException("Campo ausente no comprovante: " + name);
    private static void RequireInside(string path, string root)
    {
        string full = Path.GetFullPath(path), prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Um caminho da conversão saiu de sua pasta privada.");
        NoLinks(full);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A fila não pode escrever através de atalhos ou redirecionamentos de pastas. Escolha uma pasta local direta.");
    }
    private static IEnumerable<string> SafeFiles(string directory)
    {
        NoLinks(directory);
        foreach (string file in Directory.EnumerateFiles(directory)) { NoLinks(file); yield return file; }
        foreach (string child in Directory.EnumerateDirectories(directory)) foreach (string file in SafeFiles(child)) yield return file;
    }
    private static async Task<byte[]> ReadBounded(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("O registro da fila ultrapassa o limite seguro.");
        using var memory = new MemoryStream(); await stream.CopyToAsync(memory, token).ConfigureAwait(false); return memory.ToArray();
    }
    private static async Task<JsonObject> ReadObject(string path, CancellationToken token) => JsonNode.Parse(await ReadBounded(path, token).ConfigureAwait(false))?.AsObject() ?? throw new InvalidDataException("Registro JSON vazio.");
    private static async Task<string> Hash(string path, CancellationToken token) { NoLinks(path); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)); }
    private static async Task CopyNew(string source, string destination, CancellationToken token)
    {
        NoLinks(destination);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(flushToDisk: true);
    }
    private static async Task WriteNew(string path, byte[] bytes, CancellationToken token)
    { await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(flushToDisk: true); }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    private static void RemoveEmptyDirectories(string path)
    {
        try { foreach (string child in Directory.EnumerateDirectories(path)) RemoveEmptyDirectories(child); Directory.Delete(path, recursive: false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private Operation Enter(CancellationToken token)
    { lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); operations++; return new(this, CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token)); } }
    private void Leave() { lock (sync) { operations--; if (disposed && operations == 0) lease.Dispose(); } }
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; lifetime.Cancel(); if (operations == 0) lease.Dispose(); } }
    private sealed class Operation(ConversionQueueService owner, CancellationTokenSource source) : IDisposable
    { public CancellationToken Token => source.Token; public void Dispose() { source.Dispose(); owner.Leave(); } }
    private sealed class StageProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
    private sealed class ProcessWorker : IConversionQueueWorker
    { public Task<JsonObject> RunAsync(ConversionQueueRecipe recipe, JsonObject request, string workspace, IProgress<string>? progress, CancellationToken token) => new EngineClient(recipe.PythonPath, recipe.EngineDirectory).RunAsync(request, workspace, progress, token); }
    private sealed record Envelope(int SchemaVersion, string Payload, string Sha256);
    private sealed record Artifact(string Path, string Sha256, string RelativePath);
    private sealed record Manifest(JsonObject Result, IReadOnlyList<Artifact> Files);
    private sealed class Publication
    {
        public int SchemaVersion { get; set; }
        public string JobId { get; set; } = "";
        public string SourceSha256 { get; set; } = "";
        public string RecipeSha256 { get; set; } = "";
        public JsonObject Result { get; set; } = new();
        public Artifact[] Files { get; set; } = [];
    }
    private sealed class QueueState { public Job[] Jobs { get; set; } = []; }
    private sealed class Job
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public ConversionQueueRecipe Recipe { get; set; } = null!;
        public string SourceSha256 { get; set; } = "";
        public string RecipeSha256 { get; set; } = "";
        public string? AnalysisSha256 { get; set; }
        public ConversionQueueStatus Status { get; set; }
        public int Attempts { get; set; }
        public string Stage { get; set; } = "";
        public string? Error { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public ConversionQueueResult? Result { get; set; }
    }
}
