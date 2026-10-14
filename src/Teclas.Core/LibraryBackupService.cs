using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Teclas.Core;

public sealed record LibraryBackupSource(string RelativePath, string SourcePath);
public sealed record LibraryBackupEntry(string RelativePath, long Length, string Sha256);
public sealed record LibraryBackupManifest(int FormatVersion, DateTimeOffset CreatedAt, string LibraryJson,
    IReadOnlyList<LibraryBackupEntry> Files, IReadOnlyList<string> MissingFiles);
public sealed record LibraryBackupInspection(string PackagePath, string PackageSha256, LibraryBackupManifest Manifest);
public sealed record LibraryBackupRestore(string Directory, LibraryBackupManifest Manifest, bool Reused);

/// <summary>A portable library archive. Only listed musical assets are copied; no executable is restored.</summary>
public static class LibraryBackupService
{
    private const long MaxFile = 512L * 1024 * 1024, MaxTotal = 20L * 1024 * 1024 * 1024;
    private const int MaxEntries = 50000, MaxManifest = 16 * 1024 * 1024;
    private const string ManifestName = "teclascore-library.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mid", ".midi", ".kar", ".mscz", ".png", ".jpg", ".jpeg", ".svg" };

    public static async Task CreateAsync(string destination, string libraryJson,
        IReadOnlyList<LibraryBackupSource> sources, IReadOnlyList<string> missing,
        IProgress<string>? progress, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(destination) || File.Exists(destination))
            throw new IOException("Escolha um novo arquivo de backup, preservando os anteriores.");
        using (JsonDocument.Parse(libraryJson)) { }
        if (sources.Count > MaxEntries) throw new InvalidDataException("O backup excede o limite de arquivos.");
        if (sources.Select(s => s.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
            throw new InvalidDataException("O backup contém caminhos repetidos.");
        foreach (var source in sources) ValidateRelative(source.RelativePath);
        var target = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string pending = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var entries = new List<LibraryBackupEntry>(); long total = 0;
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                for (int index = 0; index < sources.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var source = sources[index];
                    progress?.Report($"Copiando arquivo {index + 1} de {sources.Count}: {Path.GetFileName(source.SourcePath)}");
                    await using var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    if (input.Length > MaxFile || (total += input.Length) > MaxTotal)
                        throw new InvalidDataException("O backup excede o tamanho suportado.");
                    long length = input.Length;
                    await using var output = archive.CreateEntry(source.RelativePath, CompressionLevel.Optimal).Open();
                    var hash = await CopyAndHash(input, output, length, token);
                    entries.Add(new(source.RelativePath, length, hash));
                }
                var manifest = new LibraryBackupManifest(1, DateTimeOffset.UtcNow, libraryJson, entries, missing.ToArray());
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
                if (bytes.Length > MaxManifest) throw new InvalidDataException("As informações da biblioteca excedem o limite do backup.");
                await using var manifestStream = archive.CreateEntry(ManifestName).Open();
                await manifestStream.WriteAsync(bytes, token);
            }
            progress?.Report("Conferindo a integridade do backup completo…");
            await InspectAsync(pending, progress, token);
            token.ThrowIfCancellationRequested();
            File.Move(pending, target, overwrite: false);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    public static async Task<LibraryBackupInspection> InspectAsync(string package, IProgress<string>? progress, CancellationToken token)
    {
        string fullPath = Path.GetFullPath(package);
        await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        string packageHash = Convert.ToHexString(await SHA256.HashDataAsync(file, token)); file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = await ReadManifest(archive, token);
        long total = 0;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
        for (int index = 0; index < manifest.Files.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = manifest.Files[index]; ValidateRelative(entry.RelativePath);
            if (!listed.Add(entry.RelativePath) || entry.Length < 0 || entry.Length > MaxFile ||
                (total += entry.Length) > MaxTotal || entry.Sha256.Length != 64 || !entry.Sha256.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("O inventário do backup é inválido.");
            var zipped = archive.GetEntry(entry.RelativePath) ?? throw new InvalidDataException("Um arquivo listado está ausente do backup.");
            if (zipped.Length != entry.Length) throw new InvalidDataException("O tamanho de um arquivo do backup mudou.");
            progress?.Report($"Conferindo arquivo {index + 1} de {manifest.Files.Count}…");
            await using var input = zipped.Open();
            if (!string.Equals(await CopyAndHash(input, Stream.Null, entry.Length, token), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Um arquivo do backup não passou na conferência de integridade.");
        }
        if (archive.Entries.Count != listed.Count || archive.Entries.Any(e => !listed.Contains(e.FullName)) ||
            archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
            throw new InvalidDataException("O backup contém arquivos extras ou caminhos duplicados.");
        return new(fullPath, packageHash, manifest);
    }

    public static async Task<LibraryBackupRestore> RestoreAsync(LibraryBackupInspection inspection, string destinationRoot,
        IProgress<string>? progress, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(destinationRoot)) throw new ArgumentException("Escolha uma pasta completa para a restauração.");
        // Inspect again: neither a stale dialog nor a caller-modified manifest may redirect extraction.
        var current = await InspectAsync(inspection.PackagePath, progress, token);
        if (current.PackageSha256 != inspection.PackageSha256 ||
            JsonSerializer.Serialize(current.Manifest, Json) != JsonSerializer.Serialize(inspection.Manifest, Json))
            throw new IOException("O backup mudou desde a conferência. Abra-o novamente.");
        string root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        string final = Path.Combine(root, "Biblioteca-" + current.PackageSha256[..16].ToLowerInvariant());
        string receipt = Path.Combine(final, ".teclascore-backup.json");
        if (Directory.Exists(final))
        {
            if (!File.Exists(receipt) || await File.ReadAllTextAsync(receipt, token) != current.PackageSha256)
                throw new IOException("A pasta de destino já existe com outro conteúdo. Escolha outra pasta.");
            foreach (var item in current.Manifest.Files)
            {
                string target = SafePath(final, item.RelativePath);
                await using var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                if (input.Length != item.Length || Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != item.Sha256.ToUpperInvariant())
                    throw new IOException("Uma restauração anterior foi modificada. Escolha outra pasta para preservar suas edições.");
            }
            return new(final, current.Manifest, true);
        }
        string pending = Path.Combine(root, ".teclascore-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pending); bool published = false;
        try
        {
            await using var file = new FileStream(current.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != current.PackageSha256)
                throw new IOException("O backup mudou antes da restauração.");
            file.Position = 0;
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            for (int index = 0; index < current.Manifest.Files.Count; index++)
            {
                var entry = current.Manifest.Files[index]; token.ThrowIfCancellationRequested();
                progress?.Report($"Restaurando arquivo {index + 1} de {current.Manifest.Files.Count}…");
                string target = SafePath(pending, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = archive.GetEntry(entry.RelativePath)!.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                if (await CopyAndHash(input, output, entry.Length, token) != entry.Sha256.ToUpperInvariant())
                    throw new InvalidDataException("O arquivo extraído não corresponde ao backup conferido.");
            }
            await File.WriteAllTextAsync(Path.Combine(pending, ".teclascore-backup.json"), current.PackageSha256, token);
            token.ThrowIfCancellationRequested();
            Directory.Move(pending, final); published = true;
            return new(final, current.Manifest, false);
        }
        finally
        {
            if (!published && Directory.Exists(pending))
            {
                // Only this newly-created temporary directory may be removed.
                if (!Path.GetFullPath(pending).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(pending).StartsWith(".teclascore-restore-", StringComparison.Ordinal))
                    throw new IOException("Destino temporário inesperado.");
                Directory.Delete(pending, recursive: true);
            }
        }
    }

    public static string SafePath(string root, string relative)
    {
        ValidateRelative(relative);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Caminho fora da biblioteca.");
        return result;
    }
    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || !path.StartsWith("files/", StringComparison.Ordinal) || path.Contains('\\') ||
            path.Split('/').Any(part => string.IsNullOrEmpty(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                System.Text.RegularExpressions.Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) ||
            !Extensions.Contains(Path.GetExtension(path))) throw new InvalidDataException("Caminho ou tipo de arquivo não permitido no backup.");
    }
    private static async Task<LibraryBackupManifest> ReadManifest(ZipArchive archive, CancellationToken token)
    {
        if (archive.Entries.Count is < 1 or > MaxEntries + 1) throw new InvalidDataException("Quantidade de arquivos inválida no backup.");
        var entry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("Este arquivo não é um backup de biblioteca TeclaScore.");
        if (entry.Length > MaxManifest) throw new InvalidDataException("Manifesto do backup muito grande.");
        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        await CopyAndHash(stream, buffer, entry.Length, token);
        var value = JsonSerializer.Deserialize<LibraryBackupManifest>(buffer.ToArray()) ?? throw new InvalidDataException("Manifesto vazio.");
        if (value.FormatVersion != 1 || value.Files is null || value.MissingFiles is null || value.LibraryJson is null || value.Files.Count > MaxEntries ||
            value.Files.Any(f => f is null || f.Sha256 is null) || value.MissingFiles.Count > MaxEntries)
            throw new InvalidDataException("Formato de backup não suportado.");
        using (JsonDocument.Parse(value.LibraryJson)) { }
        return value;
    }
    private static async Task<string> CopyAndHash(Stream input, Stream output, long expected, CancellationToken token)
    {
        byte[] buffer = new byte[81920]; long copied = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            int read = await input.ReadAsync(buffer, token); if (read == 0) break;
            copied += read; if (copied > expected || copied > MaxFile) throw new InvalidDataException("O tamanho do arquivo mudou durante a cópia.");
            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (copied != expected) throw new InvalidDataException("O arquivo terminou antes do tamanho informado.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
