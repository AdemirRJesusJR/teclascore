using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Only accepted drafts change the library; measures are the generator's original, stable map.</summary>
public sealed class MelodySectionsWindow : Window
{
    private sealed record SourceOption(SourceKey Key, string Label);
    private sealed record SectionItem(SavedMelodySection Value, string Source)
    {
        public int First => Value.FirstMeasure;
        public int Last => Value.LastMeasure;
    }
    private readonly MelodyMeasureMap map;
    private readonly SourceOption[] sources;
    private readonly ObservableCollection<SectionItem> items = [];
    private readonly DataGrid list;
    private readonly TextBox first, last;
    private readonly ComboBox source;
    private readonly TextBlock validation;
    private readonly Button update, remove, apply;
    public IReadOnlyList<SavedMelodySection> Sections => Settings.CloneMelodySections(items.Select(item => item.Value));

    /// <summary>Automatic proposal of the sections (where the melody passes between parts); shown as a button when set.</summary>
    public Func<IReadOnlyList<SavedMelodySection>>? Propose { get; init; }

    /// <summary>Replaces the list with the automatic proposal; returns how many sections it has.</summary>
    internal int UseProposal()
    {
        var proposal = Propose?.Invoke() ?? [];
        if (proposal.Count == 0) { validation.Text = "A melodia não troca de instrumento nesta música: uma só pista basta."; return 0; }
        items.Clear();
        foreach (var section in Settings.CloneMelodySections(proposal).OrderBy(section => section.FirstMeasure)) items.Add(ToItem(section));
        Refresh();
        validation.Text = $"Proposta automática com {proposal.Count} trecho(s), só com notas do MIDI. Confira e aplique.";
        return proposal.Count;
    }

