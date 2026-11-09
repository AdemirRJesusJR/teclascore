using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

/// <summary>One song in the autopilot report.</summary>
public sealed record AutopilotOutcome(MidiRow Row, string Group, string Detail);

/// <summary>
/// Library autopilot: every pending MIDI gets its melody (the part that follows the lyrics, or the clear automatic
/// choice), goes through analysis and generation in the conversion queue, optionally to PDF, and the report separates
/// the scores that are ready from the ones that need a look and the songs that need a human choice. Unclear melodies
/// are never guessed. The computer is kept awake while it runs, so it can work overnight.
/// </summary>
public partial class MainWindow
{
    internal Func<Window, bool?> autopilotDialog = window => window.ShowDialog();
    /// <summary>Test hook: generates the rows (instead of the queue + engine); returns an error message per failed row.</summary>
    internal Func<IReadOnlyList<MidiRow>, CancellationToken, Task<IReadOnlyDictionary<MidiRow, string>>>? autopilotGenerate = null;
    /// <summary>Test hook: exports the PDFs of the rows; returns how many were saved.</summary>
    internal Func<IReadOnlyList<MidiRow>, string, CancellationToken, Task<int>>? autopilotPdf = null;
    public IReadOnlyList<AutopilotOutcome> LastAutopilot { get; private set; } = [];

    /// <summary>Songs the autopilot would take: MIDIs read without problems and without a score yet.</summary>
    /// <summary>A track that barely changes pitch (MidiFile marks it "Nota-guia"): the karaoke guide under the words.</summary>
    internal static bool IsGuideTrack(TrackRow track) => track.Source.Hint.StartsWith("Nota-guia", StringComparison.Ordinal);

    internal IReadOnlyList<MidiRow> AutopilotCandidates() =>
        Files.Where(row => !row.UsesNativeScoreWorkflow && row.Document is not null && row.ScorePath is null).ToList();

    /// <summary>Chooses the melody when it is clear; returns null when ready, or the reason it needs a human.</summary>
    internal string? PrepareAutopilotMelody(MidiRow row) => PlanAutopilotMelody(row, apply: true);

    /// <summary>
    /// The autopilot's melody decision. With <paramref name="apply"/> false nothing on the card changes (the preview and
    /// <c>--simular</c> only ask what would happen); with true the clear choice is marked as the melody.
    /// </summary>
    internal string? PlanAutopilotMelody(MidiRow row, bool apply)
    {
        var (track, reason) = ProposeAutopilotMelody(row);
        if (apply && track is not null) { track.IsMelody = true; row.AutoMelody = true; row.AutoMelodyConfident = true; }
        return reason;
    }

    /// <summary>
    /// The autopilot's verdict without changing the song: the track it would mark as melody (when the choice is clear),
    /// or the reason it needs a human. Both null: the song already has its melody (or uses the reviewed profile).
    /// </summary>
    internal (TrackRow? Track, string? Reason) ProposeAutopilotMelody(MidiRow row)
    {
        if (row.UseReviewedProfile) return (null, null);
        if (row.Tracks.FirstOrDefault(t => t.IsMelody && IsGuideTrack(t)) is { } guide)
            return (null, $"a pista {TrackLabel(guide)} marcada como melodia é uma nota-guia (altura quase fixa), não a voz cantada");
        if (row.HasMelodyChoice) return (null, null);
        // A missing voice is not replaced by arpeggios, bass or lyric guide notes: the user decides (another MIDI or Só cifras).
        if (row.CatalogLacksMelody) return (null, "o diagnóstico do lote indica que o MIDI não traz a melodia completa; escolha outra fonte ou ligue Só cifras");
        // The lyrics decide: the part with 90% or more of the syllables, clearly ahead of the other lines, is the melody.
        if (row.Tracks.FirstOrDefault(t => t.MelodySuggestion?.FollowsLyrics == true && !IsGuideTrack(t)) is { } lyricsMelody) return (lyricsMelody, null);
        var coverage = LyricsCoverageOf(row);
        if (coverage.Count > 0 && coverage[0].Share >= .5 && row.Tracks.FirstOrDefault(t => t.Source.Key == coverage[0].Source) is { } sung && !IsGuideTrack(sung))
            return (sung, null);
        var proposal = MelodyBatchAdvisor.Propose(row.Tracks.Select(t => t.MelodySuggestion).OfType<MelodySourceSuggestion>().ToArray());
        if (proposal.Source is { } source && proposal.Confident && row.Tracks.FirstOrDefault(t => t.Source.Key == source) is { } track) return (track, null);
        if (coverage.Count > 0 && row.Tracks.FirstOrDefault(t => t.Source.Key == coverage[0].Source) is { } lyricGuide && IsGuideTrack(lyricGuide))
            return (null, "a letra acompanha uma nota-guia (altura quase fixa), não a voz; escolha a melodia ou use Melodia de uma gravação");
        return (null, "melodia sem escolha clara" + (proposal.Reason.Length > 0 ? ": " + proposal.Reason : ""));
    }

