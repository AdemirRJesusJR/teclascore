using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
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
    /// <param name="savedPosition">The position the app has saved for a chord (root, quality), shown as a badge on each row.</param>
    public DiagramPositionsWindow(MidiRow row, int comfortableMaximumSpan, int otherSongsWithSameChords, bool applyToLibrary, IReadOnlyList<int?>? initialInversions,
        Func<string, string, int?>? savedPosition = null)
    {
        Title = "Posições dos diagramas";
        Width = 920; Height = 760; MinWidth = 800; MinHeight = 600;
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
                defaultInversion, transpose, previews, savedPosition)).ToList();
        ComfortableMaximumSpan = comfortableMaximumSpan is 7 or 9 or 10 or 12 ? comfortableMaximumSpan : 12;

        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(heading);
        var titleRow = new DockPanel();
        heading.Children.Add(titleRow);
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 14, 0),
            Child = Glyph("\uEC4F", 19, "AccentInk") };
        tile.SetResourceReference(Border.BackgroundProperty, "Accent");
        DockPanel.SetDock(tile, Dock.Left); titleRow.Children.Add(tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(titles);
        titles.Children.Add(new TextBlock { Text = "Escolha a posição de cada acorde", FontSize = 22,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        titles.Children.Add(Muted("Cada cifra aparece uma vez. A posição escolhida vale automaticamente para todas as suas ocorrências na música.", 2));
        // Inversions explained with C, from the lowest to the highest note.
        var examples = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(examples, "Exemplo em C, da nota mais grave para a mais aguda: fundamental Dó–Mi–Sol · 1ª Mi–Sol–Dó · 2ª Sol–Dó–Mi.");
        heading.Children.Add(examples);
        var exampleLabel = Muted("Exemplo em C, do grave ao agudo:", 0); exampleLabel.VerticalAlignment = VerticalAlignment.Center; exampleLabel.Margin = new Thickness(0, 0, 8, 0);
        examples.Children.Add(exampleLabel);
        foreach (var (name, notes) in new[] { ("Fundamental", "Dó–Mi–Sol"), ("1ª inversão", "Mi–Sol–Dó"), ("2ª inversão", "Sol–Dó–Mi") })
            examples.Children.Add(Chip(name, notes));
        // The app always keeps the chosen positions; say so where the choices are made.
        var saved = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(1) };
        saved.SetResourceReference(Border.BackgroundProperty, "Selection");
        saved.SetResourceReference(Border.BorderBrushProperty, "SelectionBorder");
        var savedRow = new DockPanel();
        var savedIcon = Glyph("\uE74E", 16, "Accent"); savedIcon.Margin = new Thickness(0, 1, 10, 0); savedIcon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(savedIcon, Dock.Left); savedRow.Children.Add(savedIcon);
        var savedText = new StackPanel();
        savedText.Children.Add(new TextBlock { Text = "As posições ficam salvas no app", FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
        savedText.Children.Add(Muted("Ao aplicar, cada posição fica guardada e é usada automaticamente nas cifras iguais das próximas músicas analisadas, inclusive as adicionadas depois."
            + (remembered == 0 ? "" : remembered == 1 ? " 1 cifra já vem com a posição salva de outra música." : $" {remembered} cifras já vêm com as posições salvas de outras músicas."), 2));
        savedRow.Children.Add(savedText); saved.Child = savedRow;
        AutomationProperties.SetName(saved, "As posições ficam salvas no app");
        heading.Children.Add(saved);
        if (row.TransposeSemitones != 0)
            heading.Children.Add(Muted($"Cifras no tom de origem; os teclados já mostram a transposição de {transpose:+0;-0;0} semitons no tom final da partitura.", 8));

        var options = new[] { "Usar padrão · " + InversionLabel(row.EffectiveDiagramInversion),
            "Posição fundamental", "1ª inversão", "2ª inversão", "Variações salvas · escolher" };
        table = new DataGrid
        {
            Name = "DiagramPositionsTable", ItemsSource = drafts, AutoGenerateColumns = false,
            CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
            CanUserResizeRows = false, CanUserResizeColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, RowHeight = 88, MaxHeight = 600,
            VerticalAlignment = VerticalAlignment.Stretch, EnableRowVirtualization = true,
            GridLinesVisibility = DataGridGridLinesVisibility.None, HeadersVisibility = DataGridHeadersVisibility.Column,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, RowBackground = Brushes.Transparent,
            AlternatingRowBackground = Brushes.Transparent
        };
        table.ColumnHeaderStyle = HeaderStyle();
        table.RowStyle = RowStyle();
        table.CellStyle = CellStyle();
        AutomationProperties.SetName(table, "Posição dos diagramas por cifra, aplicada a todas as ocorrências");
        ScrollViewer.SetVerticalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Disabled);
        RegisterName(table.Name, table);
        Grid.SetRow(table, 1); root.Children.Add(table);
        var countPill = new FrameworkElementFactory(typeof(Border));
        countPill.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        countPill.SetValue(Border.PaddingProperty, new Thickness(9, 3, 9, 3));
        countPill.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        countPill.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        countPill.SetResourceReference(Border.BackgroundProperty, "Soft");
        countPill.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(DiagramDraft.OccurrenceCount)) { StringFormat = "{0} ocorrências na música" });
        var countText = new FrameworkElementFactory(typeof(TextBlock));
        countText.SetBinding(TextBlock.TextProperty, new Binding(nameof(DiagramDraft.OccurrenceCount)) { StringFormat = "{0}×" });
        countText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        countText.SetValue(TextBlock.FontSizeProperty, 12d);
        countText.SetResourceReference(TextBlock.ForegroundProperty, "SoftInk");
        countPill.AppendChild(countText);
        table.Columns.Add(new DataGridTemplateColumn { Header = "Ocorrências",
            CellTemplate = new DataTemplate { VisualTree = countPill }, Width = 100 });
        var chordPanel = new FrameworkElementFactory(typeof(StackPanel));
        chordPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        chordPanel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var symbol = new FrameworkElementFactory(typeof(TextBlock));
        symbol.SetBinding(TextBlock.TextProperty, new Binding(nameof(DiagramDraft.Symbol)));
        symbol.SetValue(FrameworkElement.WidthProperty, 84d);
        symbol.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        symbol.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        symbol.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        symbol.SetValue(TextBlock.FontSizeProperty, 19d);
        chordPanel.AppendChild(symbol);
        var previewFrame = new FrameworkElementFactory(typeof(Border));
        previewFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        previewFrame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        previewFrame.SetValue(Border.PaddingProperty, new Thickness(6, 4, 6, 4));
        previewFrame.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        previewFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        previewFrame.SetValue(Border.BackgroundProperty, Brushes.White);
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
        unavailable.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x44, 0x4C, 0x58)));
        unavailable.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(DiagramDraft.PreviewUnavailableVisibility)));
        previewPanel.AppendChild(unavailable);
        previewFrame.AppendChild(previewPanel);
        chordPanel.AppendChild(previewFrame);
        table.Columns.Add(new DataGridTemplateColumn { Header = "Cifra · teclado",
            CellTemplate = new DataTemplate { VisualTree = chordPanel },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 296 });

        var positionPanel = new FrameworkElementFactory(typeof(StackPanel));
        positionPanel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        positionPanel.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        var combo = new FrameworkElementFactory(typeof(ComboBox));
        combo.SetValue(ItemsControl.ItemsSourceProperty, options);
        combo.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 5));
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
        positionPanel.AppendChild(combo);
        // Where this position stands in the app's memory.
        var badge = new FrameworkElementFactory(typeof(StackPanel));
        badge.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        badge.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(DiagramDraft.SavedDescription)));
        badge.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(DiagramDraft.SavedDescription)));
        var badgeIcon = new FrameworkElementFactory(typeof(TextBlock));
        badgeIcon.SetBinding(TextBlock.TextProperty, new Binding(nameof(DiagramDraft.SavedGlyph)));
        badgeIcon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        badgeIcon.SetValue(TextBlock.FontSizeProperty, 11d);
        badgeIcon.SetValue(FrameworkElement.MarginProperty, new Thickness(1, 1, 6, 0));
        badgeIcon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badgeIcon.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(DiagramDraft.SavedBrush)));
        badge.AppendChild(badgeIcon);
        var badgeText = new FrameworkElementFactory(typeof(TextBlock));
        badgeText.SetBinding(TextBlock.TextProperty, new Binding(nameof(DiagramDraft.SavedLabel)));
        badgeText.SetValue(TextBlock.FontSizeProperty, 11.5d);
        badgeText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badgeText.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(DiagramDraft.SavedBrush)));
        badge.AppendChild(badgeText);
        positionPanel.AppendChild(badge);
        table.Columns.Add(new DataGridTemplateColumn { Header = "Posição do acorde",
            CellTemplate = new DataTemplate { VisualTree = positionPanel }, Width = 250 });

        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
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
            propagate = new CheckBox { Name = "PropagateDiagramPositionsCheckBox", IsChecked = applyToLibrary, Margin = new Thickness(0, 8, 0, 0),
                Content = otherSongsWithSameChords == 1 ? "Aplicar também à outra música da biblioteca que tem as mesmas cifras"
                    : $"Aplicar também às outras {otherSongsWithSameChords} músicas da biblioteca que têm as mesmas cifras",
                ToolTip = "Só as cifras alteradas aqui mudam nas outras músicas, comparando o acorde tocado (nota e tipo, já com a transposição). As partituras geradas são atualizadas em seguida e as versões anteriores ficam no Histórico." };
            AutomationProperties.SetName(propagate, "Aplicar às outras músicas com as mesmas cifras");
            RegisterName(propagate.Name, propagate); footer.Children.Add(propagate);
        }
        var bar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        footer.Children.Add(bar);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, -4, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(actions, Dock.Right); bar.Children.Add(actions);
        var note = Muted("Ao aplicar, todas as ocorrências das cifras alteradas são atualizadas na partitura e as posições ficam salvas no app. Cancelar mantém as escolhas anteriores.", 0);
        note.VerticalAlignment = VerticalAlignment.Center; bar.Children.Add(note);
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

    private static TextBlock Glyph(string glyph, double size, string brush)
    {
        var block = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }
    private static Border Chip(string name, string notes)
    {
        var text = new TextBlock { FontSize = 11.5 };
        text.Inlines.Add(new Run(name) { FontWeight = FontWeights.SemiBold });
        var detail = new Run("  " + notes); detail.SetResourceReference(TextElement.ForegroundProperty, "Muted");
        text.Inlines.Add(detail);
        var chip = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(0, 2, 6, 2), Child = text, BorderThickness = new Thickness(1) };
        chip.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        chip.SetResourceReference(Border.BorderBrushProperty, "Line");
        return chip;
    }
    private static Style HeaderStyle()
    {
        var style = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 11.5));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(13, 4, 8, 6)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Muted")));
        return style;
    }
    private static Style RowStyle()
    {
        var style = new Style(typeof(DataGridRow));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 3, 6, 3)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Line")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("CardBrush")));
        var template = new ControlTemplate(typeof(DataGridRow));
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        frame.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        frame.AppendChild(new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.DataGridCellsPresenter)));
        template.VisualTree = frame;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("SelectionBorder")));
        style.Triggers.Add(hover);
        var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Selection")));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Accent")));
        style.Triggers.Add(selected);
        return style;
    }
    private static Style CellStyle()
    {
        var style = new Style(typeof(DataGridCell));
        var template = new ControlTemplate(typeof(DataGridCell));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 4, 0));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = content;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink")));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        return style;
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
        private readonly int? savedInversion;
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
        /// <summary>True when the chosen position is the one the app already keeps for this chord.</summary>
        public bool IsSaved => !HasMixedPositions && Inversion is { } value && value == savedInversion;
        public string SavedLabel => HasMixedPositions ? "Escolha uma para salvar"
            : IsSaved ? "Salva no app" : Inversion is null ? savedInversion is null ? "Padrão da música" : "Remove a posição salva"
            : "Será salva ao aplicar";
        public string SavedGlyph => IsSaved ? "\uE73E" : HasMixedPositions || Inversion is not null ? "\uE74E" : "\uE946";
        public Brush SavedBrush => (Application.Current?.TryFindResource(IsSaved ? "Ready" : Inversion is null && !HasMixedPositions ? "Muted" : "Accent") as Brush)
            ?? Brushes.Gray;
        public string SavedDescription => IsSaved ? $"{Symbol}: esta posição está salva no app e é usada nas próximas músicas com esta cifra."
            : Inversion is null && !HasMixedPositions ? savedInversion is null ? $"{Symbol}: usa a posição padrão da música."
                : $"{Symbol}: ao aplicar, volta ao padrão e deixa de usar a posição salva."
            : $"{Symbol}: ao aplicar, esta posição fica salva no app para as próximas músicas.";
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
                Notify(nameof(IsSaved)); Notify(nameof(SavedLabel)); Notify(nameof(SavedGlyph));
                Notify(nameof(SavedBrush)); Notify(nameof(SavedDescription));
            }
        }
        public int SelectedIndex
        {
            get => HasMixedPositions ? 4 : Inversion is int value ? value + 1 : 0;
            set { if (value is >= 0 and <= 3) Inversion = value == 0 ? null : value - 1; }
        }
        public DiagramDraft(VoicingOccurrence chord, int[] indices, int?[] occurrenceInversions,
            int defaultInversion, int transpose, KeyboardDiagramPreview previews, Func<string, string, int?>? savedPosition)
        {
            this.defaultInversion = defaultInversion; this.transpose = transpose; this.previews = previews;
            Indices = indices; this.occurrenceInversions = occurrenceInversions;
            string root = chord.Root.Trim().Replace('♯', '#').Replace('♭', 'b');
            Root = root.Length > 0 ? char.ToUpperInvariant(root[0]) + root[1..] : root;
            Quality = chord.Quality.Trim();
            savedInversion = savedPosition?.Invoke(chord.Root, chord.Quality);
        }
    }
}
