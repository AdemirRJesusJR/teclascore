using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    /// <summary>Answers fixed URLs; records every request. No network is used.</summary>
    private sealed class FixedHttpHandler(Dictionary<string, byte[]> answers) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString(); Requests.Add(url);
            return Task.FromResult(answers.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // Automatic update notice and verified installer download. No GitHub access, nothing installed.
    private async void VerifyV033Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v033-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.33 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException(what);
            Flush();
        }
        try
        {
            // Release parsing: the official installer and its digest.
            byte[] installer = Encoding.ASCII.GetBytes("instalador de teste");
            string hash = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant();
            string Release(string tag, bool digest) => $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":false,"assets":[{"name":"TeclaScore-{{tag.TrimStart('v')}}-Setup-x64.exe"{{(digest ? $",\"digest\":\"sha256:{hash}\"" : "")}}},{"name":"TeclaScore-{{tag.TrimStart('v')}}-Windows.zip"}]}""";
            var parsed = ReleaseUpdateService.ParseRelease(JsonDocument.Parse(Release("v9.9.9", true)).RootElement);
            Check(parsed is { SetupName: "TeclaScore-9.9.9-Setup-x64.exe" } && parsed.SetupSha256 == hash, "release-names-the-installer-and-digest");

            // Download: fixed GitHub address, hash checked, mismatches discarded, SHA256SUMS used without a digest.
            string baseUrl = ReleaseUpdateService.ReleasesUrl + "/download/v9.9.9/";
            var handler = new FixedHttpHandler(new() { [baseUrl + "TeclaScore-9.9.9-Setup-x64.exe"] = installer,
                [baseUrl + "SHA256SUMS"] = Encoding.ASCII.GetBytes($"{hash}  TeclaScore-9.9.9-Setup-x64.exe\n") });
            var service = new ReleaseUpdateService(new HttpClient(handler));
            string saved = await service.DownloadSetupAsync(parsed, Path.Combine(work, "a"), null);
            Check(File.ReadAllBytes(saved).SequenceEqual(installer) && handler.Requests.All(url => url.StartsWith(ReleaseUpdateService.ReleasesUrl, StringComparison.Ordinal)), "installer-downloads-from-the-release-and-matches");
            bool rejected = false;
            try { await service.DownloadSetupAsync(parsed with { SetupSha256 = new string('0', 64) }, Path.Combine(work, "b"), null); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && !Directory.EnumerateFiles(Path.Combine(work, "b")).Any(), "wrong-hash-is-discarded");
            var noDigest = ReleaseUpdateService.ParseRelease(JsonDocument.Parse(Release("v9.9.9", false)).RootElement);
            string fromSums = await service.DownloadSetupAsync(noDigest, Path.Combine(work, "c"), null);
            Check(File.Exists(fromSums) && handler.Requests.Contains(baseUrl + "SHA256SUMS"), "sha256sums-is-used-without-digest");

            // App: a daily check shows the header button; it survives a restart without asking again.
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida") }.Save(settingsPath);
            int checksMade = 0;
            MainWindow Open()
            {
                var opened = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
                typeof(MainWindow).GetField("updateCheck", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(opened,
                    new Func<CancellationToken, Task<ReleaseUpdate?>>(_ => { checksMade++; return Task.FromResult<ReleaseUpdate?>(parsed); }));
                opened.Show(); Flush(); return opened;
            }
            window = Open();
            await Wait(() => window.UpdateButtonVisibility == Visibility.Visible, "update button did not appear");
            Check(checksMade == 1 && window.UpdateButtonText.Contains("9.9.9") && Settings.Load(settingsPath).LastUpdateCheck is not null, "new-version-shows-the-button");
            CaptureFolderWindow(window, Path.Combine(reports, "update-button.png"));
            window.Close(); window = null;
            window = Open();
            await Wait(() => window.UpdateButtonVisibility == Visibility.Visible, "remembered update not shown");
            Check(checksMade == 1, "remembered-update-does-not-ask-again");

            // Install: confirm, verified download, then the installer starts.
            string? launched = null;
            typeof(MainWindow).GetField("updateConfirm", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new Func<string, bool>(_ => true));
            typeof(MainWindow).GetField("updateDownload", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window,
                new Func<ReleaseUpdate, IProgress<double>, CancellationToken, Task<string>>((release, _, token) => service.DownloadSetupAsync(release, Path.Combine(work, "d"), null, token)));
            typeof(MainWindow).GetField("updateLaunch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new Action<string>(path => launched = path));
            await window.InstallUpdate();
            Check(launched is not null && File.ReadAllBytes(launched).SequenceEqual(installer), "install-downloads-checks-and-launches");

            // Turning the option off stops the daily check.
            window.AutoCheckUpdates = false; window.Close(); window = null;
            var stale = Settings.Load(settingsPath); stale.LastUpdateCheck = DateTime.Now.AddDays(-3); stale.KnownUpdateTag = ""; stale.Save(settingsPath);
            window = Open(); await Task.Delay(300); Flush();
            Check(checksMade == 1 && window.UpdateButtonVisibility == Visibility.Collapsed, "option-off-means-no-check");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
