using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>What the synced folder holds: who sent the library, when, and how many songs.</summary>
public sealed record LibrarySyncMarker(DateTimeOffset CreatedAt, string Machine, int Songs, string AppVersion);

/// <summary>
/// Library between computers: a portable backup kept in a folder the user syncs (OneDrive, Google Drive, pen drive).
/// Each computer sends its library when closing (only after changes) or on demand; at startup, a newer library sent by
/// another computer is offered, and its songs that are not here yet are added. Songs already here stay as they are.
/// </summary>
public partial class MainWindow
{
    public const string SyncPackageName = "TeclaScore-biblioteca.teclasbackup", SyncMarkerName = "TeclaScore-biblioteca.json";
    private bool syncingOnClose, syncCloseDone;
    internal Func<string, bool>? librarySyncOffer = null;
    internal string MachineName { get; set; } = Environment.MachineName;
    public bool CanSyncLibrary => IsIdle && settings.LibrarySyncFolder.Length > 0;

    /// <summary>Offers a newer library from another computer once the window is up and idle.</summary>
    private void InitializeLibrarySync() => Loaded += (_, _) =>
        Dispatcher.InvokeAsync(async () => { if (IsIdle) await OfferSyncedLibrary(); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

    public string LibrarySyncFolder
    {
        get => settings.LibrarySyncFolder;
        set
        {
            value = (value ?? "").Trim();
            if (settings.LibrarySyncFolder == value) return;
            settings.LibrarySyncFolder = value; settings.LibrarySyncSentHash = "";
            Notify(); Notify(nameof(LibrarySyncStatus)); Notify(nameof(CanSyncLibrary)); PersistPreferences();
        }
    }

    public string LibrarySyncStatus => settings.LibrarySyncFolder.Length == 0 ? "Desligado. Escolha uma pasta sincronizada para usar a mesma biblioteca em outro computador."
        : ReadSyncMarker(settings.LibrarySyncFolder) is { } marker
            ? $"Última biblioteca na pasta: {marker.Songs} músicas, enviada por {(marker.Machine == MachineName ? "este computador" : marker.Machine)} em {marker.CreatedAt.LocalDateTime:dd/MM/yyyy HH:mm}."
            : "Pasta escolhida. A biblioteca será enviada ao fechar o app ou em Sincronizar agora.";

    private void ChooseLibrarySyncFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pasta sincronizada para a biblioteca (OneDrive, Google Drive, Dropbox ou pen drive)" };
        if (settings.LibrarySyncFolder.Length > 0 && Directory.Exists(settings.LibrarySyncFolder)) dialog.InitialDirectory = settings.LibrarySyncFolder;
        if (dialog.ShowDialog(this) == true) LibrarySyncFolder = dialog.FolderName;
    }

    private void TurnOffLibrarySyncClick(object sender, RoutedEventArgs e) => LibrarySyncFolder = "";

    private async void SyncLibraryNowClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || settings.LibrarySyncFolder.Length == 0 || !CommitEdits()) return;
        if (await OfferSyncedLibrary(force: true)) return;
        await SendLibraryToSyncFolder(force: true);
    }

    internal static LibrarySyncMarker? ReadSyncMarker(string folder)
    {
        try
        {
            string path = Path.Combine(folder, SyncMarkerName);
            return File.Exists(path) ? JsonSerializer.Deserialize<LibrarySyncMarker>(File.ReadAllText(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private string LibraryHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings.Library))));

    /// <summary>Sends the library when it changed since the last send (or always when forced). Returns false on failure.</summary>
    internal async Task<bool> SendLibraryToSyncFolder(bool force)
    {
        string folder = settings.LibrarySyncFolder;
        if (folder.Length == 0) return true;
        CaptureSession();
        string hash = LibraryHash();
        if (!force && hash == settings.LibrarySyncSentHash && File.Exists(Path.Combine(folder, SyncPackageName))) return true;
        string snapshot = JsonSerializer.Serialize(settings.Library);
        string? selected = settings.SelectedMidiPath;
        bool sent = false;
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"A pasta sincronizada não está disponível: {folder}");
            Status = "Conferindo arquivos da biblioteca…";
            var plan = await Task.Run(() => LibraryBackupSnapshot.Capture(JsonSerializer.Deserialize<List<SavedMidi>>(snapshot)!, selected), token);
            string temporary = Path.Combine(folder, ".teclascore-envio-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await LibraryBackupService.CreateAsync(temporary, plan.LibraryJson, plan.Sources, plan.MissingFiles, CreateOperationProgress(), token);
                File.Move(temporary, Path.Combine(folder, SyncPackageName), overwrite: true);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            var marker = new LibrarySyncMarker(DateTimeOffset.Now, MachineName, plan.ItemCount, AppVersion);
            File.WriteAllText(Path.Combine(folder, SyncMarkerName), JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
            settings.LibrarySyncSentHash = hash; settings.LibrarySyncSeen = marker.CreatedAt;
            PersistPreferences();
            Status = $"Biblioteca enviada para a pasta sincronizada: {plan.ItemCount} músicas" + (plan.MissingFiles.Count > 0 ? $" · {plan.MissingFiles.Count} arquivos indisponíveis ficaram de fora." : ".");
            sent = true;
        }, operationTitle: "Enviando a biblioteca para a pasta sincronizada");
        Notify(nameof(LibrarySyncStatus));
        return sent;
    }

    /// <summary>
    /// At startup (or on demand): when another computer sent a newer library, asks once and adds its new songs.
    /// Returns true when a library was received.
    /// </summary>
    internal async Task<bool> OfferSyncedLibrary(bool force = false)
    {
        string folder = settings.LibrarySyncFolder;
        if (folder.Length == 0 || !persistenceEnabled && librarySyncOffer is null) return false;
        if (ReadSyncMarker(folder) is not { } marker || marker.Machine == MachineName) return false;
        if (!force && settings.LibrarySyncSeen is { } seen && marker.CreatedAt <= seen) return false;
        string package = Path.Combine(folder, SyncPackageName);
        if (!File.Exists(package)) return false;
        string question = $"O computador {marker.Machine} enviou a biblioteca em {marker.CreatedAt.LocalDateTime:dd/MM/yyyy HH:mm} ({marker.Songs} músicas).\n\n" +
            "Trazer para cá as músicas que ainda não estão nesta biblioteca? As músicas daqui e suas configurações ficam como estão.";
        bool accept = librarySyncOffer is { } offer ? offer(question)
            : MessageBox.Show(this, question, "Biblioteca sincronizada", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        settings.LibrarySyncSeen = marker.CreatedAt; PersistPreferences();
        if (!accept) { Status = "Biblioteca de outro computador não foi trazida. Use Sincronizar agora quando quiser."; return false; }
        int before = Files.Count;
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            var inspected = await LibraryBackupService.InspectAsync(package, CreateOperationProgress(), token);
            string root = settings.LibrarySyncRestoreRoot.Length > 0 ? settings.LibrarySyncRestoreRoot : DefaultSyncRestoreRoot;
            var restored = await LibraryBackupService.RestoreAsync(inspected, root, CreateOperationProgress(), token);
            var data = await Task.Run(() => LibraryBackupSnapshot.Resolve(restored.Manifest, restored.Directory), token);
            await ApplyRestoredLibrary(data, token);
        }, operationTitle: "Trazendo a biblioteca de outro computador");
        int added = Files.Count - before;
        Status = added == 0 ? "Biblioteca sincronizada: nenhuma música nova." : $"Biblioteca sincronizada: {added} música(s) nova(s) de {marker.Machine}.";
        Notify(nameof(LibrarySyncStatus));
        return true;
    }

    private static string DefaultSyncRestoreRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TeclaScore", "Biblioteca sincronizada");

    /// <summary>Called from closing: sends the changed library before the window goes away.</summary>
    private bool DeferCloseForSync(CancelEventArgs e)
    {
        if (syncCloseDone || syncingOnClose || settings.LibrarySyncFolder.Length == 0 || !Directory.Exists(settings.LibrarySyncFolder)) return false;
        CaptureSession();
        if (LibraryHash() == settings.LibrarySyncSentHash && File.Exists(Path.Combine(settings.LibrarySyncFolder, SyncPackageName))) return false;
        e.Cancel = true; syncingOnClose = true;
        Dispatcher.InvokeAsync(async () =>
        {
            try { await SendLibraryToSyncFolder(force: false); }
            finally { syncingOnClose = false; syncCloseDone = true; Close(); }
        });
        return true;
    }
}