    public MelodySectionsWindow(MelodyMeasureMap map, IReadOnlyList<TrackRow> tracks, IEnumerable<SavedMelodySection> current, int? initialMeasure = null)
    {
        ArgumentNullException.ThrowIfNull(map); ArgumentNullException.ThrowIfNull(tracks);
        this.map = map;
        sources = tracks.Where(track => track.Channel != 10).DistinctBy(track => track.Source.Key)
            .OrderBy(track => track.Track).ThenBy(track => track.Channel).ThenBy(track => track.Source.Key.Program)
            .Select(track => new SourceOption(track.Source.Key, $"Pista {track.Track} · Canal {track.Channel} · {track.Instrument}")).ToArray();
        foreach (var section in Settings.CloneMelodySections(current).OrderBy(section => section.FirstMeasure))
            items.Add(ToItem(section));
        Title = "Pistas por trecho · TeclaScore"; Width = 1040; Height = 770; MinWidth = 850; MinHeight = 670;
        DialogUi.Theme(this);
        var root = new Grid { Margin = new(24) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new(0, 0, 0, 14) }; root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Escolha a melodia em cada trecho", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Dentro do trecho, somente a fonte escolhida fornece a melodia; seus silêncios são mantidos. Nos demais compassos, valem as pistas e a prioridade geral.",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 8) });
        header.Children.Add(new TextBlock { Name = "MelodySectionsMapSummary", FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = $"{map.Measures.Count} compassos na partitura. O compasso 1 começa em {map.OriginQuarter:0.###} semínimas do MIDI original. Essa numeração é fixa, mesmo quando você troca a fonte." });

        var body = new Grid();
        var bodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(bodyScroll, 1); root.Children.Add(bodyScroll);
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var editor = new Grid { Margin = new(0, 0, 0, 12) };
        editor.ColumnDefinitions.Add(new() { Width = new(110) });
        editor.ColumnDefinitions.Add(new() { Width = new(110) });
        editor.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        editor.RowDefinitions.Add(new() { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.Children.Add(editor);
        first = new TextBox { Name = "MelodySectionsFirst", Text = "1", Margin = new(0, 5, 10, 0), MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
        last = new TextBox { Name = "MelodySectionsLast", Text = Math.Min(8, map.Measures.Count).ToString(CultureInfo.InvariantCulture), Margin = new(0, 5, 10, 0), MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
        source = new ComboBox { Name = "MelodySectionsSource", ItemsSource = sources, DisplayMemberPath = nameof(SourceOption.Label),
            SelectedIndex = sources.Length > 0 ? 0 : -1, Margin = new(0, 5, 0, 0), MinHeight = 34 };
        AddField(editor, 0, "Do compasso", first); AddField(editor, 1, "Até o compasso", last); AddField(editor, 2, "Fonte exclusiva neste trecho", source);
        var editActions = new WrapPanel { Margin = new(0, 8, 0, 0) }; Grid.SetRow(editActions, 1); Grid.SetColumnSpan(editActions, 3); editor.Children.Add(editActions);
        var add = DialogUi.Button("Adicionar trecho", (_, _) => Edit(false)); add.Name = "MelodySectionsAdd"; editActions.Children.Add(add);
        update = DialogUi.Button("Atualizar selecionado", (_, _) => Edit(true)); update.Name = "MelodySectionsUpdate"; editActions.Children.Add(update);
        remove = DialogUi.Button("Remover selecionado", (_, _) => Remove()); remove.Name = "MelodySectionsRemove"; editActions.Children.Add(remove);
        list = new DataGrid { Name = "MelodySectionsList", ItemsSource = items, AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 100, MaxHeight = 245 };
        list.Columns.Add(new DataGridTextColumn { Header = "Do compasso", Binding = new Binding(nameof(SectionItem.First)), Width = new(120) });
        list.Columns.Add(new DataGridTextColumn { Header = "Até o compasso", Binding = new Binding(nameof(SectionItem.Last)), Width = new(130) });
        list.Columns.Add(new DataGridTextColumn { Header = "Fonte exclusiva", Binding = new Binding(nameof(SectionItem.Source)), Width = new(1, DataGridLengthUnitType.Star), MinWidth = 300 });
        Grid.SetRow(list, 1); body.Children.Add(list);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is SectionItem item)
            {
                first.Text = item.First.ToString(CultureInfo.InvariantCulture); last.Text = item.Last.ToString(CultureInfo.InvariantCulture);
                source.SelectedItem = sources.FirstOrDefault(candidate => candidate.Key == Key(item.Value.Source));
            }
            Refresh();
        };

        var mapGrid = new DataGrid { Name = "MelodySectionsMeasureMap", ItemsSource = map.Measures, Height = 145,
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserSortColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new(0, 8, 0, 0) };
        mapGrid.Columns.Add(new DataGridTextColumn { Header = "Compasso", Binding = new Binding(nameof(MelodyMeasure.Number)), Width = new(100) });
        mapGrid.Columns.Add(new DataGridTextColumn { Header = "Fórmula de compasso", Binding = new Binding(nameof(MelodyMeasure.Meter)), Width = new(200) });
        mapGrid.Columns.Add(new DataGridTextColumn { Header = "Semínimas no MIDI original · início–fim", Binding = new Binding(nameof(MelodyMeasure.OriginalPosition)), Width = new(1, DataGridLengthUnitType.Star) });
        var expansion = new Expander { Header = "Conferir numeração e mudanças de compasso", Content = mapGrid, Margin = new(0, 12, 0, 0) };
        Grid.SetRow(expansion, 2); body.Children.Add(expansion);

        var footer = new StackPanel { Margin = new(0, 14, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        validation = new TextBlock { Name = "MelodySectionsValidation", TextWrapping = TextWrapping.Wrap, MinHeight = 34, Margin = new(0, 0, 0, 8), FontSize = 12 };
        footer.Children.Add(validation);
        footer.Children.Add(new TextBlock { Text = "A troca considera os ataques das notas. Uma nota de outra fonte que já começou antes do trecho não será reiniciada. Aplicar exige analisar ou gerar novamente e preserva a partitura anterior no Histórico.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 10) });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        var propose = DialogUi.Button("Propor automaticamente", (_, _) => UseProposal()); propose.Name = "MelodySectionsPropose";
        propose.ToolTip = "Propõe onde a melodia passa de um instrumento para outro (voz, solo, voz), pela letra e pelo desenho melódico";
        Loaded += (_, _) => propose.Visibility = Propose is null ? Visibility.Collapsed : Visibility.Visible;
        actions.Children.Add(propose);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        apply = DialogUi.Button("Aplicar trechos", (_, _) =>
        {
            try { MelodySectionRules.Validate(Sections, map, sources.Select(option => option.Key)); DialogResult = true; }
            catch (ArgumentException error) { validation.Text = error.Message; apply!.IsEnabled = false; }
        }, true);
        apply.Name = "MelodySectionsApply"; apply.IsDefault = true; actions.Children.Add(apply);
        Refresh();
        if (initialMeasure is { } measure && measure >= 1 && measure <= map.Measures.Count)
        {
            // Opening review never changes a saved section. Select an existing
            // covering section, or prefill a one-bar draft that needs Add + Apply.
            var existing = items.FirstOrDefault(item => item.First <= measure && item.Last >= measure);
            if (existing is not null) list.SelectedItem = existing;
            else first.Text = last.Text = measure.ToString(CultureInfo.InvariantCulture);
            validation.Text = $"Revisão do compasso {measure}. Compare as pistas antes de adicionar ou atualizar o trecho.";
        }
    }

    private static SourceKey Key(SavedSource value) => new(value.Track, value.Channel, value.Program);
    private SectionItem ToItem(SavedMelodySection value) => new(value,
        sources.FirstOrDefault(candidate => candidate.Key == Key(value.Source))?.Label ??
        $"Fonte indisponível · pista {value.Source.Track + 1}, canal {value.Source.Channel}, instrumento {value.Source.Program}");
    private static void AddField(Grid parent, int column, string title, Control control)
    {
        var panel = new StackPanel(); Grid.SetColumn(panel, column); parent.Children.Add(panel);
        panel.Children.Add(new TextBlock { Text = title, FontSize = 12 }); panel.Children.Add(control);
    }
    private void Edit(bool replace)
    {
        if (replace && list.SelectedItem is not SectionItem) return;
        try
        {
            if (!int.TryParse(first.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int begin) ||
                !int.TryParse(last.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int end))
                throw new ArgumentException("Informe números inteiros para os compassos inicial e final.");
            if (source.SelectedItem is not SourceOption chosen) throw new ArgumentException("Escolha a fonte da melodia neste trecho.");
            var item = new SavedMelodySection { FirstMeasure = begin, LastMeasure = end,
                Source = new SavedSource { Track = chosen.Key.Track, Channel = chosen.Key.Channel, Program = chosen.Key.Program } };
            var candidate = items.Where(existing => !replace || !ReferenceEquals(existing, list.SelectedItem)).Select(existing => existing.Value).Append(item);
            var validated = MelodySectionRules.Validate(candidate, map, sources.Select(option => option.Key));
            items.Clear(); foreach (var section in validated) items.Add(ToItem(section));
            list.SelectedItem = items.First(row => row.First == begin); list.ScrollIntoView(list.SelectedItem); Refresh();
        }
        catch (ArgumentException error) { validation.Text = error.Message; }
    }
    private void Remove()
    {
        if (list.SelectedItem is SectionItem item) items.Remove(item);
        Refresh();
    }
    private void Refresh()
    {
        update.IsEnabled = remove.IsEnabled = list.SelectedItem is SectionItem;
        try
        {
            MelodySectionRules.Validate(Sections, map, sources.Select(option => option.Key));
            apply.IsEnabled = true;
            validation.Text = items.Count == 0 ? "Nenhum trecho definido: toda a música usará a prioridade geral." :
                $"{items.Count} trecho(s) definidos. É possível escolher fontes que não estejam marcadas na seleção geral.";
        }
        catch (ArgumentException error) { apply.IsEnabled = false; validation.Text = error.Message; }
    }
}
