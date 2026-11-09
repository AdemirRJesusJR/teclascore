using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // 1.7.0 · protections for batch work: restore points, "O que vai acontecer", locked approvals, import duplicates and
    // the batch log. Generation is replaced by stubs: no engine or MuseScore runs.
    private async void VerifyBatchGuardUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "guard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Batch guard smoke timed out. " + window?.Status })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name + " · " + window?.Status); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        // A batch action saves its restore point (off the UI thread) before the operation turns busy: give it a moment, then wait for idle.
        async Task Idle() { for (int settle = 0; settle < 3; settle++) { await Task.Delay(150); Flush(); for (int i = 0; i < 200 && (window!.IsBusy || !window.IsIdle); i++) { await Task.Delay(50); Flush(); } } Flush(); }
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe") }.Save(settingsPath);
            // Earlier generations measured by the app: the preview's estimate comes from their median.
            Directory.CreateDirectory(Path.Combine(work, "performance"));
            var records = new[] { 20.0, 30.0, 100.0 }.Select(seconds => new PerformanceRecord(DateTimeOffset.UtcNow, PerformanceOperation.EngineGenerate, PerformancePhase.Process,
                PerformanceProcessKind.Worker, PerformanceOutcome.Success, seconds * 1000, null, null, 0, new Dictionary<PerformanceCount, long>())).ToArray();
            File.WriteAllText(Path.Combine(work, "performance", "performance-0.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            Check(new LocalPerformanceRecorder(Path.Combine(work, "performance")).ReadReport().Records.Count == 3, "seeded-performance-records-are-readable");
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            var library = (ListBox)window.FindName("Library");
            string score = Path.Combine(work, "pronta.mscz"); WriteVerseScore(score);
            string preview = Path.Combine(work, "pronta-1.png"); File.WriteAllBytes(preview, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
            var rows = new List<MidiRow>();
            foreach (var (name, shift) in new[] { ("Trio Nordestino - Forró do Xote", 1), ("Dominguinhos - Lamento Sertanejo", 2), ("Elba Ramalho - Chão de Giz", 3), ("Alceu Valença - Anunciação", 4) })
            {
                string path = Path.Combine(work, name + ".mid"); File.WriteAllBytes(path, TwoPartMidi(shift));
                window.ImportForSmoke(path); Flush(); rows.Add(window.Files.Single(r => r.FullPath == path));
            }
            // rows[0]: generated and approved (locked). rows[1]: generated, not approved. rows[2], rows[3]: pending, melody clear on the first track.
            rows[0].ScorePath = score; rows[0].PreviewPath = preview; rows[0].HasPendingScoreProperties = false; rows[0].Approved = true;
            rows[1].ScorePath = score; rows[1].HasPendingScoreProperties = false;
            foreach (var r in new[] { rows[2], rows[3] }) foreach (var t in r.Tracks) t.IsMelody = t.Source.Key.Track == 1;
            library.SelectedItem = rows[1]; Flush(); library.SelectedItem = rows[0]; Flush();

            // 1. The approval can be withdrawn only on the song itself; the menu item follows the open song.
            Check(window.CanUnapprove && window.FindName("UnapproveButton") is Button { IsEnabled: true },
                $"unapprove-is-offered-for-an-approved-song can={window.CanUnapprove} sel={window.SelectedFile == rows[0]} button={(window.FindName("UnapproveButton") as Button)?.IsEnabled}");
            library.SelectedItem = rows[1]; Flush();
            Check(!window.CanUnapprove, "unapprove-is-off-for-an-unapproved-song");
            library.SelectedItem = rows[0]; Flush();

            // 2. Piloto automático: the preview lists what will be generated, what is already done (the approved one marked) and the estimate.
            BatchPreviewWindow? shown = null;
            window.autopilotDialog = w => { shown = w as BatchPreviewWindow; if (shown is not null) { shown.ShowActivated = false; shown.Opacity = 0; shown.Show(); Flush(); CaptureElement((FrameworkElement)shown.Content, Path.Combine(reports, "batch-preview.png")); shown.Close(); } return false; };
            typeof(MainWindow).GetMethod("AutopilotClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
            await Idle();
            Check(shown is not null && shown.Preview.Count(BatchPreview.Generate) == 2 && shown.Preview.Count(BatchPreview.AlreadyGenerated) == 2
                && shown.Preview.Of(BatchPreview.AlreadyGenerated).Any(i => i.Detail == "aprovada") && shown.Preview.Items.Count == 4,
                "autopilot-preview-groups-the-songs " + (shown?.Preview.Headline() ?? "sem prévia"));
            Check(shown!.Preview.EstimatedSeconds is { } est && est > 0 && shown.Preview.EstimateSamples >= 1 && shown.Headline.Text.Contains("Vai gerar 2"),
                $"autopilot-preview-estimates-the-time est={shown.Preview.EstimatedSeconds} samples={shown.Preview.EstimateSamples} · {shown.Headline.Text}");
            Check(shown.Checks.Count == 2 && shown.Checks.Values.All(c => c.IsChecked == true), "songs-to-generate-can-be-unticked");
            Check(!rows[2].AutoMelody && !rows[3].AutoMelody, "the-preview-changes-nothing-on-the-cards");
            Check(window.ListRestorePoints().Count == 0, "cancelling-the-preview-saves-no-restore-point");

            // 3. Running the autopilot saves a restore point first, writes the batch log and reports "nada foi apagado".
            window.autopilotGenerate = (rows2, _) =>
            {
                foreach (var row in rows2)
                {
                    row.ScorePath = Path.Combine(work, Path.GetFileNameWithoutExtension(row.FullPath) + ".mscz"); File.Copy(score, row.ScorePath, true);
                    row.PageCount = 1; row.GeneratedAt = DateTimeOffset.Now; row.Chords.Clear(); row.Chords.Add(new ChordRow { Root = "C", Quality = "", Confidence = .95 });
                }
                return Task.FromResult<IReadOnlyDictionary<MidiRow, string>>(new Dictionary<MidiRow, string>());
            };
            window.autopilotPdf = (rows2, _, _) => Task.FromResult(rows2.Count);
            TextBlock? summary = null;
            window.autopilotReportDialog = w =>
            {
                summary = FindDescendant<TextBlock>(w, "AutopilotSummaryText");
                w.ShowActivated = false; w.Opacity = 0; w.Show(); Flush(); CaptureElement((FrameworkElement)w.Content, Path.Combine(reports, "autopilot-result.png")); w.Close();
                return false;
            };
            var outcomes = await window.RunAutopilotAsync([rows[2], rows[3], rows[0]], exportPdf: false, keepAwake: false); Flush();
            Check(outcomes.Count == 3 && outcomes.Count(o => o.Group == "Prontas") == 2 && outcomes.Single(o => o.Row == rows[0]).Group == "Puladas" && rows[0].Approved && rows[0].ScorePath == score,
                "autopilot-skips-the-approved-score " + string.Join(" | ", outcomes.Select(o => o.Group + ":" + o.Detail)));
            var points = window.ListRestorePoints();
            Check(points.Count == 1 && points[0].Label.StartsWith("Antes de: Piloto automático · ") && points[0].Label.EndsWith(" · 4 músicas") && File.Exists(points[0].Package),
                "autopilot-saves-a-restore-point " + (points.Count > 0 ? points[0].Label : "nenhum"));
            Check(window.LastBatchLog is { NadaApagado: true, Operacao: "Piloto automático", Total: 3 } && window.LastBatchLogPath is { } logPath && File.Exists(logPath)
                && Path.GetDirectoryName(logPath)!.EndsWith("lotes") && File.ReadAllText(logPath).Contains("\"nadaApagado\": true"), "autopilot-writes-the-batch-log " + window.LastBatchLogPath);
            Check(summary is not null && summary.Text.Contains("Nada foi apagado") && summary.Text.Contains("Ponto de restauração"), "report-says-nothing-was-deleted " + summary?.Text);
            Check(window.Status.Contains("pulada(s) · aprovada(s)") && window.Status.Contains("Nada foi apagado"), "status-names-the-skipped-approved " + window.Status);
            var logs = new BatchLogStore(Path.Combine(work, "lotes")).ReadRecent();
            Check(logs.Count == 1 && logs[0].Resumo.GetValueOrDefault("pulada · aprovada") == 1 && logs[0].Resumo.GetValueOrDefault("Prontas") == 2, "batch-log-can-be-read-back");

            // 4. Gerar as marcadas: the preview separates "Vai refazer" from "Pula · aprovada"; the approved one never reaches the queue.
            window.EnterLibrarySelection(rows[0]); window.ToggleLibraryCheck(rows[1]); window.ToggleLibraryCheck(rows[2]); Flush();
            BatchPreviewWindow? generatePreview = null;
            window.batchPreviewDialog = w => { generatePreview = w; return true; };
            var queued = new List<MidiRow>();
            window.melodyBatchEnqueue = (row, _) => { queued.Add(row); return Task.CompletedTask; };
            ((Button)window.FindName("SelectedGenerateButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Idle();
            // rows[1] has a score but no melody chosen yet: it waits for the user; rows[2] is redone; rows[0] is approved and skipped.
            Check(generatePreview is not null && generatePreview.Preview.Count(BatchPreview.SkipApproved) == 1 && generatePreview.Preview.Count(BatchPreview.Regenerate) == 1
                && generatePreview.Preview.Count(BatchPreview.NeedsYou) == 1, "generate-preview-skips-the-approved " + generatePreview?.Preview.Headline());
            Check(queued.SequenceEqual([rows[2]]) && window.Status.Contains("1 pulada(s) · aprovada(s)"), "approved-score-never-reaches-the-queue " + window.Status);
            Check(window.ListRestorePoints().Count == 2 && window.ListRestorePoints()[0].Operation == "Gerar as marcadas" && window.ListRestorePoints()[0].CopiedFiles >= 1,
                "generate-saves-a-restore-point-with-the-affected-scores " + string.Join(" | ", window.ListRestorePoints().Select(p => p.Label + "/" + p.CopiedFiles)));

            // 5. Remover as marcadas: the approved card stays, the others leave; the log lists what changed.
            window.ExitLibrarySelection(); window.EnterLibrarySelection(rows[0]); window.ToggleLibraryCheck(rows[3]); Flush();
            BatchPreviewWindow? removePreview = null;
            window.batchPreviewDialog = w => { removePreview = w; return true; };
            ((Button)window.FindName("SelectedRemoveButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Idle();
            Check(removePreview is not null && removePreview.Preview.Count(BatchPreview.Remove) == 1 && removePreview.Preview.Count(BatchPreview.SkipApproved) == 1, "remove-preview-skips-the-approved");
            Check(window.Files.Contains(rows[0]) && !window.Files.Contains(rows[3]) && window.Status.Contains("1 pulada(s) · aprovada(s)") && window.CanUndoLibraryRemoval, "approved-card-is-not-removed-in-bulk " + window.Status);
            Check(window.LastBatchLog is { NadaApagado: false } log2 && log2.Mudancas.Count == 1 && log2.Mudancas[0].Contains("saíram da biblioteca"), "removal-log-lists-what-changed");
            window.UndoLibraryRemovalForSmoke(); Flush();
            Check(window.Files.Contains(rows[3]), "undo-brings-the-card-back");

            // 6. Limpar biblioteca keeps the approved score; the restore point list keeps the newest five.
            typeof(MainWindow).GetMethod("ClearLibraryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [true]);
            await Idle();
            Check(window.Files.Count == 1 && window.Files[0] == rows[0] && window.Status.Contains("1 partitura(s) aprovada(s) ficaram"), "clear-library-keeps-the-approved-score " + window.Status);
            window.UndoLibraryRemovalForSmoke(); Flush();
            Check(window.Files.Count == 4, "undo-restores-the-cleared-cards");

            // 7. "Voltar a este ponto": the library goes back to how it was before the autopilot (rows 2 and 3 without a score), without deleting the new scores.
            var before = window.ListRestorePoints().Single(p => p.Operation == "Piloto automático");
            string made = rows[2].ScorePath!;
            RestorePointsWindow? pointsWindow = null;
            window.restorePointsDialog = w =>
            {
                pointsWindow = w; w.List.SelectedItem = before;
                w.ShowActivated = false; w.Opacity = 0; w.Show(); Flush(); CaptureElement((FrameworkElement)w.Content, Path.Combine(reports, "restore-points.png")); w.Close();
                return true;
            };
            window.restorePointConfirm = _ => true;
            await window.ShowRestorePointsAsync(); await Idle();
            // Piloto, Gerar and Limpar saved points; removing a single card does not (Desfazer covers it).
            Check(pointsWindow is not null && pointsWindow.List.Items.Count == 3, "restore-points-window-lists-the-points " + pointsWindow?.List.Items.Count);
            var back2 = window.Files.Single(r => r.FullPath == rows[2].FullPath); var back0 = window.Files.Single(r => r.FullPath == rows[0].FullPath);
            Check(window.Files.Count == 4 && back2.ScorePath is null && back0 is { Approved: true, ScorePath: not null } && File.Exists(made), "returning-to-a-point-restores-the-library-without-deleting " + window.Status);
            Check(window.ListRestorePoints()[0].Operation.StartsWith("Voltar ao ponto"), "returning-saves-a-point-to-undo-the-return");
            Check(Settings.Load(settingsPath).Library.Single(s => s.FullPath == rows[2].FullPath).ScorePath is null, "the-returned-library-is-saved");
            // Four points now (Piloto, Gerar, Limpar, Voltar): two more push the oldest out, package and all.
            await window.CreateRestorePointAsync("Teste", []); await window.CreateRestorePointAsync("Teste 2", []);
            var kept = window.ListRestorePoints();
            Check(kept.Count == RestorePointStore.Kept && kept.All(p => File.Exists(p.Package)) && !kept.Any(p => p.Operation == "Piloto automático")
                && Directory.GetFiles(Path.GetDirectoryName(kept[0].Package)!, "*.teclasbackup").Length == RestorePointStore.Kept,
                "only-the-newest-five-restore-points-are-kept " + string.Join(" | ", kept.Select(p => p.Operation)));

            // 8. Import duplicates: same content, or same title and artist, ask before entering; Pular, Manter os dois and Substituir.
            string copy = Path.Combine(work, "copia", "Trio Nordestino - Forró do Xote (1).mid"); Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(rows[0].FullPath, copy);
            string sameName = Path.Combine(work, "copia", "Elba Ramalho - Chão de Giz.mid"); File.WriteAllBytes(sameName, TwoPartMidi(7));
            string fresh = Path.Combine(work, "copia", "Luiz Gonzaga - Asa Branca.mid"); File.WriteAllBytes(fresh, TwoPartMidi(8));
            ImportDuplicatesWindow? dupWindow = null;
            window.importDuplicatesDialog = w =>
            {
                dupWindow = w; w.ShowActivated = false; w.Opacity = 0; w.Show(); Flush();
                CaptureElement((FrameworkElement)w.Content, Path.Combine(reports, "import-duplicates.png"));
                foreach (var row in w.Rows) (row.Choice.Duplicate.Reason == ImportDuplicateDetector.SameContent ? row.Skip : row.Replace).IsChecked = true;
                w.Close(); return true;
            };
            int countBefore = window.Files.Count;
            typeof(MainWindow).GetMethod("ImportAndOpenLibraryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [new[] { copy, sameName, fresh }]);
            await Idle();
            Check(dupWindow is not null && dupWindow.Choices.Count == 2 && dupWindow.Choices.Any(c => c.Duplicate.Reason == ImportDuplicateDetector.SameContent)
                && dupWindow.Choices.Any(c => c.Duplicate.Reason == ImportDuplicateDetector.SameTitleArtist), "import-asks-about-both-kinds-of-duplicate");
            Check(window.Files.Count == countBefore + 1 && !window.Files.Any(r => r.FullPath == copy) && window.Files.Any(r => r.FullPath == sameName) && !window.Files.Any(r => r.FullPath == rows[2].FullPath)
                && window.Files.Any(r => r.FullPath == fresh) && window.Status.Contains("1 repetido(s) pulado(s)") && window.Status.Contains("1 card(s) substituído(s)") && window.Status.Contains("Nada foi apagado"),
                "import-applies-skip-and-replace " + window.Status);
            Check(File.Exists(rows[2].FullPath) && window.CanUndoLibraryRemoval, "replaced-card-keeps-its-file-and-can-be-undone");
            window.importDuplicatesDialog = _ => false;
            typeof(MainWindow).GetMethod("ImportAndOpenLibraryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [new[] { copy }]);
            await Idle();
            Check(window.Files.Count == countBefore + 1 && window.Status.Contains("Importação cancelada"), "cancelling-the-duplicates-window-imports-nothing");

            // 9. The Pasta de MIDIs marks a file whose title and artist are already in the library.
            var titles = window.Files.Select(f => ImportDuplicateDetector.Key(f.ScoreTitle, f.ScoreArtist)).ToHashSet(StringComparer.Ordinal);
            string folder = Path.Combine(work, "pasta"); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "Alceu Valença - Anunciação.mid"), TwoPartMidi(9));
            File.WriteAllBytes(Path.Combine(folder, "Inédita.mid"), TwoPartMidi(10));
            var folderWindow = new MidiFolderWindow(folder, false, new Dictionary<string, bool>(), null, () => { }, null, false, false, titles) { Opacity = 0, ShowActivated = false };
            folderWindow.Show(); Flush();
            for (int i = 0; i < 100 && folderWindow.ScanTask is not { IsCompleted: true }; i++) { await Task.Delay(50); Flush(); }
            Check(folderWindow.Items.Single(i => i.Name.StartsWith("Alceu")).State.StartsWith("Parece repetida") && folderWindow.Items.Single(i => i.Name.StartsWith("Inédita")).State == "Fora da biblioteca"
                && folderWindow.Items.All(i => i.CanSelect), "folder-window-marks-repeated-titles");
            folderWindow.Close();

            // 10. --simular prints the preview and changes nothing.
            var lines = new List<string>();
            var simulated = window.SimulateCommandLineBatch([rows[2].FullPath, Path.Combine(work, "pasta", "Inédita.mid"), rows[0].FullPath], lines.Add);
            Check(simulated.Count == 3 && simulated.All(s => s.Situacao == BatchFileReport.Simulated) && lines.Any(l => l.StartsWith("O que vai acontecer")) && lines.Any(l => l.Contains("Já geradas (1)"))
                && !window.Files.Any(r => r.FullPath.EndsWith("Inédita.mid")), "simulate-prints-the-preview-without-importing " + string.Join(" / ", simulated.Select(s => s.Motivo)));
            Check(BatchCommandLine.Parse(["--gerar", "a.mid", "--simular"], work).Options is { Simulate: true } && BatchCommandLine.Usage.Contains("--simular"), "simulate-option-is-parsed");

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

    /// <summary>A named element of a window that was never shown (no visual tree yet): the logical tree is enough.</summary>
    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T { } match && match.Name == name) return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindDescendant<T>(child, name) is { } found) return found;
        return null;
    }
}
