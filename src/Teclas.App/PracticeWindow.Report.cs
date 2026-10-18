using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

/// <summary>After each attempt: the measures that went wrong, and a metronome that speeds up after clean passes.</summary>
public sealed partial class PracticeWindow
{
    public const int AccelerateStep = 5;
    private CheckBox? accelerate;
    private Button? reportButton;
    private int? pendingTempo;
    internal Func<MeasureReportWindow, bool?> reportDialog = window => window.ShowDialog();
    public bool Accelerate => accelerate?.IsChecked == true;
    /// <summary>Measure by measure results of the last attempt (wait or in time).</summary>
    public IReadOnlyList<MeasureResult> LastReport { get; private set; } = [];

    internal void ChooseAccelerate(bool value) { accelerate!.IsChecked = value; if (value) inTime.IsChecked = true; Reset(); }

    private void BuildExtras(Panel study, Panel footer)
    {
        accelerate = new CheckBox { Content = $"Acelerar (+{AccelerateStep}) a cada passagem limpa", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 6),
            ToolTip = "No tempo com metrônomo: depois de uma passagem com 90 pontos ou mais, o andamento sobe e o trecho recomeça, até a meta de andamento da música (ou ♩ = 200)." };
        AutomationProperties.SetName(accelerate, "Acelerar a cada passagem limpa");
        accelerate.Click += (_, _) => { if (accelerate.IsChecked == true) inTime.IsChecked = true; Reset(); };
        study.Children.Add(accelerate);
        reportButton = DialogUi.Button("Ver compassos", (_, _) => ShowReport()); reportButton.IsEnabled = false;
        reportButton.ToolTip = "Resultado de cada compasso da última tentativa; escolha um para treinar só ele";
        footer.Children.Insert(0, reportButton);
    }

    /// <summary>Builds the report of the attempt that just ended and names the hardest measures.</summary>
    private string SummarizeMeasures()
    {
        LastReport = MeasureReport.Build(timed?.Log ?? session.Log);
        if (reportButton is not null) reportButton.IsEnabled = LastReport.Count > 0;
        var hardest = MeasureReport.Hardest(LastReport);
        return hardest.Count == 0 ? "" : " Mais difíceis: compasso" + (hardest.Count > 1 ? "s " : " ") + string.Join(", ", hardest.Select(r => r.Measure)) + ".";
    }

    /// <summary>With "Acelerar": after a clean in-time pass, schedules the next tempo; returns false when there is nothing to raise.</summary>
    private bool ScheduleFasterPass()
    {
        if (!Accelerate || timed is null || WithMusic) return false;
        int goal = TempoGoal ?? 200, next = Math.Min(goal, (int)Bpm + AccelerateStep);
        if (next <= Bpm) { feedback.Text += $" Andamento-alvo ♩ = {goal} alcançado!"; return false; }
        pendingTempo = next; feedback.Text += $" Agora ♩ = {next}.";
        restart.Start();
        return true;
    }

    private void ShowReport()
    {
        if (LastReport.Count == 0) return;
        var window = new MeasureReportWindow(LastReport) { Owner = this };
        if (reportDialog(window) == true && window.ChosenMeasure is { } measure) ChoosePassage(measure.ToString(), measure.ToString(), true);
    }
}

/// <summary>Measure-by-measure results of an attempt; a measure can be chosen to practice alone.</summary>
public sealed class MeasureReportWindow : Window
{
    private readonly DataGrid grid;
    public int? ChosenMeasure { get; private set; }

    public MeasureReportWindow(IReadOnlyList<MeasureResult> results)
    {
        Title = "Resultado por compasso"; Width = 560; Height = 520; MinWidth = 440; MinHeight = 360; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new TextBlock { Text = "Compassos com menos pontos aparecem primeiro. Escolha um e clique em Treinar este compasso.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        grid = new DataGrid { ItemsSource = results.OrderBy(r => r.Score).ThenBy(r => r.Measure).ToArray(), AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, RowHeight = 28, SelectionMode = DataGridSelectionMode.Single };
        AutomationProperties.SetName(grid, "Resultado de cada compasso");
        grid.SetResourceReference(BackgroundProperty, "CardBrush"); grid.SetResourceReference(ForegroundProperty, "Ink");
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, "CardBrush"); grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Soft");
        void Column(string header, string path, double width) => grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
        Column("Compasso", nameof(MeasureResult.Measure), 90); Column("Pontos", nameof(MeasureResult.ScoreText), 70);
        Column("Certas", nameof(MeasureResult.OnTime), 70); Column("Fora do tempo", nameof(MeasureResult.OffTime), 100);
        Column("Perdidas", nameof(MeasureResult.Missed), 75); Column("Erradas", nameof(MeasureResult.Wrong), 75);
        root.Children.Add(grid);
        if (grid.Items.Count > 0) grid.SelectedIndex = 0;
        var train = DialogUi.Button("Treinar este compasso", (_, _) => { if (Choose()) DialogResult = true; }, true); footer.Children.Add(train);
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; footer.Children.Add(close);
    }

    internal bool Choose() { if (grid.SelectedItem is not MeasureResult result) return false; ChosenMeasure = result.Measure; return true; }
}
