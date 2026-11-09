using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>A card of a quiet import: the row (new or the one already in the library) and whether it was added now.</summary>
internal sealed record QuietImport(string Path, MidiRow? Row, bool Added, string Note);

/// <summary>
/// Pasta acompanhada (optional, off by default): the remembered MIDI folder is watched, and MIDIs that arrive there
/// become cards like a normal import, with a discreet note in the footer. The files that were already in the folder
/// stay out (the user still picks them in Pasta de MIDIs). Opening the app catches up with files that arrived while it
/// was closed. With "Gerar sozinho com o Piloto automático" the new cards go to the autopilot, which keeps its rules.
/// </summary>
public partial class MainWindow
{
    private MidiFolderWatcher? folderWatcher;
    private readonly HashSet<string> folderWatchKnown = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> folderWatchArrivals = [];
    private readonly List<MidiRow> folderWatchAutopilotQueue = [];
    private DispatcherTimer? folderWatchRetry;
    private bool folderWatchAllowed, folderWatchImporting, folderWatchAutopilotRunning;
    private long folderWatchGeneration;
    internal Func<string?>? folderWatchFolderPicker = null;
    /// <summary>How long a new file must stay unchanged before it is read (shorter in the smoke check).</summary>
    internal TimeSpan folderWatchQuietPeriod = TimeSpan.FromSeconds(2);
    internal Task? FolderWatchStartTask { get; private set; }
    /// <summary>Cards the watched folder created most recently, and the last autopilot run it started.</summary>
    public IReadOnlyList<MidiRow> LastFolderWatchImports { get; private set; } = [];
    public IReadOnlyList<AutopilotOutcome> LastFolderWatchAutopilot { get; private set; } = [];
    internal bool IsWatchingMidiFolder => folderWatcher is not null;
    internal int FolderWatchPendingCount => (folderWatcher?.PendingCount ?? 0) + folderWatchArrivals.Count + folderWatchAutopilotQueue.Count
        + (folderWatchImporting || folderWatchAutopilotRunning ? 1 : 0);

    private string FolderWatchStatePath => Path.Combine(persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!, "pasta-acompanhada.json");

    public bool WatchMidiFolderEnabled
    {
        get => settings.WatchMidiFolder;
        set
        {
            if (value == settings.WatchMidiFolder) return;
            if (value && !Directory.Exists(settings.MidiFolder) && ChooseWatchedFolder() is null) { Notify(); return; }
            settings.WatchMidiFolder = value; PersistPreferences(); NotifyFolderWatch();
            if (value) FolderWatchStartTask = StartFolderWatchAsync(catchUp: false);
            else StopFolderWatch(announce: true);
        }
    }

    public bool WatchMidiFolderAutopilot
    {
        get => settings.WatchMidiFolder && settings.WatchMidiFolderAutopilot;
        set
        {
            if (value == WatchMidiFolderAutopilot || value && !settings.WatchMidiFolder) { Notify(); return; }
            settings.WatchMidiFolderAutopilot = value; PersistPreferences(); NotifyFolderWatch();
            if (!value) folderWatchAutopilotQueue.Clear();
            AnnounceFolderWatch();
        }
    }

    public string MidiFolderWatchStatus => !settings.WatchMidiFolder
        ? "Desligado. Os MIDIs só entram na biblioteca quando você os marca na Pasta de MIDIs (Ctrl+L)."
        : !Directory.Exists(settings.MidiFolder)
            ? $"A pasta {settings.MidiFolder} não está disponível agora; os MIDIs novos são conferidos quando o app abrir com ela de volta."
            : $"Acompanhando {settings.MidiFolder}" + (settings.MidiFolderIncludeSubfolders ? " e as subpastas" : "") +
              ". MIDIs novos viram cards sozinhos" + (WatchMidiFolderAutopilot ? " e vão para o Piloto automático" : "") +
              "; os que já estavam na pasta continuam de fora até você marcá-los.";

    private void NotifyFolderWatch()
    {
        Notify(nameof(WatchMidiFolderEnabled)); Notify(nameof(WatchMidiFolderAutopilot)); Notify(nameof(MidiFolderWatchStatus));
    }

