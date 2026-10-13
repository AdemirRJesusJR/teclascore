using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

public sealed class ScoreDiagnosticsWindow : Window
{
    public ScoreDiagnosticsWindow(ScoreDiagnosticReport report, bool canImprove)
    {
        Title = "Diagnóstico da partitura · TeclaScore"; Width = 1010; Height = 720; MinWidth = 780; MinHeight = 520;
        DialogUi.Theme(this);
        var root = new Grid { Margin = new(24) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new(0, 0, 0, 16) }; root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "Conferência da partitura", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(report.Metadata.Title) ? "Partitura sem título" : report.Metadata.Title, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) });
        heading.Children.Add(new TextBlock { Text = $"{report.Metadata.StaffCount} pautas · {report.Metadata.MeasureCount} compassos · {report.Metadata.NoteCount} notas · {report.Metadata.HarmonyCount} cifras", FontSize = 12 });
        heading.Children.Add(new TextBlock { Text = "Os avisos indicam pontos para conferir. As sugestões não modificam o arquivo.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        var grid = new DataGrid { Name = "ScoreDiagnosticsGrid", ItemsSource = report.Issues.Select(issue => new IssueView(issue)).ToArray(), AutoGenerateColumns = false,
            IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single,
            RowHeight = double.NaN, MinRowHeight = 44, SelectedIndex = report.Issues.Count > 0 ? 0 : -1 };
        var wrap = new Style(typeof(TextBlock)); wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); wrap.Setters.Add(new Setter(MarginProperty, new Thickness(6)));
        grid.Columns.Add(new DataGridTextColumn { Header = "Prioridade", Binding = new Binding(nameof(IssueView.Severity)), Width = 110, ElementStyle = wrap });
        grid.Columns.Add(new DataGridTextColumn { Header = "Local", Binding = new Binding(nameof(IssueView.Location)), Width = 170, ElementStyle = wrap });
        grid.Columns.Add(new DataGridTextColumn { Header = "O que conferir", Binding = new Binding(nameof(IssueView.Message)), Width = new(1, DataGridLengthUnitType.Star), ElementStyle = wrap });
        Grid.SetRow(grid, 1); root.Children.Add(grid);
        var detail = new StackPanel { Margin = new(0, 14, 0, 0) }; Grid.SetRow(detail, 2); root.Children.Add(detail);
        var advice = new TextBlock { Name = "DiagnosticSuggestion", TextWrapping = TextWrapping.Wrap, MinHeight = 40, MaxHeight = 86 };
        detail.Children.Add(advice);
        void Selection() => advice.Text = grid.SelectedItem is IssueView selected ? selected.Issue.Suggestion
            : "Nenhum problema identificado nos itens verificados. Confira também a música e a aparência das páginas.";
        grid.SelectionChanged += (_, _) => Selection(); Selection();
        var limits = new TextBlock { Text = string.Join("\n", report.Limitations), TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        limits.SetResourceReference(ForegroundProperty, "Muted");
        var limitsScroll = new ScrollViewer { Content = limits, MaxHeight = 48, Padding = new(0, 6, 12, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = true };
        AutomationProperties.SetName(limitsScroll, "Detalhes dos limites da análise");
        var limitsPanel = new Expander { Name = "DiagnosticLimitations", Header = "Limites da análise", IsExpanded = false,
            Content = limitsScroll, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new(0, 8, 0, 0) };
        AutomationProperties.SetName(limitsPanel, "Limites da análise");
        AutomationProperties.SetHelpText(limitsPanel, "Expanda para conhecer as verificações que não foram realizadas. Use a rolagem para ler todos os detalhes.");
        // Preserve a complete finding row even at the minimum window size. Expanded limitations
        // remain reachable with the keyboard and scroll independently from the findings.
        root.SizeChanged += (_, _) => limitsScroll.MaxHeight = root.ActualHeight < 560 ? 48 : 95;
        detail.Children.Add(limitsPanel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Grid.SetRow(buttons, 3); root.Children.Add(buttons);
        var close = DialogUi.Button("Fechar", (_, _) => DialogResult = false); close.IsCancel = true; buttons.Children.Add(close);
        if (canImprove) buttons.Children.Add(DialogUi.Button("Melhorar apresentação…", (_, _) => DialogResult = true, true));
    }

    private sealed record IssueView(ScoreDiagnosticIssue Issue)
    {
        public string Severity => Issue.Severity switch { ScoreDiagnosticSeverity.Error => "Problema", ScoreDiagnosticSeverity.Review => "Conferir", _ => "Observação" };
        public string Location => Issue.Location;
        public string Message => Issue.Message;
    }
}