    private bool autopilotRunning;
    private CancellationTokenSource? autopilotCancellation;
    /// <summary>The autopilot is running (in the background queue, outside the busy state).</summary>
    public bool IsAutopilotRunning => autopilotRunning;
    /// <summary>Called on the UI thread for each song as soon as the queue brings its score back to the card.</summary>
    private Action<MidiRow>? generationCompleted;
    /// <summary>MuseScores used for PDFs at the same time (up to 4, never more than the simultaneous conversions).</summary>
    private int PdfWorkers => Math.Clamp(ConversionParallelism.Resolve(settings.ConversionParallelism), 1, 4);

    private async void AutopilotClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        if (autopilotRunning) { Status = "O Piloto automático já está rodando: acompanhe a fila no rodapé."; return; }
        var candidates = AutopilotCandidates();
        if (candidates.Count == 0) { Status = "Nenhuma música pendente: todas as MIDIs da biblioteca já têm partitura."; return; }
        var pdf = new CheckBox { Content = "Salvar também o PDF de cada partitura pronta", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        var awake = new CheckBox { Content = "Manter o computador acordado até terminar (pode deixar rodando à noite)", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
            Text = "Para cada música marcada, o TeclaScore escolhe a melodia quando ela é clara (a pista que acompanha a letra ou a candidata bem à frente das outras), analisa os acordes e gera a partitura. Melodias duvidosas não são adivinhadas." };
        // The preview asks nothing of the cards: the melody is only marked when the run starts.
        var preview = AutopilotPreview(candidates);
        var window = new BatchPreviewWindow(preview, "Começar", true, note, pdf, awake) { Owner = this };
        if (autopilotDialog(window) != true) return;
        var selected = window.SelectedIds.ToHashSet(StringComparer.Ordinal);
        var chosen = candidates.Where(row => selected.Contains(row.FullPath)).ToList();
        if (chosen.Count == 0) { Status = "Nenhuma música ficou marcada para o Piloto automático."; return; }
        await RunAutopilotAsync(chosen, pdf.IsChecked == true, awake.IsChecked == true);
    }

    /// <summary>"O que vai acontecer" for the autopilot: what will be generated, what needs a human and why, what is already done.</summary>
    internal BatchPreview AutopilotPreview(IReadOnlyList<MidiRow> candidates)
    {
        var items = new List<BatchPreviewItem>();
        foreach (var row in candidates)
            items.Add(PlanAutopilotMelody(row, apply: false) is { } reason
                ? new(row.FullPath, SongTitle(row), BatchPreview.NeedsYou, reason, false)
                : new(row.FullPath, SongTitle(row), BatchPreview.Generate, row.HasMelodyChoice || row.UseReviewedProfile ? "melodia já escolhida" : "melodia clara", true));
        foreach (var row in Files.Where(row => row.ScorePath is not null))
            items.Add(new(row.FullPath, SongTitle(row), BatchPreview.AlreadyGenerated, row.Approved ? "aprovada" : "", false));
        return BuildBatchPreview("Piloto automático", items);
    }