    private void InitializeFolderWatch(bool smoke, string? settingsPath)
    {
        // Automated previews never watch the user's real folder; isolated smokes pass their own settings file.
        folderWatchAllowed = !smoke || settingsPath is not null;
        Loaded += (_, _) =>
        {
            if (folderWatchAllowed && settings.WatchMidiFolder)
                Dispatcher.InvokeAsync(() => FolderWatchStartTask = StartFolderWatchAsync(catchUp: true), DispatcherPriority.ApplicationIdle);
        };
        Closing += (_, _) => SaveFolderWatchState();
        Closed += (_, _) => StopFolderWatch();
    }

    private void ChooseWatchedFolderClick(object sender, RoutedEventArgs e)
    {
        if (ChooseWatchedFolder() is null) return;
        NotifyFolderWatch();
        if (settings.WatchMidiFolder) FolderWatchStartTask = StartFolderWatchAsync(catchUp: false);
    }

    private string? ChooseWatchedFolder()
    {
        string? folder = folderWatchFolderPicker is { } pick ? pick() : null;
        if (folderWatchFolderPicker is null)
        {
            var dialog = new OpenFolderDialog { Title = "Pasta de MIDIs para acompanhar", Multiselect = false };
            if (Directory.Exists(settings.MidiFolder)) dialog.InitialDirectory = settings.MidiFolder;
            folder = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }
        if (folder is null || !Directory.Exists(folder)) return null;
        settings.MidiFolder = Path.GetFullPath(folder); PersistPreferences();
        return settings.MidiFolder;
    }

