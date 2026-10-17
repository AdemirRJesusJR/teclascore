using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Teclas.Core;

namespace Teclas.App;

public sealed class DiagramPositionsWindow : Window
{
    private readonly List<DiagramDraft> drafts;
    private readonly VoicingOccurrence[] occurrences;
    private readonly int?[] occurrenceInversions;
    private readonly DataGrid table;
    private readonly TextBlock summary;
    private readonly int defaultInversion;
    private readonly int transpose;

    public IReadOnlyList<int?> Inversions => occurrenceInversions.ToArray();
    public int ComfortableMaximumSpan { get; private set; }
    /// <summary>True when the changed chords should also be applied to other library songs with the same chords.</summary>
    public bool ApplyToLibrary => propagate?.IsChecked == true;
    private readonly CheckBox? propagate;

    public DiagramPositionsWindow(MidiRow row) : this(row, 12) { }

    public DiagramPositionsWindow(MidiRow row, int comfortableMaximumSpan) : this(row, comfortableMaximumSpan, 0, false) { }

    public DiagramPositionsWindow(MidiRow row, int comfortableMaximumSpan, int otherSongsWithSameChords, bool applyToLibrary)
        : this(row, comfortableMaximumSpan, otherSongsWithSameChords, applyToLibrary, null) { }

