using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Protections for batch work over hundreds of MIDIs: an approved score is never regenerated, redrawn or removed by a
/// batch action ("pulada · aprovada" in the summaries; to touch it, withdraw the approval on the song itself), every
/// batch is previewed in "O que vai acontecer", and every batch leaves a summary ("nada foi apagado" or what changed)
/// plus a JSON log in %LOCALAPPDATA%\TeclasPartituras\lotes\.
/// </summary>
public partial class MainWindow
{
    internal Func<BatchPreviewWindow, bool?> batchPreviewDialog = window => window.ShowDialog();
    private BatchLogStore? batchLogStore;
    public BatchLogEntry? LastBatchLog { get; private set; }
    public string? LastBatchLogPath { get; private set; }

    private BatchLogStore BatchLogs => batchLogStore ??= new(Path.Combine(DataDirectoryForSession(), "lotes"));

    /// <summary>An approved score (reviewed by the user) is locked against batch actions.</summary>
    internal static bool IsApprovalLocked(MidiRow row) => row.Approved && row.ScorePath is not null;
    internal static string SongTitle(MidiRow row) => string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle;

    /// <summary>The median duration of one generation and how many were measured, from the app's performance records.</summary>
    internal (double? Median, int Samples) GenerationMedian()
    {
        try { return BatchEstimate.MedianGenerationSeconds(performanceRecorder?.ReadReport().Records ?? []); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return (null, 0); }
    }

    /// <summary>Builds the preview with the time estimate for the songs that will be generated.</summary>
    internal BatchPreview BuildBatchPreview(string operation, IEnumerable<BatchPreviewItem> items, bool estimate = true)
    {
        var list = items.ToList();
        int generating = list.Count(i => i.Group is BatchPreview.Generate or BatchPreview.Regenerate or BatchPreview.Redraw);
        var (median, samples) = estimate && generating > 0 ? GenerationMedian() : (null, 0);
        return new(operation, list, BatchEstimate.Seconds(median, generating, ConversionParallelism.Resolve(settings.ConversionParallelism)), samples);
    }

    /// <summary>Shows the preview; returns the rows kept ticked, or null when the user cancelled.</summary>
    internal IReadOnlyList<MidiRow>? ConfirmBatch(BatchPreview preview, IReadOnlyList<MidiRow> active, string startLabel, bool restorePoint = true, params UIElement[] extras)
    {
        var window = new BatchPreviewWindow(preview, startLabel, restorePoint, extras) { Owner = IsLoaded ? this : null };
        if (batchPreviewDialog(window) != true) return null;
        var selected = window.SelectedIds.ToHashSet(StringComparer.Ordinal);
        return active.Where(row => selected.Contains(row.FullPath)).ToList();
    }

    /// <summary>Writes the batch log and keeps the entry for the result window; never throws.</summary>
    internal BatchLogEntry RecordBatch(string operation, DateTimeOffset start, IReadOnlyList<BatchLogItem> items, IReadOnlyList<string> changes, RestorePointInfo? point, bool cancelled = false)
    {
        var entry = BatchLogEntry.Create(AppVersion, operation, start, items, changes, point?.Label, cancelled);
        LastBatchLog = entry; LastBatchLogPath = null;
        if (!persistenceEnabled) return entry;
        try { LastBatchLogPath = BatchLogs.Write(entry); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status = "O registro do lote não pôde ser gravado: " + error.Message; }
        return entry;
    }

    /// <summary>"Nada foi apagado. Ponto de restauração: Antes de: … · Registro: lotes\2026-11-09-140200.json".</summary>
    internal string BatchSummaryText(BatchLogEntry entry) => entry.ChangesText()
        + (entry.PontoDeRestauracao is { } point ? $" Ponto de restauração: {point}." : "")
        + (LastBatchLogPath is { } path ? $" Registro: {Path.GetFileName(path)}." : "");

    internal static BatchLogItem LogItem(MidiRow row, string situation, string? reason = null) => new(SongTitle(row), row.FullPath, situation, reason, row.ScorePath);

    // ---- Withdrawing the approval of the open song ----

    public bool CanUnapprove => IsIdle && selectedFile is { } row && IsApprovalLocked(row);

    private void UnapproveClick(object sender, RoutedEventArgs e)
    {
        if (!CanUnapprove || selectedFile is not { } row || !CommitEdits()) return;
        row.Approved = false;
        Status = $"{SongTitle(row)} deixou de estar aprovada: as ações em lote voltam a poder refazê-la ou removê-la.";
        PersistPreferences(); libraryDirty = true; RefreshDashboard(); Notify(nameof(CanUnapprove));
    }
}
