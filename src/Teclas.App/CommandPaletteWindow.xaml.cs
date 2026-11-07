using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace Teclas.App;

/// <summary>One entry of the command palette: an action of the app or a song of the library.</summary>
public sealed record PaletteItem(string Label, string Detail, string Keywords, Action Run)
{
    public const string ActionsGroup = "AÇÕES", SongsGroup = "MÚSICAS";
    public string Display => Detail.Length > 0 ? $"{Label}   ·   {Detail}" : Label;
    /// <summary>Segoe Fluent Icons glyph shown in the row tile.</summary>
    public string Glyph { get; init; } = "";
    public string Group { get; init; } = ActionsGroup;
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Ctrl+K: type part of the name of any action or song (accents ignored) and press Enter.</summary>
public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteItem> items;
    private bool closing;
    public IReadOnlyList<PaletteItem> Shown { get; private set; } = [];
    public PaletteItem? Ran { get; private set; }

    public CommandPaletteWindow(IReadOnlyList<PaletteItem> items)
    {
        this.items = items;
        InitializeComponent();
        Query.TextChanged += (_, _) => { Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Filter(Query.Text); };
        Results.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject source && ItemsControlFromSource(source)) RunSelected(); };
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: e.Handled = true; SafeClose(); break;
                case Key.Enter: e.Handled = true; RunSelected(); break;
                case Key.Down: e.Handled = true; Move(1); break;
                case Key.Up: e.Handled = true; Move(-1); break;
                case Key.PageDown: e.Handled = true; Move(8); break;
                case Key.PageUp: e.Handled = true; Move(-8); break;
            }
        };
        Loaded += (_, _) =>
        {
            if (Owner is { WindowState: not WindowState.Minimized } owner)
            {
                // Near the top of the owner, like a search bar dropping from the header.
                var area = owner.WindowState == WindowState.Maximized ? SystemParameters.WorkArea : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
                Left = area.Left + (area.Width - ActualWidth) / 2; Top = area.Top + 56;
            }
            Query.Focus();
        };
        Deactivated += (_, _) => { if (IsLoaded) SafeClose(); };
        Closing += (_, _) => closing = true;
        Filter("");
    }

    private bool ItemsControlFromSource(DependencyObject source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.ListBoxItem) return true;
        return false;
    }

    private void SafeClose() { if (!closing) { closing = true; Close(); } }

    private void Move(int delta)
    {
        if (Shown.Count == 0) return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + delta, 0, Shown.Count - 1);
        Results.ScrollIntoView(Results.SelectedItem);
    }

    internal static string Fold(string text)
    {
        var builder = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    /// <summary>Every word typed must appear in the label, detail or keywords; label matches come first.
    /// Results are shown grouped (actions, songs) in the order the best match of each group appears.</summary>
    internal void Filter(string text)
    {
        var words = Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ranked = items.Select(item => (item, haystack: Fold(item.Label + " " + item.Detail + " " + item.Keywords), label: Fold(item.Label)))
            .Where(x => words.All(w => x.haystack.Contains(w)))
            .OrderByDescending(x => words.Length > 0 && x.label.StartsWith(words[0]))
            .ThenByDescending(x => words.All(w => x.label.Contains(w)))
            .Select(x => x.item).Take(60);
        Shown = ranked.GroupBy(item => item.Group).SelectMany(group => group).ToList();
        var view = new ListCollectionView(Shown.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PaletteItem.Group)));
        Results.ItemsSource = view;
        if (Shown.Count > 0) Results.SelectedIndex = 0;
        bool empty = Shown.Count == 0;
        Results.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = $"Nada encontrado para “{text.Trim()}”.";
        CountText.Text = empty ? "" : Shown.Count == 1 ? "1 resultado" : $"{Shown.Count} resultados";
    }

    internal void Search(string text) { Query.Text = text; Filter(text); }

    internal void RunSelected()
    {
        if (Results.SelectedItem is not PaletteItem item) return;
        Ran = item; SafeClose();
        item.Run();
    }
}
