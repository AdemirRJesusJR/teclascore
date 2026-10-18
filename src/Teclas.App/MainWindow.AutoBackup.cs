using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Weekly backup of the whole library into a folder the user chose, keeping the newest copies.</summary>
public partial class MainWindow
{
    public const int AutoBackupDays = 7, AutoBackupKept = 4;
    private const string AutoBackupPrefix = "TeclaScore-automatico-";
    internal Func<string?>? autoBackupFolderPicker = null;
    private bool autoBackupRunning;

    public bool AutoBackupEnabled
    {
        get => settings.AutoBackup && settings.AutoBackupFolder.Length > 0;
        set
        {
            if (value == AutoBackupEnabled) return;
            if (value && settings.AutoBackupFolder.Length == 0 && ChooseAutoBackupFolder() is null) { Notify(); return; }
            settings.AutoBackup = value; PersistPreferences(); Notify(); Notify(nameof(AutoBackupStatus));
            if (value) _ = RunAutoBackupIfDue();
        }
    }

    public string AutoBackupStatus => !AutoBackupEnabled
        ? "Desligada. Ligue para salvar uma cópia da biblioteca toda semana, sem precisar lembrar."
        : $"Pasta: {settings.AutoBackupFolder} · " + (settings.LastAutoBackup is { } last
            ? $"última cópia em {last.LocalDateTime:dd/MM/yyyy HH:mm}; as {AutoBackupKept} mais recentes ficam guardadas."
            : "a primeira cópia sai em instantes.");

    private void InitializeAutoBackup(bool smoke)
    {
        if (smoke) return;
        Loaded += (_, _) => Dispatcher.InvokeAsync(async () => await RunAutoBackupIfDue(), DispatcherPriority.ApplicationIdle);
    }

    private void ChooseAutoBackupFolderClick(object sender, RoutedEventArgs e)
    {
        if (ChooseAutoBackupFolder() is null) return;
        Notify(nameof(AutoBackupEnabled)); Notify(nameof(AutoBackupStatus));
        if (AutoBackupEnabled) _ = RunAutoBackupIfDue();
    }

    private string? ChooseAutoBackupFolder()
    {
        string? folder = autoBackupFolderPicker is { } pick ? pick() : null;
        if (autoBackupFolderPicker is null)
        {
            var dialog = new OpenFolderDialog { Title = "Pasta para a cópia automática da biblioteca", Multiselect = false };
            if (settings.AutoBackupFolder.Length > 0 && Directory.Exists(settings.AutoBackupFolder)) dialog.InitialDirectory = settings.AutoBackupFolder;
            folder = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }
        if (folder is null) return null;
        settings.AutoBackupFolder = Path.GetFullPath(folder); PersistPreferences();
        return settings.AutoBackupFolder;
    }

    /// <summary>Saves a copy when the last one is a week old (or when forced), in the background, then keeps the newest four.</summary>
    internal async Task<string?> RunAutoBackupIfDue(bool force = false, DateTimeOffset? now = null)
    {
        var moment = now ?? DateTimeOffset.Now;
        if (!AutoBackupEnabled || autoBackupRunning || operationsDisposed) return null;
        if (!force && settings.LastAutoBackup is { } last && moment - last < TimeSpan.FromDays(AutoBackupDays)) return null;
        string folder = settings.AutoBackupFolder;
        if (!Directory.Exists(folder)) { Status = $"Cópia automática adiada: a pasta {folder} não está disponível (pen drive ou nuvem desconectada?)."; return null; }
        autoBackupRunning = true;
        try
        {
            CaptureSession();
            string snapshot = JsonSerializer.Serialize(settings.Library); string? selected = settings.SelectedMidiPath;
            string target = Path.Combine(folder, AutoBackupPrefix + moment.LocalDateTime.ToString("yyyy-MM-dd-HHmmss") + ".teclasbackup");
            var plan = await Task.Run(async () =>
            {
                var prepared = LibraryBackupSnapshot.Capture(JsonSerializer.Deserialize<List<SavedMidi>>(snapshot)!, selected);
                string temporary = Path.Combine(folder, ".teclascore-copia-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await LibraryBackupService.CreateAsync(temporary, prepared.LibraryJson, prepared.Sources, prepared.MissingFiles, null, CancellationToken.None);
                    File.Move(temporary, target);
                }
                finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                foreach (var old in Directory.EnumerateFiles(folder, AutoBackupPrefix + "*.teclasbackup").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(AutoBackupKept))
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return prepared;
            });
            settings.LastAutoBackup = moment; PersistPreferences(); Notify(nameof(AutoBackupStatus));
            Status = $"Cópia automática da biblioteca salva: {plan.ItemCount} músicas" + (plan.MissingFiles.Count > 0 ? $", {plan.MissingFiles.Count} arquivos indisponíveis ficaram de fora" : "") + ".";
            return target;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { Status = "Não foi possível salvar a cópia automática: " + error.Message; return null; }
        finally { autoBackupRunning = false; }
    }
}
