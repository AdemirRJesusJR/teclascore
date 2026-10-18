using System.IO;
using System.Windows;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private async void ExportLibraryBackupClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        CaptureSession();
        // Capture the UI snapshot before entering the worker; operations cannot change it meanwhile.
        var snapshot = System.Text.Json.JsonSerializer.Serialize(settings.Library);
        string? selected = settings.SelectedMidiPath;
        PortableLibraryPlan? plan = null;
        await RunOperation(async () =>
        {
            Status = "Conferindo arquivos e versões da biblioteca…";
            var token = cancellation!.Token;
            var prepared = await Task.Run(() => LibraryBackupSnapshot.Capture(
                System.Text.Json.JsonSerializer.Deserialize<List<SavedMidi>>(snapshot)!, selected), token);
            token.ThrowIfCancellationRequested();
            plan = prepared;
        }, operationTitle: "Preparando backup da biblioteca");
        if (plan is null || operationsDisposed) return;
        if (plan.MissingFiles.Count > 0 && MessageBox.Show(this,
            $"{plan.MissingFiles.Count} arquivo(s) estão indisponíveis e não poderão ser copiados:\n\n" +
            string.Join("\n", plan.MissingFiles.Take(10)) + (plan.MissingFiles.Count > 10 ? "\n…" : "") +
            "\n\nSalvar os arquivos disponíveis e manter as referências ausentes identificadas?",
            "Arquivos indisponíveis", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Title = "Salvar backup portátil da biblioteca", Filter = "Backup TeclaScore|*.teclasbackup",
            DefaultExt = ".teclasbackup", AddExtension = true, FileName = "TeclaScore-biblioteca-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".teclasbackup" };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperation(async () =>
        {
            await LibraryBackupService.CreateAsync(dialog.FileName, plan.LibraryJson, plan.Sources, plan.MissingFiles,
                CreateOperationProgress(), cancellation!.Token);
            Status = $"Backup salvo: {plan.ItemCount} músicas, {plan.Sources.Count} arquivos" +
                (plan.MissingFiles.Count > 0 ? $" · {plan.MissingFiles.Count} referências indisponíveis" : "") + ". " + dialog.FileName;
        }, operationTitle: "Salvando backup da biblioteca");
    }

    private async void RestoreLibraryBackupClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var open = new OpenFileDialog { Title = "Abrir backup da biblioteca", Filter = "Backup TeclaScore|*.teclasbackup", CheckFileExists = true };
        if (open.ShowDialog(this) != true) return;
        LibraryBackupInspection? inspected = null;
        int count = 0;
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            var candidate = await LibraryBackupService.InspectAsync(open.FileName, CreateOperationProgress(), token);
            // Validate all path references before choosing a destination or extracting a single file.
            count = (await Task.Run(() => LibraryBackupSnapshot.Resolve(candidate.Manifest, Path.GetTempPath()), token)).Library.Count;
            token.ThrowIfCancellationRequested(); inspected = candidate;
            Status = $"Backup conferido: {count} músicas e {candidate.Manifest.Files.Count} arquivos.";
        }, operationTitle: "Conferindo backup da biblioteca");
        if (inspected is null || operationsDisposed) return;
        var folder = new OpenFolderDialog { Title = $"Restaurar {count} músicas em uma nova pasta dentro de…", Multiselect = false };
        if (folder.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, $"Adicionar {count} músicas com suas versões à biblioteca atual?\n\n" +
            "Os arquivos serão restaurados em uma pasta própria. Suas músicas e configurações atuais serão mantidas." +
            (inspected.Manifest.MissingFiles.Count > 0 ? $"\n\nEste backup registra {inspected.Manifest.MissingFiles.Count} arquivo(s) indisponíveis na origem." : ""),
            "Restaurar biblioteca", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            var restored = await LibraryBackupService.RestoreAsync(inspected, folder.FolderName, CreateOperationProgress(), token);
            var data = await Task.Run(() => LibraryBackupSnapshot.Resolve(restored.Manifest, restored.Directory), token);
            await ApplyRestoredLibrary(data, token);
            Status = $"Backup restaurado. Arquivos e versões disponíveis em {restored.Directory}";
        }, operationTitle: "Restaurando biblioteca");
    }

    private async Task ApplyRestoredLibrary(PortableLibraryData data, CancellationToken token)
    {
        // A song already here (same path, or the same MIDI restored elsewhere) keeps its own settings.
        var existing = Files.Select(row => row.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = Files.Select(row => row.SourceSha256).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newItems = data.Library.Where(row => !existing.Contains(row.FullPath) && (string.IsNullOrEmpty(row.Sha256) || !known.Contains(row.Sha256)))
            .GroupBy(row => string.IsNullOrEmpty(row.Sha256) ? row.FullPath : row.Sha256, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        var loaded = new List<(SavedMidi Saved, MidiRow Row)>();
        foreach (var saved in newItems)
        {
            token.ThrowIfCancellationRequested(); Status = $"Abrindo música {loaded.Count + 1} de {newItems.Length}…";
            MidiRow row;
            try { row = await Task.Run(() => LoadMidi(saved.FullPath), token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException or ArgumentException or NotSupportedException)
            { row = new MidiRow { FullPath = saved.FullPath, Status = "Origem indisponível no backup · cópias salvas preservadas" }; }
            loaded.Add((saved, row));
        }
        token.ThrowIfCancellationRequested();
        bool oldLoading = loading; loading = true;
        try
        {
            foreach (var item in loaded)
            {
                var row = item.Row; var saved = item.Saved;
                bool sameSource = string.Equals(row.SourceSha256, saved.Sha256, StringComparison.OrdinalIgnoreCase);
                if (row.Document is not null && sameSource) ApplySavedMidi(row, saved);
                else
                {
                    row.ScorePath = saved.ScorePath; row.PreviewPath = saved.PreviewPath;
                    row.PageCount = saved.PageCount; row.GeneratedAt = saved.GeneratedAt;
                    row.KeyFifths = saved.KeyFifths; row.KeyMinor = saved.KeyMinor;
                    foreach (var chord in saved.Chords.Where(_ => row.Document is null || saved.HasMusicalEdits)) row.Chords.Add(new ChordRow
                    { Quarter = chord.Quarter, Bass = chord.Bass, Root = chord.Root, Quality = chord.Quality, Confidence = chord.Confidence,
                        DiagramInversion = chord.DiagramInversion, Recommendation = chord.Recommendation, Alternatives = chord.Alternatives });
                }
                ApplySavedScoreProperties(row, saved); RememberVersion(row);
                if (row.Document is not null && !sameSource && !saved.HasMusicalEdits)
                    row.Status = "MIDI alterado antes do backup · confira as pistas e analise novamente";
            }
        }
        finally { loading = oldLoading; }
        token.ThrowIfCancellationRequested();
        // Publish the merged session atomically before adding rows; cancellation after this point is successful restoration.
        CaptureSession(); var previous = settings.Library;
        settings.Library = previous.Concat(newItems).ToList();
        try { if (persistenceEnabled) settings.Save(persistencePath); }
        catch { settings.Library = previous; throw; }
        foreach (var item in loaded)
        {
            if (item.Row.SourceSha256 is null) unavailableSessionRows[item.Row.FullPath] = item.Saved;
            Files.Add(item.Row);
        }
        SearchText = ""; CurrentFilter = "all"; FavoritesOnly = false; CollectionFilter = "";
        SetCurrentPage("library");
        Library.SelectedItem = Files.FirstOrDefault(row => string.Equals(row.FullPath, data.SelectedPath, StringComparison.OrdinalIgnoreCase)) ?? loaded.FirstOrDefault().Row;
        RefreshSelection();
    }
}
