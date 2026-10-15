using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Teclas.Core;

public sealed record EditorDraftCheckpoint(string Id, string SourcePath, string SourceSha256, string SnapshotPath,
    string Title, DateTimeOffset UpdatedAt, string Payload);
public sealed record EditorDraftRecoveryIndex(IReadOnlyList<EditorDraftCheckpoint> Drafts, IReadOnlyList<string> Warnings);

/// <summary>Durable editor drafts with a private immutable base score and atomic JSON checkpoints.</summary>
public sealed class EditorDraftRecoveryStore
{
    private sealed record Stored(int Version, string Id, string SourcePath, string SourceSha256, string Title,
        DateTimeOffset UpdatedAt, string Payload, string PayloadSha256);
    private const int MaxPayload = 4 * 1024 * 1024;
    private readonly string root;
    public EditorDraftRecoveryStore(string directory) => root = Path.GetFullPath(directory);

    public EditorDraftCheckpoint Create(string sourcePath, string sourceSha256, string title, string payload)
    {
        ValidatePayload(payload);
        string source = Path.GetFullPath(sourcePath);
        if (!Path.GetExtension(source).Equals(".mscz", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A base do rascunho precisa ser uma partitura MuseScore.");
        string id = Guid.NewGuid().ToString("N"), directory = DirectoryFor(id);
        Directory.CreateDirectory(directory);
        string snapshot = Path.Combine(directory, "base.mscz");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                if (input.Length > 512L * 1024 * 1024) throw new InvalidDataException("A partitura excede o tamanho suportado pela recuperação.");
                input.CopyTo(output); output.Flush(true);
            }
            if (!HashFile(snapshot).Equals(sourceSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("A partitura mudou antes de proteger o rascunho. Reabra a versão atual.");
            return Save(new(id, source, sourceSha256.ToUpperInvariant(), snapshot, title, DateTimeOffset.UtcNow, payload), payload);
        }
        catch
        {
            // Only our new fixed-name files are eligible for cleanup; never follow caller-provided paths.
            if (File.Exists(snapshot)) File.Delete(snapshot);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            throw;
        }
    }

    public EditorDraftCheckpoint Save(EditorDraftCheckpoint checkpoint, string payload)
    {
        ValidatePayload(payload);
        string directory = DirectoryFor(checkpoint.Id), snapshot = Path.Combine(directory, "base.mscz");
        using var gate = LockSession(directory);
        if (!string.Equals(Path.GetFullPath(checkpoint.SnapshotPath), snapshot, StringComparison.OrdinalIgnoreCase) ||
            HashFile(snapshot) != checkpoint.SourceSha256.ToUpperInvariant())
            throw new InvalidDataException("A cópia de recuperação mudou. O rascunho anterior foi preservado.");
        EnsureCurrent(checkpoint, directory, allowAbsent: true);
        var result = checkpoint with { UpdatedAt = DateTimeOffset.UtcNow, Payload = payload };
        var stored = new Stored(1, result.Id, Path.GetFullPath(result.SourcePath), result.SourceSha256.ToUpperInvariant(),
            result.Title.Length > 256 ? result.Title[..256] : result.Title, result.UpdatedAt, payload, Hash(payload));
        string path = Path.Combine(directory, "draft.json"), temporary = Path.Combine(directory, "draft-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(output, stored); output.Flush(true); }
            if (File.Exists(path))
            {
                // A damaged latest checkpoint must never replace a usable backup.
                if (ReadStored(path, checkpoint.Id) is not null) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path, overwrite: true);
            }
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return result;
    }

    public EditorDraftRecoveryIndex List()
    {
        if (!Directory.Exists(root)) return new([], []);
        var drafts = new List<EditorDraftCheckpoint>(); var warnings = new List<string>();
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                var draft = Read(id);
                if (draft is not null) drafts.Add(draft);
                else if (File.Exists(Path.Combine(directory, "draft.json")) || File.Exists(Path.Combine(directory, "draft.json.bak")))
                    warnings.Add("Um rascunho não pôde ser conferido e permanece preservado para recuperação: " + id[..8]);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { warnings.Add("Um rascunho está indisponível e foi preservado: " + id[..8]); }
        }
        return new(drafts.OrderByDescending(d => d.UpdatedAt).ToArray(), warnings);
    }

    public EditorDraftCheckpoint? Read(string id)
    {
        string directory = DirectoryFor(id), path = Path.Combine(directory, "draft.json");
        var stored = ReadStored(path, id) ?? ReadStored(path + ".bak", id);
        if (stored is null) return null;
        string snapshot = Path.Combine(directory, "base.mscz");
        if (HashFile(snapshot) != stored.SourceSha256.ToUpperInvariant()) throw new InvalidDataException("A partitura base de recuperação não passou na conferência de integridade.");
        return new(id, stored.SourcePath, stored.SourceSha256.ToUpperInvariant(), snapshot, stored.Title, stored.UpdatedAt, stored.Payload);
    }

    public void Discard(string id)
    {
        string directory = DirectoryFor(id);
        using var gate = LockSession(directory);
        DiscardFiles(directory);
    }

    public void Discard(EditorDraftCheckpoint expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        string directory = DirectoryFor(expected.Id);
        using var gate = LockSession(directory);
        if (!Directory.Exists(directory)) return;
        EnsureCurrent(expected, directory, allowAbsent: false);
        DiscardFiles(directory);
    }

    private static void DiscardFiles(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string name in new[] { "draft.json", "draft.json.bak", "base.mscz" })
        { string path = Path.Combine(directory, name); if (File.Exists(path)) File.Delete(path); }
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static void EnsureCurrent(EditorDraftCheckpoint expected, string directory, bool allowAbsent)
    {
        string path = Path.Combine(directory, "draft.json");
        var current = ReadStored(path, expected.Id) ?? ReadStored(path + ".bak", expected.Id);
        if (current is null)
        {
            if (allowAbsent && !File.Exists(path) && !File.Exists(path + ".bak")) return;
            throw new IOException("O rascunho salvo não pôde ser conferido. A cópia existente foi preservada; reabra a recuperação antes de continuar.");
        }
        if (current.UpdatedAt != expected.UpdatedAt || current.Payload != expected.Payload ||
            !current.SourceSha256.Equals(expected.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !current.SourcePath.Equals(expected.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(expected.SnapshotPath).Equals(Path.Combine(directory, "base.mscz"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Outra sessão alterou este rascunho. A versão mais recente foi preservada; reabra a recuperação antes de continuar.");
    }

    private static IDisposable LockSession(string directory) => new SessionLock(directory);
    private sealed class SessionLock : IDisposable
    {
        private readonly Mutex mutex;
        private bool acquired;
        internal SessionLock(string directory)
        {
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
            mutex = new Mutex(false, "TeclaScore.EditorDraft." + key);
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Outra sessão está salvando este rascunho. Aguarde um instante e tente novamente.");
            }
            catch { mutex.Dispose(); throw; }
        }
        public void Dispose()
        {
            if (acquired) { acquired = false; mutex.ReleaseMutex(); }
            mutex.Dispose();
        }
    }

    private string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Identificação de recuperação inválida.");
        return Path.Combine(root, id);
    }
    private static Stored? ReadStored(string path, string id)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxPayload * 3L) return null;
            var data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (data is null || data.Version != 1 || data.Id != id || data.SourcePath is null || !Path.IsPathFullyQualified(data.SourcePath) ||
                data.SourceSha256 is null || data.SourceSha256.Length != 64 || !data.SourceSha256.All(char.IsAsciiHexDigit) ||
                data.Title is null || data.Payload is null || data.PayloadSha256 != Hash(data.Payload)) return null;
            ValidatePayload(data.Payload); return data;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
    }
    private static void ValidatePayload(string payload)
    {
        if (payload is null || Encoding.UTF8.GetByteCount(payload) > MaxPayload) throw new InvalidDataException("Rascunho de recuperação inválido ou muito grande.");
        using var json = JsonDocument.Parse(payload);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("O rascunho precisa conter o estado do editor.");
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string HashFile(string path)
    { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(file)); }
}
