using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Teclas.Core;

namespace Teclas.App;

/// <summary>One line of the titles sheet: the song's editable names, applied to the library on Save.</summary>
public sealed class MetadataSheetRow : Observable
{
    public required MidiRow Row { get; init; }
    public string File => Row.Name;
    public string Key => MainWindow.KeyName(Row, Row.TransposeSemitones);
    private string title = "", artist = "", composer = "", collection = "";
    public string Title { get => title; set => Set(ref title, (value ?? "").Trim()); }
    public string Artist { get => artist; set => Set(ref artist, (value ?? "").Trim()); }
    public string Composer { get => composer; set => Set(ref composer, (value ?? "").Trim()); }
    public string Collection { get => collection; set => Set(ref collection, (value ?? "").Trim()); }
    public bool Changed => Title != Row.ScoreTitle || Artist != Row.ScoreArtist || Composer != Row.ScoreComposer || Collection != Row.Collection;
}

/// <summary>
/// Titles sheet: every song in a table to edit title, artist, composer and collection in sequence, like a spreadsheet.
/// Empty cells can be filled from the file name and the MIDI's texts; several lines copied from a spreadsheet paste
/// down the column (tabs fill the next columns).
/// </summary>
public sealed class MetadataSheetWindow : Window
{
    internal static readonly string[] Editable = [nameof(MetadataSheetRow.Title), nameof(MetadataSheetRow.Artist), nameof(MetadataSheetRow.Composer), nameof(MetadataSheetRow.Collection)];
    private readonly DataGrid grid;
    private readonly TextBox filter;
    private readonly TextBlock status;
    public ObservableCollection<MetadataSheetRow> Rows { get; } = [];
    public string StatusText => status.Text;

    public MetadataSheetWindow(IEnumerable<MidiRow> songs)
    {
        foreach (var row in songs) Rows.Add(new MetadataSheetRow { Row = row, Title = row.ScoreTitle, Artist = row.ScoreArtist, Composer = row.ScoreComposer, Collection = row.Collection });
        Title = "Títulos e artistas"; Width = 1100; Height = 700; MinWidth = 760; MinHeight = 420; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var fill = DialogUi.Button("Preencher vazios pelo nome do arquivo", (_, _) => FillFromFileNames());
        fill.ToolTip = "Completa título, artista e compositor vazios a partir do nome do arquivo (\"Artista - Título\") e dos textos do MIDI"; DockPanel.SetDock(fill, Dock.Right); top.Children.Add(fill);
        top.Children.Add(new TextBlock { Text = "Filtrar", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        filter = new TextBox { MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        AutomationProperties.SetName(filter, "Filtrar músicas"); top.Children.Add(filter);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        buttons.Children.Add(DialogUi.Button("Salvar", (_, _) => { grid!.CommitEdit(DataGridEditingUnit.Row, true); DialogResult = true; }, true));
        status = new TextBlock { Text = "Dica: copie uma coluna de títulos do Excel, clique na primeira célula e cole com Ctrl+V.", Opacity = .75, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(status);
        grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = Rows, CanUserAddRows = false, CanUserDeleteRows = false, SelectionUnit = DataGridSelectionUnit.Cell, HeadersVisibility = DataGridHeadersVisibility.Column };
        AutomationProperties.SetName(grid, "Títulos, artistas e coleções");
        grid.Columns.Add(new DataGridTextColumn { Header = "Arquivo", Binding = new Binding(nameof(MetadataSheetRow.File)), IsReadOnly = true, Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        foreach (var (path, header, width) in new[] { ("Title", "Título", 1.6), ("Artist", "Artista", 1.2), ("Composer", "Compositor", 1.0), ("Collection", "Coleção", .9) })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Tom", Binding = new Binding(nameof(MetadataSheetRow.Key)), IsReadOnly = true, Width = new DataGridLength(60) });
        grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && grid.CurrentCell.Column is { IsReadOnly: false }) { e.Handled = PasteFromClipboard(); } };
        root.Children.Add(grid);
        var view = CollectionViewSource.GetDefaultView(Rows);
        filter.TextChanged += (_, _) =>
        {
            string q = CommandPaletteWindow.Fold(filter.Text);
            view.Filter = q.Length == 0 ? null : item => item is MetadataSheetRow r && CommandPaletteWindow.Fold($"{r.File} {r.Title} {r.Artist} {r.Collection}").Contains(q);
        };
    }

    /// <summary>Fills empty title, artist and composer from the file name and the MIDI's own texts.</summary>
    internal int FillFromFileNames()
    {
        int filled = 0;
        foreach (var line in Rows)
        {
            var suggestion = ScoreMetadataOrganizer.Suggest(line.Row.FullPath, line.Row.Document?.TextEvents);
            bool any = false;
            if (IsBlank(line.Title) && suggestion.Title.Length > 0) { line.Title = suggestion.Title; any = true; }
            if (IsBlank(line.Artist) && suggestion.Artist.Length > 0) { line.Artist = suggestion.Artist; any = true; }
            if (IsBlank(line.Composer) && suggestion.Composer.Length > 0) { line.Composer = suggestion.Composer; any = true; }
            if (any) filled++;
        }
        status.Text = filled == 0 ? "Nenhuma célula vazia para preencher." : $"{filled} música(s) completada(s) pelo nome do arquivo. Confira antes de salvar.";
        return filled;
    }

    private static bool IsBlank(string value) => string.IsNullOrWhiteSpace(value) || value == "Sem título";

    /// <summary>Pastes text down the current column from the current line; tab-separated values fill the next columns.</summary>
    internal bool Paste(string text, int startRow, int startColumn)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var visible = CollectionViewSource.GetDefaultView(Rows).Cast<MetadataSheetRow>().ToList();
        int pasted = 0;
        for (int i = 0; i < lines.Count && startRow + i < visible.Count; i++)
        {
            var cells = lines[i].Split('\t');
            for (int c = 0; c < cells.Length && startColumn + c < Editable.Length; c++)
                typeof(MetadataSheetRow).GetProperty(Editable[startColumn + c])!.SetValue(visible[startRow + i], cells[c]);
            pasted++;
        }
        status.Text = pasted == 0 ? "Nada para colar." : $"{pasted} linha(s) colada(s).";
        return pasted > 0;
    }

    private bool PasteFromClipboard()
    {
        string text;
        try { text = Clipboard.GetText(); } catch (System.Runtime.InteropServices.ExternalException) { return false; }
        if (text.Length == 0 || grid.CurrentCell.Item is not MetadataSheetRow line || grid.CurrentCell.Column is not DataGridBoundColumn { Binding: Binding binding }) return false;
        int column = Array.IndexOf(Editable, binding.Path.Path);
        if (column < 0) return false;
        int row = CollectionViewSource.GetDefaultView(Rows).Cast<MetadataSheetRow>().ToList().IndexOf(line);
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        return Paste(text, row, column);
    }
}