    /// <param name="quiet">Background runs (watched folder, command line): progress only in the footer, no report window.</param>
    /// <remarks>
    /// The songs go through the background queue, so the library stays free: the cards being generated are locked, the
    /// ready ones can be reviewed meanwhile, and each PDF is saved as soon as its song is ready. One run at a time: a
    /// watched-folder run waits for the one in progress.
    /// </remarks>
    /// <param name="restorePoint">Saves a restore point of the library first (the watched folder, one song at a time, does not).</param>
    internal async Task<IReadOnlyList<AutopilotOutcome>> RunAutopilotAsync(IReadOnlyList<MidiRow> candidates, bool exportPdf, bool keepAwake, bool quiet = false, bool restorePoint = true)
    {
        while (autopilotRunning) await Task.Delay(200);
        autopilotRunning = true; Notify(nameof(IsAutopilotRunning)); Notify(nameof(CanGenerateInBackground));
        using var stop = autopilotCancellation = new CancellationTokenSource();
        try { return await RunAutopilotCoreAsync(candidates, exportPdf, keepAwake, quiet, restorePoint, stop.Token); }
        finally
        {
            autopilotRunning = false; autopilotCancellation = null;
            Notify(nameof(IsAutopilotRunning)); Notify(nameof(CanGenerateInBackground));
        }
    }

