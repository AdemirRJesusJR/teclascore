using System.Windows;

namespace Teclas.App;

public partial class MainWindow
{
    private MidiRow? lastRemovedLibraryItem;
    private int lastRemovedLibraryIndex;

    public bool CanRemoveLibraryItem => IsIdle && selectedFile is not null;
    private bool HasUndoableLibraryRemoval => lastRemovedLibraryItem is { } removed
        && !Files.Any(row => string.Equals(row.FullPath, removed.FullPath, StringComparison.OrdinalIgnoreCase));
    public bool CanUndoLibraryRemoval => IsIdle && HasUndoableLibraryRemoval;
    public Visibility LibraryUndoVisibility => HasUndoableLibraryRemoval ? Visibility.Visible : Visibility.Collapsed;

    private void NotifyLibraryActions()
    {
        Notify(nameof(CanRemoveLibraryItem));
        Notify(nameof(CanUndoLibraryRemoval));
        Notify(nameof(LibraryUndoVisibility));
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

    private void RemoveLibraryItem(MidiRow row)
    {
        var visible = LibraryView.Cast<MidiRow>().ToList();
        int selectedIndex = visible.IndexOf(row);
        var next = selectedFile != row ? selectedFile : selectedIndex >= 0 && selectedIndex + 1 < visible.Count ? visible[selectedIndex + 1]
            : selectedIndex > 0 ? visible[selectedIndex - 1] : null;
        lastRemovedLibraryIndex = Files.IndexOf(row);
        lastRemovedLibraryItem = row;
        Files.Remove(row);
        Library.SelectedItem = next;
        RefreshSelection();
        Status = $"{row.Name} removido da biblioteca. Os arquivos foram mantidos nas suas pastas. Você pode desfazer esta remoção.";
        PersistPreferences();
    }

    private void UndoRemoveLibraryItemClick(object sender, RoutedEventArgs e)
    {
        if (!CanUndoLibraryRemoval || !CommitEdits() || lastRemovedLibraryItem is not { } row) return;
        Files.Insert(Math.Clamp(lastRemovedLibraryIndex, 0, Files.Count), row);
        lastRemovedLibraryItem = null;
        if (!MatchesLibraryFilter(row)) { SearchText = ""; CurrentFilter = "all"; FavoritesOnly = false; CollectionFilter = ""; }
        SetCurrentPage("library");
        Library.SelectedItem = row;
        Library.ScrollIntoView(row);
        RefreshSelection();
        Status = $"{row.Name} voltou para a biblioteca com as suas edições.";
        PersistPreferences();
    }
}
