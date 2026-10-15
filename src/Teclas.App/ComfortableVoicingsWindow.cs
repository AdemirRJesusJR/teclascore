using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Read-only comparison until the user explicitly accepts the proposal.</summary>
public sealed class ComfortableVoicingsWindow : Window
{
    private readonly IReadOnlyList<VoicingOccurrence> sequence;
    private readonly ComboBox reach;
    private readonly DataGrid table;
    private readonly TextBlock summary;
    private readonly Button apply;
    private readonly bool allowReleasingChoices;
    private readonly Dictionary<int, bool> preservation = [];
    public ComfortableVoicingPlan Plan { get; private set; }

    public ComfortableVoicingsWindow(IReadOnlyList<VoicingOccurrence> sequence, int maximumSpan = 12, bool allowReleasingChoices = false)
    {
        this.sequence = sequence.ToArray();
        this.allowReleasingChoices = allowReleasingChoices;
        maximumSpan = maximumSpan is 7 or 9 or 10 or 12 ? maximumSpan : 12;
        Plan = ComfortableVoicingService.Suggest(this.sequence, maximumSpan);
        Title = "Sugerir posições confortáveis";
        Width = 1080; Height = 740; MinWidth = 770; MinHeight = 590;
        ShowInTaskbar = false; UseLayoutRounding = true;
        DialogUi.Theme(this); NameScope.SetNameScope(this, new NameScope());
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) }; root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "Menos saltos entre os acordes", FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(Muted("Compare as notas dos diagramas no tom final. A sugestão combina posições fundamentais, primeiras e segundas inversões, mantendo todas as notas do acorde.", 7));
        heading.Children.Add(Muted("Suas escolhas individuais permanecem fixas. Alcance e movimento são medidas entre notas; não substituem experimentar a posição no teclado.", 7));
        if (allowReleasingChoices)
            heading.Children.Add(Muted("As posições da partitura importada começam protegidas. Desmarque Preservar somente nas ocorrências para as quais deseja receber uma sugestão.", 7));
        var options = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; heading.Children.Add(options);
        options.Children.Add(new TextBlock { Text = "Alcance máximo da mão", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        reach = new ComboBox { Name = "ComfortableVoicingsReach", Width = 235, MinHeight = 36,
            ItemsSource = new[] { new Reach(7, "7 semitons · quinta justa"), new Reach(9, "9 semitons · sexta maior"),
                new Reach(10, "10 semitons · sétima menor"), new Reach(12, "12 semitons · oitava") },
            DisplayMemberPath = nameof(Reach.Label), SelectedValuePath = nameof(Reach.Semitones), SelectedValue = maximumSpan };
        RegisterName(reach.Name, reach); AutomationProperties.SetName(reach, "Alcance máximo entre a nota mais grave e a mais aguda"); options.Children.Add(reach);

        table = new DataGrid { Name = "ComfortableVoicingsComparison", IsReadOnly = true, AutoGenerateColumns = false,
            CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, CanUserResizeRows = false,
            SelectionMode = DataGridSelectionMode.Single, EnableRowVirtualization = true, RowHeight = double.NaN, MinRowHeight = 48 };
        RegisterName(table.Name, table); AutomationProperties.SetName(table, "Comparação das posições atuais e sugeridas");
        if (allowReleasingChoices)
        {
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(ComparisonRow.Preserve)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(AutomationProperties.NameProperty, "Preservar a posição atual desta ocorrência");
            table.Columns.Add(new DataGridTemplateColumn { Header = "Preservar", CellTemplate = new DataTemplate { VisualTree = check }, Width = 75, MinWidth = 75 });
        }
        table.Columns.Add(new DataGridTextColumn { Header = "Trecho / cifra", Binding = new Binding(nameof(ComparisonRow.Label)), Width = 135, MinWidth = 135 });
        table.Columns.Add(new DataGridTextColumn { Header = "Atual · grave → agudo", Binding = new Binding(nameof(ComparisonRow.Before)), Width = 215, MinWidth = 215 });
        table.Columns.Add(new DataGridTextColumn { Header = "Sugestão · grave → agudo", Binding = new Binding(nameof(ComparisonRow.After)), Width = 215, MinWidth = 215 });
        table.Columns.Add(new DataGridTextColumn { Header = "Alcance", Binding = new Binding(nameof(ComparisonRow.Span)), Width = 95, MinWidth = 95 });
        var wrap = new Style(typeof(TextBlock)); wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        table.Columns.Add(new DataGridTextColumn { Header = "Conferência", Binding = new Binding(nameof(ComparisonRow.Status)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 165, ElementStyle = wrap });
        Grid.SetRow(table, 1); root.Children.Add(table);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        summary = Muted("", 0); summary.Name = "ComfortableVoicingsSummary"; RegisterName(summary.Name, summary); footer.Children.Add(summary);
        footer.Children.Add(Muted("Esta comparação modifica apenas as posições dos diagramas. A melodia, o ritmo, as cifras e a reprodução musical permanecem os mesmos.", 8));
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, -4, 0) }; footer.Children.Add(actions);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true;
        cancel.Name = "CancelComfortableVoicingsButton"; RegisterName(cancel.Name, cancel); actions.Children.Add(cancel);
        apply = DialogUi.Button("Usar sugestões", (_, _) => DialogResult = true, true); apply.IsDefault = true;
        apply.Name = "ApplyComfortableVoicingsButton"; RegisterName(apply.Name, apply); actions.Children.Add(apply);
        reach.SelectionChanged += (_, _) => Recalculate();
        RefreshPlan();
    }

    private void Recalculate()
    {
        if (reach.SelectedItem is not Reach selected) return;
        Plan = ComfortableVoicingService.Suggest(sequence.Select((item, index) => preservation.TryGetValue(index, out bool preserve)
            ? item with { PreserveChoice = preserve } : item).ToArray(), selected.Semitones);
        RefreshPlan();
    }
    private void RefreshPlan()
    {
        table.ItemsSource = Plan.Suggestions.Select((s, index) => new ComparisonRow(s, value =>
        {
            if (!allowReleasingChoices) return;
            preservation[index] = value; Recalculate();
        })).ToArray();
        apply.IsEnabled = Plan.ChangeCount > 0;
        string before = Plan.BeforeMovement.ToString("0.#", CultureInfo.CurrentCulture);
        string after = Plan.AfterMovement.ToString("0.#", CultureInfo.CurrentCulture);
        int outside = Plan.Suggestions.Count(s => s.Span > Plan.MaximumSpan);
        string comparison = Plan.AfterMovement < Plan.BeforeMovement - .000001
            ? "Menor movimento estimado na sequência."
            : Plan.AfterMovement > Plan.BeforeMovement + .000001
                ? "O alcance escolhido exige mais movimento entre acordes; confira a comparação."
                : "Movimento estimado igual ao atual.";
        summary.Text = $"{Plan.ChangeCount} posições sugeridas · {Plan.PreservedCount} escolhas individuais preservadas. " +
            $"Movimento acumulado: {before} → {after} (média por transição, somada em semitons). {comparison}" +
            (outside > 0 ? $" {outside} ocorrências ainda excedem o alcance: foram mantidas e estão indicadas acima." : "") +
            (Plan.ChangeCount == 0 ? " Nenhuma mudança necessária nas posições disponíveis." : "");
    }
    private static TextBlock Muted(string text, double top)
    {
        var result = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        result.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return result;
    }
    private sealed record Reach(int Semitones, string Label);
    private sealed class ComparisonRow(VoicingSuggestion item, Action<bool> changed) : Observable
    {
        private bool preserve = item.Source.PreserveChoice;
        public bool Preserve { get => preserve; set { if (Set(ref preserve, value)) changed(value); } }
        public string Label => item.Source.Location + "\n" + item.Source.Root + item.Source.Quality +
            (item.Source.TransposeSemitones == 0 ? "" : $" ({item.Source.TransposeSemitones:+0;-0} semitons)");
        public string Before => Inversion(item.Source.CurrentInversion) + "\n" + Notes(item.BeforePitches);
        public string After => Inversion(item.SuggestedInversion) + "\n" + Notes(item.AfterPitches);
        public string Span => item.AfterPitches.Count == 0 ? "—" : item.Span + " semitons";
        public string Status => item.Limitation.Length > 0 ? item.Limitation : item.Changed ? "Nova posição sugerida" : "Manter posição atual";
        private static string Inversion(int value) => value switch { 0 => "Fundamental", 1 => "1ª inversão", _ => "2ª inversão" };
        private static string Notes(IReadOnlyList<int> pitches) => pitches.Count == 0 ? "Não reconhecido" : string.Join("–", pitches.Select(p =>
            new[] { "Dó", "Dó♯", "Ré", "Ré♯", "Mi", "Fá", "Fá♯", "Sol", "Sol♯", "Lá", "Lá♯", "Si" }[p % 12] + (p / 12 - 1)));
    }
}
