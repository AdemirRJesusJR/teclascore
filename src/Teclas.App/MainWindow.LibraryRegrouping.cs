using System.Collections;
using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace Teclas.App;

/// <summary>
/// The library groups its cards live (Em preparo, Prontas, Com falha). When a card changes group, WPF adds it
/// to the new group and later removes it from the old one; that removal also drops the ListBox selection although the card
/// is still in the list. Advancing a step (an analysis that changes the rating) then left the editor blank until the card
/// was clicked again. The open song is kept during the move and selected again right after it.
/// </summary>
public partial class MainWindow
{
    private bool libraryRegrouping, restoringLibrarySelection;

    /// <summary>The library view; tells the window while a removal is only a card moving to another group.</summary>
    private sealed class LibraryCollectionView(IList rows, Func<object, bool> stillListed, Action<bool> regrouping) : ListCollectionView(rows)
    {
        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs args)
        {
            bool moving = args.Action == NotifyCollectionChangedAction.Remove && args.OldItems is { Count: > 0 } old && old.Cast<object>().All(stillListed);
            if (moving) regrouping(true);
            try { base.OnCollectionChanged(args); }
            finally { if (moving) regrouping(false); }
        }
    }

    /// <summary>The card is still in the library and in the current filter: its removal from the view is a group change.</summary>
    private bool IsStillListed(object item) => item is MidiRow row && Files.Contains(row) && MatchesLibraryFilter(row);

    /// <summary>Returns true when the selection change only comes from a card changing group (the open song stays open).</summary>
    private bool KeepSelectionWhileRegrouping(SelectionChangedEventArgs e)
    {
        if (restoringLibrarySelection && ReferenceEquals(Library.SelectedItem, selectedFile)) return true;
        if (!libraryRegrouping || selectedFile is not { } kept || Library.SelectedItem is not null || !e.RemovedItems.Contains(kept)) return false;
        Dispatcher.BeginInvoke(() =>
        {
            if (Library.SelectedItem is not null || !ReferenceEquals(selectedFile, kept) || !Library.Items.Contains(kept)) return;
            restoringLibrarySelection = true;
            try { Library.SelectedItem = kept; }
            finally { restoringLibrarySelection = false; }
        }, DispatcherPriority.Normal);
        return true;
    }

    /// <summary>Rounds the card's clip so the progress bar along its bottom edge follows the corners.</summary>
    private void LibraryCardShellSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement shell) shell.Clip = new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(e.NewSize), 10, 10);
    }
}
