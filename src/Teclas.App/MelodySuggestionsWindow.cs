using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace Teclas.App;

public sealed class MelodySuggestionsWindow : Window
{
    private CancellationTokenSource? preparation;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool closed;
    private Action<string>? playbackFailure;
    public void ReportPlaybackFailure(string message) { if (!closed) playbackFailure?.Invoke(message); }
    public TrackRow? ChosenTrack { get; private set; }

    public MelodySuggestionsWindow(MidiRow row, double initialSpeed,
        Func<TrackRow, double, CancellationToken, Task<string>> play, Action stop, Func<bool> playing)
    {
        Title = "Comparar pistas de melodia · TeclaScore"; Width = 1020; Height = 720; MinWidth = 800; MinHeight = 540;
        DialogUi.Theme(this);
        var root = new Grid { Margin = new(24) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new(0, 0, 0, 16) }; root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Ouça e compare as candidatas", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = row.ScoreTitle, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 10) });
        header.Children.Add(new TextBlock { Text = "A ordem considera o comportamento das notas e serve como orientação. A melodia pode estar em mais de uma pista; confira os trechos antes de escolher.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (row.UseReviewedProfile) header.Children.Add(new TextBlock { Text = "Usar uma pista abaixo substitui a revisão aprovada pela sua escolha manual.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        if (row.Tracks.Count(track => track.IsMelody) > 1) header.Children.Add(new TextBlock { Text = $"Usar esta pista substituirá as {row.Tracks.Count(track => track.IsMelody)} fontes selecionadas. Para juntar pistas, marque-as na lista principal e ajuste a prioridade.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        var grid = new DataGrid { Name = "MelodyCandidatesGrid", ItemsSource = row.Tracks.OrderBy(track => track.MelodySuggestion?.Rank ?? int.MaxValue).Select(track => new Candidate(track)).ToArray(),
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single,
            RowHeight = double.NaN, MinRowHeight = 52, SelectedIndex = 0 };
        var wrap = new Style(typeof(TextBlock)); wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); wrap.Setters.Add(new Setter(MarginProperty, new Thickness(6)));
        grid.Columns.Add(new DataGridTextColumn { Header = "Ordem", Binding = new Binding(nameof(Candidate.Rank)), Width = 62 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Pista / fonte", Binding = new Binding(nameof(Candidate.Source)), Width = 230, ElementStyle = wrap });
        grid.Columns.Add(new DataGridTextColumn { Header = "Indício", Binding = new Binding(nameof(Candidate.Label)), Width = 160, ElementStyle = wrap });
        grid.Columns.Add(new DataGridTextColumn { Header = "Por que conferir", Binding = new Binding(nameof(Candidate.Reason)), Width = new(1, DataGridLengthUnitType.Star), ElementStyle = wrap });
        Grid.SetRow(grid, 1); root.Children.Add(grid);
        var controls = new StackPanel { Margin = new(0, 16, 0, 0) }; Grid.SetRow(controls, 2); root.Children.Add(controls);
        var progress = new ProgressBar { Name = "CandidatePreparationProgress", IsIndeterminate = true, Height = 6, Visibility = Visibility.Collapsed, Margin = new(0, 0, 0, 10) }; controls.Children.Add(progress);
        var status = new TextBlock { Name = "CandidateAuditionStatus", Text = "Selecione uma pista e ouça um trecho representativo.", TextWrapping = TextWrapping.Wrap, MinHeight = 36 }; controls.Children.Add(status);
        playbackFailure = message => { timer.Stop(); status.Text = "Não foi possível ouvir o trecho. " + message; };
        var buttons = new WrapPanel { Margin = new(0, 8, 0, 0) }; controls.Children.Add(buttons);
        var speed = new ComboBox { Name = "CandidateSpeedCombo", Width = 118, ItemsSource = new[] { .25, .5, .75, 1.0 }, SelectedItem = initialSpeed is .25 or .5 or .75 or 1.0 ? initialSpeed : 1.0,
            ItemStringFormat = "{0:P0}", Margin = new(0, 0, 8, 8) }; buttons.Children.Add(speed);
        var listen = DialogUi.Button("Ouvir trecho", (_, _) => { }); listen.Name = "CandidateListenButton"; buttons.Children.Add(listen);
        var halt = DialogUi.Button("Parar", (_, _) => { Stop(); status.Text = preparation is null ? "Reprodução parada." : "Cancelando preparação…"; }); halt.Name = "CandidateStopButton"; buttons.Children.Add(halt);
        var use = DialogUi.Button("Usar esta pista como melodia", (_, _) =>
        {
            if (preparation is null && grid.SelectedItem is Candidate candidate && candidate.Track.Channel != 10)
            { ChosenTrack = candidate.Track; DialogResult = true; }
        }, true); use.Name = "CandidateUseButton"; buttons.Children.Add(use);
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; buttons.Children.Add(close);
        void Stop() { preparation?.Cancel(); timer.Stop(); stop(); }
        void Refresh()
        {
            bool idle = preparation is null;
            grid.IsEnabled = speed.IsEnabled = idle;
            listen.IsEnabled = idle && grid.SelectedItem is Candidate;
            use.IsEnabled = idle && grid.SelectedItem is Candidate candidate && candidate.Track.Channel != 10;
            progress.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        }
        listen.Click += async (_, _) =>
        {
            if (closed || preparation is not null || grid.SelectedItem is not Candidate candidate || speed.SelectedItem is not double rate) return;
            Stop(); var request = new CancellationTokenSource(); preparation = request; Refresh(); status.Text = "Preparando o trecho desta pista…";
            try
            {
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                request.Token.ThrowIfCancellationRequested();
                string message = await play(candidate.Track, rate, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (!closed) { status.Text = message; if (playing()) timer.Start(); }
            }
            catch (OperationCanceledException) { if (!closed) { stop(); status.Text = "Preparação cancelada. Escolha uma pista para ouvir novamente."; } }
            catch (Exception error) { if (!closed) { stop(); status.Text = "Não foi possível ouvir o trecho. " + error.Message; } }
            finally { preparation = null; request.Dispose(); if (!closed) Refresh(); }
        };
        grid.SelectionChanged += (_, _) => { Stop(); status.Text = "Ouça o trecho e compare com as outras fontes."; Refresh(); };
        speed.SelectionChanged += (_, _) => { Stop(); status.Text = "Clique em Ouvir trecho para usar esta velocidade."; };
        timer.Tick += (_, _) => { if (!playing()) { timer.Stop(); status.Text = "Trecho concluído. Compare outra pista ou confirme sua escolha."; } };
        Closed += (_, _) => { closed = true; Stop(); };
        Refresh();
    }

    private sealed record Candidate(TrackRow Track)
    {
        public int Rank => Track.MelodySuggestion?.Rank ?? 0;
        public string Source => $"Pista {Track.Track} · canal {Track.Channel}\n{Track.Instrument}";
        public string Label => Track.Hint;
        public string Reason => Track.SuggestionReason;
    }
}