    /// <summary>
    /// Starts watching the remembered folder. With <paramref name="catchUp"/> (opening the app) the files that arrived
    /// since the last session are brought in; otherwise the folder's current files become the starting point.
    /// </summary>
    internal async Task StartFolderWatchAsync(bool catchUp)
    {
        StopFolderWatch();
        if (!folderWatchAllowed || !settings.WatchMidiFolder || operationsDisposed) return;
        long generation = ++folderWatchGeneration;
        string folder = settings.MidiFolder; bool subfolders = settings.MidiFolderIncludeSubfolders;
        if (!Directory.Exists(folder)) { Status = $"Pasta acompanhada indisponível: {folder}. Os MIDIs novos serão conferidos quando o app abrir com ela de volta."; NotifyFolderWatch(); return; }
        string statePath = FolderWatchStatePath;
        MidiFolderScan scan; MidiFolderWatchSnapshot? snapshot;
        try
        {
            (scan, snapshot) = await Task.Run(() => (MidiFolderScanner.Scan(folder, subfolders), MidiFolderWatchSnapshot.Load(statePath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Status = "Não foi possível acompanhar a pasta de MIDIs: " + ex.Message; return; }
        if (generation != folderWatchGeneration || operationsDisposed || !settings.WatchMidiFolder) return;
        try
        {
            folderWatcher = new MidiFolderWatcher(folder, subfolders,
                paths => Dispatcher.InvokeAsync(() => OnFolderWatchFilesReady(generation, paths)),
                error => Dispatcher.InvokeAsync(() => OnFolderWatchError(generation, error)), folderWatchQuietPeriod);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        { Status = "Não foi possível acompanhar a pasta de MIDIs: " + ex.Message; return; }
        // Only a snapshot of this same folder can tell what arrived meanwhile; without one, nothing is imported in bulk.
        var arrived = catchUp && snapshot is not null && snapshot.Matches(folder, subfolders) ? snapshot.NewFiles(scan) : [];
        folderWatchKnown.Clear();
        foreach (var entry in scan.Entries) folderWatchKnown.Add(entry.FullPath);
        foreach (var path in arrived) { folderWatchKnown.Remove(path); folderWatcher.Enqueue(path); }
        SaveFolderWatchState();
        NotifyFolderWatch();
        if (arrived.Count > 0) Status = $"Pasta acompanhada: {arrived.Count} MIDI(s) chegaram com o app fechado; os cards aparecem em instantes.";
        else if (!catchUp) AnnounceFolderWatch();
    }

    private void AnnounceFolderWatch()
    {
        if (settings.WatchMidiFolder) Status = $"Acompanhando a pasta {Path.GetFileName(settings.MidiFolder.TrimEnd('\\'))}: MIDIs novos viram cards sozinhos" +
            (WatchMidiFolderAutopilot ? " e vão para o Piloto automático." : ".");
    }

    internal void StopFolderWatch(bool announce = false)
    {
        folderWatchGeneration++;
        if (folderWatcher is { } watcher) { SaveFolderWatchState(); folderWatcher = null; watcher.Dispose(); }
        folderWatchArrivals.Clear(); folderWatchAutopilotQueue.Clear();
        folderWatchRetry?.Stop();
        if (announce) { Status = "A pasta de MIDIs não é mais acompanhada. Os cards que já entraram continuam na biblioteca."; NotifyFolderWatch(); }
    }

    private void SaveFolderWatchState()
    {
        if (folderWatcher is not { } watcher) return;
        try
        {
            new MidiFolderWatchSnapshot { Folder = watcher.Folder, IncludeSubfolders = watcher.IncludeSubfolders, SavedAt = DateTimeOffset.Now, Files = folderWatchKnown.Order(StringComparer.OrdinalIgnoreCase).ToList() }
                .Save(FolderWatchStatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void OnFolderWatchFilesReady(long generation, IReadOnlyList<string> paths)
    {
        if (generation != folderWatchGeneration || operationsDisposed) return;
        // A known file (there before watching, or already handled) that is only rewritten or touched is not a new arrival:
        // the user left it out, or removed its card, on purpose.
        foreach (var path in paths)
            if (!folderWatchKnown.Contains(path) && !folderWatchArrivals.Contains(path, StringComparer.OrdinalIgnoreCase)) folderWatchArrivals.Add(path);
        _ = ProcessFolderWatchArrivalsAsync();
    }

    private async void OnFolderWatchError(long generation, Exception error)
    {
        if (generation != folderWatchGeneration || folderWatcher is not { } watcher) return;
        if (!Directory.Exists(watcher.Folder))
        {
            StopFolderWatch();
            Status = $"A pasta acompanhada {watcher.Folder} ficou indisponível. Os MIDIs novos serão conferidos quando o app abrir com ela de volta.";
            NotifyFolderWatch();
            return;
        }
        // Windows dropped events (many files at once): look again for what is not known yet.
        try
        {
            var scan = await Task.Run(() => MidiFolderScanner.Scan(watcher.Folder, watcher.IncludeSubfolders));
            if (generation != folderWatchGeneration) return;
            foreach (var entry in scan.Entries.Where(e => !folderWatchKnown.Contains(e.FullPath))) watcher.Enqueue(entry.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Status = "A pasta acompanhada não pôde ser conferida: " + (ex.Message.Length > 0 ? ex.Message : error.Message); }
    }

    private async Task ProcessFolderWatchArrivalsAsync()
    {
        if (folderWatchImporting || folderWatchArrivals.Count == 0) return;
        // Never in the middle of another task: the cards come in as soon as the app is free.
        if (busy || loading || operationsDisposed) { ScheduleFolderWatchRetry(); return; }
        folderWatchImporting = true;
        long generation = folderWatchGeneration;
        var added = new List<MidiRow>();
        try
        {
            while (folderWatchArrivals.Count > 0 && generation == folderWatchGeneration && !operationsDisposed)
            {
                var batch = folderWatchArrivals.ToArray(); folderWatchArrivals.Clear();
                foreach (var item in await ImportQuietlyAsync(batch))
                    if (item.Added && item.Row is { } row) added.Add(row);
                foreach (var path in batch) folderWatchKnown.Add(Path.GetFullPath(path));
            }
            SaveFolderWatchState();
        }
        finally { folderWatchImporting = false; }
        if (added.Count == 0 || operationsDisposed) return;
        LastFolderWatchImports = added;
        PersistPreferences(); RefreshDashboard();
        string names = string.Join(", ", added.Take(3).Select(r => string.IsNullOrWhiteSpace(r.ScoreTitle) ? r.Name : r.ScoreTitle)) + (added.Count > 3 ? $" e mais {added.Count - 3}" : "");
        Status = $"Pasta acompanhada: {added.Count} MIDI(s) novo(s) na biblioteca — {names}." + (WatchMidiFolderAutopilot ? " O Piloto automático vai gerar as partituras." : "");
        if (WatchMidiFolderAutopilot)
        {
            var candidates = AutopilotCandidates();
            folderWatchAutopilotQueue.AddRange(added.Where(candidates.Contains));
            await RunFolderWatchAutopilotAsync();
        }
    }

    private async Task RunFolderWatchAutopilotAsync()
    {
        if (folderWatchAutopilotRunning || folderWatchAutopilotQueue.Count == 0) return;
        if (!WatchMidiFolderAutopilot) { folderWatchAutopilotQueue.Clear(); return; }
        // Waits while the user works in a dialog, plays music or edits chords, so nothing is interrupted.
        if (busy || loading || operationsDisposed || folderWatchImporting || OwnedWindows.Count > 0 || audioPlaying || countingIn || ChordGrid.IsKeyboardFocusWithin)
        { ScheduleFolderWatchRetry(); return; }
        var candidates = AutopilotCandidates();
        var rows = folderWatchAutopilotQueue.Distinct().Where(candidates.Contains).ToList();
        folderWatchAutopilotQueue.Clear();
        if (rows.Count == 0) return;
        folderWatchAutopilotRunning = true;
        try
        {
            // One song at a time as files arrive: no restore point per arrival (they would push out the useful ones).
            LastFolderWatchAutopilot = await RunAutopilotAsync(rows, exportPdf: false, keepAwake: false, quiet: true, restorePoint: false);
            Status = "Pasta acompanhada · " + Status;
        }
        finally { folderWatchAutopilotRunning = false; }
        if (folderWatchAutopilotQueue.Count > 0) ScheduleFolderWatchRetry();
    }

    private void ScheduleFolderWatchRetry()
    {
        if (operationsDisposed) return;
        if (folderWatchRetry is null)
        {
            folderWatchRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            folderWatchRetry.Tick += async (_, _) =>
            {
                folderWatchRetry!.Stop();
                await ProcessFolderWatchArrivalsAsync();
                await RunFolderWatchAutopilotAsync();
            };
        }
        if (!folderWatchRetry.IsEnabled) folderWatchRetry.Start();
    }

    /// <summary>
    /// Adds MIDIs as cards like the normal import (same reading, title and artist organization) without the progress
    /// overlay, changing pages or the selection. A file already in the library, by path or with the same content, is not added again.
    /// </summary>
    internal const string AlreadyInLibrary = "já estava na biblioteca";

    private async Task<IReadOnlyList<QuietImport>> ImportQuietlyCoreAsync(IReadOnlyList<string> paths)
    {
        var results = new List<QuietImport>();
        foreach (var path in paths)
        {
            if (FindCard(path) is { } existing) { results.Add(new(path, existing, false, AlreadyInLibrary)); continue; }
            MidiRow row;
            try { row = await Task.Run(() => LoadMidi(path)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { row = new() { FullPath = path, Status = "Não foi possível ler: " + ex.Message }; }
            if (operationsDisposed) break;
            if (FindCard(path) is { } raced) { results.Add(new(path, raced, false, AlreadyInLibrary)); continue; }
            if (row.SourceSha256 is { } sha && Files.FirstOrDefault(f => string.Equals(f.SourceSha256, sha, StringComparison.OrdinalIgnoreCase)) is { } same)
            { results.Add(new(path, same, false, "mesmo conteúdo de " + Path.GetFileName(same.FullPath) + ", que já está na biblioteca")); continue; }
            Files.Add(row);
            results.Add(new(path, row, true, row.Document is null ? row.Status : ""));
        }
        Notify(nameof(FileCountText));
        return results;

        MidiRow? FindCard(string path) => Files.FirstOrDefault(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
    }
}
