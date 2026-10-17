using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Teclas.App;

public partial class MainWindow
{
    private void InitializeLibraryLayout()
    {
        double share = settings.LibraryFiltersShare;
        if (!double.IsFinite(share) || share <= 0 || share >= 1) share = .55;
        share = Math.Clamp(share, .05, .95);
        LibraryFiltersRow.Height = new GridLength(share, GridUnitType.Star);
        LibraryCardsRow.Height = new GridLength(1 - share, GridUnitType.Star);
    }

    private void LibrarySplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!e.Canceled) SaveLibrarySplit();
    }

    private void LibrarySplitterKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) SaveLibrarySplit();
    }

    private void SaveLibrarySplit()
    {
        if (!IsIdle) return;
        double total = LibraryFiltersRow.ActualHeight + LibraryCardsRow.ActualHeight;
        if (total <= 0) return;
        settings.LibraryFiltersShare = Math.Clamp(LibraryFiltersRow.ActualHeight / total, .05, .95);
        PersistPreferences();
    }
}
