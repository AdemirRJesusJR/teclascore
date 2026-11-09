using System.Windows;

namespace Teclas.App;

public partial class MainWindow
{
    private readonly List<(MidiRow Row, int Index)> lastRemovedLibraryItems = [];
    private MidiRow? removedLibrarySelection;

    public bool CanRemoveLibraryItem => IsIdle && selectedFile is not null;
    public bool CanClearLibrary => IsIdle && Files.Count > 0;
    private bool IsAbsent(MidiRow removed) => !Files.Any(row => string.Equals(row.FullPath, removed.FullPath, StringComparison.OrdinalIgnoreCase));
    private bool HasUndoableLibraryRemoval => lastRemovedLibraryItems.Any(item => IsAbsent(item.Row));
    public bool CanUndoLibraryRemoval => IsIdle && HasUndoableLibraryRemoval;
    public Visibility LibraryUndoVisibility => HasUndoableLibraryRemoval ? Visibility.Visible : Visibility.Collapsed;

    private void NotifyLibraryActions()
    {
        Notify(nameof(CanRemoveLibraryItem));
        Notify(nameof(CanClearLibrary));
        Notify(nameof(CanUndoLibraryRemoval));
        Notify(nameof(LibraryUndoVisibility));
        Notify(nameof(CanUnapprove)); Notify(nameof(CanShowRestorePoints));
    }

    private void RemoveLibraryItemClick(object sender, RoutedEventArgs e)
    {
        if (!CanRemoveLibraryItem || !CommitEdits() || selectedFile is not { } row) return;
        RemoveLibraryItem(row);
    }

    private void RemoveProductionItemClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits() || sender is not FrameworkElement { Tag: MidiRow row } || !Files.Contains(row)) return;
        RemoveLibraryItem(row);
    }

    private void RemoveLibraryCardClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RemoveProductionItemClick(sender, e);
    }

    private void RemoveLibraryItem(MidiRow row)
    {
        var visible = LibraryView.Cast<MidiRow>().ToList();
        int selectedIndex = visible.IndexOf(row);
        var next = selectedFile != row ? selectedFile : selectedIndex >= 0 && selectedIndex + 1 < visible.Count ? visible[selectedIndex + 1]
            : selectedIndex > 0 ? visible[selectedIndex - 1] : null;
        lastRemovedLibraryItems.Clear();
        lastRemovedLibraryItems.Add((row, Files.IndexOf(row)));
        removedLibrarySelection = row;
        Files.Remove(row);
        Library.SelectedItem = next;
        RefreshSelection();
        Status = $"{row.Name} removido da biblioteca. Os arquivos foram mantidos nas suas pastas. Você pode desfazer esta remoção.";
        PersistPreferences();
    }

    private void UndoRemoveLibraryItemClick(object sender, RoutedEventArgs e)
    {
        if (!CanUndoLibraryRemoval || !CommitEdits()) return;
        var restored = lastRemovedLibraryItems.Where(item => IsAbsent(item.Row)).ToArray();
        foreach (var item in restored) Files.Insert(Math.Clamp(item.Index, 0, Files.Count), item.Row);
        var row = restored.Select(item => item.Row).FirstOrDefault(item => item == removedLibrarySelection) ?? restored[0].Row;
        lastRemovedLibraryItems.Clear(); removedLibrarySelection = null;
        if (restored.Any(item => !MatchesLibraryFilter(item.Row))) { SearchText = ""; CurrentFilter = "all"; FavoritesOnly = false; CollectionFilter = ""; }
        SetCurrentPage("library");
        Library.SelectedItem = row;
        Library.ScrollIntoView(row);
        RefreshSelection();
        Status = restored.Length == 1 ? $"{row.Name} voltou para a biblioteca com as suas edições."
            : $"{restored.Length} músicas voltaram para a biblioteca com as suas edições.";
        PersistPreferences();
    }

    private void ClearLibraryClick(object sender, RoutedEventArgs e)
    {
        if (!CanClearLibrary || !CommitEdits()) return;
        var dialog = new ClearLibraryWindow(Files.Count, Files.Count(IsApprovalLocked)) { Owner = this };
        if (dialog.ShowDialog() == true) _ = ClearLibraryAsync();
    }

    private void ClearLibrary() => _ = ClearLibraryAsync(restorePoint: false);

    /// <summary>Removes every card except the approved scores (a batch never touches them); a restore point is saved first.</summary>
    private async Task ClearLibraryAsync(bool restorePoint = true)
    {
        if (!CanClearLibrary) return;
        var removing = Files.Where(row => !IsApprovalLocked(row)).ToList();
        int kept = Files.Count - removing.Count;
        if (removing.Count == 0) { Status = $"Nada removido: as {kept} músicas da biblioteca estão aprovadas. Retire a aprovação na própria música para removê-las."; return; }
        var started = DateTimeOffset.Now;
        var point = restorePoint ? await CreateRestorePointAsync("Limpar biblioteca", removing) : null;
        if (!CanClearLibrary) return;
        lastRemovedLibraryItems.Clear();
        lastRemovedLibraryItems.AddRange(removing.Select(row => (row, Files.IndexOf(row))));
        removedLibrarySelection = selectedFile;
        StopTrackAudition(); ResetPlayback();
        foreach (var row in removing) Files.Remove(row);
        Library.SelectedItem = null;
        RefreshSelection(); PersistPreferences();
        RecordBatch("Limpar biblioteca", started, removing.Select(row => LogItem(row, "removida", "card fora da biblioteca; arquivos mantidos")).ToList(),
            [$"{removing.Count} card(s) saíram da biblioteca (os arquivos continuam nas pastas; Desfazer traz os cards de volta)"], point);
        Status = "Biblioteca limpa. Os arquivos foram mantidos nas suas pastas. Use Desfazer para restaurar os cards."
            + (kept > 0 ? $" {kept} partitura(s) aprovada(s) ficaram." : "") + (point is not null ? $" Ponto de restauração: {point.Label}." : "");
    }
}

public sealed class ClearLibraryWindow : Window
{
    public ClearLibraryWindow(int count, int approved = 0)
    {
        Title = "Limpar biblioteca"; Width = 470; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; DialogUi.Theme(this);
        var panel = new System.Windows.Controls.StackPanel { Margin = new(24) }; Content = panel;
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = approved > 0 ? $"Remover {count - approved} dos {count} cards?" : $"Remover todos os {count} cards?", FontSize = 20, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Inclui as músicas ocultas pelos filtros. Os MIDIs, PDFs e arquivos MuseScore permanecem nas suas pastas. Você poderá desfazer enquanto o aplicativo estiver aberto."
            + (approved > 0 ? $" {approved} partitura(s) aprovada(s) ficam na biblioteca: para removê-las, retire a aprovação na própria música." : "") + " Um ponto de restauração é guardado antes.", TextWrapping = TextWrapping.Wrap, Margin = new(0,16,0,20) });
        var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; cancel.IsDefault = true; actions.Children.Add(cancel);
        var confirm = DialogUi.Button("Limpar biblioteca", (_, _) => DialogResult = true); confirm.Name = "ConfirmClearLibraryButton";
        confirm.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Error"); actions.Children.Add(confirm); panel.Children.Add(actions);
    }
}
