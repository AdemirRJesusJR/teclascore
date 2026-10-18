using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Library cards that are the same song (same file or same name): the user keeps one of each group; files stay on disk.</summary>
public partial class MainWindow
{
    internal Func<DuplicatesWindow, bool?> duplicatesDialog = window => window.ShowDialog();

    internal IReadOnlyList<DuplicateGroup> FindDuplicates() => DuplicateFinder.Find(Files.Select(row =>
        new DuplicateCandidate(row.FullPath, string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle, row.SourceSha256, row.FullPath)).ToArray());

    private void FindDuplicatesClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var groups = FindDuplicates();
        if (groups.Count == 0) { Status = "Nenhuma música repetida na biblioteca."; return; }
        var window = new DuplicatesWindow(groups, id => Files.FirstOrDefault(row => row.FullPath == id)) { Owner = this };
        if (duplicatesDialog(window) != true) return;
        RemoveLibraryItems(window.ToRemove.Select(id => Files.FirstOrDefault(row => row.FullPath == id)).OfType<MidiRow>().ToArray());
    }

    /// <summary>Removes several cards at once (undo brings them all back); the files are kept in their folders.</summary>
    internal void RemoveLibraryItems(IReadOnlyList<MidiRow> rows)
    {
        if (rows.Count == 0) return;
        lastRemovedLibraryItems.Clear();
        foreach (var row in rows) lastRemovedLibraryItems.Add((row, Files.IndexOf(row)));
        removedLibrarySelection = rows[0];
        foreach (var row in rows) Files.Remove(row);
        if (selectedFile is { } selected && rows.Contains(selected)) Library.SelectedItem = Files.FirstOrDefault();
        RefreshSelection();
        Status = $"{rows.Count} card(s) repetido(s) retirado(s) da biblioteca. Os arquivos continuam nas pastas; use Desfazer para trazê-los de volta.";
        PersistPreferences();
    }
}

/// <summary>Each group of repeated cards with the one to keep chosen (the one with a score, by default).</summary>
public sealed class DuplicatesWindow : Window
{
    private readonly List<(string Id, RadioButton Keep)> choices = [];
    public IReadOnlyList<string> ToRemove => choices.Where(c => c.Keep.IsChecked != true).Select(c => c.Id).ToArray();

    public DuplicatesWindow(IReadOnlyList<DuplicateGroup> groups, Func<string, MidiRow?> row)
    {
        Title = "Músicas repetidas"; Width = 760; Height = 600; MinWidth = 560; MinHeight = 380; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Músicas repetidas", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = $"{groups.Count} grupo(s) com o mesmo arquivo ou o mesmo nome. Escolha o card que fica em cada grupo; os outros saem da biblioteca, mas nenhum arquivo é apagado (e dá para desfazer).",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var list = new StackPanel();
        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            list.Children.Add(new TextBlock { Text = $"{group.Reason} · {group.Items[0].Title}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, g == 0 ? 0 : 14, 0, 4) });
            // Keep the card that has a score (the newest one), otherwise the first.
            var keep = group.Items.OrderByDescending(item => row(item.Id)?.ScorePath is not null).ThenByDescending(item => row(item.Id)?.GeneratedAt ?? DateTimeOffset.MinValue).First();
            foreach (var item in group.Items)
            {
                var card = row(item.Id);
                string details = (card?.ScorePath is not null ? "com partitura" : "sem partitura") + " · " + item.Path;
                var radio = new RadioButton { GroupName = "dup" + g, IsChecked = item == keep, Margin = new Thickness(8, 2, 0, 2),
                    Content = new TextBlock { Text = $"Manter: {item.Title}  —  {details}", TextWrapping = TextWrapping.Wrap } };
                AutomationProperties.SetName(radio, $"Manter {item.Title}, {details}");
                choices.Add((item.Id, radio)); list.Children.Add(radio);
            }
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var remove = DialogUi.Button("Retirar os outros cards", (_, _) => DialogResult = true, true); remove.IsDefault = true; buttons.Children.Add(remove);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
    }
}
