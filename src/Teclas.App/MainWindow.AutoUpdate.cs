using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Automatic update notice: at most once a day, a quiet check of the latest GitHub release. A newer version shows a
/// button in the header; installing downloads the official Setup, checks its SHA-256, starts it and closes the app.
/// Nothing is downloaded or installed without the user's click.
/// </summary>
public partial class MainWindow
{
    private ReleaseUpdate? availableUpdate;
    internal Func<CancellationToken, Task<ReleaseUpdate?>>? updateCheck = null;
    internal Func<ReleaseUpdate, IProgress<double>, CancellationToken, Task<string>>? updateDownload = null;
    internal Func<string, bool>? updateConfirm = null;
    internal Action<string>? updateLaunch = null;
    internal static Version InstalledVersion => Assembly.GetExecutingAssembly().GetName().Version is { } v ? new(v.Major, v.Minor, Math.Max(0, v.Build)) : new(0, 0, 0);

    public bool AutoCheckUpdates
    {
        get => settings.AutoCheckUpdates;
        set { if (settings.AutoCheckUpdates == value) return; settings.AutoCheckUpdates = value; Notify(); PersistPreferences(); }
    }
    public Visibility UpdateButtonVisibility => availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
    public string UpdateButtonText => availableUpdate is null ? "" : $"⬆ Atualizar para {availableUpdate.Version}";
    public ReleaseUpdate? AvailableUpdate => availableUpdate;

    private bool updateSmoke;

    /// <summary>Automated test windows never reach GitHub: they check only when a test supplies the answer.</summary>
    private void InitializeAutoUpdate(bool smoke) { updateSmoke = smoke; Loaded += (_, _) =>
        Dispatcher.InvokeAsync(async () => await CheckForUpdates(force: false), DispatcherPriority.ApplicationIdle); }

    /// <summary>Checks quietly (failures only leave a note in the status); "force" ignores the daily limit.</summary>
    internal async Task CheckForUpdates(bool force)
    {
        if (!force && (!settings.AutoCheckUpdates || (updateSmoke || !persistenceEnabled) && updateCheck is null)) return;
        if (!force && settings.LastUpdateCheck is { } last && DateTime.Now - last < TimeSpan.FromHours(20)) { ShowUpdate(KnownUpdate()); return; }
        try
        {
            ReleaseUpdate? release;
            if (updateCheck is { } check) release = await check(CancellationToken.None);
            else
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                release = await new ReleaseUpdateService(client).CheckAsync();
            }
            settings.LastUpdateCheck = DateTime.Now;
            var newer = release is not null && release.Version > InstalledVersion ? release : null;
            settings.KnownUpdateTag = newer?.Tag ?? ""; settings.KnownUpdateSetup = newer?.SetupName ?? ""; settings.KnownUpdateSha256 = newer?.SetupSha256 ?? "";
            PersistPreferences();
            ShowUpdate(newer);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        { /* offline or GitHub unavailable: try again next time, without bothering the user */ }
    }

    /// <summary>The newer release remembered from the last check (so the button survives a restart without asking GitHub again).</summary>
    private ReleaseUpdate? KnownUpdate() =>
        settings.KnownUpdateTag.Length > 1 && Version.TryParse(settings.KnownUpdateTag.TrimStart('v'), out var version)
            ? new ReleaseUpdate(version, settings.KnownUpdateTag, ReleaseUpdateService.ReleasesUrl + "/tag/" + settings.KnownUpdateTag, null, null)
                { SetupName = settings.KnownUpdateSetup.Length > 0 ? settings.KnownUpdateSetup : null, SetupSha256 = settings.KnownUpdateSha256.Length == 64 ? settings.KnownUpdateSha256 : null }
            : null;

    private void ShowUpdate(ReleaseUpdate? release)
    {
        availableUpdate = release is not null && release.Version > InstalledVersion && release.SetupName is not null ? release : null;
        Notify(nameof(UpdateButtonVisibility)); Notify(nameof(UpdateButtonText)); Notify(nameof(AvailableUpdate));
        if (availableUpdate is not null) Status = $"TeclaScore {availableUpdate.Version} está disponível. Use o botão Atualizar no topo quando quiser.";
    }

    private async void InstallUpdateClick(object sender, RoutedEventArgs e) => await InstallUpdate();

    internal async Task InstallUpdate()
    {
        if (availableUpdate is not { } release || !IsIdle) return;
        string question = $"Baixar e instalar o TeclaScore {release.Version}?\n\nO instalador oficial é baixado do GitHub e conferido pelo SHA-256 publicado. " +
            "Depois o TeclaScore fecha (sua sessão é salva) e o instalador abre. Biblioteca, partituras e configurações são mantidas.";
        bool confirmed = updateConfirm is { } confirm ? confirm(question)
            : MessageBox.Show(this, question, "Atualizar o TeclaScore", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        if (!confirmed) return;
        string? setup = null;
        await RunOperation(async () =>
        {
            var id = operationId;
            var progress = new Progress<double>(fraction => { if (operationId == id && IsBusy) Status = $"Baixando o TeclaScore {release.Version}: {fraction:P0}"; });
            string folder = Path.Combine(PlaybackCacheDirectory, "atualizacao");
            if (updateDownload is { } download) setup = await download(release, progress, cancellation!.Token);
            else
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                setup = await new ReleaseUpdateService(client).DownloadSetupAsync(release, folder, progress, cancellation!.Token);
            }
            Status = "Instalador conferido. Abrindo…";
        }, operationTitle: $"Baixando o TeclaScore {release.Version}");
        if (setup is null || !File.Exists(setup)) return;
        if (updateLaunch is { } launch) { launch(setup); return; }
        try { Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Status = "Não foi possível abrir o instalador: " + error.Message + " Ele está em " + setup; return; }
        Close();
    }
}
