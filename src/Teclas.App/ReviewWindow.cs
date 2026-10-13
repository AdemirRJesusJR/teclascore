using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Teclas.App;

public sealed record ScoreReviewPoint
{
    public double Quarter { get; init; }
    public int Measure { get; init; }
    public double Beat { get; init; }
    public double MeasureStartQuarter { get; init; }
    public double MeasureEndQuarter { get; init; }
    public string Kind { get; init; } = "";
    public string Message { get; init; } = "";
    public bool Blocking { get; init; }
    public string KindLabel => Kind switch
    {
        "chord_low_confidence" or "chord_weak_evidence" => "Conferir cifra",
        "chord_ambiguity" => "Cifra ambígua",
        "harmony_gap" or "harmony_missing" => "Sem cifra",
        "key_conflict" or "key_uncertain" => "Tonalidade",
        "rhythm_refined" => "Ritmo refinado",
        "melody_source_overlap" => "Pistas sobrepostas",
        _ => "Revisão rítmica"
    };
}

public sealed class ReviewWindow : Window
{
    private readonly DataGrid points;
    private readonly ComboBox sources;
    private readonly ComboBox speeds;
    private readonly TextBlock status;
    private readonly Button listen;
    private readonly Button chord;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly Action stop;
    private TimeSpan regionEnd;
    private CancellationTokenSource? preparation;
    private bool closed;
    public ScoreReviewPoint? ChordToEdit { get; private set; }

