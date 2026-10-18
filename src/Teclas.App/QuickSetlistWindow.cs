using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Teclas.App;

/// <summary>A song in the quick repertoire, with the key chosen for this occasion (semitones from the original).</summary>
public sealed class SetlistEntry : Observable
{
    public required MidiRow Row { get; init; }
    public string Title => string.IsNullOrWhiteSpace(Row.ScoreTitle) ? Row.Name : Row.ScoreTitle;
    private int transpose; public int Transpose { get => transpose; set { if (Set(ref transpose, Math.Clamp(value, -11, 11))) Notify(nameof(KeyText)); } }
    public string KeyText => MainWindow.KeyName(Row, Transpose) is { Length: > 0 } key ? "Tom " + key : "tom não analisado";
}

/// <summary>Quick repertoire: pick songs, order them and set each key; the app produces the PDF, the text and the stage list.</summary>
public sealed class QuickSetlistWindow : Window
{
    private readonly IReadOnlyList<MidiRow> library;
    private readonly ListBox available, chosen;
    private readonly TextBox search, title;
    public ObservableCollection<SetlistEntry> Entries { get; } = [];
    public string SetlistTitle => string.IsNullOrWhiteSpace(title.Text) ? "Repertório" : title.Text.Trim();

    public QuickSetlistWindow(IReadOnlyList<MidiRow> library)
    {
        this.library = library;
        Title = "Repertório rápido"; Width = 980; Height = 640; MinWidth = 760; MinHeight = 460; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Repertório rápido", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Escolha as músicas (duplo clique), ponha na ordem e ajuste o tom de cada uma. O TeclaScore gera de novo as que mudaram de tom e cria o PDF único, a cifra em texto para o grupo e a lista do palco com a duração total.",
            TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 6, 0, 8) });
        var titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; top.Children.Add(titleRow);
        titleRow.Children.Add(new TextBlock { Text = "Nome", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        title = new TextBox { Text = "Culto " + DateTime.Now.ToString("dd/MM"), MaxLength = 120, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(title, "Nome do repertório"); titleRow.Children.Add(title);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; footer.Children.Add(cancel);
        footer.Children.Add(DialogUi.Button("Montar repertório", (_, _) => { if (Entries.Count > 0) DialogResult = true; }, true));
        var grid = new Grid(); root.Children.Add(grid);
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        var left = new DockPanel(); grid.Children.Add(left);
        search = new TextBox { MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(search, "Buscar música"); DockPanel.SetDock(search, Dock.Top); left.Children.Add(search);
        available = new ListBox { DisplayMemberPath = "Label" }; AutomationProperties.SetName(available, "Músicas da biblioteca"); left.Children.Add(available);
        available.MouseDoubleClick += (_, _) => AddSelected();
        search.TextChanged += (_, _) => Refresh();
        var right = new DockPanel(); Grid.SetColumn(right, 2); grid.Children.Add(right);
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) }; DockPanel.SetDock(tools, Dock.Top); right.Children.Add(tools);
        tools.Children.Add(DialogUi.Button("↑", (_, _) => MoveSelected(-1))); tools.Children.Add(DialogUi.Button("↓", (_, _) => MoveSelected(1)));
        tools.Children.Add(DialogUi.Button("Tom −", (_, _) => { if (chosen!.SelectedItem is SetlistEntry s) s.Transpose--; }));
        tools.Children.Add(DialogUi.Button("Tom +", (_, _) => { if (chosen!.SelectedItem is SetlistEntry s) s.Transpose++; }));
        tools.Children.Add(DialogUi.Button("Tirar", (_, _) => { if (chosen!.SelectedItem is SetlistEntry s) Entries.Remove(s); }));
        chosen = new ListBox { ItemsSource = Entries }; AutomationProperties.SetName(chosen, "Repertório na ordem");
        var template = new DataTemplate();
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SetlistEntry.Title))); name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var key = new FrameworkElementFactory(typeof(TextBlock)); key.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SetlistEntry.KeyText))); key.SetValue(MarginProperty, new Thickness(12, 0, 0, 0)); key.SetValue(OpacityProperty, .75);
        panel.AppendChild(name); panel.AppendChild(key); template.VisualTree = panel;
        chosen.ItemTemplate = template;
        right.Children.Add(chosen);
        Refresh();
        Loaded += (_, _) => search.Focus();
    }

    private void Refresh()
    {
        string query = CommandPaletteWindow.Fold(search.Text);
        available.ItemsSource = library.Where(r => r.Document is not null || r.ScorePath is not null)
            .Select(r => new { Row = r, Label = (string.IsNullOrWhiteSpace(r.ScoreTitle) ? r.Name : r.ScoreTitle) + (r.ScorePath is null ? "  (sem partitura)" : "") })
            .Where(x => query.Length == 0 || CommandPaletteWindow.Fold(x.Label + " " + x.Row.ScoreArtist).Contains(query))
            .OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal void Add(MidiRow row) { if (Entries.All(e => e.Row != row)) Entries.Add(new SetlistEntry { Row = row, Transpose = row.TransposeSemitones }); }

    private void AddSelected() { if (available.SelectedItem is { } item && item.GetType().GetProperty("Row")?.GetValue(item) is MidiRow row) Add(row); }

    private void MoveSelected(int step)
    {
        if (chosen.SelectedItem is not SetlistEntry entry) return;
        int index = Entries.IndexOf(entry), target = Math.Clamp(index + step, 0, Entries.Count - 1);
        if (target != index) { Entries.Move(index, target); chosen.SelectedItem = entry; }
    }
}
