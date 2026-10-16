using System.Collections.Concurrent;

namespace Teclas.Core;

/// <summary>Prepares a private editable score and only publishes a validated, explicitly accepted copy.</summary>
public sealed class NativeScoreArrangementService(string museScorePath, string cacheDirectory)
{
    private static readonly ConcurrentDictionary<string, PreparedStamp> Prepared = new(StringComparer.OrdinalIgnoreCase);

    public async Task<NativeScoreResult> PrepareAsync(ArrangementPlan plan,
        IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string planSignature = NativeScoreArrangement.PlanStamp(plan);
        string job = Path.Combine(Path.GetFullPath(cacheDirectory), "arranjo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        string draft = Path.Combine(job, ".arranjo.mscz"), rendered = Path.Combine(job, "previa-arranjo.mscz");
        IReadOnlyList<string> pages = [];
        bool completed = false;
        try
        {
            progress?.Report("Conferindo a versão aberta e aplicando somente as alterações escolhidas…");
            await Task.Run(() => NativeScoreArrangement.WriteDraft(plan, draft, token), token);
            var expected = await Task.Run(() => NativeScoreArchive.Read(draft), token);
            string expectedFingerprint = await Task.Run(expected.MusicalFingerprint, token);
            token.ThrowIfCancellationRequested();
            var exporter = new ScoreExporter(museScorePath, cacheDirectory);
            progress?.Report("Preparando a prévia musical no MuseScore…");
            await exporter.ExportNativeAsync(draft, rendered, token);
            progress?.Report("Conferindo notas, vozes, cifras, ligações e diagramas da adaptação…");
            var final = await Task.Run(() => NativeScoreArchive.Read(rendered), token);
            string actualFingerprint = await Task.Run(final.MusicalFingerprint, token);
            if (actualFingerprint != expectedFingerprint)
            {
                // Automatic glyphs may change on an adapted note and its dependent diatonic
                // position later in this measure. Pitch, spelling and every other property
                // remain strict; unrelated, manual and special accidentals are never ignored.
                string normalizedExpected = await Task.Run(() => NativeScoreArrangement.NormalizedFingerprint(draft, plan, token), token);
                string normalizedActual = await Task.Run(() => NativeScoreArrangement.NormalizedFingerprint(rendered, plan, token), token);
                if (normalizedExpected != normalizedActual)
                    throw new InvalidDataException("O MuseScore modificou conteúdo além das alterações escolhidas. A adaptação não foi salva; abra este trecho no MuseScore para conferir sua estrutura.");
            }
            if (!SameMetadata(expected.Metadata, final.Metadata))
                throw new InvalidDataException("O MuseScore não preservou o cabeçalho ou o inventário musical da adaptação. A cópia não foi publicada.");
            progress?.Report("Gerando todas as páginas da prévia adaptada…");
            pages = await exporter.ExportPreviewAsync(rendered, Path.Combine(job, "pagina.png"), token);
            progress?.Report("Conferindo a origem e preparando a adaptação para sua aprovação…");
            await Task.Run(() => NativeScoreArrangement.CheckSource(plan, token), token);
            string scoreHash = await Task.Run(() => NativeMusicEditor.HashFile(rendered, token), token);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string page in pages) hashes.Add(page, await Task.Run(() => NativeMusicEditor.HashFile(page, token), token));
            token.ThrowIfCancellationRequested();
            var result = new NativeScoreResult(rendered, pages.ToArray(), final.Metadata, actualFingerprint);
            if (NativeScoreArrangement.PlanStamp(plan) != planSignature)
                throw new InvalidOperationException("O plano foi alterado durante a preparação. Analise novamente o arranjo.");
            Prepared[rendered] = new(plan.SourcePath, plan.SourceSha256, scoreHash, result, hashes, planSignature);
            completed = true;
            return result;
        }
        finally
        {
            TryDelete(draft);
            if (!completed)
            {
                TryDelete(rendered);
                foreach (string page in pages) TryDelete(page);
                // A failed renderer may have emitted partial pages before returning its error.
                foreach (string page in Directory.EnumerateFiles(job, "pagina*.png")) TryDelete(page);
                TryDeleteDirectory(job);
            }
        }
    }

