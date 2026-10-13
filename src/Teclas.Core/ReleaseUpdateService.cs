using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;

namespace Teclas.Core;

public sealed record ReleaseUpdate(Version Version, string Tag, string PageUrl, string? PackageName, string? PackageSha256);

/// <summary>On-demand release discovery. Never installs or changes settings.</summary>
public sealed partial class ReleaseUpdateService(HttpClient client)
{
    public const string ReleasesUrl = "https://github.com/AdemirRJesusJR/teclascore/releases";
    private const string LatestApi = "https://api.github.com/repos/AdemirRJesusJR/teclascore/releases/latest";

    public async Task<ReleaseUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.UserAgent.ParseAdd("TeclaScore-update-check");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseRelease(json.RootElement);
    }

    public static ReleaseUpdate ParseRelease(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!VersionPattern().IsMatch(tag) || !Version.TryParse(tag.TrimStart('v'), out var version)
            || release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("A publicação não é uma versão estável reconhecida do TeclaScore.");
        var packageName = $"TeclaScore-{version}-Windows.zip";
        string? checksum = null;
        var found = false;
        if (release.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
                if (asset.GetProperty("name").GetString() == packageName)
                {
                    found = true;
                    if (asset.TryGetProperty("digest", out var digest))
                    {
                        var value = digest.GetString();
                        if (value is not null && value.StartsWith("sha256:", StringComparison.Ordinal)
                            && HashPattern().IsMatch(value[7..])) checksum = value[7..].ToLowerInvariant();
                    }
                }
        // Never trust an arbitrary URL from a downloaded document.
        return new(version, tag, ReleasesUrl + "/tag/" + tag, found ? packageName : null, checksum);
    }

    public static async Task VerifyPackageAsync(string packagePath, string expectedSha256, CancellationToken cancellationToken = default)
    {
        if (!HashPattern().IsMatch(expectedSha256)) throw new InvalidDataException("O SHA-256 deve ter 64 caracteres hexadecimais.");
        await using var stream = File.OpenRead(packagePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O pacote não corresponde ao SHA-256 publicado. Baixe-o novamente na página da versão.");
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            total = checked(total + entry.Length);
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is ".." or ".")
                || !names.Add(name) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("O pacote contém caminhos inválidos ou entradas repetidas.");
            if (entry.Length > 512L * 1024 * 1024 || total > 1024L * 1024 * 1024 || zip.Entries.Count > 10000)
                throw new InvalidDataException("O pacote excede os limites de tamanho do TeclaScore.");
        }
        foreach (var required in new[] { "Teclas.Partituras.exe", "Teclas.Partituras.dll", "Teclas.Core.dll", "Install.ps1", "Instalar.cmd", "installation-manifest.json" })
            if (!names.Contains(required)) throw new InvalidDataException($"Pacote incompleto: falta {required}.");
    }

    public static string ReadChecksum(string checksumPath, string packageName)
    {
        if (new FileInfo(checksumPath).Length > 1024 * 1024) throw new InvalidDataException("Arquivo de checksums muito grande.");
        var matches = new List<string>();
        foreach (var line in File.ReadLines(checksumPath))
        {
            var match = ChecksumLinePattern().Match(line);
            if (match.Success && match.Groups[2].Value.Equals(packageName, StringComparison.Ordinal)) matches.Add(match.Groups[1].Value);
        }
        if (matches.Count != 1) throw new InvalidDataException("O arquivo SHA256SUMS deve conter uma única entrada para este ZIP.");
        return matches[0];
    }

    [GeneratedRegex(@"^v?\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?([^\r\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLinePattern();
}