    public ReviewWindow(MidiRow row, Func<MidiRow, ScoreReviewPoint, TrackRow, double, CancellationToken, Task<string>> play,
        Action stop, Func<TimeSpan> position, Func<bool> playing, Func<ScoreReviewPoint, TrackRow, TimeSpan> end)
    {
        this.stop = stop;
        Title = "Revisão guiada · TeclaScore"; Width = 940; Height = 660; MinWidth = 700; MinHeight = 510;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        SetResourceReference(BackgroundProperty, "Background"); SetResourceReference(ForegroundProperty, "Ink");
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(new TextBlock { Text = "Revisão por compasso", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = row.ScoreTitle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) });
        var guidance = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Text =
            "Ouça o compasso na pista original e confira a cifra. Ritmos refinados são avisos para conferência. " +
            "Para pistas sobrepostas, compare as fontes e ajuste a Prioridade das pistas na aba Melodia. " +
            "Quando houver bloqueio rítmico, escolha outra pista de melodia ou revise o MIDI no editor; esta tela não altera notas automaticamente." };
        guidance.SetResourceReference(ForegroundProperty, "Muted"); heading.Children.Add(guidance);
        grid.Children.Add(heading);
        points = new DataGrid { Name = "ReviewPointsGrid", ItemsSource = row.ReviewPoints, AutoGenerateColumns = false,
            IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single,
            RowHeight = double.NaN, MinRowHeight = 46, SelectedIndex = 0 };
        var messageStyle = new Style(typeof(TextBlock));
        messageStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        messageStyle.Setters.Add(new Setter(MarginProperty, new Thickness(6)));
        points.Columns.Add(new DataGridTextColumn { Header = "Compasso", Binding = new Binding(nameof(ScoreReviewPoint.Measure)), Width = 84 });
        points.Columns.Add(new DataGridTextColumn { Header = "Tempo", Binding = new Binding(nameof(ScoreReviewPoint.Beat)) { StringFormat = "0.##" }, Width = 65 });
        points.Columns.Add(new DataGridTextColumn { Header = "Tipo", Binding = new Binding(nameof(ScoreReviewPoint.KindLabel)), Width = 112 });
        points.Columns.Add(new DataGridTextColumn { Header = "Conferir", Binding = new Binding(nameof(ScoreReviewPoint.Message)), ElementStyle = messageStyle, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(points, 1); grid.Children.Add(points);
        var controls = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var playback = new WrapPanel();
        sources = new ComboBox { Name = "ReviewSourceCombo", Width = 250, MinHeight = 34, Margin = new Thickness(0, 0, 8, 8),
            ItemsSource = row.Tracks.Select(track => new ReviewSource(track)).ToArray(), DisplayMemberPath = nameof(ReviewSource.Label), SelectedValuePath = nameof(ReviewSource.Track) };
        sources.SelectedValue = row.Tracks.FirstOrDefault(track => track.IsMelody && ActiveAtPoint(track, points.SelectedItem as ScoreReviewPoint))
            ?? row.Tracks.FirstOrDefault(track => track.IsMelody) ?? row.Tracks.FirstOrDefault();
        speeds = new ComboBox { Name = "ReviewSpeedCombo", Width = 105, MinHeight = 34, Margin = new Thickness(0, 0, 8, 8),
            ItemsSource = new[] { new ReviewSpeed(.25, "25%"), new ReviewSpeed(.5, "50%"), new ReviewSpeed(.75, "75%"), new ReviewSpeed(1, "100%") },
            DisplayMemberPath = nameof(ReviewSpeed.Label), SelectedValuePath = nameof(ReviewSpeed.Value), SelectedValue = .5 };
        listen = MakeButton("Ouvir compasso", "ReviewListenButton");
        var stopButton = MakeButton("Parar", "ReviewStopButton");
        chord = MakeButton("Editar cifra", "ReviewChordButton");
        var close = MakeButton("Fechar", "ReviewCloseButton"); close.IsCancel = true;
        playback.Children.Add(sources); playback.Children.Add(speeds); playback.Children.Add(listen); playback.Children.Add(stopButton); playback.Children.Add(chord); playback.Children.Add(close);
        controls.Children.Add(playback);
        var preparationProgress = new ProgressBar
        {
            Name = "ReviewPreparationProgress", IsIndeterminate = true, Height = 5,
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 9)
        };
        preparationProgress.SetResourceReference(ForegroundProperty, "Accent");
        preparationProgress.SetResourceReference(BackgroundProperty, "ChartTrack");
        System.Windows.Automation.AutomationProperties.SetName(preparationProgress, "Preparando áudio do compasso");
        controls.Children.Add(preparationProgress);
        status = new TextBlock { Text = "Escolha um ponto e ouça com velocidade reduzida. A escuta conserva o tom do MIDI original.", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        status.SetResourceReference(ForegroundProperty, "Muted"); controls.Children.Add(status);
        Grid.SetRow(controls, 2); grid.Children.Add(controls); Content = grid;
        listen.Click += async (_, _) =>
        {
            if (closed || preparation is not null || points.SelectedItem is not ScoreReviewPoint point || sources.SelectedValue is not TrackRow track || speeds.SelectedValue is not double speed) return;
            StopRegion();
            var request = new CancellationTokenSource();
            preparation = request;
            RefreshSelection();
            status.Text = $"Preparando compasso {point.Measure} · pista {track.Track}, canal {track.Channel}…";
            try
            {
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                request.Token.ThrowIfCancellationRequested();
                regionEnd = end(point, track);
                var message = await play(row, point, track, speed, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (!closed)
                {
                    status.Text = message;
                    if (playing()) timer.Start();
                }
            }
            catch (OperationCanceledException)
            {
                if (!closed)
                {
                    stop();
                    status.Text = "Preparação cancelada. Você pode ouvir novamente ou escolher outro compasso.";
                }
            }
            catch (Exception error)
            {
                if (!closed)
                {
                    stop();
                    status.Text = "Não foi possível preparar o compasso. " + error.Message;
                }
            }
            finally
            {
                if (ReferenceEquals(preparation, request)) preparation = null;
                request.Dispose();
                if (!closed) RefreshSelection();
            }
        };
        stopButton.Click += (_, _) => { StopRegion(); status.Text = preparation is null ? "Reprodução parada." : "Cancelando a preparação do compasso…"; };
        chord.Click += (_, _) => { if (points.SelectedItem is ScoreReviewPoint point) { ChordToEdit = point; Close(); } };
        close.Click += (_, _) => Close();
        points.SelectionChanged += (_, _) =>
        {
            StopRegion();
            if (points.SelectedItem is ScoreReviewPoint point && (sources.SelectedValue is not TrackRow current || !ActiveAtPoint(current, point)))
                sources.SelectedValue = row.Tracks.FirstOrDefault(track => track.IsMelody && ActiveAtPoint(track, point))
                    ?? sources.SelectedValue;
            RefreshSelection();
        };
        sources.SelectionChanged += (_, _) => StopRegion();
        speeds.SelectionChanged += (_, _) => { StopRegion(); status.Text = "Velocidade alterada. Clique em Ouvir compasso para repetir."; };
        timer.Tick += (_, _) =>
        {
            if (!playing() || position() >= regionEnd) { StopRegion(); status.Text = "Compasso concluído. Você pode ouvi-lo novamente ou conferir outro ponto."; }
        };
        Closed += (_, _) => { closed = true; StopRegion(); };
        SourceInitialized += (_, _) => ApplyNativeTheme();
        RefreshSelection();
        void RefreshSelection()
        {
            bool idle = preparation is null;
            preparationProgress.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
            points.IsEnabled = sources.IsEnabled = speeds.IsEnabled = idle;
            listen.IsEnabled = idle && points.SelectedItem is ScoreReviewPoint && sources.SelectedValue is TrackRow;
            chord.IsEnabled = idle && points.SelectedItem is ScoreReviewPoint && row.Chords.Count > 0;
        }
        bool ActiveAtPoint(TrackRow track, ScoreReviewPoint? point) => point is not null && row.Document is { } document &&
            document.Notes.Any(note => note.Track == track.Source.Key.Track && note.Channel == track.Source.Key.Channel && note.Program == track.Source.Key.Program
                && note.Start < point.MeasureEndQuarter * document.Ppq && note.End > point.MeasureStartQuarter * document.Ppq);
    }

    private void StopRegion() { preparation?.Cancel(); timer.Stop(); stop(); }
    private static Button MakeButton(string text, string name) => new()
        { Name = name, Content = text, MinHeight = 34, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8) };
    private sealed record ReviewSource(TrackRow Track) { public string Label => $"Pista {Track.Track} · canal {Track.Channel} · {Track.Instrument}"; }
    private sealed record ReviewSpeed(double Value, string Label);

    private void ApplyNativeTheme()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            uint dark = ThemeManager.Current.IsDark ? 1u : 0u; _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(uint));
            foreach (var (attribute, resource) in new[] { (35u, "Background"), (36u, "Ink"), (34u, "Line") })
            {
                var color = (Color)ColorConverter.ConvertFromString(ThemeManager.ColorHex(resource));
                uint value = (uint)(color.R | color.G << 8 | color.B << 16);
                _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);
}