    public async Task<NativeScoreResult> PublishAsync(ArrangementPlan plan, NativeScoreResult prepared,
        string outputDirectory, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Prepared.TryGetValue(Path.GetFullPath(prepared.Path), out var stamp) ||
            !string.Equals(stamp.SourcePath, plan.SourcePath, StringComparison.OrdinalIgnoreCase) || stamp.SourceSha256 != plan.SourceSha256 ||
            stamp.PlanSignature != NativeScoreArrangement.PlanStamp(plan) || stamp.Result.MusicalFingerprint != prepared.MusicalFingerprint || !SameMetadata(stamp.Result.Metadata, prepared.Metadata) ||
            !stamp.Result.PreviewPages.SequenceEqual(prepared.PreviewPages, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Prepare uma nova prévia desta versão antes de salvar a adaptação.");
        if (!Path.IsPathFullyQualified(outputDirectory)) throw new ArgumentException("Escolha uma pasta de saída com caminho completo.", nameof(outputDirectory));
        await stamp.Gate.WaitAsync(token);
        string? pendingDirectory = null;
        var pendingFiles = new List<string>();
        bool published = false;
        try
        {
            if (stamp.Published) throw new InvalidOperationException("Esta prévia já foi salva. Reabra a nova versão para continuar editando.");
            progress?.Report("Conferindo se a prévia aprovada e a partitura original continuam intactas…");
            await CheckPrepared(plan, stamp, token);
            Directory.CreateDirectory(outputDirectory);
            string id = Guid.NewGuid().ToString("N");
            pendingDirectory = Path.Combine(outputDirectory, ".teclas-arranjo-" + id);
            if (Directory.Exists(pendingDirectory)) throw new IOException("O destino temporário já existe. Tente salvar novamente.");
            Directory.CreateDirectory(pendingDirectory);
            string basename = ScoreFileName.Stem(stamp.Result.Metadata.Artist, stamp.Result.Metadata.Title, plan.SourcePath);
            string scoreCopy = Path.Combine(pendingDirectory, basename + ".mscz");
            pendingFiles.Add(scoreCopy);
            progress?.Report("Salvando uma nova versão com todas as páginas…");
            await CopyNew(stamp.Result.Path, scoreCopy, token);
            var pageCopies = new List<string>();
            for (int index = 0; index < stamp.Result.PreviewPages.Count; index++)
            {
                string pageCopy = Path.Combine(pendingDirectory, $"pagina-{index + 1}.png");
                pendingFiles.Add(pageCopy); pageCopies.Add(pageCopy);
                await CopyNew(stamp.Result.PreviewPages[index], pageCopy, token);
            }
            if (await Task.Run(() => NativeMusicEditor.HashFile(scoreCopy, token), token) != stamp.ScoreSha256)
                throw new IOException("A cópia não corresponde à prévia aprovada. A publicação foi interrompida.");
            for (int index = 0; index < pageCopies.Count; index++)
                if (await Task.Run(() => NativeMusicEditor.HashFile(pageCopies[index], token), token) != stamp.PageHashes[stamp.Result.PreviewPages[index]])
                    throw new IOException("Uma página da cópia não corresponde à prévia aprovada.");
            await CheckPrepared(plan, stamp, token);
            string finalDirectory = Path.Combine(outputDirectory, basename + " - " + id[..10]);
            token.ThrowIfCancellationRequested();
            // All files and hashes are settled before this single same-volume publication step.
            // A cancellation arriving after the move cannot turn a saved version into a failure.
            Directory.Move(pendingDirectory, finalDirectory);
            published = true; stamp.Published = true;
            return new(Path.Combine(finalDirectory, Path.GetFileName(scoreCopy)),
                pageCopies.Select(path => Path.Combine(finalDirectory, Path.GetFileName(path))).ToArray(),
                stamp.Result.Metadata, stamp.Result.MusicalFingerprint);
        }
        finally
        {
            if (!published && pendingDirectory is not null)
            {
                foreach (string path in pendingFiles) TryDelete(path);
                TryDeleteDirectory(pendingDirectory);
            }
            stamp.Gate.Release();
        }
    }

    private static async Task CheckPrepared(ArrangementPlan plan, PreparedStamp stamp, CancellationToken token)
    {
        await Task.Run(() => NativeScoreArrangement.CheckSource(plan, token), token);
        if (await Task.Run(() => NativeMusicEditor.HashFile(stamp.Result.Path, token), token) != stamp.ScoreSha256 ||
            await Task.Run(() => NativeScoreDocument.MusicalFingerprint(stamp.Result.Path), token) != stamp.Result.MusicalFingerprint)
            throw new IOException("A prévia foi alterada desde a conferência. Prepare-a novamente antes de salvar.");
        foreach (var page in stamp.PageHashes)
            if (await Task.Run(() => NativeMusicEditor.HashFile(page.Key, token), token) != page.Value)
                throw new IOException("Uma página da prévia mudou. Prepare-a novamente antes de salvar.");
        token.ThrowIfCancellationRequested();
    }

    private static async Task CopyNew(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token);
        await output.FlushAsync(token);
    }
    private static bool SameMetadata(NativeScoreMetadata first, NativeScoreMetadata second) =>
        first.Title == second.Title && first.Subtitle == second.Subtitle && first.Composer == second.Composer && first.Artist == second.Artist &&
        first.ArrangementSummary == second.ArrangementSummary &&
        first.StaffCount == second.StaffCount && first.MeasureCount == second.MeasureCount && first.NoteCount == second.NoteCount &&
        first.HarmonyCount == second.HarmonyCount && first.ImageCount == second.ImageCount;
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: false); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    private sealed class PreparedStamp(string sourcePath, string sourceSha256, string scoreSha256, NativeScoreResult result,
        IReadOnlyDictionary<string, string> pageHashes, string planSignature)
    {
        internal string SourcePath { get; } = sourcePath;
        internal string PlanSignature { get; } = planSignature;
        internal string SourceSha256 { get; } = sourceSha256;
        internal string ScoreSha256 { get; } = scoreSha256;
        internal NativeScoreResult Result { get; } = result;
        internal IReadOnlyDictionary<string, string> PageHashes { get; } = pageHashes;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal bool Published { get; set; }
    }
}
