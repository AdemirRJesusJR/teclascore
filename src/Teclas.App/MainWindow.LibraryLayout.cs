using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Teclas.App;

/// <summary>
/// Library panel: actions grouped in four task menus (Adicionar, Gerar, Exportar, Estudar) plus the "⋯" menu,
/// and the collection / smart-list filters folded behind the filter toggle next to the search box.
/// </summary>
public partial class MainWindow
{
    private Popup? lastClosedLibraryMenu;
    private DateTime lastLibraryMenuClose;

    public bool HasLibraryAdvancedFilter => !string.IsNullOrWhiteSpace(CollectionFilter) || !string.IsNullOrWhiteSpace(SmartList);
    public Visibility LibraryAdvancedFilterVisibility => HasLibraryAdvancedFilter ? Visibility.Visible : Visibility.Collapsed;

    private void InitializeLibraryLayout()
    {
        // An active collection or smart list stays visible after a restart instead of filtering silently.
        LibraryFiltersToggle.IsChecked = HasLibraryAdvancedFilter;
    }

    private void NotifyLibraryAdvancedFilter()
    {
        Notify(nameof(HasLibraryAdvancedFilter)); Notify(nameof(LibraryAdvancedFilterVisibility));
    }

    private void LibraryMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Popup menu }) return;
        // StaysOpen=False closes the menu on the mouse-down over its own button; don't reopen it on that click.
        if (ReferenceEquals(lastClosedLibraryMenu, menu) && DateTime.UtcNow - lastLibraryMenuClose < TimeSpan.FromMilliseconds(250)) return;
        menu.IsOpen = !menu.IsOpen;
    }

    private void LibraryMenuOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup { Child: FrameworkElement content }) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (FirstFocusable(content) is { } first) Keyboard.Focus(first);
        });
    }

    private void LibraryMenuClosed(object? sender, EventArgs e)
    {
        lastClosedLibraryMenu = sender as Popup; lastLibraryMenuClose = DateTime.UtcNow;
    }

    private void LibraryMenuKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Popup menu) return;
        if (e.Key == Key.Escape)
        {
            menu.IsOpen = false; (menu.PlacementTarget as IInputElement)?.Focus(); e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up && Keyboard.FocusedElement is UIElement focused)
        {
            focused.MoveFocus(new TraversalRequest(e.Key == Key.Down ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous));
            e.Handled = true;
        }
    }

    private void LibraryMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Popup menu) menu.IsOpen = false;
    }

    private static IInputElement? FirstFocusable(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button { IsEnabled: true, Focusable: true } button) return button;
            if (FirstFocusable(child) is { } found) return found;
        }
        return null;
    }
}
