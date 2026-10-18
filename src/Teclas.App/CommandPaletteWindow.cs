using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Teclas.App;

/// <summary>One entry of the command palette: an action of the app or a song of the library.</summary>
public sealed record PaletteItem(string Label, string Detail, string Keywords, Action Run)
{
    public string Display => Detail.Length > 0 ? $"{Label}   ·   {Detail}" : Label;
}

/// <summary>Ctrl+K: type part of the name of any action or song (accents ignored) and press Enter.</summary>
public sealed class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteItem> items;
    private readonly TextBox query;
    private readonly ListBox list;
    public IReadOnlyList<PaletteItem> Shown { get; private set; } = [];
    public PaletteItem? Ran { get; private set; }

    public CommandPaletteWindow(IReadOnlyList<PaletteItem> items)
    {
        this.items = items;
        Title = "Comandos"; Width = 640; Height = 460; WindowStyle = WindowStyle.ToolWindow; ShowInTaskbar = false; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        query = new TextBox { FontSize = 16, MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(query, "Buscar comando ou música");
        DockPanel.SetDock(query, Dock.Top); root.Children.Add(query);
        var hint = new TextBlock { Text = "Digite parte do nome de uma ação ou música · Enter executa · Esc fecha", Opacity = .7, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(hint, Dock.Bottom); root.Children.Add(hint);
        list = new ListBox { DisplayMemberPath = nameof(PaletteItem.Display) };
        AutomationProperties.SetName(list, "Resultados");
        root.Children.Add(list);
        query.TextChanged += (_, _) => Filter(query.Text);
        list.MouseDoubleClick += (_, _) => RunSelected();
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: e.Handled = true; Close(); break;
                case Key.Enter: e.Handled = true; RunSelected(); break;
                case Key.Down: e.Handled = true; list.SelectedIndex = Math.Min(Shown.Count - 1, list.SelectedIndex + 1); list.ScrollIntoView(list.SelectedItem); break;
                case Key.Up: e.Handled = true; list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); list.ScrollIntoView(list.SelectedItem); break;
            }
        };
        Loaded += (_, _) => query.Focus();
        Filter("");
    }

    internal static string Fold(string text)
    {
        var builder = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    /// <summary>Every word typed must appear in the label, detail or keywords; label matches come first.</summary>
    internal void Filter(string text)
    {
        var words = Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Shown = items.Select(item => (item, haystack: Fold(item.Label + " " + item.Detail + " " + item.Keywords), label: Fold(item.Label)))
            .Where(x => words.All(w => x.haystack.Contains(w)))
            .OrderByDescending(x => words.Length > 0 && x.label.StartsWith(words[0]))
            .ThenByDescending(x => words.All(w => x.label.Contains(w)))
            .Select(x => x.item).Take(60).ToList();
        list.ItemsSource = Shown;
        if (Shown.Count > 0) list.SelectedIndex = 0;
    }

    internal void Search(string text) { query.Text = text; Filter(text); }

    internal void RunSelected()
    {
        if (list.SelectedItem is not PaletteItem item) return;
        Ran = item; Close();
        item.Run();
    }
}
