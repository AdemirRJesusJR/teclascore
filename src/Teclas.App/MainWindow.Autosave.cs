using System.IO;
using System.Windows.Threading;

namespace Teclas.App;

/// <summary>
/// PersistPreferences no longer saves the whole session on the UI thread at every change: requests are coalesced
/// (500 ms, at most 2 s), the UI thread only takes a cheap snapshot, and the JSON is written in the background.
/// Closing, smoke saves, the backup restore and <see cref="Settings.Load"/> flush what is pending first.
/// </summary>
public partial class MainWindow
{
    private SettingsAutosave? settingsAutosave;

    private void InitializeAutosave()
    {
        if (!persistenceEnabled || settingsAutosave is not null) return;
        settingsAutosave = new SettingsAutosave(persistencePath ?? Settings.DefaultPath, CaptureForAutosave,
            new DispatcherSynchronizationContext(Dispatcher), failed: error =>
            {
                PersistenceError = "Não foi possível salvar suas configurações. O aplicativo permanecerá aberto: " + error.Message;
                Status = PersistenceError;
            });
        Closed += (_, _) => settingsAutosave?.Dispose();
    }

    /// <summary>Runs on the UI thread when the quiet period ends; null postpones (an operation or a cell edit is in progress).</summary>
    private Settings? CaptureForAutosave()
    {
        if (!persistenceEnabled || restoringSession || operationsDisposed) return null;
        // Mid-operation or while a chord cell is being typed the session is not captured: try again shortly.
        if (busy || ChordGrid.IsKeyboardFocusWithin || HasValidationError(this))
        {
            settingsAutosave?.Request();
            return null;
        }
        CaptureSession();
        return SettingsAutosave.Snapshot(settings);
    }

    /// <summary>Writes a pending coalesced save now (before reading settings.json from disk or replacing it).</summary>
    internal void FlushPreferences()
    {
        try { settingsAutosave?.Flush(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { PersistenceError = "Não foi possível salvar suas configurações. O aplicativo permanecerá aberto: " + error.Message; Status = PersistenceError; }
    }

    internal int AutosaveWrites => settingsAutosave?.Writes ?? 0;
    internal int AutosaveRequests => settingsAutosave?.Requests ?? 0;
}
