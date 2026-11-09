using System.IO;
using System.Text.Json;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Automatic restore points: before any batch operation (Piloto automático, Gerar as marcadas, redesenhar diagramas da
/// biblioteca, remoção múltipla) a snapshot of the library and of the affected scores is saved; the newest five are kept
/// and listed in the Histórico as "Antes de: Piloto automático · 09/11 14:02 · 168 músicas". "Voltar a este ponto" puts
/// the library back as it was, in one click, without deleting anything made afterwards.
/// </summary>
public partial class MainWindow
{
    private RestorePointStore? restorePointStore;
    internal Func<RestorePointsWindow, bool?> restorePointsDialog = window => window.ShowDialog();
    /// <summary>Test hook: the confirmation before returning to a point (the message is the point's label).</summary>
    internal Func<string, bool> restorePointConfirm = label =>
        MessageBox.Show($"Voltar a {label}?\n\nA biblioteca fica como estava nesse momento. As partituras geradas depois continuam nas pastas; um novo ponto é guardado antes, para desfazer esta volta.",
            "Voltar a este ponto", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    public RestorePointInfo? LastRestorePoint { get; private set; }

    private RestorePointStore RestorePoints => restorePointStore ??= new(Path.Combine(DataDirectoryForSession(), "pontos-de-restauracao"));

    /// <summary>The folder of this session's data: next to an explicit settings file (smokes, --configuracoes), else the app's data folder.</summary>
    private string DataDirectoryForSession() => persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!;

    public IReadOnlyList<RestorePointInfo> ListRestorePoints() => persistenceEnabled ? RestorePoints.List() : [];
    public bool CanShowRestorePoints => IsIdle && persistenceEnabled;

    /// <summary>
    /// Saves the point before <paramref name="operation"/> runs over <paramref name="affected"/>: the whole library's choices
    /// plus a copy of the current score and preview of each affected song. Never throws: a failure only leaves a message.
    /// </summary>
    internal async Task<RestorePointInfo?> CreateRestorePointAsync(string operation, IReadOnlyList<MidiRow> affected)
    {
        if (!persistenceEnabled || operationsDisposed) return null;
        try
        {
            settingsAutosave?.Flush();
            CaptureSession();
            string snapshot = JsonSerializer.Serialize(settings.Library); string? selected = settings.SelectedMidiPath;
            var copy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in affected)
            {
                if (row.ScorePath is { } score) copy.Add(Path.GetFullPath(score));
                if (row.PreviewPath is { } preview) { copy.Add(Path.GetFullPath(preview)); foreach (string page in ScorePageFiles.Discover(preview)) copy.Add(Path.GetFullPath(page)); }
            }
            var store = RestorePoints; string id = store.NewId(); string package = store.PackagePath(id);
            int songs = Files.Count;
            var point = await Task.Run(async () =>
            {
                var plan = LibraryBackupSnapshot.Capture(JsonSerializer.Deserialize<List<SavedMidi>>(snapshot)!, selected, copy.Contains);
                await LibraryBackupService.CreateAsync(package, plan.LibraryJson, plan.Sources, plan.MissingFiles, null, CancellationToken.None);
                var info = new RestorePointInfo(id, operation, DateTimeOffset.Now, songs, plan.Sources.Count, package);
                store.Register(info);
                return info;
            });
            LastRestorePoint = point;
            Notify(nameof(RestorePointsHint));
            return point;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            Status = "Não foi possível guardar o ponto de restauração: " + error.Message + " A operação continua.";
            return null;
        }
    }

    public string RestorePointsHint => ListRestorePoints() is { Count: > 0 } points
        ? $"{points.Count} ponto(s) de restauração · o mais recente: {points[0].Label}"
        : "Nenhum ponto de restauração ainda: o primeiro é guardado antes da próxima operação em lote.";

    private void RestorePointsClick(object sender, RoutedEventArgs e) => _ = ShowRestorePointsAsync();

    internal async Task ShowRestorePointsAsync()
    {
        if (!CanShowRestorePoints || !CommitEdits()) return;
        var points = ListRestorePoints();
        var window = new RestorePointsWindow(points, RestorePoints.Directory) { Owner = this };
        if (restorePointsDialog(window) != true || window.Selected is not { } point) return;
        await ReturnToRestorePointAsync(point);
    }

    /// <summary>Puts the library back as it was at the point; a new point ("Antes de: Voltar ao ponto…") is saved first.</summary>
    internal async Task<bool> ReturnToRestorePointAsync(RestorePointInfo point)
    {
        if (!IsIdle || !persistenceEnabled || !CommitEdits()) return false;
        if (!point.IsAvailable) { Status = "O arquivo deste ponto de restauração não está mais disponível."; return false; }
        if (!restorePointConfirm(point.Label)) return false;
        if (IsBackgroundQueueRunning || IsAutopilotRunning) { Status = "Pause a fila de conversões antes de voltar a um ponto de restauração."; return false; }
        await CreateRestorePointAsync("Voltar ao ponto " + point.CreatedAt.DateTime.ToString("dd/MM HH:mm"), Files.Where(r => r.ScorePath is not null).ToList());
        bool done = false;
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            var inspected = await LibraryBackupService.InspectAsync(point.Package, CreateOperationProgress(), token);
            var restored = await LibraryBackupService.RestoreAsync(inspected, RestorePoints.RestoreDirectory(point.Id), CreateOperationProgress(), token);
            var data = await Task.Run(() => LibraryBackupSnapshot.Resolve(restored.Manifest, restored.Directory, allowLocal: true), token);
            await ReplaceLibraryAsync(data, token);
            done = true;
            Status = $"Biblioteca de volta a {point.Label}. Nada foi apagado: as partituras feitas depois continuam nas pastas.";
        }, operationTitle: "Voltando ao ponto de restauração");
        if (done) { libraryDirty = true; RefreshDashboard(); Notify(nameof(RestorePointsHint)); }
        return done;
    }

    /// <summary>Replaces every card with the snapshot's rows (the MIDIs are read again; a missing one keeps its saved state).</summary>
    private async Task ReplaceLibraryAsync(PortableLibraryData data, CancellationToken token)
    {
        StopTrackAudition(); ResetPlayback();
        var loaded = new List<(SavedMidi Saved, MidiRow Row)>();
        // Rows already open with the same file are rebuilt from the same bytes; everything else is read on several cores first.
        await PrefetchForImportAsync(data.Library.Select(item => item.FullPath).ToArray(), token);
        try
        {
            foreach (var saved in data.Library)
            {
                token.ThrowIfCancellationRequested(); Status = $"Abrindo música {loaded.Count + 1} de {data.Library.Count}…";
                MidiRow row;
                try { row = await Task.Run(() => LoadMidi(saved.FullPath), token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException or ArgumentException or NotSupportedException)
                { row = new MidiRow { FullPath = saved.FullPath, Status = "Arquivo indisponível · estado do ponto de restauração preservado" }; }
                loaded.Add((saved, row));
            }
        }
        finally { prefetchedMidis = null; }
        token.ThrowIfCancellationRequested();
        bool oldLoading = loading; loading = true;
        try
        {
            foreach (var (saved, row) in loaded)
            {
                bool sameSource = string.Equals(row.SourceSha256, saved.Sha256, StringComparison.OrdinalIgnoreCase);
                if (row.Document is not null && sameSource) ApplySavedMidi(row, saved);
                else
                {
                    row.ScorePath = saved.ScorePath; row.PreviewPath = saved.PreviewPath;
                    row.PageCount = saved.PageCount; row.GeneratedAt = saved.GeneratedAt;
                    row.KeyFifths = saved.KeyFifths; row.KeyMinor = saved.KeyMinor;
                    foreach (var chord in saved.Chords) row.Chords.Add(new ChordRow
                    { Quarter = chord.Quarter, Bass = chord.Bass, Root = chord.Root, Quality = chord.Quality, Confidence = chord.Confidence,
                        DiagramInversion = chord.DiagramInversion, Recommendation = chord.Recommendation, Alternatives = chord.Alternatives });
                }
                ApplySavedScoreProperties(row, saved); RememberVersion(row);
                if (row.Document is not null && !sameSource && !saved.HasMusicalEdits) row.Status = "MIDI alterado depois do ponto · confira as pistas e analise novamente";
            }
        }
        finally { loading = oldLoading; }
        token.ThrowIfCancellationRequested();
        settingsAutosave?.Flush();
        CaptureSession(); var previous = settings.Library;
        settings.Library = data.Library.ToList();
        try { if (persistenceEnabled) settings.Save(persistencePath); }
        catch { settings.Library = previous; throw; }
        unavailableSessionRows.Clear();
        Library.SelectedItem = null;
        Files.Clear();
        foreach (var (saved, row) in loaded)
        {
            if (row.SourceSha256 is null) unavailableSessionRows[row.FullPath] = saved;
            Files.Add(row);
        }
        SearchText = ""; CurrentFilter = "all"; FavoritesOnly = false; CollectionFilter = "";
        SetCurrentPage("library");
        Library.SelectedItem = Files.FirstOrDefault(row => string.Equals(row.FullPath, data.SelectedPath, StringComparison.OrdinalIgnoreCase)) ?? Files.FirstOrDefault();
        RefreshSelection(); Notify(nameof(FileCountText));
    }
}
