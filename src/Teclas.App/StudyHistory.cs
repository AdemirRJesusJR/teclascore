using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace Teclas.App;

/// <summary>Per-song summary of the study history.</summary>
public sealed record StudySongSummary(string Title, int Sessions, TimeSpan Time, double Best, double Last, DateTime LastDate, int Completed, int? FastestBpm)
{
    public string TimeText => Time.TotalHours >= 1 ? $"{(int)Time.TotalHours} h {Time.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(Time.TotalMinutes))} min";
    public string BestText => $"{Best:0}";
    public string LastText => $"{Last:0}";
    public string LastDateText => LastDate.ToString("dd/MM/yyyy HH:mm");
    public string TempoText => FastestBpm is { } bpm ? $"♩ = {bpm}" : "—";
}

public static class StudyHistory
{
    public const int Limit = Settings.StudyHistoryLimit;
    public static List<StudyRecord> Normalize(IEnumerable<StudyRecord?>? records) => Settings.NormalizeStudyHistory(records);

    public static IReadOnlyList<StudySongSummary> Summaries(IEnumerable<StudyRecord> records) => records
        .GroupBy(r => r.Song.Length > 0 ? r.Song : r.Title, StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var ordered = group.OrderBy(r => r.Date).ToArray();
            return new StudySongSummary(ordered[^1].Title, ordered.Length, TimeSpan.FromSeconds(ordered.Sum(r => r.Seconds)),
                ordered.Max(r => r.Score), ordered[^1].Score, ordered[^1].Date, ordered.Count(r => r.Completed),
                ordered.Where(r => r.Completed && r.Bpm is not null).Select(r => r.Bpm).Max());
        })
        .OrderByDescending(summary => summary.LastDate).ToArray();
}

/// <summary>Study history: totals per song plus the latest attempts.</summary>
public sealed class StudyHistoryWindow : Window
{
    public IReadOnlyList<StudySongSummary> Songs { get; }

    public StudyHistoryWindow(IReadOnlyList<StudyRecord> records, Action? clear = null)
    {
        Title = "Histórico de estudo"; Width = 940; Height = 600; MinWidth = 700; MinHeight = 420; DialogUi.Theme(this);
        Songs = StudyHistory.Summaries(records);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Histórico de estudo", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var total = TimeSpan.FromSeconds(records.Sum(r => r.Seconds));
        top.Children.Add(new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = records.Count == 0 ? "Ainda não há práticas registradas. Use 🎹 Praticar na prévia da partitura."
                : $"{records.Count} práticas em {Songs.Count} músicas · {(total.TotalHours >= 1 ? $"{(int)total.TotalHours} h {total.Minutes:00} min" : $"{(int)Math.Round(total.TotalMinutes)} min")} no total. Pontos de 0 a 100: na espera, a precisão; no tempo, a precisão com o ritmo." });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        if (clear is not null && records.Count > 0)
            footer.Children.Add(DialogUi.Button("Apagar histórico", (_, _) =>
            {
                if (MessageBox.Show(this, "Apagar todas as práticas registradas? As partituras não são afetadas.", "Histórico de estudo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                clear(); DialogResult = true;
            }));
        var close = DialogUi.Button("Fechar", (_, _) => Close(), true); close.IsCancel = true; footer.Children.Add(close);

        var grid = new DataGrid { ItemsSource = Songs, AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, RowHeight = 28 };
        AutomationProperties.SetName(grid, "Estudo por música");
        grid.SetResourceReference(BackgroundProperty, "CardBrush"); grid.SetResourceReference(ForegroundProperty, "Ink");
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, "CardBrush"); grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Soft");
        void Column(string header, string path, DataGridLength width) => grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
        Column("Música", nameof(StudySongSummary.Title), new DataGridLength(3, DataGridLengthUnitType.Star));
        Column("Práticas", nameof(StudySongSummary.Sessions), 70);
        Column("Tempo", nameof(StudySongSummary.TimeText), 90);
        Column("Melhor", nameof(StudySongSummary.BestText), 70);
        Column("Última", nameof(StudySongSummary.LastText), 70);
        Column("Concluídas", nameof(StudySongSummary.Completed), 90);
        Column("Mais rápida", nameof(StudySongSummary.TempoText), 95);
        Column("Último estudo", nameof(StudySongSummary.LastDateText), new DataGridLength(1.4, DataGridLengthUnitType.Star));
        root.Children.Add(grid);
    }
}
