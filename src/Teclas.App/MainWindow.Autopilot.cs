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
    internal IReadOnlyList<MidiRow> AutopilotCandidates() =>
        Files.Where(row => !row.UsesNativeScoreWorkflow && row.Document is not null && row.ScorePath is null).ToList();

    /// <summary>Chooses the melody when it is clear; returns null when ready, or the reason it needs a human.</summary>
    internal string? PrepareAutopilotMelody(MidiRow row)
    {
        if (row.UseReviewedProfile || row.HasMelodyChoice) return null;
        var coverage = LyricsCoverageOf(row);
        if (coverage.Count > 0 && coverage[0].Share >= .5 && row.Tracks.FirstOrDefault(t => t.Source.Key == coverage[0].Source) is { } sung)
        {
            sung.IsMelody = true; row.AutoMelody = true; row.AutoMelodyConfident = true;
            return null;
        }
        var proposal = MelodyBatchAdvisor.Propose(row.Tracks.Select(t => t.MelodySuggestion).OfType<MelodySourceSuggestion>().ToArray());
        if (proposal.Source is { } source && proposal.Confident && row.Tracks.FirstOrDefault(t => t.Source.Key == source) is { } track)
        {
            track.IsMelody = true; row.AutoMelody = true; row.AutoMelodyConfident = true;
            return null;
        }
        return "melodia sem escolha clara" + (proposal.Reason.Length > 0 ? ": " + proposal.Reason : "");
    }

    private async void AutopilotClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var candidates = AutopilotCandidates();
        if (candidates.Count == 0) { Status = "Nenhuma música pendente: todas as MIDIs da biblioteca já têm partitura."; return; }
        var pdf = new CheckBox { Content = "Salvar também o PDF de cada partitura pronta", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        var awake = new CheckBox { Content = "Manter o computador acordado até terminar (pode deixar rodando à noite)", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
        var confirm = SimpleDialog("Piloto automático",
            $"{candidates.Count} música(s) sem partitura. Para cada uma, o TeclaScore escolhe a melodia quando ela é clara (a pista que acompanha a letra ou a candidata bem à frente das outras), analisa os acordes e gera a partitura. " +
            "Melodias duvidosas não são adivinhadas: essas músicas ficam na lista \"Precisam de você\".", "Começar", pdf, awake);
        if (autopilotDialog(confirm) != true) return;
        await RunAutopilotAsync(candidates, pdf.IsChecked == true, awake.IsChecked == true);
    }

    internal async Task<IReadOnlyList<AutopilotOutcome>> RunAutopilotAsync(IReadOnlyList<MidiRow> candidates, bool exportPdf, bool keepAwake)
    {
        LastAdjustments = new Dictionary<MidiRow, string>();
        var outcomes = new List<AutopilotOutcome>();
        var ready = new List<MidiRow>();
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
            try
            {
                await RunOperation(async () =>
                {
                    var token = cancellation!.Token;
                    Status = $"Piloto automático: gerando {ready.Count} partitura(s)…";
                    failures = autopilotGenerate is { } generate ? await generate(ready, token) : await GenerateWithAdjustmentsAsync(ready, token);
                    var made = ready.Where(r => r.ScorePath is not null && !failures.ContainsKey(r)).ToList();
                    if (exportPdf && made.Count > 0)
                    {
                        string folder = Path.Combine(await PrepareOutputDirectoryAsync(token), "PDFs do piloto automático");
                        Directory.CreateDirectory(folder);
                        Status = $"Piloto automático: salvando {made.Count} PDF(s)…";
                        pdfs = autopilotPdf is { } export ? await export(made, folder, token) : await ExportAutopilotPdfsAsync(made, folder, token);
                    }
                }, operationTitle: "Piloto automático");
            }
            finally { if (keepAwake) SetThreadExecutionState(ContinuousState); }
        }
        foreach (var row in ready)
        {
            RefreshConfidence(row);
            if (failures.TryGetValue(row, out var error)) outcomes.Add(new(row, "Falharam", error));
            else if (row.ScorePath is null) outcomes.Add(new(row, "Falharam", "a partitura não foi gerada (fila pausada ou cancelada)"));
            else if (LastAdjustments.TryGetValue(row, out var adjusted)) outcomes.Add(new(row, "Para revisar", $"gerada {adjusted} depois de uma falha"));
            else if (row.Confidence is { Score: >= 85 } good) outcomes.Add(new(row, "Prontas", $"{good.Score}%"));
            else outcomes.Add(new(row, "Para revisar", row.Confidence is { } c ? $"{c.Score}% · {string.Join("; ", c.Reasons)}" : "conferir"));
        }
        LastAutopilot = outcomes;
        RefreshSelection(); PersistPreferences(); libraryDirty = true; RefreshDashboard();
        int Count(string group) => outcomes.Count(o => o.Group == group);
        Status = $"Piloto automático: {Count("Prontas")} pronta(s), {Count("Para revisar")} para revisar, {Count("Precisam de você")} precisam de você, {Count("Falharam")} falharam" +
                 (exportPdf && pdfs > 0 ? $"; {pdfs} PDF(s) salvos." : ".");
        ShowAutopilotReport(outcomes);
        return outcomes;
    }

    /// <summary>Enqueues the rows, runs the conversion queue to the end and brings the results back to the library.</summary>
    private async Task<IReadOnlyDictionary<MidiRow, string>> GenerateThroughQueueAsync(IReadOnlyList<MidiRow> rows, CancellationToken token)
    {
        var failures = new Dictionary<MidiRow, string>();
        foreach (var row in rows)
        {
            try { await EnqueueConversion(row, token); }
            catch (Exception error) when (error is InvalidOperationException or IOException) { failures[row] = error.Message; }
        }
        var queue = GetConversionQueue();
        int done = 0;
        var progress = new Progress<ConversionQueueProgress>(value => { done = value.Completed; SetOperationItems(value.Completed, Math.Max(value.Total, 1), value.Stage); });
        await queue.RunAsync(progress, token);
        ReconcileQueueResults();
        foreach (var row in rows.Where(r => !failures.ContainsKey(r)))
            if (queue.Items.LastOrDefault(i => string.Equals(i.OriginalSourcePath, row.FullPath, StringComparison.OrdinalIgnoreCase)) is { Status: ConversionQueueStatus.Failed } item)
            { failures[row] = item.Error ?? "falha na conversão"; row.HasOperationError = true; }
        return failures;
    }

    private async Task<int> ExportAutopilotPdfsAsync(IReadOnlyList<MidiRow> rows, string folder, CancellationToken token)
    {
        var sources = rows.Select(BatchPdfCandidate).Where(s => s.BlockedReason is null).ToList();
        if (sources.Count == 0) return 0;
        var report = await new BatchPdfExportService(MuseScorePath, PlaybackCacheDirectory)
            .ExportAsync(sources, folder, new Progress<BatchPdfProgress>(_ => { }), token, directlyIntoDestination: true);
        return report.SavedCount;
    }

    internal Func<Window, bool?> autopilotReportDialog = window => window.ShowDialog();

    private void ShowAutopilotReport(IReadOnlyList<AutopilotOutcome> outcomes)
    {
        var text = new System.Text.StringBuilder();
        foreach (string group in new[] { "Prontas", "Para revisar", "Precisam de você", "Falharam" })
        {
            var items = outcomes.Where(o => o.Group == group).ToList();
            if (items.Count == 0) continue;
            text.AppendLine($"{group} ({items.Count})");
            foreach (var item in items) text.AppendLine($"  • {(string.IsNullOrWhiteSpace(item.Row.ScoreTitle) ? item.Row.Name : item.Row.ScoreTitle)} — {item.Detail}");
            text.AppendLine();
        }
        var list = new TextBox { Text = text.ToString().TrimEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 220, Name = "AutopilotReportText" };
        AutomationProperties.SetName(list, "Resultado do piloto automático");
        var window = SimpleDialog("Resultado do piloto automático", "As partituras \"Para revisar\" aparecem primeiro na lista inteligente Revisar primeiro e na Revisão em sequência.",
            outcomes.Any(o => o.Group == "Para revisar" || o.Group == "Prontas") ? "Revisar em sequência" : "Fechar", list);
        window.Height = 520;
        if (autopilotReportDialog(window) == true && outcomes.Any(o => o.Row.ScorePath is not null)) ReviewSequenceClick(this, new RoutedEventArgs());
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
