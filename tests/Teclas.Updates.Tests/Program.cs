using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Teclas.Core;

var checks = 0;
void Check(bool success, string name) { if (!success) throw new Exception(name); checks++; }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); } catch (InvalidDataException) { checks++; return; }
    throw new Exception(name);
}
string Payload(string tag = "v0.7.0", bool prerelease = false, string hash = "") => JsonSerializer.Serialize(new
{
    tag_name = tag, draft = false, prerelease, html_url = "https://untrusted.invalid/release",
    assets = new[] { new { name = "TeclaScore-0.7.0-Windows.zip", digest = "sha256:" + hash } }
});
using var client = new HttpClient(new FakeHandler(HttpStatusCode.OK, Payload(hash: new string('a', 64))));
var release = await new ReleaseUpdateService(client).CheckAsync();
Check(release?.Version == new Version(0, 7, 0), "Stable release version");
Check(release!.PageUrl == ReleaseUpdateService.ReleasesUrl + "/tag/v0.7.0", "Untrusted release URL ignored");
Check(release.PackageSha256 == new string('a', 64), "GitHub asset digest");
using var unavailable = new HttpClient(new FakeHandler(HttpStatusCode.NotFound, ""));
Check(await new ReleaseUpdateService(unavailable).CheckAsync() is null, "Private repository handled");
foreach (var invalid in new[] { Payload("v0.7.0-beta"), Payload(prerelease: true), Payload("../anything") })
    await Reject(() => { using var json = JsonDocument.Parse(invalid); ReleaseUpdateService.ParseRelease(json.RootElement); return Task.CompletedTask; }, "Reject nonstable release");

var temp = Path.Combine(Path.GetTempPath(), "TeclaScoreUpdateTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var required = new[] { "Teclas.Partituras.exe", "Teclas.Partituras.dll", "Teclas.Core.dll", "Install.ps1", "Instalar.cmd", "installation-manifest.json" };
    string MakeZip(string file, string[] names)
    {
        var path = Path.Combine(temp, file);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in names) { using var stream = archive.CreateEntry(name).Open(); stream.Write(Encoding.UTF8.GetBytes("fixture")); }
        return path;
    }
    string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    var valid = MakeZip("TeclaScore-0.7.0-Windows.zip", required);
    await ReleaseUpdateService.VerifyPackageAsync(valid, Digest(valid)); checks++;
    await Reject(() => ReleaseUpdateService.VerifyPackageAsync(valid, new string('0', 64)), "Reject altered package");
    await Reject(() => ReleaseUpdateService.VerifyPackageAsync(valid, "invalid"), "Reject invalid digest");
    var traversal = MakeZip("traversal.zip", [.. required, "../outside.txt"]);
    await Reject(() => ReleaseUpdateService.VerifyPackageAsync(traversal, Digest(traversal)), "Reject Zip Slip");
    var duplicate = MakeZip("duplicate.zip", [.. required, "TECLAS.CORE.DLL"]);
    await Reject(() => ReleaseUpdateService.VerifyPackageAsync(duplicate, Digest(duplicate)), "Reject case insensitive duplicate");
    var incomplete = MakeZip("incomplete.zip", required[..3]);
    await Reject(() => ReleaseUpdateService.VerifyPackageAsync(incomplete, Digest(incomplete)), "Reject incomplete ZIP");
    var sums = Path.Combine(temp, "SHA256SUMS");
    File.WriteAllText(sums, Digest(valid) + "  " + Path.GetFileName(valid) + "\n");
    Check(ReleaseUpdateService.ReadChecksum(sums, Path.GetFileName(valid)) == Digest(valid), "Read exact release digest");
    File.AppendAllText(sums, new string('a', 64) + "  TeclaScore-0.7.0-Setup-x64.exe\n");
    Check(ReleaseUpdateService.ReadChecksum(sums, Path.GetFileName(valid)) == Digest(valid), "Installer checksum does not replace ZIP digest");
    await Reject(() => { ReleaseUpdateService.ReadChecksum(sums, "different.zip"); return Task.CompletedTask; }, "Checksum tied to exact ZIP name");
    File.AppendAllText(sums, File.ReadAllText(sums));
    await Reject(() => { ReleaseUpdateService.ReadChecksum(sums, Path.GetFileName(valid)); return Task.CompletedTask; }, "Reject ambiguous checksum file");
}
finally { Directory.Delete(temp, true); }
Console.WriteLine($"{checks} verificações de atualização aprovadas.");

sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "api.github.com" || !request.Headers.UserAgent.Any()) throw new Exception("Unexpected endpoint");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
