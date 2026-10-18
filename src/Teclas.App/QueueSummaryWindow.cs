using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Teclas.Core;

namespace Teclas.App;

/// <summary>End-of-queue summary: ready scores, scores to check (doubtful chords, review points) and failures.</summary>
public sealed class QueueSummaryWindow : Window
{
    public IReadOnlyList<QueueOutcome> Outcomes { get; }
    /// <summary>The song the user chose to open (double click or Abrir), if any.</summary>
    public string? OpenSource { get; private set; }

    public QueueSummaryWindow(IReadOnlyList<QueueOutcome> outcomes)
    {
        Title = "Resumo da fila"; Width = 880; Height = 560; MinWidth = 640; MinHeight = 380; DialogUi.Theme(this);
        Outcomes = outcomes;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Resumo da fila", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Name = "QueueSummaryHeadline", Text = ConversionQueueSummary.Headline(outcomes), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        top.Children.Add(new TextBlock { Text = "Prontas não têm cifras com dúvida nem pontos de revisão. Em \"Conferir\", abra a música e veja a aba Cifras. Clique duas vezes para abrir.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var grid = new DataGrid { ItemsSource = outcomes, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, RowHeight = 30, SelectionMode = DataGridSelectionMode.Single };
        AutomationProperties.SetName(grid, "Resultado de cada música");
        grid.SetResourceReference(BackgroundProperty, "CardBrush"); grid.SetResourceReference(ForegroundProperty, "Ink");
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, "CardBrush"); grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Soft");
        grid.Columns.Add(new DataGridTextColumn { Header = "Música", Binding = new Binding(nameof(QueueOutcome.Name)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Resultado", Binding = new Binding(nameof(QueueOutcome.Verdict)), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Detalhes", Binding = new Binding(nameof(QueueOutcome.Detail)), Width = new DataGridLength(3, DataGridLengthUnitType.Star) });
        void Open() { if (grid.SelectedItem is QueueOutcome outcome) { OpenSource = outcome.SourcePath; DialogResult = true; } }
        grid.MouseDoubleClick += (_, e) => { if (e.ChangedButton == MouseButton.Left) Open(); };
        var open = DialogUi.Button("Abrir a música", (_, _) => Open()); open.Name = "QueueSummaryOpen"; footer.Children.Add(open);
        var close = DialogUi.Button("Fechar", (_, _) => Close(), true); close.IsCancel = true; footer.Children.Add(close);
        root.Children.Add(grid);
        if (outcomes.FirstOrDefault(o => o.Level != QueueOutcomeLevel.Ready) is { } first) grid.SelectedItem = first;
    }
}
