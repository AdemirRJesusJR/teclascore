using System.IO;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// The work behind <c>--gerar</c>: the MIDIs join the library like a normal import, the pending ones go through the same
/// autopilot (no confirmation and no report window), PDFs are saved when asked, and every file gets a line in the report.
/// </summary>
public partial class MainWindow
{
    internal async Task<IReadOnlyList<BatchFileReport>> RunCommandLineBatchAsync(BatchCommandLineOptions options, IReadOnlyList<string> midis, Action<string> log)
    {
        string previousOutput = settings.OutputDirectory;
        void StatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(Status)) log(Status); }
        PropertyChanged += StatusChanged;
        try
        {
            if (options.OutputDirectory is { } output)
            {
                if (!Settings.TryValidateOutputDirectory(output, out string normalized, out string error)) throw new InvalidOperationException("--saida: " + error);
                // Only this run: the default folder in Configurações is restored before saving.
                settings.OutputDirectory = normalized;
            }
            if (options.Simulate) return SimulateCommandLineBatch(midis, log);
            log($"Importando {midis.Count} MIDI(s) para a biblioteca…");
            var imports = await ImportQuietlyAsync(midis);
            if (imports.Any(i => i.Added)) { PersistPreferences(); RefreshDashboard(); }
            var rows = imports.Where(i => i.Row is not null).Select(i => i.Row!).Distinct().ToList();
            var candidates = AutopilotCandidates();
            var pending = rows.Where(candidates.Contains).ToList();
            bool needsTools = pending.Count > 0 || options.PdfDirectory is not null && rows.Any(r => r.ScorePath is not null);
            if (needsTools && autopilotGenerate is null)
            {
                if (!File.Exists(PythonPath)) throw new InvalidOperationException("Python com Pillow não encontrado. Abra o TeclaScore e use Configurações › Preparar este computador.");
                if (!File.Exists(MuseScorePath)) throw new InvalidOperationException("MuseScore Studio 4 não encontrado. Abra o TeclaScore e localize-o em Configurações.");
            }
            var outcomes = new Dictionary<MidiRow, AutopilotOutcome>();
            if (pending.Count > 0)
            {
                log($"Piloto automático: {pending.Count} música(s) sem partitura.");
                foreach (var outcome in await RunAutopilotAsync(pending, exportPdf: false, keepAwake: true, quiet: true)) outcomes[outcome.Row] = outcome;
            }
            var pdfs = new Dictionary<MidiRow, (string? Path, string? Error)>();
            if (options.PdfDirectory is { } pdfFolder)
            {
                var withScore = rows.Where(r => r.ScorePath is not null && (!outcomes.TryGetValue(r, out var o) || o.Group is "Prontas" or "Para revisar")).ToList();
                if (withScore.Count > 0)
                {
                    log($"Salvando {withScore.Count} PDF(s) em {pdfFolder}…");
                    foreach (var (row, result) in await ExportCommandLinePdfsAsync(withScore, pdfFolder)) pdfs[row] = result;
                }
            }
            var report = new List<BatchFileReport>();
            foreach (var import in imports)
            {
                var row = import.Row;
                string? title = row is null ? null : string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle;
                var (pdf, pdfError) = row is not null && pdfs.TryGetValue(row, out var exported) ? exported : (null, null);
                string? note = import.Note.Length > 0 ? import.Note : null;
                if (row is null) report.Add(BatchFileReport.Failure(import.Path, note ?? "não foi possível importar"));
                else if (row.Document is null && !row.UsesNativeScoreWorkflow) report.Add(new(import.Path, BatchFileReport.Failed, title, null, null, row.Status));
                else if (outcomes.TryGetValue(row, out var outcome))
                    report.Add(new(import.Path, outcome.Group switch
                    {
                        "Prontas" => BatchFileReport.Ready, "Para revisar" => BatchFileReport.Review,
                        "Precisam de você" => BatchFileReport.NeedsYou, _ => BatchFileReport.Failed
                    }, title, row.ScorePath, pdf, Join(outcome.Group == "Prontas" ? $"confiança {outcome.Detail}" : outcome.Detail, note), pdfError));
                else if (row.ScorePath is not null)
                    report.Add(new(import.Path, BatchFileReport.AlreadyGenerated, title, row.ScorePath, pdf,
                        import.Added || note == AlreadyInLibrary ? "a partitura já existia na biblioteca" : Join("a partitura já existia na biblioteca", note), pdfError));
                else report.Add(new(import.Path, BatchFileReport.NeedsYou, title, null, null, Join(row.UsesNativeScoreWorkflow
                    ? "partitura com edições musicais: continue no Editor musical" : row.Status, note)));
            }
            return report;
        }
        finally
        {
            PropertyChanged -= StatusChanged;
            settings.OutputDirectory = previousOutput;
            PersistPreferences();
        }
        static string? Join(string? first, string? second) => string.Join(" · ", new[] { first, second }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } text ? text : null;
    }

    /// <summary>
    /// <c>--simular</c>: the same "O que vai acontecer" of the window, printed, without importing or generating anything.
    /// Files already in the library are judged as they are; new ones are read in memory only.
    /// </summary>
    internal IReadOnlyList<BatchFileReport> SimulateCommandLineBatch(IReadOnlyList<string> midis, Action<string> log)
    {
        var items = new List<BatchPreviewItem>(); var reports = new List<BatchFileReport>();
        foreach (string path in midis)
        {
            string full = Path.GetFullPath(path);
            var row = Files.FirstOrDefault(f => string.Equals(f.FullPath, full, StringComparison.OrdinalIgnoreCase));
            bool inLibrary = row is not null;
            if (row is null)
            {
                try { row = LoadMidi(full); }
                catch (Exception error) when (error is IOException or InvalidDataException or OverflowException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { items.Add(new(full, Path.GetFileName(full), BatchPreview.Skip, "não foi possível ler: " + error.Message, false)); continue; }
                if (row.SourceSha256 is { } sha && Files.FirstOrDefault(f => string.Equals(f.SourceSha256, sha, StringComparison.OrdinalIgnoreCase)) is { } same) { row = same; inLibrary = true; }
            }
            string title = SongTitle(row) + (inLibrary ? "" : " (novo na biblioteca)");
            if (row.ScorePath is not null) items.Add(new(full, title, BatchPreview.AlreadyGenerated, row.Approved ? "aprovada" : "", false));
            else if (row.UsesNativeScoreWorkflow) items.Add(new(full, title, BatchPreview.Skip, "partitura importada ou editada: continue no Editor musical", false));
            else if (row.Document is null) items.Add(new(full, title, BatchPreview.Skip, row.Status, false));
            else if (PlanAutopilotMelody(row, apply: false) is { } reason) items.Add(new(full, title, BatchPreview.NeedsYou, reason, false));
            else items.Add(new(full, title, BatchPreview.Generate, row.HasMelodyChoice || row.UseReviewedProfile ? "melodia já escolhida" : "melodia clara", true));
        }
        var preview = BuildBatchPreview("--gerar (simulação)", items);
        foreach (string line in preview.ToText().Split('\n')) log(line.TrimEnd('\r'));
        log("Simulação: nada foi importado nem gerado. Sem --simular, um ponto de restauração é guardado antes de gerar.");
        foreach (var item in items) reports.Add(new(item.Id, BatchFileReport.Simulated, item.Title, null, null, item.Detail.Length == 0 ? item.Group : item.Group + " · " + item.Detail));
        return reports;
    }

    /// <summary>One PDF per score, straight into the folder; returns where each one went or why it did not.</summary>
    private async Task<IReadOnlyList<(MidiRow Row, (string? Path, string? Error) Result)>> ExportCommandLinePdfsAsync(IReadOnlyList<MidiRow> rows, string folder)
    {
        if (!Settings.TryValidateOutputDirectory(folder, out string destination, out string error)) throw new InvalidOperationException("--pdf: " + error);
        var sources = rows.Select(BatchPdfCandidate).ToList();
        var service = batchPdfExport is { } export ? new BatchPdfExportService(export, PlaybackCacheDirectory) { MaxParallel = PdfWorkers } : new BatchPdfExportService(MuseScorePath, PlaybackCacheDirectory) { MaxParallel = PdfWorkers };
        var report = await service.ExportAsync(sources, destination, new Progress<BatchPdfProgress>(_ => { }), CancellationToken.None, directlyIntoDestination: true);
        return rows.Select(row => (row, report.Items.FirstOrDefault(i => i.Id == row.FullPath) is { } item
            ? item.Status == BatchPdfStatus.Success ? (item.PdfPath, (string?)null) : ((string?)null, (string?)item.Message)
            : ((string?)null, (string?)"o PDF não foi exportado"))).ToList();
    }
}
