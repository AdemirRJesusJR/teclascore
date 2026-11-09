using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Mesa de triagem (1.9.0): the library as a table, the saved preference, columns, sort, filters, keyboard, the batch
    // actions on the marked songs and the queue strip with its estimate and the end-of-run export. No engine or MuseScore runs.
    private async void VerifyV190TriageUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v190-triage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v1.90 triage smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task<bool> WaitFor(Func<bool> condition, double seconds = 20)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) { await Task.Delay(25); Flush(); }
            return condition();
        }
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Environment.ProcessPath!, MuseScorePath = Environment.ProcessPath!, ConversionParallelism = 2 }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1360, Height = 900 };
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new SmokeQueueWorker(gate.Task);
            window.conversionQueueWorker = worker;
            // The 1.7.0 batch protections show "O que vai acontecer" before Gerar and Excluir; the smoke answers it.
            window.batchPreviewDialog = _ => true;
            window.Show(); Flush();
            window.HandleShortcut(Key.D2, ModifierKeys.Control, null); Flush();
            T Named<T>(string name) where T : class => (T)window.FindName(name);
            string score = Path.Combine(work, "pronta.mscz"); WriteVerseScore(score);
            string Copy(string name) { string target = Path.Combine(work, name + ".mscz"); File.Copy(score, target, true); return target; }
            var rows = new List<MidiRow>();
            foreach (var (name, shift) in new[] { ("Aviões do Forró - Inquilina", 1), ("Os Barões - Chupadinha", 2), ("O bebê", 3), ("Sexta-feira rotineira", 4), ("Mamãe que é mamãe", 5) })
            {
                string path = Path.Combine(work, name + ".mid"); File.WriteAllBytes(path, TwoPartMidi(shift));
                window.ImportForSmoke(path); Flush(); rows.Add(window.Files.Single(r => r.FullPath == path));
            }
            // r0: a ready score (good chords); r1: a doubtful score; r2: melody chosen, no score; r3: untouched; r4: failed.
            // No song open: the first import stays selected and its draft observer would mark the chords below as pending edits.
            ((ListBox)window.FindName("Library")).SelectedItem = null; Flush();
            // The melody goes first: changing it invalidates the analysis (chords cleared, score pending), like in the app.
            foreach (var t in rows[0].Tracks) t.IsMelody = t.Source.Key.Track == 1;
            rows[0].ScorePath = Copy("inquilina"); rows[0].PageCount = 3; rows[0].GeneratedAt = DateTimeOffset.Now; rows[0].KeyFifths = 1; rows[0].HasPendingScoreProperties = false;
            rows[0].Chords.Add(new ChordRow { Root = "G", Quality = "", Confidence = .95 }); rows[0].Chords.Add(new ChordRow { Root = "D", Quality = "7", Confidence = .9 });
            foreach (var t in rows[1].Tracks) t.IsMelody = t.Source.Key.Track == 1;
            rows[1].ScorePath = Copy("chupadinha"); rows[1].PageCount = 2; rows[1].GeneratedAt = DateTimeOffset.Now; rows[1].KeyFifths = -1; rows[1].KeyMinor = true; rows[1].HasPendingScoreProperties = false;
            foreach (string root in new[] { "D", "G", "A", "C" }) rows[1].Chords.Add(new ChordRow { Root = root, Quality = "m", Confidence = .3, Alternatives = root + "7" });
            foreach (var t in rows[2].Tracks) t.IsMelody = t.Source.Key.Track == 1;
            rows[0].HasPendingScoreProperties = false; rows[1].HasPendingScoreProperties = false;
            rows[4].HasOperationError = true; rows[4].Status = "MuseScore recusou a partitura";
            window.RefreshAllConfidence(); Flush();

            // 1. Cards by default; the toggle in the list bar opens the table, hides the cards and the editor, and is saved.
            var cards = Named<Border>("LibraryCardsPanel"); var table = Named<Border>("LibraryTablePanel"); var grid = Named<DataGrid>("LibraryTable");
            Check(!window.IsLibraryTableView && cards.Visibility == Visibility.Visible && table.Visibility == Visibility.Collapsed, "starts-with-the-cards");
            Named<ToggleButton>("LibraryTableToggle").IsChecked = true; Flush();
            Check(window.IsLibraryTableView && table.Visibility == Visibility.Visible && cards.Visibility == Visibility.Collapsed && window.ProgressPanelVisibility == Visibility.Collapsed, "toggle-opens-the-table");
            window.SaveSessionForSmoke();
            Check(Settings.Load(settingsPath).LibraryTableView, "table-preference-is-saved");

            // 2. Columns and values.
            grid.UpdateLayout(); Flush();
            Check(grid.Columns.Count == 7 && grid.Items.Count == 5 && window.TriageRows.Count == 5, $"table-lists-every-song columns={grid.Columns.Count} items={grid.Items.Count}");
            Check(rows[2].MelodyChoiceText.StartsWith("Pista 2") && rows[3].MelodyChoiceText == "—" && rows[0].KeyText == "G" && rows[1].KeyText == "Dm" && rows[0].PagesText == "3" && rows[1].PagesText == "2",
                $"melody-key-and-pages-columns melody={rows[2].MelodyChoiceText} key0={rows[0].KeyText} key1={rows[1].KeyText}");
            Check(rows[0].TriageStatus == "Pronta" && rows[0].ConfidenceText.EndsWith("%") && rows[0].ConfidenceScore >= 85, $"ready-score-is-pronta {rows[0].TriageStatus} {rows[0].ConfidenceText}");
            Check(rows[1].TriageStatus == "Para revisar" && rows[1].TriageReason.Contains("acorde", StringComparison.OrdinalIgnoreCase) && rows[1].ConfidenceScore < 85, $"doubtful-score-is-para-revisar {rows[1].TriageStatus} {rows[1].ConfidenceScore} {rows[1].TriageReason}");
            Check(rows[2].TriageStatus == "Para gerar" && rows[2].TriageReason.Contains("melodia escolhida"), $"chosen-melody-without-score-is-para-gerar {rows[2].TriageStatus} {rows[2].TriageReason}");
            Check(rows[3].TriageStatus is "Precisa de você" or "Para gerar" && rows[3].TriageReason.Length > 0, $"untouched-song-has-a-verdict {rows[3].TriageStatus} {rows[3].TriageReason}");
            Check(rows[4].TriageStatus == "Falhou" && rows[4].TriageReason.Contains("MuseScore recusou"), $"failed-song-is-falhou {rows[4].TriageStatus} {rows[4].TriageReason}");

            // 3. Sorting by column and the status filters shared with the cards.
            var byConfidence = window.SortTriage(nameof(MidiRow.ConfidenceScore), descending: true);
            Check(byConfidence[0] == rows[0] && byConfidence[1] == rows[1], "sort-by-confidence-descending");
            var byTitle = window.SortTriage(nameof(MidiRow.DisplayTitle));
            Check(byTitle.Select(r => r.DisplayTitle).SequenceEqual(byTitle.Select(r => r.DisplayTitle).OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)), "sort-by-title");
            var bySituation = window.SortTriage(nameof(MidiRow.TriageOrder));
            Check(bySituation.Select(r => r.TriageOrder).SequenceEqual(bySituation.Select(r => r.TriageOrder).OrderBy(o => o)) && grid.Columns.Count(c => c.SortDirection is not null) == 1, "sort-by-situation-marks-one-header");
            window.CurrentFilter = "error"; Flush();
            Check(window.TriageRows.Count == 1 && window.TriageRows[0] == rows[4], "status-filter-applies-to-the-table");
            window.CurrentFilter = "all"; window.SearchText = "Chupadinha"; Flush();
            Check(window.TriageRows.Count == 1 && window.TriageRows[0] == rows[1], "search-applies-to-the-table " + window.TriageRows.Count);
            window.SearchText = ""; Flush();
            Check(window.TriageRows.Count == 5, "clearing-the-search-brings-every-song-back");

            // 4. Keyboard: Space marks, Enter opens (back to the cards), the header box marks all the filtered rows.
            grid.Focus(); grid.SelectedIndex = 0; grid.CurrentItem = grid.Items[0]; Flush();
            var source = PresentationSource.FromVisual(window)!;
            void Press(Key key) { grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = UIElement.PreviewKeyDownEvent }); Flush(); }
            var first = (MidiRow)grid.Items[0]!;
            Press(Key.Space);
            Check(first.IsChecked && window.TriageSelectionText == "1 marcada" && window.TriageAllChecked is null, "space-marks-the-current-row " + window.TriageSelectionText);
            Press(Key.Space);
            Check(!first.IsChecked && window.TriageAllChecked == false, "space-again-unmarks");
            window.TriageAllChecked = true; Flush();
            Check(window.Files.All(r => r.IsChecked) && window.TriageSelectAllText == "Limpar seleção" && window.TriageSelectionText == "5 marcadas", "header-box-marks-all-the-filtered " + window.TriageSelectAllText);
            window.CurrentFilter = "generated"; Flush();
            Check(window.TriageRows.Count == 2 && window.TriageSelectAllText == "Limpar seleção", "filtered-rows-all-marked");
            window.SetAllTriageChecks(false); window.CurrentFilter = "all"; Flush();
            Check(window.Files.Count(r => r.IsChecked) == 3 && window.TriageSelectAllText.StartsWith("Selecionar"), "clearing-only-touches-the-filtered-rows " + window.TriageSelectAllText);
            window.SetAllTriageChecks(false); Flush();
            grid.SelectedIndex = grid.Items.IndexOf(rows[2]); grid.CurrentItem = rows[2]; Flush();
            Press(Key.Enter);
            Check(!window.IsLibraryTableView && window.SelectedFile == rows[2] && cards.Visibility == Visibility.Visible, "enter-opens-the-song-in-the-cards");
            window.IsLibraryTableView = true; Flush();

            // 5. Batch actions on the marked songs.
            var queued = new List<MidiRow>(); int proposed = -1;
            window.melodyBatchDialog = dialog => { proposed = dialog.Rows.Count; foreach (var row in dialog.Rows) row.Selected = true; return true; };
            window.melodyBatchEnqueue = (row, _) => { queued.Add(row); return Task.CompletedTask; };
            window.ToggleLibraryCheck(rows[2]); window.ToggleLibraryCheck(rows[3]); Flush();
            Named<Button>("TriageGenerateButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(await WaitFor(() => queued.Count > 0 && window.IsIdle), "generate-finished");
            Check(queued.Contains(rows[2]) && proposed >= 1 && window.Status.Contains("Fila de conversões"), $"generate-the-marked-songs proposed={proposed} queued={queued.Count} {window.Status}");
            window.melodyBatchEnqueue = null;
            window.SetAllTriageChecks(false); window.ToggleLibraryCheck(rows[0]); window.ToggleLibraryCheck(rows[1]); window.ToggleLibraryCheck(rows[2]); Flush();
            string? asked = null; window.approveReadyConfirm = message => { asked = message; return true; };
            Named<Button>("TriageApproveButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(rows[0].Approved && !rows[1].Approved && asked is not null && asked.Contains("1 partitura") && rows[0].TriageStatus == "Pronta" && rows[0].ConfidenceText == "✓",
                $"approve-only-the-ready-ones approved0={rows[0].Approved} approved1={rows[1].Approved} asked={asked is not null}");
            window.SetAllTriageChecks(false); window.ToggleLibraryCheck(rows[1]); Flush();
            Named<Button>("TriageApproveButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(!rows[1].Approved && window.Status.Contains("85%"), "approve-explains-when-nothing-qualifies " + window.Status);
            var exported = new List<string>();
            window.batchPdfSelectionDialog = _ => throw new InvalidOperationException("A janela de escolha não deveria abrir.");
            window.batchPdfFolderOverride = () => Task.FromResult<string?>(Path.Combine(work, "pdfs"));
            Directory.CreateDirectory(Path.Combine(work, "pdfs"));
            window.batchPdfExport = (from, target, _, _) => { exported.Add(from); File.WriteAllBytes(target, System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 " + new string('x', 150) + " %%EOF")); return Task.CompletedTask; };
            window.SetAllTriageChecks(false); window.ToggleLibraryCheck(rows[0]); window.ToggleLibraryCheck(rows[1]); window.ToggleLibraryCheck(rows[3]); Flush();
            Named<Button>("TriagePdfsButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(await WaitFor(() => exported.Count >= 2 && window.IsIdle), "pdfs-finished");
            Check(exported.Count == 2 && Directory.GetFiles(Path.Combine(work, "pdfs"), "*.pdf").Length == 2, "pdfs-of-the-marked-songs-with-score " + exported.Count);
            var regenerated = new List<MidiRow>();
            window.reviewRegenerate = row => { regenerated.Add(row); row.ScorePath = Copy("refeita-" + regenerated.Count); return Task.CompletedTask; };
            var melodyBefore = rows[1].Tracks.Single(t => t.IsMelody).Source.Key;
            window.SetAllTriageChecks(false); window.ToggleLibraryCheck(rows[1]); window.ToggleLibraryCheck(rows[4]); Flush();
            Named<Button>("TriageRedoButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(await WaitFor(() => regenerated.Count > 0 && window.IsIdle), "redo-finished");
            // The failed song has another candidate too, so both marked songs are redone.
            Check(regenerated.Count == 2 && regenerated.Contains(rows[1]) && regenerated.Contains(rows[4]) && rows[1].Tracks.Single(t => t.IsMelody).Source.Key != melodyBefore && !rows[1].AutoMelody
                && window.Status.Contains("2 música(s) refeita(s)"), "redo-moves-the-marked-songs-to-the-next-melody " + window.Status);
            window.SetAllTriageChecks(false); window.ToggleLibraryCheck(rows[0]); Flush();
            Named<Button>("TriageRedoButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(await WaitFor(() => window.IsIdle && window.Status.Contains("aprovada")), "redo-skips-the-approved-score " + window.Status);
            Check(regenerated.Count == 2 && rows[0].Approved, "approved-score-was-not-redone");
            window.reviewRegenerate = null;
            BatchPreviewWindow? removePreview = null; window.batchPreviewDialog = dialog => { removePreview = dialog; return false; };
            window.SetAllTriageChecks(false); window.ToggleLibraryCheck(rows[3]); window.ToggleLibraryCheck(rows[0]); Flush();
            Named<Button>("TriageRemoveButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(window.Files.Count == 5 && removePreview is not null, "remove-asks-first-and-respects-no");
            window.batchPreviewDialog = _ => true;
            Named<Button>("TriageRemoveButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(await WaitFor(() => window.Files.Count == 4), "remove-finished");
            Check(!window.Files.Contains(rows[3]) && window.Files.Contains(rows[0]) && rows[0].Approved && window.CanUndoLibraryRemoval && window.TriageRows.Count == 4 && window.Status.Contains("aprovada"),
                "remove-takes-the-marked-song-out-and-keeps-the-approved-one " + window.Status);
            window.UndoLibraryRemovalForSmoke(); Flush();
            Check(window.Files.Count == 5 && window.Files.Contains(rows[3]), "undo-brings-it-back");
            window.IsLibraryTableView = true; Flush();

            // 6. The queue strip: counts, estimate from the measured history, pause/resume and the export at the end.
            Check(window.QueueStripVisibility == Visibility.Collapsed, "strip-hidden-without-a-queue");
            window.queueSecondsPerSongOverride = () => 120;
            worker.Slow = rows[2].FullPath;
            window.SetAllTriageChecks(false); window.ToggleLibraryCheck(rows[2]); window.ToggleLibraryCheck(rows[3]); Flush();
            foreach (var t in rows[3].Tracks) t.IsMelody = t.Source.Key.Track == 1;
            window.ExportWhenQueueEnds = true;
            int pdfRun = 0; window.autopilotPdf = (made, folder, _) => { pdfRun = made.Count; return Task.FromResult(made.Count); };
            Named<Button>("TriageGenerateButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(await WaitFor(() => window.IsBackgroundQueueRunning && window.QueueStripVisibility == Visibility.Visible), "strip-appears-with-the-running-queue");
            Check(await WaitFor(() => window.QueueStripText.Contains("em andamento") && window.QueueStripText.Contains("de 2")), "strip-counts-the-run " + window.QueueStripText);
            Check(window.QueueStripText.Contains("termina em") && Named<Button>("QueueStripPauseButton").IsEnabled && !Named<Button>("QueueStripResumeButton").IsEnabled, "strip-estimates-the-end " + window.QueueStripText);
            CaptureElement(table, Path.Combine(reports, "mesa-de-triagem.png"));
            CaptureElement((FrameworkElement)window.Content, Path.Combine(reports, "mesa-de-triagem-janela.png"));
            Check(await WaitFor(() => rows[3].ScorePath is not null), "fast-song-finishes-while-the-slow-one-runs");
            Named<Button>("QueueStripPauseButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            gate.SetResult();
            Check(await WaitFor(() => !window.IsBackgroundQueueRunning), "pause-stops-the-queue");
            Check(window.QueueStripVisibility == Visibility.Visible && window.QueueStripText.StartsWith("Fila pausada") && window.CanResumeQueue, "paused-strip-offers-resume " + window.QueueStripText);
            Check(window.LastQueueRunExport is null, "no-export-while-paused");
            int resumed = await window.ResumeQueueAsync(); Flush();
            Check(resumed >= 1, "resume-brings-the-paused-items-back " + resumed);
            Check(await WaitFor(() => !window.IsBackgroundQueueRunning && window.LastQueueRunExport is not null, 30), "queue-finishes-and-exports " + window.Status);
            string export = window.LastQueueRunExport!;
            Check(File.Exists(Path.Combine(export, "relatorio-do-lote.csv")) && File.Exists(Path.Combine(export, "relatorio-do-lote.json")) && pdfRun >= 1 && window.Status.Contains("relatório"),
                $"end-of-run-report-and-pdfs pdfs={pdfRun} {window.Status}");
            string csv = File.ReadAllText(Path.Combine(export, "relatorio-do-lote.csv"));
            Check(csv.StartsWith("﻿Título;Intérprete;Situação") && csv.Contains("Pronta"), "report-lists-the-songs-with-their-situation");
            Check(window.QueueStripVisibility == Visibility.Collapsed, "strip-hides-when-the-queue-is-done");
            grid.UpdateLayout(); Flush();
            Check(grid.Items.Count == window.Files.Count && window.TriageRows.Distinct().Count() == window.Files.Count, $"table-never-duplicates-rows items={grid.Items.Count} files={window.Files.Count}");
            Check(Teclas.App.MainWindow.KeyboardShortcuts.Any(s => s.Action.Contains("Mesa de triagem")), "shortcut-list-mentions-the-table");
            Check(ConversionQueueEstimate.Strip(3, 12, 40, 9 * 60) == "3 em andamento · 12 de 40 · termina em ~9 min", "strip-text-format");

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
