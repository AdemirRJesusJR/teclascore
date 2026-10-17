using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Teclas.App;

/// <summary>Per-song summary of the study history.</summary>
public sealed record StudySongSummary(string Key, string Title, int Sessions, TimeSpan Time, double Best, double Last, DateTime LastDate, int Completed, int? FastestBpm)
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

    public static string KeyOf(StudyRecord record) => record.Song.Length > 0 ? record.Song : record.Title;

    /// <summary>Attempts of one song in date order, for the progress chart.</summary>
    public static IReadOnlyList<StudyRecord> Attempts(IEnumerable<StudyRecord> records, string key) =>
        records.Where(r => string.Equals(KeyOf(r), key, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Date).ToArray();

    public static IReadOnlyList<StudySongSummary> Summaries(IEnumerable<StudyRecord> records) => records
        .GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var ordered = group.OrderBy(r => r.Date).ToArray();
            return new StudySongSummary(group.Key, ordered[^1].Title, ordered.Length, TimeSpan.FromSeconds(ordered.Sum(r => r.Seconds)),
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
        Title = "Histórico de estudo"; Width = 940; Height = 720; MinWidth = 700; MinHeight = 420; DialogUi.Theme(this);
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
        // Progress of the selected song: points per attempt (filled dots: in time, with the tempo).
        var chartPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(chartPanel, Dock.Bottom); root.Children.Insert(root.Children.Count - 0, chartPanel);
        ChartTitle = new TextBlock { FontWeight = FontWeights.SemiBold, Text = "Escolha uma música para ver a evolução." }; chartPanel.Children.Add(ChartTitle);
        Chart = new Canvas { Height = 150, Margin = new Thickness(0, 6, 0, 0), ClipToBounds = true }; chartPanel.Children.Add(Chart);
        AutomationProperties.SetName(Chart, "Evolução da pontuação por prática");
        grid.SelectionChanged += (_, _) => DrawChart(grid.SelectedItem as StudySongSummary);
        Chart.SizeChanged += (_, _) => DrawChart(grid.SelectedItem as StudySongSummary);
        this.records = records;
        root.Children.Add(grid);
        if (Songs.Count > 0) grid.SelectedIndex = 0;
    }

    private readonly IReadOnlyList<StudyRecord> records;
    internal TextBlock ChartTitle { get; }
    internal Canvas Chart { get; }
    internal int ChartPoints { get; private set; }

    private void DrawChart(StudySongSummary? song)
    {
        Chart.Children.Clear(); ChartPoints = 0;
        if (song is null) return;
        var attempts = StudyHistory.Attempts(records, song.Key);
        var trend = attempts.Count >= 2 ? attempts[^1].Score - attempts[0].Score : 0;
        ChartTitle.Text = $"Evolução de {song.Title}: {attempts.Count} prática(s)" + (attempts.Count >= 2 ? $" · {(trend >= 0 ? "+" : "")}{trend:0} pontos desde a primeira" : "");
        double width = Math.Max(200, Chart.ActualWidth > 0 ? Chart.ActualWidth : 860), height = Chart.Height, left = 34, bottom = 18, top = 8;
        var ink = TryFindResource("Muted") as Brush ?? Brushes.Gray; var accent = TryFindResource("Accent") as Brush ?? Brushes.SteelBlue;
        foreach (int level in new[] { 0, 50, 100 })
        {
            double y = top + (height - top - bottom) * (1 - level / 100.0);
            Chart.Children.Add(new Line { X1 = left, X2 = width - 8, Y1 = y, Y2 = y, Stroke = ink, StrokeThickness = .5, Opacity = .5 });
            var label = new TextBlock { Text = level.ToString(), FontSize = 10, Foreground = ink }; Canvas.SetLeft(label, 4); Canvas.SetTop(label, y - 7); Chart.Children.Add(label);
        }
        if (attempts.Count == 0) return;
        double step = attempts.Count == 1 ? 0 : (width - left - 20) / (attempts.Count - 1);
        var line = new Polyline { Stroke = accent, StrokeThickness = 2 };
        for (int i = 0; i < attempts.Count; i++)
        {
            double x = left + 6 + i * step, y = top + (height - top - bottom) * (1 - attempts[i].Score / 100);
            line.Points.Add(new Point(x, y));
            bool timed = attempts[i].Mode.EndsWith("no tempo", StringComparison.Ordinal);
            var dot = new Ellipse { Width = 8, Height = 8, Stroke = accent, StrokeThickness = 2, Fill = timed ? accent : Brushes.White,
                ToolTip = $"{attempts[i].Date:dd/MM HH:mm} · {attempts[i].Mode}{(attempts[i].Bpm is { } bpm ? $" · ♩ = {bpm}" : "")} · {attempts[i].Score:0} pontos" };
            Canvas.SetLeft(dot, x - 4); Canvas.SetTop(dot, y - 4); Chart.Children.Add(dot); ChartPoints++;
        }
        Chart.Children.Insert(0, line);
        var legend = new TextBlock { Text = "● no tempo   ○ na espera", FontSize = 10, Foreground = ink }; Canvas.SetLeft(legend, left); Canvas.SetTop(legend, height - 14); Chart.Children.Add(legend);
    }
}