    /// <param name="initialInversions">Starting choices (the song's own, else the library's remembered positions).</param>
    public DiagramPositionsWindow(MidiRow row, int comfortableMaximumSpan, int otherSongsWithSameChords, bool applyToLibrary, IReadOnlyList<int?>? initialInversions)
    {
        Title = "Posições dos diagramas";
        Width = 900; Height = 730; MinWidth = 800; MinHeight = 580;
        ShowInTaskbar = false; UseLayoutRounding = true;
        DialogUi.Theme(this);
        NameScope.SetNameScope(this, new NameScope());
        defaultInversion = row.EffectiveDiagramInversion;
        transpose = row.TransposeSemitones;
        var previews = new KeyboardDiagramPreview();
        occurrenceInversions = initialInversions is { } initial && initial.Count == row.Chords.Count
            ? initial.ToArray() : row.Chords.Select(chord => chord.DiagramInversion).ToArray();
        int remembered = Enumerable.Range(0, occurrenceInversions.Length)
            .Where(index => row.Chords[index].DiagramInversion is null && occurrenceInversions[index] is not null)
            .Select(index => row.Chords[index].Root + row.Chords[index].Quality).Distinct().Count();
        occurrences = row.Chords.Select((chord, index) => new VoicingOccurrence(
            index.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Semínima {chord.Quarter:0.###}",
            chord.Root, chord.Quality, occurrenceInversions[index] ?? defaultInversion,
            occurrenceInversions[index].HasValue, transpose)).ToArray();
        drafts = Enumerable.Range(0, occurrences.Length)
            .GroupBy(index => ComfortableVoicingService.GetChordGroupKey(occurrences[index]))
            .Select(group => new DiagramDraft(occurrences[group.First()], group.ToArray(), occurrenceInversions,
                defaultInversion, transpose, previews)).ToList();
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
        heading.Children.Add(Muted("Cada cifra aparece uma vez. A posição escolhida vale automaticamente para todas as suas ocorrências na música.", 7));
        heading.Children.Add(Muted("Exemplo em C, da nota mais grave para a mais aguda: fundamental Dó–Mi–Sol · 1ª Mi–Sol–Dó · 2ª Sol–Dó–Mi.", 8));
        if (remembered > 0)
            heading.Children.Add(Muted(remembered == 1 ? "1 cifra já vem com a posição escolhida em outras músicas da biblioteca."
                : $"{remembered} cifras já vêm com as posições escolhidas em outras músicas da biblioteca.", 8));
        if (row.TransposeSemitones != 0)
            heading.Children.Add(Muted($"Cifras no tom de origem; os teclados já mostram a transposição de {transpose:+0;-0;0} semitons no tom final da partitura.", 8));

        var options = new[] { "Usar padrão · " + InversionLabel(row.EffectiveDiagramInversion),
            "Posição fundamental", "1ª inversão", "2ª inversão", "Variações salvas · escolher" };
        table = new DataGrid
        {
            Name = "DiagramPositionsTable", ItemsSource = drafts, AutoGenerateColumns = false,
            CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
            CanUserResizeRows = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, RowHeight = 82, MaxHeight = 600,
            VerticalAlignment = VerticalAlignment.Stretch, EnableRowVirtualization = true
        };
        AutomationProperties.SetName(table, "Posição dos diagramas por cifra, aplicada a todas as ocorrências");
        ScrollViewer.SetVerticalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        RegisterName(table.Name, table);
        Grid.SetRow(table, 1); root.Children.Add(table);
        table.Columns.Add(new DataGridTextColumn { Header = "Ocorrências",
            Binding = new Binding(nameof(DiagramDraft.OccurrenceCount)), Width = 100 });
        var chordPanel = new FrameworkElementFactory(typeof(StackPanel));
        chordPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        chordPanel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var symbol = new FrameworkElementFactory(typeof(TextBlock));
        symbol.SetBinding(TextBlock.TextProperty, new Binding(nameof(DiagramDraft.Symbol)));
        symbol.SetValue(FrameworkElement.WidthProperty, 80d);
        symbol.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        symbol.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        symbol.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        chordPanel.AppendChild(symbol);
        var previewPanel = new FrameworkElementFactory(typeof(StackPanel));
        previewPanel.SetValue(FrameworkElement.WidthProperty, 200d);
        previewPanel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        previewPanel.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(DiagramDraft.PreviewDescription)));
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetValue(FrameworkElement.HeightProperty, 60d);
        image.SetValue(Image.StretchProperty, Stretch.Uniform);
        image.SetBinding(Image.SourceProperty, new Binding(nameof(DiagramDraft.PreviewImage)));
        image.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(DiagramDraft.PreviewVisibility)));
        image.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(DiagramDraft.PreviewDescription)));
        previewPanel.AppendChild(image);
        var unavailable = new FrameworkElementFactory(typeof(TextBlock));
        unavailable.SetBinding(TextBlock.TextProperty, new Binding(nameof(DiagramDraft.PreviewPlaceholder)));
        unavailable.SetValue(TextBlock.FontSizeProperty, 11d);
        unavailable.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        unavailable.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(DiagramDraft.PreviewUnavailableVisibility)));
        previewPanel.AppendChild(unavailable);
        chordPanel.AppendChild(previewPanel);
        table.Columns.Add(new DataGridTemplateColumn { Header = "Cifra · teclado",
            CellTemplate = new DataTemplate { VisualTree = chordPanel },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 300 });

        var combo = new FrameworkElementFactory(typeof(ComboBox));
        combo.SetValue(ItemsControl.ItemsSourceProperty, options);
        combo.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3));
        combo.SetValue(FrameworkElement.MinHeightProperty, 34d);
        combo.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        combo.SetValue(Control.FontSizeProperty, 12d);
        combo.SetValue(AutomationProperties.NameProperty, "Posição deste diagrama");
        var optionStyle = new Style(typeof(ComboBoxItem));
        var mixedOption = new DataTrigger { Binding = new Binding(), Value = options[4] };
        mixedOption.Setters.Add(new Setter(UIElement.IsEnabledProperty, false));
        optionStyle.Triggers.Add(mixedOption);
        combo.SetValue(ItemsControl.ItemContainerStyleProperty, optionStyle);
        combo.SetBinding(ComboBox.SelectedIndexProperty, new Binding(nameof(DiagramDraft.SelectedIndex))
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        table.Columns.Add(new DataGridTemplateColumn { Header = "Posição do acorde",
            CellTemplate = new DataTemplate { VisualTree = combo }, Width = 235 });

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        var bulk = new WrapPanel { Margin = new Thickness(-4, 0, 0, 0) };
        footer.Children.Add(bulk);
        var reset = DialogUi.Button("Usar padrão em todos", (_, _) =>
        {
            foreach (var draft in drafts) draft.Inversion = null;
        });
        reset.Name = "ResetDiagramPositionsButton"; reset.IsEnabled = drafts.Count > 0;
        RegisterName(reset.Name, reset); bulk.Children.Add(reset);
        var suggest = DialogUi.Button("Sugerir posições confortáveis", (_, _) => SuggestComfortablePositions());
        suggest.Name = "SuggestComfortableVoicingsButton"; suggest.IsEnabled = drafts.Count > 0;
        suggest.ToolTip = "Sugerir uma posição por cifra, considerando a sequência da música e preservando suas escolhas salvas.";
        RegisterName(suggest.Name, suggest); bulk.Children.Add(suggest);
        summary = Muted("", 8); footer.Children.Add(summary);
        if (otherSongsWithSameChords > 0)
        {
            propagate = new CheckBox { Name = "PropagateDiagramPositionsCheckBox", IsChecked = applyToLibrary, Margin = new Thickness(0, 10, 0, 0),
                Content = otherSongsWithSameChords == 1 ? "Aplicar também à outra música da biblioteca que tem as mesmas cifras"
                    : $"Aplicar também às outras {otherSongsWithSameChords} músicas da biblioteca que têm as mesmas cifras",
                ToolTip = "Só as cifras alteradas aqui mudam nas outras músicas, comparando o acorde tocado (nota e tipo, já com a transposição). As partituras geradas são atualizadas em seguida e as versões anteriores ficam no Histórico." };
            AutomationProperties.SetName(propagate, "Aplicar às outras músicas com as mesmas cifras");
            RegisterName(propagate.Name, propagate); footer.Children.Add(propagate);
        }
        footer.Children.Add(Muted("Ao aplicar, todas as ocorrências das cifras alteradas serão atualizadas na partitura, e as posições ficam guardadas para as cifras das próximas músicas analisadas. Cancelar mantém as escolhas anteriores.", 5));

        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, -4, 0) };
        footer.Children.Add(actions);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false);
        cancel.Name = "CancelDiagramPositionsButton"; cancel.IsCancel = true;
        RegisterName(cancel.Name, cancel); actions.Children.Add(cancel);
        var apply = DialogUi.Button("Aplicar e atualizar", (_, _) => DialogResult = true, true);
        apply.Name = "ApplyDiagramPositionsButton"; apply.IsDefault = true; apply.IsEnabled = drafts.Count > 0;
        RegisterName(apply.Name, apply); actions.Children.Add(apply);

        foreach (var draft in drafts) draft.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DiagramDraft.Inversion)) RefreshSummary();
        };
        if (drafts.Count > 0) table.SelectedIndex = 0;
        RefreshSummary();
    }

    private void SuggestComfortablePositions()
    {
        var sequence = occurrences.Select((source, index) => source with
        {
            CurrentInversion = occurrenceInversions[index] ?? defaultInversion,
            PreserveChoice = occurrenceInversions[index].HasValue
        }).ToArray();
        var dialog = new ComfortableVoicingsWindow(sequence, ComfortableMaximumSpan, groupRepeatedChords: true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        ComfortableMaximumSpan = dialog.Plan.MaximumSpan;
        foreach (var draft in drafts)
        {
            if (draft.HasMixedPositions || draft.ContainsCustomChoices) continue;
            var suggestions = draft.Indices.Select(index => dialog.Plan.Suggestions[index]).ToArray();
            var choices = suggestions.Select(suggestion => suggestion.SuggestedInversion).Distinct().ToArray();
            if (choices.Length == 1 && suggestions.Any(suggestion => suggestion.Changed))
                draft.Inversion = choices[0];
        }
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        int custom = drafts.Count(draft => !draft.HasMixedPositions && draft.Inversion.HasValue);
        int mixed = drafts.Count(draft => draft.HasMixedPositions);
        summary.Text = drafts.Count == 0 ? "Não há cifras nesta partitura. Analise os acordes primeiro."
            : $"{drafts.Count} cifras · {occurrences.Length} ocorrências · {custom} cifras com posição personalizada."
                + (mixed > 0 ? $" {mixed} cifras têm variações salvas; escolha uma posição para unificá-las." : "");
    }

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
        private readonly int defaultInversion, transpose;
        private readonly KeyboardDiagramPreview previews;
        private readonly int?[] occurrenceInversions;
        public int[] Indices { get; }
        public int OccurrenceCount => Indices.Length;
        public bool HasMixedPositions => Indices.Any(index => occurrenceInversions[index] != occurrenceInversions[Indices[0]]);
        public bool ContainsCustomChoices => Indices.Any(index => occurrenceInversions[index].HasValue);
        public string Root { get; }
        public string Quality { get; }
        public string Symbol => Root + Quality;
        public DrawingImage? PreviewImage => HasMixedPositions ? null
            : previews.GetImage(Root, Quality, Inversion ?? defaultInversion, transpose);
        public string PreviewDescription => HasMixedPositions
            ? $"{Symbol}: há configurações diferentes salvas. Escolha uma posição para aplicar às {OccurrenceCount} ocorrências."
            : KeyboardDiagramPreview.Describe(Root, Quality, Inversion ?? defaultInversion, transpose);
        public string PreviewPlaceholder => HasMixedPositions ? "Variações salvas\nEscolha uma posição."
            : "Diagrama indisponível\nConfira a cifra.";
        public Visibility PreviewVisibility => PreviewImage is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PreviewUnavailableVisibility => PreviewImage is null ? Visibility.Visible : Visibility.Collapsed;
        public int? Inversion
        {
            get => HasMixedPositions ? null : occurrenceInversions[Indices[0]];
            set
            {
                if (value is < 0 or > 2) return;
                if (Indices.All(index => occurrenceInversions[index] == value)) return;
                foreach (int index in Indices) occurrenceInversions[index] = value;
                Notify();
                Notify(nameof(HasMixedPositions));
                Notify(nameof(ContainsCustomChoices));
                Notify(nameof(SelectedIndex));
                Notify(nameof(PreviewImage));
                Notify(nameof(PreviewDescription));
                Notify(nameof(PreviewPlaceholder));
                Notify(nameof(PreviewVisibility));
                Notify(nameof(PreviewUnavailableVisibility));
            }
        }
        public int SelectedIndex
        {
            get => HasMixedPositions ? 4 : Inversion is int value ? value + 1 : 0;
            set { if (value is >= 0 and <= 3) Inversion = value == 0 ? null : value - 1; }
        }
        public DiagramDraft(VoicingOccurrence chord, int[] indices, int?[] occurrenceInversions,
            int defaultInversion, int transpose, KeyboardDiagramPreview previews)
        {
            this.defaultInversion = defaultInversion; this.transpose = transpose; this.previews = previews;
            Indices = indices; this.occurrenceInversions = occurrenceInversions;
            string root = chord.Root.Trim().Replace('♯', '#').Replace('♭', 'b');
            Root = root.Length > 0 ? char.ToUpperInvariant(root[0]) + root[1..] : root;
            Quality = chord.Quality.Trim();
        }
    }
}