    private async Task<IReadOnlyList<AutopilotOutcome>> RunAutopilotCoreAsync(IReadOnlyList<MidiRow> candidates, bool exportPdf, bool keepAwake, bool quiet, bool restorePoint, CancellationToken token)
    {
        var started = DateTimeOffset.Now;
        // Approved scores are never candidates (they have a score), but a caller's list is filtered all the same.
        var skippedApproved = candidates.Where(IsApprovalLocked).ToList();
        candidates = candidates.Where(row => !IsApprovalLocked(row)).ToList();
        var point = restorePoint && candidates.Count > 0 ? await CreateRestorePointAsync("Piloto automático", candidates) : null;
        LastAdjustments = new Dictionary<MidiRow, string>();
        var outcomes = new List<AutopilotOutcome>();
        var ready = new List<MidiRow>();
        foreach (var row in skippedApproved) outcomes.Add(new(row, "Puladas", "aprovada"));
        foreach (var row in candidates)
        {
            if (PrepareAutopilotMelody(row) is { } reason) outcomes.Add(new(row, "Precisam de você", reason));
            else ready.Add(row);
        }
        PersistPreferences();
        IReadOnlyDictionary<MidiRow, string> failures = new Dictionary<MidiRow, string>();
        int pdfs = 0;
        if (ready.Count > 0)
        {
            if (keepAwake) SetThreadExecutionState(ContinuousState | SystemRequired);
            var exports = new List<Task<int>>(); var exported = new HashSet<MidiRow>();
            // PDFs run beside the generations: half of the PDF workers while the queue is busy.
            using var pdfGate = new SemaphoreSlim(Math.Max(1, PdfWorkers / 2));
            try
            {
                Status = $"Piloto automático: gerando {ready.Count} partitura(s) em segundo plano…";
                string? folder = null;
                if (exportPdf)
                {
                    folder = Path.Combine(await PrepareOutputDirectoryAsync(token), "PDFs do piloto automático");
                    Directory.CreateDirectory(folder);
                }
                // Each song's PDF is saved as soon as the queue finishes it (the test hooks keep the single batch at the end).
                bool perSong = folder is not null && autopilotGenerate is null && autopilotPdf is null;
                async Task<int> ExportOne(MidiRow row)
                {
                    await pdfGate.WaitAsync(token);
                    try { return await ExportAutopilotPdfsAsync([row], folder!, token); }
                    catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { return 0; }
                    finally { pdfGate.Release(); }
                }
                if (perSong) generationCompleted = row => { if (exported.Add(row)) exports.Add(ExportOne(row)); };
                try { failures = autopilotGenerate is { } generate ? await generate(ready, token) : await GenerateWithAdjustmentsAsync(ready, token); }
                finally { generationCompleted = null; }
                var made = ready.Where(r => r.ScorePath is not null && !failures.ContainsKey(r)).ToList();
                if (folder is not null && made.Count > 0)
                {
                    Status = $"Piloto automático: salvando {made.Count} PDF(s)…";
                    if (perSong)
                    {
                        foreach (var row in made.Where(r => exported.Add(r))) exports.Add(ExportOne(row));
                        pdfs = (await Task.WhenAll(exports)).Sum();
                    }
                    else pdfs = autopilotPdf is { } export ? await export(made, folder, token) : await ExportAutopilotPdfsAsync(made, folder, token);
                }
            }
            catch (OperationCanceledException) { Status = "Piloto automático pausado. As partituras prontas ficaram salvas."; }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { Status = "Piloto automático: " + error.Message; }
            finally
            {
                if (keepAwake) SetThreadExecutionState(ContinuousState);
                // PDFs already started finish (or are cancelled with the run) before the report.
                try { await Task.WhenAll(exports); } catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException) { }
            }
        }
        foreach (var row in ready)
        {
            RefreshConfidence(row);
            if (failures.TryGetValue(row, out var error))
                outcomes.Add(new(row, "Falharam", generationFailureKinds.TryGetValue(row, out var kind) ? $"{EngineErrorKind.Describe(kind)}: {error}" : error));
            else if (row.ScorePath is null) outcomes.Add(new(row, "Falharam", "a partitura não foi gerada (fila pausada ou cancelada)"));
            else if (LastAdjustments.TryGetValue(row, out var adjusted)) outcomes.Add(new(row, "Para revisar", $"gerada {adjusted} depois de uma falha"));
            else if (row.Confidence is { Score: >= 85 } good) outcomes.Add(new(row, "Prontas", $"{good.Score}%"));
            else outcomes.Add(new(row, "Para revisar", row.Confidence is { } c ? $"{c.Score}% · {string.Join("; ", c.Reasons)}" : "conferir"));
        }
        LastAutopilot = outcomes;
        RefreshSelection(); PersistPreferences(); libraryDirty = true; RefreshDashboard();
        int Count(string group) => outcomes.Count(o => o.Group == group);
        // The autopilot only adds scores (a generation never overwrites a file): nothing is deleted, so the log says so.
        var entry = RecordBatch("Piloto automático", started, outcomes.Select(o => LogItem(o.Row, o.Group == "Puladas" ? "pulada · aprovada" : o.Group, o.Detail)).ToList(),
            [], point, token.IsCancellationRequested);
        Status = $"Piloto automático: {Count("Prontas")} pronta(s), {Count("Para revisar")} para revisar, {Count("Precisam de você")} precisam de você, {Count("Falharam")} falharam" +
                 (Count("Puladas") > 0 ? $", {Count("Puladas")} pulada(s) · aprovada(s)" : "") +
                 (exportPdf && pdfs > 0 ? $"; {pdfs} PDF(s) salvos." : ".") + " " + entry.ChangesText();
        if (!quiet) ShowAutopilotReport(outcomes, entry);
        return outcomes;
    }

    /// <summary>Enqueues the rows in the background queue and waits for them; each finished song comes back to its card at once.</summary>
    private Task<IReadOnlyDictionary<MidiRow, string>> GenerateThroughQueueAsync(IReadOnlyList<MidiRow> rows, CancellationToken token) =>
        GenerateInBackgroundAsync(rows, token, generationCompleted);

    private async Task<int> ExportAutopilotPdfsAsync(IReadOnlyList<MidiRow> rows, string folder, CancellationToken token)
    {
        var sources = rows.Select(BatchPdfCandidate).Where(s => s.BlockedReason is null).ToList();
        if (sources.Count == 0) return 0;
        var report = await new BatchPdfExportService(MuseScorePath, PlaybackCacheDirectory) { MaxParallel = PdfWorkers }
            .ExportAsync(sources, folder, new Progress<BatchPdfProgress>(_ => { }), token, directlyIntoDestination: true);
        return report.SavedCount;
    }

    internal Func<Window, bool?> autopilotReportDialog = window => window.ShowDialog();

    private void ShowAutopilotReport(IReadOnlyList<AutopilotOutcome> outcomes, BatchLogEntry? entry = null)
    {
        var text = new System.Text.StringBuilder();
        foreach (string group in new[] { "Prontas", "Para revisar", "Precisam de você", "Falharam", "Puladas" })
        {
            var items = outcomes.Where(o => o.Group == group).ToList();
            if (items.Count == 0) continue;
            text.AppendLine(group == "Puladas" ? $"Puladas · aprovadas ({items.Count})" : $"{group} ({items.Count})");
            foreach (var item in items) text.AppendLine($"  • {(string.IsNullOrWhiteSpace(item.Row.ScoreTitle) ? item.Row.Name : item.Row.ScoreTitle)} — {item.Detail}");
            text.AppendLine();
        }
        var list = new TextBox { Text = text.ToString().TrimEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 220, Name = "AutopilotReportText" };
        AutomationProperties.SetName(list, "Resultado do piloto automático");
        var readyRows = outcomes.Where(o => o.Group == "Prontas" && o.Row.ScorePath is not null && !o.Row.Approved).Select(o => o.Row).ToList();
        var content = new List<UIElement> { list };
        if (entry is not null)
        {
            int made = outcomes.Count(o => o.Group is "Prontas" or "Para revisar" && o.Row.ScorePath is not null);
            var summary = new TextBlock { Name = "AutopilotSummaryText", TextWrapping = TextWrapping.Wrap, Opacity = .85, FontSize = 12, Margin = new Thickness(0, 8, 0, 0),
                Text = $"{made} partitura(s) nova(s). " + BatchSummaryText(entry) };
            AutomationProperties.SetName(summary, "Resumo da operação");
            content.Add(summary);
        }
        if (readyRows.Count > 0)
        {
            var approveReady = DialogUi.Button($"Aprovar as {readyRows.Count} prontas (≥85%)", (sender, _) =>
            {
                if (ApproveReadyScores(readyRows) > 0 && sender is Button button) { button.IsEnabled = false; button.Content = "Prontas aprovadas"; }
            });
            approveReady.Name = "ApproveReadyButton"; approveReady.HorizontalAlignment = HorizontalAlignment.Left; approveReady.Margin = new Thickness(0, 10, 0, 0);
            approveReady.ToolTip = "Aprovar todas as partituras prontas (nota 85% ou mais), sem abrir uma por uma; pede confirmação";
            AutomationProperties.SetName(approveReady, "Aprovar todas as partituras prontas");
            content.Add(approveReady);
        }
        var window = SimpleDialog("Resultado do piloto automático", "As partituras \"Para revisar\" aparecem primeiro na lista inteligente Revisar primeiro e na Revisão em sequência.",
            outcomes.Any(o => o.Group == "Para revisar" || o.Group == "Prontas") ? "Revisar em sequência" : "Fechar", [.. content]);
        window.Height = 520;
        if (autopilotReportDialog(window) == true && outcomes.Any(o => o.Row.ScorePath is not null)) ReviewSequenceClick(this, new RoutedEventArgs());
    }

    /// <summary>Test hook: the confirmation before approving the ready scores at once.</summary>
    internal Func<string, bool> approveReadyConfirm = message =>
        MessageBox.Show(message, "Aprovar as prontas", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Approves the ready scores (85% or more) after one confirmation; returns how many were approved.</summary>
    internal int ApproveReadyScores(IReadOnlyList<MidiRow> rows)
    {
        var approvable = rows.Where(r => r.ScorePath is not null && !r.Approved && r.Confidence is { Score: >= 85 }).ToList();
        if (approvable.Count == 0) return 0;
        if (!approveReadyConfirm($"Aprovar {approvable.Count} partitura(s) com nota 85% ou mais sem revisar uma por uma?\n\n" +
            string.Join("\n", approvable.Take(12).Select(r => "• " + (string.IsNullOrWhiteSpace(r.ScoreTitle) ? r.Name : r.ScoreTitle))) +
            (approvable.Count > 12 ? $"\n… e mais {approvable.Count - 12}" : ""))) return 0;
        foreach (var row in approvable) row.Approved = true;
        PersistPreferences(); libraryDirty = true; RefreshDashboard();
        Status = $"{approvable.Count} partitura(s) pronta(s) aprovada(s).";
        return approvable.Count;
    }

    /// <summary>A small themed dialog: title, explanation, extra content and a primary button (DialogResult true) plus Cancel.</summary>
    internal Window SimpleDialog(string title, string message, string primary, params UIElement[] content)
    {
        var window = new Window { Title = title, Width = 620, SizeToContent = SizeToContent.Height, MinHeight = 200, Owner = IsLoaded ? this : null, ResizeMode = ResizeMode.CanResizeWithGrip };
        DialogUi.Theme(window);
        var panel = new StackPanel { Margin = new Thickness(20) }; window.Content = panel;
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = .85, Margin = new Thickness(0, 6, 0, 4) });
        foreach (var element in content) panel.Children.Add(element);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = DialogUi.Button("Cancelar", (_, _) => window.DialogResult = false); cancel.IsCancel = true;
        buttons.Children.Add(cancel); buttons.Children.Add(DialogUi.Button(primary, (_, _) => window.DialogResult = true, true));
        panel.Children.Add(buttons);
        return window;
    }

    private const uint ContinuousState = 0x80000000, SystemRequired = 0x1;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
