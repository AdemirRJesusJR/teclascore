using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

public sealed class MelodyBatchRow(MidiRow row, TrackRow? track, MelodyBatchProposal proposal) : Observable
{
    private bool selected = proposal.Confident && track is not null;
    public MidiRow Row { get; } = row;
    public TrackRow? Track { get; } = track;
    public MelodyBatchProposal Proposal { get; } = proposal;
    public string Song => Row.Name;
    public string TrackLabel => Track is null ? "—" : $"{Track.Instrument} · pista {Track.Track}, canal {Track.Channel}";
    public string Score => Proposal.Score > 0 ? Proposal.Score.ToString("0") : "—";
    public bool CanSelect => Track is not null;
    public bool Selected { get => selected; set { if (CanSelect) Set(ref selected, value); } }
}

/// <summary>Review screen for batch melody choice: confident proposals start checked, the rest explain why not.</summary>
public sealed class MelodyBatchWindow : Window
{
    private readonly TextBlock summary;
    private readonly Button confirm;
    public IReadOnlyList<MelodyBatchRow> Rows { get; }
    public IReadOnlyList<MelodyBatchRow> SelectedRows => Rows.Where(row => row.Selected && row.CanSelect).ToArray();

    public static IReadOnlyList<MelodyBatchRow> Build(IEnumerable<MidiRow> files) => files
        .Where(row => !row.UsesNativeScoreWorkflow && row.Document is not null && !row.UseReviewedProfile
            && row.ScorePath is null && !row.Tracks.Any(track => track.IsMelody))
        .Select(row =>
        {
            var proposal = MelodyBatchAdvisor.Propose(row.Tracks.Select(track => track.MelodySuggestion).OfType<MelodySourceSuggestion>().ToArray());
            var track = row.Tracks.FirstOrDefault(track => track.Source.Key == proposal.Source);
            return new MelodyBatchRow(row, track, proposal);
        }).ToArray();

    public MelodyBatchWindow(IReadOnlyList<MelodyBatchRow> rows)
    {
        Title = "Melodias em lote"; Width = 980; Height = 620; MinWidth = 760; MinHeight = 440; DialogUi.Theme(this);
        Rows = rows;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Escolher a melodia de vários MIDIs", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Para cada MIDI ainda sem melodia escolhida, o app propõe a pista mais provável. Vêm marcadas só as escolhas claras; as demais mostram o motivo. As marcadas vão para a Fila de conversões, que gera as partituras.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        confirm = DialogUi.Button("Escolher e enfileirar", (_, _) => { if (SelectedRows.Count > 0) DialogResult = true; }, true); confirm.Name = "ConfirmMelodyBatchButton"; buttons.Children.Add(confirm);
        summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }; footer.Children.Add(summary);
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, RowHeight = 30 };
        AutomationProperties.SetName(grid, "Melodias propostas");
        grid.SetResourceReference(BackgroundProperty, "CardBrush"); grid.SetResourceReference(ForegroundProperty, "Ink");
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, "CardBrush"); grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Soft");
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(MelodyBatchRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetBinding(IsEnabledProperty, new Binding(nameof(MelodyBatchRow.CanSelect)));
        check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.Columns.Add(new DataGridTemplateColumn { Header = "✓", Width = 36, CellTemplate = new DataTemplate { VisualTree = check } });
        grid.Columns.Add(new DataGridTextColumn { Header = "Música", Binding = new Binding(nameof(MelodyBatchRow.Song)), Width = new DataGridLength(2, DataGridLengthUnitType.Star), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Pista proposta", Binding = new Binding(nameof(MelodyBatchRow.TrackLabel)), Width = new DataGridLength(2, DataGridLengthUnitType.Star), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Pontos", Binding = new Binding(nameof(MelodyBatchRow.Score)), Width = 60, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Motivo", Binding = new Binding("Proposal.Reason"), Width = new DataGridLength(3, DataGridLengthUnitType.Star), IsReadOnly = true });
        root.Children.Add(grid);
        foreach (var row in rows) row.PropertyChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int chosen = SelectedRows.Count, confident = Rows.Count(row => row.Proposal.Confident);
        summary.Text = Rows.Count == 0 ? "Nenhum MIDI sem melodia escolhida e sem partitura."
            : $"{Rows.Count} MIDIs · {confident} com escolha clara · {chosen} marcados para a fila.";
        confirm.IsEnabled = chosen > 0;
    }
}
