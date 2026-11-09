using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Mesa de triagem: the library as a dense table (Música, Melodia escolhida, Tom, Páginas, Confiança, Situação) for
/// libraries with hundreds of MIDIs. It shares the search and the status filters with the card list, marks songs with
/// check boxes (Space) and runs the batch actions on the marked ones: Gerar, Aprovar as ≥85%, PDFs, Refazer com outra
/// melodia and Excluir. A fixed strip at its foot follows the background queue ("3 em andamento · 12 de 40 · termina
/// em ~9 min") with Pausar/Retomar and the option to export PDFs and a report when the queue ends.
/// </summary>
public partial class MainWindow
{
    private ListCollectionView? libraryTableView;
    private string triageSortMember = nameof(MidiRow.TriageOrder);
    private ListSortDirection triageSortDirection = ListSortDirection.Ascending;
    private double? queueSecondsPerSong;
    private DateTime queueSecondsPerSongStamp;
    /// <summary>Test hook: seconds one song takes in the queue (instead of the recorded history).</summary>
    internal Func<double>? queueSecondsPerSongOverride = null;
    /// <summary>The folder of the last "ao terminar" export (PDFs and report), for the smokes and the status.</summary>
    internal string? LastQueueRunExport { get; private set; }

    // ---- Table or cards ------------------------------------------------------------------------------------------------

    /// <summary>The library is shown as the triage table instead of the card list (saved in the settings).</summary>
    public bool IsLibraryTableView
    {
        get => settings.LibraryTableView;
        set
        {
            if (settings.LibraryTableView == value) return;
            settings.LibraryTableView = value;
            if (value) { RefreshAllConfidence(); libraryTableView?.Refresh(); }
            Notify(); Notify(nameof(LibraryTableVisibility)); Notify(nameof(LibraryCardsVisibility)); Notify(nameof(LibraryViewToggleToolTip)); Notify(nameof(ProgressPanelVisibility));
            NotifyTriageSelection();
            PersistPreferences();
            if (value && LibraryTable is { } table) Dispatcher.BeginInvoke(() => { if (table.Items.Count > 0 && table.SelectedIndex < 0) table.SelectedIndex = 0; table.Focus(); });
        }
    }

    public Visibility LibraryTableVisibility => IsLibraryTableView ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LibraryCardsVisibility => IsLibraryTableView ? Visibility.Collapsed : Visibility.Visible;
    public string LibraryViewToggleToolTip => IsLibraryTableView
        ? "Voltar à lista de cards (uma música por vez, com o editor ao lado)"
        : "Mesa de triagem: a biblioteca inteira numa tabela, com seleção e ações em massa (Gerar, Aprovar, PDFs, Refazer, Excluir)";

    /// <summary>The rows of the table: the same songs and filters as the card list, in the order of the chosen column.</summary>
    public ICollectionView LibraryTableView
    {
        get
        {
            if (libraryTableView is null)
            {
                libraryTableView = new ListCollectionView(Files);
                libraryTableView.Filter = item => item is MidiRow row && MatchesLibraryFilter(row);
                ApplyTriageSort(triageSortMember, triageSortDirection);
            }
            return libraryTableView;
        }
    }

    private void RefreshLibraryTable() { if (libraryTableView is { } view && !view.IsEditingItem) view.Refresh(); }

    private void ApplyTriageSort(string member, ListSortDirection direction)
    {
        if (libraryTableView is null) return;
        triageSortMember = member; triageSortDirection = direction;
        using (libraryTableView.DeferRefresh())
        {
            libraryTableView.SortDescriptions.Clear();
            libraryTableView.SortDescriptions.Add(new SortDescription(member, direction));
            if (member != nameof(MidiRow.DisplayTitle)) libraryTableView.SortDescriptions.Add(new SortDescription(nameof(MidiRow.DisplayTitle), ListSortDirection.Ascending));
        }
    }

