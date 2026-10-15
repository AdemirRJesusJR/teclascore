using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

public sealed class DiagramPositionsWindow : Window
{
    private readonly List<DiagramDraft> drafts;
    private readonly DataGrid table;
    private readonly TextBlock summary;
    private readonly int defaultInversion;
    private readonly int transpose;

    public IReadOnlyList<int?> Inversions => drafts.Select(draft => draft.Inversion).ToArray();
    public int ComfortableMaximumSpan { get; private set; }

    public DiagramPositionsWindow(MidiRow row) : this(row, 12) { }

    public DiagramPositionsWindow(MidiRow row, int comfortableMaximumSpan)
    {
        Title = "Posições dos diagramas";
        Width = 790; Height = 690; MinWidth = 650; MinHeight = 530;
        ShowInTaskbar = false; UseLayoutRounding = true;
        DialogUi.Theme(this);
        NameScope.SetNameScope(this, new NameScope());
        drafts = row.Chords.Select(chord => new DiagramDraft(chord)).ToList();
        defaultInversion = row.EffectiveDiagramInversion;
        transpose = row.TransposeSemitones;
        ComfortableMaximumSpan = comfortableMaximumSpan is 7 or 9 or 10 or 12 ? comfortableMaximumSpan : 12;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "Escolha a posição de cada acorde", FontSize = 23,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(Muted("Cada linha representa uma ocorrência da cifra. Você pode usar posições diferentes para o mesmo acorde.", 7));
        heading.Children.Add(Muted("Exemplo em C, da nota mais grave para a mais aguda: fundamental Dó–Mi–Sol · 1ª Mi–Sol–Dó · 2ª Sol–Dó–Mi.", 8));
        if (row.TransposeSemitones != 0)
            heading.Children.Add(Muted("As cifras abaixo estão no tom de origem. A posição escolhida será aplicada ao acorde no tom final da partitura.", 8));

        var options = new[] { "Usar padrão · " + InversionLabel(row.EffectiveDiagramInversion),
            "Posição fundamental", "1ª inversão", "2ª inversão" };
        table = new DataGrid
        {
            Name = "DiagramPositionsTable", ItemsSource = drafts, AutoGenerateColumns = false,
            CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
            CanUserResizeRows = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, RowHeight = 46, MaxHeight = 600,
            VerticalAlignment = VerticalAlignment.Stretch, EnableRowVirtualization = true
        };
        AutomationProperties.SetName(table, "Posição dos diagramas por ocorrência da cifra");
        ScrollViewer.SetVerticalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        RegisterName(table.Name, table);
        Grid.SetRow(table, 1); root.Children.Add(table);
        table.Columns.Add(new DataGridTextColumn { Header = "Posição (semínimas)",
            Binding = new Binding(nameof(DiagramDraft.Quarter)) { StringFormat = "0.###" }, Width = 165 });
        table.Columns.Add(new DataGridTextColumn { Header = "Cifra", Binding = new Binding(nameof(DiagramDraft.Symbol)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 70 });

        var combo = new FrameworkElementFactory(typeof(ComboBox));
        combo.SetValue(ItemsControl.ItemsSourceProperty, options);
        combo.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3));
        combo.SetValue(FrameworkElement.MinHeightProperty, 34d);
        combo.SetValue(Control.FontSizeProperty, 12d);
        combo.SetValue(AutomationProperties.NameProperty, "Posição deste diagrama");
        combo.SetBinding(ComboBox.SelectedIndexProperty, new Binding(nameof(DiagramDraft.SelectedIndex))
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        table.Columns.Add(new DataGridTemplateColumn { Header = "Posição do acorde",
            CellTemplate = new DataTemplate { VisualTree = combo }, Width = 295 });

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        var bulk = new WrapPanel { Margin = new Thickness(-4, 0, 0, 0) };
        footer.Children.Add(bulk);
        var same = DialogUi.Button("Aplicar à mesma cifra", (_, _) => ApplyToSameChord());
        same.Name = "ApplySameChordButton";
        same.ToolTip = "Copiar a posição da linha selecionada para todas as ocorrências da mesma cifra.";
        same.IsEnabled = drafts.Count > 0;
        RegisterName(same.Name, same); bulk.Children.Add(same);
        var reset = DialogUi.Button("Usar padrão em todos", (_, _) =>
        {
            foreach (var draft in drafts) draft.Inversion = null;
        });
        reset.Name = "ResetDiagramPositionsButton"; reset.IsEnabled = drafts.Count > 0;
        RegisterName(reset.Name, reset); bulk.Children.Add(reset);
        var suggest = DialogUi.Button("Sugerir posições confortáveis", (_, _) => SuggestComfortablePositions());
        suggest.Name = "SuggestComfortableVoicingsButton"; suggest.IsEnabled = drafts.Count > 0;
        suggest.ToolTip = "Comparar posições que reduzem saltos da mão; suas escolhas individuais serão preservadas.";
        RegisterName(suggest.Name, suggest); bulk.Children.Add(suggest);
        summary = Muted("", 8); footer.Children.Add(summary);
        footer.Children.Add(Muted("A posição conta semínimas desde o início do MIDI. Ao aplicar, a partitura será atualizada automaticamente.", 5));

        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, -4, 0) };
        footer.Children.Add(actions);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false);
        cancel.Name = "CancelDiagramPositionsButton"; cancel.IsCancel = true;
        RegisterName(cancel.Name, cancel); actions.Children.Add(cancel);
        var apply = DialogUi.Button("Aplicar e atualizar", (_, _) => DialogResult = true, true);
        apply.Name = "ApplyDiagramPositionsButton"; apply.IsDefault = true; apply.IsEnabled = drafts.Count > 0;
        RegisterName(apply.Name, apply); actions.Children.Add(apply);

        foreach (var draft in drafts) draft.PropertyChanged += (_, _) => RefreshSummary();
        table.SelectionChanged += (_, _) => same.IsEnabled = table.SelectedItem is DiagramDraft;
        if (drafts.Count > 0) table.SelectedIndex = 0;
        RefreshSummary();
    }

    private void SuggestComfortablePositions()
    {
        var sequence = drafts.Select((draft, index) => new VoicingOccurrence(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"Semínima {draft.Quarter:0.###}", draft.Root, draft.Quality, draft.Inversion ?? defaultInversion,
            draft.Inversion.HasValue, transpose)).ToArray();
        var dialog = new ComfortableVoicingsWindow(sequence, ComfortableMaximumSpan) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        ComfortableMaximumSpan = dialog.Plan.MaximumSpan;
        for (int i = 0; i < drafts.Count; i++)
        {
            var suggestion = dialog.Plan.Suggestions[i];
            if (suggestion.Changed && !drafts[i].Inversion.HasValue)
                drafts[i].Inversion = suggestion.SuggestedInversion;
        }
        RefreshSummary();
    }

    private void ApplyToSameChord()
    {
        if (table.SelectedItem is not DiagramDraft selected) return;
        foreach (var draft in drafts)
            if (NormalizeRoot(draft.Root).Equals(NormalizeRoot(selected.Root), StringComparison.OrdinalIgnoreCase)
                && draft.Quality.Trim().Equals(selected.Quality.Trim(), StringComparison.Ordinal))
                draft.Inversion = selected.Inversion;
    }

    private void RefreshSummary()
    {
        var custom = drafts.Count(draft => draft.Inversion.HasValue);
        summary.Text = drafts.Count == 0 ? "Não há cifras nesta partitura. Analise os acordes primeiro."
            : $"{custom} de {drafts.Count} ocorrências com posição personalizada. As demais seguem o padrão da partitura.";
    }

    private static string NormalizeRoot(string value) => value.Trim().Replace('♯', '#').Replace('♭', 'b');
    private static string InversionLabel(int inversion) => inversion switch
        { 1 => "1ª inversão", 2 => "2ª inversão", _ => "fundamental" };
    private static TextBlock Muted(string text, double topMargin)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, topMargin, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return block;
    }

    private sealed class DiagramDraft : Observable
    {
        public double Quarter { get; }
        public string Root { get; }
        public string Quality { get; }
        public string Symbol => Root + Quality;
        private int? inversion;
        public int? Inversion
        {
            get => inversion;
            set { if (Set(ref inversion, value)) Notify(nameof(SelectedIndex)); }
        }
        public int SelectedIndex
        {
            get => Inversion is int value ? value + 1 : 0;
            set => Inversion = value is >= 1 and <= 3 ? value - 1 : null;
        }
        public DiagramDraft(ChordRow chord)
        {
            Quarter = chord.Quarter; Root = chord.Root; Quality = chord.Quality;
            inversion = chord.DiagramInversion;
        }
    }
}