    private void LibraryTableSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (sender is not DataGrid grid || string.IsNullOrEmpty(e.Column.SortMemberPath)) return;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in grid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        ApplyTriageSort(e.Column.SortMemberPath, direction);
    }

    /// <summary>Sorts the table by a column (smokes and the command palette); returns the rows in the new order.</summary>
    internal IReadOnlyList<MidiRow> SortTriage(string member, bool descending = false)
    {
        ApplyTriageSort(member, descending ? ListSortDirection.Descending : ListSortDirection.Ascending);
        if (LibraryTable is { } grid)
        {
            foreach (var column in grid.Columns) column.SortDirection = column.SortMemberPath == member ? (descending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
        }
        return TriageRows;
    }

    /// <summary>The songs the table shows right now (after the search, the filters and the sort).</summary>
    internal IReadOnlyList<MidiRow> TriageRows => LibraryTableView.Cast<MidiRow>().ToArray();

    private void LibraryTableLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        foreach (var column in grid.Columns) column.SortDirection = column.SortMemberPath == triageSortMember ? triageSortDirection : null;
    }

    // ---- Marks (check boxes) and keyboard ----------------------------------------------------------------------------------

    /// <summary>Header check box: all filtered rows marked (true), none (false) or some (null).</summary>
    public bool? TriageAllChecked
    {
        get
        {
            var rows = TriageRows;
            if (rows.Count == 0) return false;
            int marked = rows.Count(row => row.IsChecked);
            return marked == 0 ? false : marked == rows.Count ? true : null;
        }
        set => SetAllTriageChecks(value == true);
    }

    public string TriageSelectionText => CheckedRows.Count switch { 0 => "Nenhuma marcada", 1 => "1 marcada", var n => $"{n} marcadas" };
    public string TriageSelectAllText => TriageAllChecked == true ? "Limpar seleção" : TriageRows.Count == Files.Count ? "Selecionar todas" : $"Selecionar as {TriageRows.Count} filtradas";
    public bool HasTriageSelection => CheckedRows.Count > 0;

    /// <summary>Marks or clears every row of the current filter (the hidden ones keep their marks).</summary>
    internal void SetAllTriageChecks(bool value)
    {
        foreach (var row in TriageRows) row.IsChecked = value;
        NotifyLibrarySelection();
    }

    private void NotifyTriageSelection()
    {
        foreach (string name in new[] { nameof(TriageAllChecked), nameof(TriageSelectionText), nameof(TriageSelectAllText), nameof(HasTriageSelection) }) Notify(name);
    }

    private void TriageSelectAllClick(object sender, RoutedEventArgs e) => SetAllTriageChecks(TriageAllChecked != true);

    private void LibraryTableKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is TextBox) return;
        var row = grid.CurrentItem as MidiRow ?? grid.SelectedItem as MidiRow;
        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Space when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                if (row is not null) { ToggleLibraryCheck(row, modifiers == ModifierKeys.Shift); e.Handled = true; }
                break;
            case Key.Enter when modifiers == ModifierKeys.None:
                if (row is not null && IsIdle) { OpenTriageRow(row); e.Handled = true; }
                break;
            case Key.A when modifiers == ModifierKeys.Control:
                SetAllTriageChecks(true); e.Handled = true; break;
            case Key.Escape when CheckedRows.Count > 0:
                SetAllTriageChecks(false); e.Handled = true; break;
            case Key.Delete when modifiers == ModifierKeys.None && CheckedRows.Count > 0:
                TriageRemoveClick(sender, e); e.Handled = true; break;
        }
    }

    private void LibraryTableDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && Ancestor<DataGridRow>(source) is { DataContext: MidiRow row } && IsIdle) { OpenTriageRow(row); e.Handled = true; }
    }

    private void LibraryTableRowClick(object sender, MouseButtonEventArgs e)
    {
        // A click on the row (outside its check box) with Ctrl marks it, like Ctrl+click on a card.
        if (e.OriginalSource is not DependencyObject source || Ancestor<CheckBox>(source) is not null) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && Ancestor<DataGridRow>(source) is { DataContext: MidiRow row }) { ToggleLibraryCheck(row); e.Handled = true; }
    }

    /// <summary>Enter or a double click: back to the cards with the song open in the editor.</summary>
    internal void OpenTriageRow(MidiRow row)
    {
        if (!Files.Contains(row) || !CommitEdits()) return;
        IsLibraryTableView = false;
        SetCurrentPage("library");
        Library.SelectedItem = row; Library.ScrollIntoView(row); RefreshSelection();
    }

    /// <summary>A click on a row's check box: the counter, the header box and the range anchor follow it.</summary>
    private void TriageRowCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MidiRow row }) librarySelectionAnchor = row;
        NotifyLibrarySelection();
    }

    /// <summary>True when the keyboard focus is inside the triage table (its own Space and Enter win over the window's).</summary>
    private bool IsInLibraryTable(IInputElement? focus) => focus is DependencyObject node && LibraryTable is { } table && (ReferenceEquals(node, table) || Ancestor<DataGrid>(node) == table);

    // ---- Batch actions -------------------------------------------------------------------------------------------------------

    private void TriageGenerateClick(object sender, RoutedEventArgs e) => SelectedGenerateClick(sender, e);
    private void TriagePdfsClick(object sender, RoutedEventArgs e) => SelectedPdfsClick(sender, e);

    private void TriageApproveClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var rows)) return;
        var candidates = rows.Where(r => r.ScorePath is not null && !r.Approved && r.Confidence is { Score: >= 85 }).ToList();
        if (candidates.Count == 0) { Status = "Nenhuma das marcadas tem partitura com nota 85% ou mais ainda sem aprovação."; return; }
        int approved = ApproveReadyScores(rows);
        if (approved > 0) RefreshLibraryTable();
    }

    private async void TriageRedoClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var rows)) return;
        await RedoMarkedWithNextMelodyAsync(rows);
    }

    /// <summary>Refazer com outra melodia for several songs: each one moves to its next melody candidate and goes to the queue.</summary>
    internal async Task<int> RedoMarkedWithNextMelodyAsync(IReadOnlyList<MidiRow> rows)
    {
        var changed = new List<(MidiRow Row, string Melody)>(); var skipped = new List<string>();
        foreach (var row in rows)
        {
            if (IsApprovalLocked(row)) { skipped.Add(row.ScoreTitle + ": pulada · aprovada (retire a aprovação na própria música para refazê-la)"); continue; }
            if (row.UsesNativeScoreWorkflow || row.Document is null) { skipped.Add(row.ScoreTitle + ": partitura importada ou MIDI ilegível"); continue; }
            if (TryAdvanceMelody(row, out string name)) changed.Add((row, name));
            else skipped.Add(row.ScoreTitle + ": não há outra pista candidata");
        }
        if (changed.Count == 0) { Status = "Nenhuma das marcadas tem outra pista candidata a melodia." + (skipped.Count > 0 ? " " + string.Join(" · ", skipped) : ""); return 0; }
        PersistPreferences();
        int done = 0;
        if (reviewRegenerate is { } regenerate)
        {
            await RunOperation(async () =>
            {
                foreach (var (row, _) in changed) { SetOperationItems(done, changed.Count, row.ScoreTitle); await regenerate(row); RefreshConfidence(row); done++; }
            }, operationTitle: "Gerando com outra melodia");
        }
        else
        {
            var failed = new List<string>();
            await RunOperation(async () => { done = await EnqueueRowsAsync(changed.Select(c => c.Row).ToArray(), cancellation!.Token, failed); }, operationTitle: "Enfileirando com outra melodia");
            if (done > 0) StartBackgroundQueue();
            if (failed.Count > 0) skipped.AddRange(failed);
        }
        Status = $"{done} música(s) refeita(s) com a próxima melodia candidata" + (reviewRegenerate is null ? ", gerando em segundo plano." : ".")
            + (skipped.Count > 0 ? $" {skipped.Count} ficaram de fora: " + string.Join(" · ", skipped.Take(5)) + (skipped.Count > 5 ? " …" : "") : "");
        RefreshLibraryTable();
        return done;
    }

    /// <summary>Excluir: the same "O que vai acontecer" confirmation as Remover as marcadas (approved scores stay, restore point, Desfazer).</summary>
    private void TriageRemoveClick(object sender, RoutedEventArgs e)
    {
        SelectedRemoveClick(sender, e);
        RefreshLibraryTable();
    }

    // ---- Autopilot verdict per song (Precisa de você) ----------------------------------------------------------------------

    /// <summary>Fills the Situação of the songs without a score with the autopilot's verdict, without choosing anything.</summary>
    internal void RefreshTriage(MidiRow row)
    {
        if (row.ScorePath is not null || row.UsesNativeScoreWorkflow || row.Document is null) { row.TriageNeed = ""; return; }
        row.TriageNeed = ProposeAutopilotMelody(row).Reason ?? "";
    }

    // ---- Queue strip ---------------------------------------------------------------------------------------------------------

    private IReadOnlyList<ConversionQueueSummaryItem> QueueRunItems => queueSnapshot.Where(item => backgroundQueueRun.Contains(item.Id)).ToArray();

    /// <summary>"3 em andamento · 12 de 40 · termina em ~9 min", or "Fila pausada · 5 aguardando".</summary>
    public string QueueStripText
    {
        get
        {
            var run = QueueRunItems;
            int running = run.Count(i => i.Status == ConversionQueueStatus.Running);
            int done = run.Count(i => i.Status is ConversionQueueStatus.Completed or ConversionQueueStatus.Failed);
            int remaining = run.Count(i => i.Status is ConversionQueueStatus.Pending or ConversionQueueStatus.Running);
            if (!IsBackgroundQueueRunning)
            {
                int paused = queueSnapshot.Count(i => i.Status is ConversionQueueStatus.Paused or ConversionQueueStatus.Pending);
                return paused == 0 ? "" : paused == 1 ? "Fila pausada · 1 aguardando" : $"Fila pausada · {paused} aguardando";
            }
            if (backgroundQueueCancellation is { IsCancellationRequested: true }) return "Pausando a fila…";
            double eta = ConversionQueueEstimate.RemainingSeconds(QueueSecondsPerSong(), remaining, conversionQueue?.MaxParallel ?? ConversionParallelism.Resolve(settings.ConversionParallelism));
            return ConversionQueueEstimate.Strip(running, done, Math.Max(run.Count, done), eta);
        }
    }

    public string QueueStripToolTip => IsBackgroundQueueRunning
        ? "Fila em segundo plano. A previsão usa a mediana do tempo das gerações já medidas neste computador (Configurações › Desempenho) dividida pelas conversões simultâneas."
        : "Itens pausados ou aguardando na Fila de conversões. Retomar volta a gerar em segundo plano.";
    public Visibility QueueStripVisibility => IsBackgroundQueueRunning || queueSnapshot.Any(i => i.Status is ConversionQueueStatus.Paused or ConversionQueueStatus.Pending) ? Visibility.Visible : Visibility.Collapsed;
    public bool CanResumeQueue => !IsBackgroundQueueRunning && IsIdle && queueSnapshot.Any(i => i.Status is ConversionQueueStatus.Paused or ConversionQueueStatus.Pending);

    private bool exportWhenQueueEnds;
    /// <summary>"Ao terminar: exportar PDFs e relatório" — the finished run leaves its PDFs and a report in the output folder.</summary>
    public bool ExportWhenQueueEnds { get => exportWhenQueueEnds; set { if (exportWhenQueueEnds == value) return; exportWhenQueueEnds = value; Notify(); } }

    private double QueueSecondsPerSong()
    {
        if (queueSecondsPerSongOverride is { } hook) return hook();
        if (queueSecondsPerSong is { } cached && DateTime.UtcNow - queueSecondsPerSongStamp < TimeSpan.FromSeconds(30)) return cached;
        double seconds = ConversionQueueEstimate.DefaultSecondsPerSong;
        try { if (performanceRecorder is { } recorder) seconds = ConversionQueueEstimate.SecondsPerSong(recorder.ReadReport().Records); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        queueSecondsPerSong = seconds; queueSecondsPerSongStamp = DateTime.UtcNow;
        return seconds;
    }

    private void NotifyQueueStrip()
    {
        foreach (string name in new[] { nameof(QueueStripText), nameof(QueueStripToolTip), nameof(QueueStripVisibility), nameof(CanResumeQueue) }) Notify(name);
    }

    private void QueueStripPauseClick(object sender, RoutedEventArgs e) => PauseBackgroundQueue();

    private async void QueueStripResumeClick(object sender, RoutedEventArgs e) => await ResumeQueueAsync();

    /// <summary>Retomar: paused items go back to waiting and the background queue starts again.</summary>
    internal async Task<int> ResumeQueueAsync()
    {
        if (!CanResumeQueue || conversionQueue is not { } queue) return 0;
        int resumed = 0;
        foreach (var item in queue.Summaries.Where(i => i.Status == ConversionQueueStatus.Paused))
        {
            try { await queue.ResumeAsync(item.Id); resumed++; }
            catch (Exception error) when (error is InvalidOperationException or IOException) { Status = "Não foi possível retomar " + item.DisplayName + ": " + error.Message; }
        }
        StartBackgroundQueue();
        Status = resumed > 0 ? $"{resumed} música(s) retomada(s); a fila voltou a gerar em segundo plano." : "A fila voltou a gerar em segundo plano.";
        return resumed;
    }

    /// <summary>The run ended: with "ao terminar" on, the PDFs of the generated songs and a report go to the output folder.</summary>
    private void OnBackgroundQueueFinished(IReadOnlyList<ConversionQueueSummaryItem> run)
    {
        if (!ExportWhenQueueEnds || run.Count == 0 || operationsDisposed) return;
        if (run.Any(i => i.Status is ConversionQueueStatus.Pending or ConversionQueueStatus.Paused)) return; // paused: wait for the real end
        _ = ExportQueueRunAsync(run);
    }

    internal async Task<string?> ExportQueueRunAsync(IReadOnlyList<ConversionQueueSummaryItem> run)
    {
        var rows = run.Select(item => Files.FirstOrDefault(row => string.Equals(row.FullPath, item.OriginalSourcePath, StringComparison.OrdinalIgnoreCase)))
            .OfType<MidiRow>().Distinct().ToList();
        if (rows.Count == 0) return null;
        try
        {
            string folder = Path.Combine(await PrepareOutputDirectoryAsync(CancellationToken.None), "Lote " + DateTime.Now.ToString("yyyy-MM-dd HH'h'mm", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            foreach (var row in rows) RefreshConfidence(row);
            var made = rows.Where(r => r.ScorePath is { } score && File.Exists(score) && !r.HasPendingScoreProperties).ToList();
            Status = $"Lote concluído: salvando {made.Count} PDF(s) e o relatório…";
            int pdfs = made.Count == 0 ? 0 : autopilotPdf is { } export ? await export(made, folder, CancellationToken.None) : await ExportAutopilotPdfsAsync(made, folder, CancellationToken.None);
            WriteQueueRunReport(folder, rows, run);
            LastQueueRunExport = folder;
            Status = $"Lote concluído: {pdfs} PDF(s) e o relatório (relatorio-do-lote.csv) salvos em {folder}.";
            return folder;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Status = "Lote concluído, mas a exportação final falhou: " + error.Message; return null; }
    }

    private static void WriteQueueRunReport(string folder, IReadOnlyList<MidiRow> rows, IReadOnlyList<ConversionQueueSummaryItem> run)
    {
        var items = rows.Select(row =>
        {
            var item = run.LastOrDefault(i => string.Equals(i.OriginalSourcePath, row.FullPath, StringComparison.OrdinalIgnoreCase));
            return new
            {
                titulo = row.ScoreTitle, interprete = row.ScoreArtist, arquivo = row.FullPath, situacao = row.TriageStatus, motivo = row.TriageReason,
                confianca = row.Confidence?.Score, paginas = row.PageCount, tom = row.KeyText, melodia = row.MelodyChoiceText, partitura = row.ScorePath,
                fila = item?.Status.ToString(), erro = item?.Error
            };
        }).ToArray();
        File.WriteAllText(Path.Combine(folder, "relatorio-do-lote.json"), JsonSerializer.Serialize(new { geradoEm = DateTimeOffset.Now, musicas = items }, new JsonSerializerOptions { WriteIndented = true }));
        var csv = new StringBuilder("Título;Intérprete;Situação;Motivo;Confiança;Páginas;Tom;Melodia;Partitura;Arquivo MIDI\n");
        foreach (var item in items)
            csv.AppendLine(string.Join(";", new[] { item.titulo, item.interprete, item.situacao, item.motivo, item.confianca?.ToString() ?? "", item.paginas.ToString(), item.tom, item.melodia, item.partitura ?? "", item.arquivo }.Select(Csv)));
        File.WriteAllText(Path.Combine(folder, "relatorio-do-lote.csv"), csv.ToString(), new UTF8Encoding(true));
        static string Csv(string text) => text.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
