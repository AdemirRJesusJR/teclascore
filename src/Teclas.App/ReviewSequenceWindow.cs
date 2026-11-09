using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Services the review in sequence takes from the main window (every one optional, for the smokes).</summary>
public sealed record ReviewSequenceServices
{
    /// <summary>Prepares (or finds in the cache) the measure map of a score: where each measure sits on its page and when it sounds.</summary>
    public Func<MidiRow, CancellationToken, Task<ScorePlaybackMap>>? PrepareMap { get; init; }
    /// <summary>Applies an alternative to a chord of the song (the score needs a new generation afterwards).</summary>
    public Action<MidiRow, ChordRow, ChordAlternative>? ChooseChord { get; init; }
    /// <summary>Generates the song again with its current melody and chords (after 1/2 changed a chord); returns the status text.</summary>
    public Func<MidiRow, Task<string>>? Regenerate { get; init; }
    /// <summary>Reads the engine's report of a score (harmonic warnings, writing issues, empty measures).</summary>
    public Func<MidiRow, ScoreValidationReport>? ReadReport { get; init; }
}

/// <summary>
/// Review in sequence 2.0: the generated scores one after another, the riskiest first. The page on the left shows the
/// review points highlighted over their measures (doubtful chords in amber, empty measures in gray, flagged passages
/// in blue); the list on the right names each point with the chord alternatives (1/2 choose, 0 keeps), P goes to the
/// next point, Space plays from the point (audio prepared ahead), A approves and moves on, R makes the score again
/// with the next melody candidate, E opens the editor. The header counts "n de N · ~minutos restantes" and the next
/// songs wait as thumbnails under the list.
/// </summary>
public sealed class ReviewSequenceWindow : Window
{
    private readonly IReadOnlyList<MidiRow> rows;
    private readonly Action<MidiRow> approve;
    private readonly Func<MidiRow, Task<string>> redo;
    private readonly Func<MidiRow, CancellationToken, Task<string>>? prepareAudio;
    private readonly ReviewSequenceServices services;
    private readonly Image page = new() { Stretch = Stretch.Fill };
    private readonly Canvas overlay = new() { IsHitTestVisible = true, Background = Brushes.Transparent };
    private readonly Grid pageSurface = new();
    private readonly Viewbox pageBox = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock counter = new() { Opacity = .75, FontSize = 12.5 }, title = new() { FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
        reasons = new() { TextTrimming = TextTrimming.CharacterEllipsis, Opacity = .85, FontSize = 12 }, status = new() { TextWrapping = TextWrapping.Wrap, Opacity = .85, FontSize = 12 },
        pointsHeader = new() { FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) }, pageLabel = new() { Opacity = .6, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ListBox pointsList = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel thumbnails = new() { Orientation = Orientation.Horizontal };
    private readonly Button listenButton, nextPointButton;
    private readonly Dictionary<MidiRow, Task<string>> audioTasks = [];
    private readonly Dictionary<MidiRow, Task<ScorePlaybackMap>> mapTasks = [];
    private readonly Dictionary<MidiRow, List<ReviewSequencePoint>> pointCache = [];
    private readonly CancellationTokenSource prepareCancellation = new();
    private readonly MediaPlayer player = new();
    private readonly Stopwatch songWatch = new();
    private readonly List<double> songSeconds = [];
    private IReadOnlyList<string> pages = [];
    private ScorePlaybackMap? map;
    private bool busy, playing;
    private double? pendingSeek;
    private int highlightCount;

    public int Index { get; private set; }
    public int PageIndex { get; private set; }
    public int PageCount => pages.Count;
    public bool IsPlaying => playing;
    public MidiRow? Current => Index < rows.Count ? rows[Index] : null;
    public MidiRow? EditRequested { get; private set; }
    public int Approved { get; private set; }
    public string StatusText => status.Text;
    public string CounterText => counter.Text;
    /// <summary>The review points of the current song, in score order.</summary>
    public IReadOnlyList<ReviewSequencePoint> Points => Current is { } row && pointCache.TryGetValue(row, out var list) ? list : [];
    public ReviewSequencePoint? CurrentPoint => pointsList.SelectedItem as ReviewSequencePoint;
    /// <summary>How many measures are highlighted on the page right now (0 until the map arrives).</summary>
    public int HighlightCount => highlightCount;
    public int ThumbnailCount => thumbnails.Children.OfType<Button>().Count();
    public bool HasMap => map is not null;

    public ReviewSequenceWindow(IReadOnlyList<MidiRow> rows, Action<MidiRow> approve, Func<MidiRow, Task<string>> redo,
        Func<MidiRow, CancellationToken, Task<string>>? prepareAudio = null, ReviewSequenceServices? services = null)
    {
        this.rows = rows; this.approve = approve; this.redo = redo; this.prepareAudio = prepareAudio; this.services = services ?? new();
        player.MediaEnded += (_, _) => { playing = false; player.Stop(); };
        player.MediaOpened += (_, _) => { if (pendingSeek is { } seconds) { player.Position = TimeSpan.FromSeconds(seconds); pendingSeek = null; } };
        Closed += (_, _) => { prepareCancellation.Cancel(); player.Stop(); player.Close(); };
        Title = "Revisão em sequência"; Width = 1240; Height = 900; MinWidth = 900; MinHeight = 600; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;

        // Header: title and confidence reasons on the left, the counter on the right.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var titles = new StackPanel(); header.Children.Add(titles);
        titles.Children.Add(title); titles.Children.Add(reasons);
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Polite);
        counter.VerticalAlignment = VerticalAlignment.Top; counter.Margin = new Thickness(16, 4, 0, 0); Grid.SetColumn(counter, 1); header.Children.Add(counter);
        AutomationProperties.SetName(counter, "Posição na revisão");

        // Footer: the actions with their keys, then the status.
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var previous = DialogUi.Button("◀", (_, _) => Move(-1)); previous.ToolTip = "Música anterior (←); Shift+← volta a página"; buttons.Children.Add(previous);
        listenButton = DialogUi.Button("Ouvir do ponto (Espaço)", async (_, _) => await TogglePlayAsync());
        listenButton.ToolTip = "Ouvir a partitura a partir do ponto selecionado, ou pará-la (Espaço). O áudio e o mapa de compassos da próxima música são preparados enquanto você revisa.";
        listenButton.Visibility = prepareAudio is null ? Visibility.Collapsed : Visibility.Visible; buttons.Children.Add(listenButton);
        nextPointButton = DialogUi.Button("Próximo ponto (P)", (_, _) => NextPoint()); nextPointButton.ToolTip = "Seleciona o próximo ponto de revisão e mostra a página dele (P)"; buttons.Children.Add(nextPointButton);
        var approveButton = DialogUi.Button("Aprovar (A)", async (_, _) => await ApproveAsync(), true); approveButton.ToolTip = "Aprovar esta partitura e passar à próxima (A). Com cifras trocadas, ela é gerada de novo em vez de aprovada."; buttons.Children.Add(approveButton);
        var redoButton = DialogUi.Button("Refazer (R)", async (_, _) => await RedoAsync()); redoButton.ToolTip = "Refazer com outra melodia: a próxima pista candidata vira a melodia e a partitura é gerada de novo (R)"; buttons.Children.Add(redoButton);
        var editButton = DialogUi.Button("Editar (E)", (_, _) => Edit()); editButton.ToolTip = "Abrir esta música no editor musical (E); a revisão fecha"; buttons.Children.Add(editButton);
        var next = DialogUi.Button("▶", (_, _) => Move(1)); next.ToolTip = "Próxima música (→); Shift+→ avança a página"; buttons.Children.Add(next);
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; buttons.Children.Add(close);
        footer.Children.Add(status);

        // Body: the page with its overlay on the left, the points and the next songs on the right.
        var body = new Grid(); root.Children.Add(body);
        body.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(340), MinWidth = 280 });
        var frame = new Border { BorderThickness = new Thickness(1), Background = Brushes.White, Padding = new Thickness(2) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        pageSurface.Children.Add(page); pageSurface.Children.Add(overlay);
        pageBox.Child = pageSurface; frame.Child = pageBox;
        AutomationProperties.SetName(page, "Página da partitura");
        var left = new DockPanel(); DockPanel.SetDock(pageLabel, Dock.Bottom); left.Children.Add(pageLabel); left.Children.Add(frame); body.Children.Add(left);
        overlay.MouseLeftButtonDown += OverlayClick;

        var right = new DockPanel(); Grid.SetColumn(right, 2); body.Children.Add(right);
        var upcoming = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(upcoming, Dock.Bottom); right.Children.Add(upcoming);
        upcoming.Children.Add(new TextBlock { Text = "Próximas", FontWeight = FontWeights.SemiBold, FontSize = 12, Opacity = .75, Margin = new Thickness(0, 0, 0, 4) });
        upcoming.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = thumbnails });
        AutomationProperties.SetName(thumbnails, "Próximas músicas");
        DockPanel.SetDock(pointsHeader, Dock.Top); right.Children.Add(pointsHeader);
        pointsList.ItemTemplate = PointTemplate();
        pointsList.SelectionChanged += (_, _) => { if (CurrentPoint is { } point) ShowPointPage(point); DrawHighlights(); };
        pointsList.ItemContainerStyle = PointContainerStyle();
        ScrollViewer.SetHorizontalScrollBarVisibility(pointsList, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(pointsList, "Pontos de revisão");
        right.Children.Add(pointsList);

        PreviewKeyDown += async (_, e) =>
        {
            if (e.OriginalSource is TextBox) return;
            if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key is Key.Left or Key.Right) { e.Handled = true; TurnPage(e.Key == Key.Right ? 1 : -1); return; }
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            switch (e.Key)
            {
                case Key.Space when prepareAudio is not null: e.Handled = true; await TogglePlayAsync(); break;
                case Key.A: e.Handled = true; await ApproveAsync(); break;
                case Key.R: e.Handled = true; await RedoAsync(); break;
                case Key.E: e.Handled = true; Edit(); break;
                case Key.P: e.Handled = true; NextPoint(); break;
                case Key.D1 or Key.NumPad1: e.Handled = true; ChooseAlternative(0); break;
                case Key.D2 or Key.NumPad2: e.Handled = true; ChooseAlternative(1); break;
                case Key.D3 or Key.NumPad3: e.Handled = true; ChooseAlternative(2); break;
                case Key.D0 or Key.NumPad0: e.Handled = true; KeepCurrentChord(); break;
                case Key.Right or Key.PageDown: e.Handled = true; Move(1); break;
                case Key.Left or Key.PageUp: e.Handled = true; Move(-1); break;
            }
        };
        Show(0);
    }

    // ---- Presentation of one song ----

    private void Show(int index)
    {
        if (Current is not null && songWatch.IsRunning) songSeconds.Add(songWatch.Elapsed.TotalSeconds);
        songWatch.Restart();
        Index = Math.Clamp(index, 0, Math.Max(0, rows.Count));
        StopAudio(); map = null; highlightCount = 0; overlay.Children.Clear();
        if (Current is not { } row)
        {
            counter.Text = $"{rows.Count} de {rows.Count}"; title.Text = "Fim da revisão"; page.Source = null; pages = []; pageLabel.Text = "";
            reasons.Text = $"{Approved} partitura(s) aprovada(s) nesta revisão. Feche ou volte com ◀."; reasons.ToolTip = null;
            pointsHeader.Text = ""; pointsList.ItemsSource = null; thumbnails.Children.Clear();
            return;
        }
        title.Text = string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle;
        reasons.Text = (row.ConfidenceBadge.Length > 0 ? row.ConfidenceBadge + " — " : "") + row.ConfidenceToolTip;
        reasons.ToolTip = reasons.Text;
        pages = ScorePageFiles.Discover(row.PreviewPath);
        if (pages.Count == 0 && row.PreviewPath is { } single && File.Exists(single)) pages = [single];
        ShowPage(0);
        if (page.Source is null) status.Text = "Sem prévia desta partitura; abra no editor (E) para conferir.";
        var points = PointsOf(row);
        pointsList.ItemsSource = points;
        int chords = points.Count(p => p.Kind == ReviewSequenceKind.Chord);
        pointsHeader.Text = points.Count switch { 0 => "Nada chamou atenção nesta partitura", 1 => "1 ponto para conferir", int n => $"{n} pontos para conferir" }
            + (chords > 0 ? $" · {chords} cifra(s) com dúvida" : "");
        if (points.Count > 0) pointsList.SelectedIndex = 0;
        UpdateCounter();
        FillThumbnails();
        // The audio and the measure map of this song and the next one are prepared while this one is reviewed.
        PrewarmAudio(row); _ = LoadMapAsync(row);
        if (Index + 1 < rows.Count) { PrewarmAudio(rows[Index + 1]); PrewarmMap(rows[Index + 1]); }
    }

    private List<ReviewSequencePoint> PointsOf(MidiRow row)
    {
        if (pointCache.TryGetValue(row, out var cached)) return cached;
        var report = services.ReadReport?.Invoke(row) ?? ScoreValidationReport.Read(row.ScorePath);
        var list = ReviewSequencePoints.Build(row, report).ToList();
        pointCache[row] = list;
        return list;
    }

    private void UpdateCounter()
    {
        int remaining = Math.Max(0, rows.Count - Index);
        double perSong = Median(songSeconds) ?? 60;
        double seconds = remaining * perSong;
        string eta = seconds < 60 ? "menos de 1 min restante" : $"~{Math.Ceiling(seconds / 60):0} min restantes";
        counter.Text = $"{Math.Min(Index + 1, rows.Count)} de {rows.Count} · {eta}";
        counter.ToolTip = songSeconds.Count == 0 ? "Estimativa inicial de 1 minuto por música; ela se ajusta ao seu ritmo de revisão." : $"Mediana de {perSong:0} s por música nesta revisão.";
    }

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private void FillThumbnails()
    {
        thumbnails.Children.Clear();
        for (int i = Index + 1; i < rows.Count && i <= Index + 4; i++)
        {
            var row = rows[i]; int target = i;
            var panel = new StackPanel { Width = 74, Margin = new Thickness(0, 0, 8, 0) };
            var image = new Image { Height = 72, Stretch = Stretch.Uniform, Source = LoadThumbnail(ScorePageFiles.Discover(row.PreviewPath).FirstOrDefault() ?? row.PreviewPath) };
            var border = new Border { BorderThickness = new Thickness(1), Background = Brushes.White, Child = image, Height = 74 };
            border.SetResourceReference(Border.BorderBrushProperty, "Line");
            panel.Children.Add(border);
            panel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
            var button = new Button { Content = panel, Padding = new Thickness(2), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Tag = target,
                ToolTip = $"{row.ScoreTitle}{(row.ConfidenceBadge.Length > 0 ? " · " + row.ConfidenceBadge : "")} · clique para ir a esta música" };
            AutomationProperties.SetName(button, "Próxima: " + row.ScoreTitle);
            button.Click += (_, _) => JumpTo(target);
            thumbnails.Children.Add(button);
        }
        if (thumbnails.Children.Count == 0) thumbnails.Children.Add(new TextBlock { Text = "Esta é a última da revisão.", FontSize = 11, Opacity = .6 });
    }

    private void ShowPage(int index)
    {
        PageIndex = pages.Count == 0 ? 0 : Math.Clamp(index, 0, pages.Count - 1);
        var source = pages.Count == 0 ? null : LoadBitmap(pages[PageIndex], 0);
        page.Source = source;
        if (source is not null) { pageSurface.Width = source.PixelWidth; pageSurface.Height = source.PixelHeight; }
        pageLabel.Text = pages.Count > 1 ? $"página {PageIndex + 1} de {pages.Count} · Shift+← / Shift+→" : "";
        DrawHighlights();
    }

    private void ShowPointPage(ReviewSequencePoint point)
    {
        if (map is null || point.Measure == 0) return;
        var region = map.Measures.FirstOrDefault(m => m.Measure == point.Measure);
        if (region is not null && region.Bounds.Page - 1 != PageIndex && region.Bounds.Page - 1 < pages.Count) ShowPage(region.Bounds.Page - 1);
    }

    internal void TurnPage(int step) { if (Current is not null && pages.Count >= 2) ShowPage(PageIndex + step); }

    // ---- Measure map and highlights ----

    private Task<ScorePlaybackMap>? PrewarmMap(MidiRow row)
    {
        if (services.PrepareMap is null || row.ScorePath is null) return null;
        if (mapTasks.TryGetValue(row, out var running) && !running.IsFaulted && !running.IsCanceled) return running;
        var task = services.PrepareMap(row, prepareCancellation.Token);
        mapTasks[row] = task;
        return task;
    }

    private async Task LoadMapAsync(MidiRow row)
    {
        if (PrewarmMap(row) is not { } preparing) return;
        try
        {
            var prepared = await preparing;
            if (!ReferenceEquals(Current, row)) return;
            map = prepared.Measures.Count > 0 ? prepared : null;
            if (CurrentPoint is { } point) ShowPointPage(point);
            DrawHighlights();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException)
        { mapTasks.Remove(row); if (ReferenceEquals(Current, row)) status.Text = "Sem o mapa de compassos desta partitura (os destaques ficam de fora): " + error.Message; }
    }

    /// <summary>Draws one translucent box over each measure that has a point, on the page shown; the selected point gets a thicker outline.</summary>
    private void DrawHighlights()
    {
        overlay.Children.Clear(); highlightCount = 0;
        if (map is null || page.Source is not BitmapSource bitmap || Current is null) return;
        double width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var selected = CurrentPoint;
        foreach (var point in Points.Where(p => p.Measure > 0))
        {
            for (int measure = point.Measure; measure <= Math.Max(point.Measure, point.MeasureEnd); measure++)
            {
                var region = map.Measures.FirstOrDefault(m => m.Measure == measure);
                if (region is null || region.Bounds.Page - 1 != PageIndex) continue;
                var bounds = region.Bounds;
                var brush = ToneBrush(point.Kind);
                bool current = ReferenceEquals(point, selected);
                // The band reaches above the staff, where the chord symbols and diagrams sit.
                double top = Math.Max(0, (bounds.Y - .06) * height), bottom = Math.Min(height, (bounds.Y + bounds.Height + .012) * height);
                var box = new Rectangle
                {
                    Width = Math.Max(2, bounds.Width * width), Height = Math.Max(2, bottom - top), RadiusX = 6, RadiusY = 6,
                    Fill = new SolidColorBrush(brush.Color) { Opacity = point.Done ? .08 : current ? .22 : .14 },
                    Stroke = brush, StrokeThickness = current ? 4 : 1.5, StrokeDashArray = point.Done ? [3, 3] : null, Tag = point, Cursor = Cursors.Hand,
                    ToolTip = $"{point.MeasureText} · {point.Title}\n{point.Detail}"
                };
                Canvas.SetLeft(box, bounds.X * width); Canvas.SetTop(box, top);
                overlay.Children.Add(box); highlightCount++;
            }
        }
    }

    private static SolidColorBrush ToneBrush(ReviewSequenceKind kind)
    {
        string key = kind switch { ReviewSequenceKind.Chord or ReviewSequenceKind.Harmony => "Review", ReviewSequenceKind.EmptyMeasures => "Muted", _ => "Accent" };
        return Application.Current?.TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush(Colors.Orange);
    }

    private void OverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Rectangle { Tag: ReviewSequencePoint point }) { pointsList.SelectedItem = point; pointsList.ScrollIntoView(point); e.Handled = true; }
    }

    // ---- Points: next, alternatives ----

    /// <summary>P: the next point (wrapping around) becomes the current one and its page is shown.</summary>
    internal void NextPoint()
    {
        var points = Points;
        if (points.Count == 0) { status.Text = "Esta partitura não tem pontos para conferir."; return; }
        int next = (pointsList.SelectedIndex + 1) % points.Count;
        pointsList.SelectedIndex = next; pointsList.ScrollIntoView(points[next]);
        status.Text = $"{points[next].MeasureText} {points[next].Title}: {points[next].Detail}".Trim();
    }

    /// <summary>Selects a point (click on the list or on the page) so the smokes can drive it.</summary>
    internal void SelectPoint(int index) { if (index >= 0 && index < Points.Count) pointsList.SelectedIndex = index; }

    /// <summary>1/2/3: the chord of the current point takes that alternative; the score waits for a new generation.</summary>
    internal bool ChooseAlternative(int index)
    {
        if (busy || Current is not { } row || CurrentPoint is not { Kind: ReviewSequenceKind.Chord, Chord: { } chord } point) return false;
        if (index < 0 || index >= point.Alternatives.Count)
        {
            status.Text = point.Alternatives.Count == 0 ? "Esta cifra não tem alternativas; use 0 para mantê-la ou E para editar." : $"Esta cifra tem {point.Alternatives.Count} alternativa(s): teclas 1 a {point.Alternatives.Count}.";
            return false;
        }
        var alternative = point.Alternatives[index];
        string before = chord.Root + chord.Quality;
        if (services.ChooseChord is { } choose) choose(row, chord, alternative);
        else { chord.Root = alternative.Root; chord.Quality = alternative.Quality; }
        point.Chosen = alternative.Label;
        status.Text = $"{point.MeasureText}: cifra {before} → {alternative.Label}. Ao aprovar (A), a partitura é gerada de novo com as cifras escolhidas.";
        DrawHighlights();
        return true;
    }

    /// <summary>0: the current chord stays as it is; the point counts as reviewed.</summary>
    internal void KeepCurrentChord()
    {
        if (CurrentPoint is not { } point) return;
        point.Chosen = "manter";
        status.Text = $"{point.MeasureText} {point.Title}: mantida.".Trim();
        DrawHighlights();
    }

    private bool HasChordChanges => Points.Any(p => p.Kind == ReviewSequenceKind.Chord && p.Done && p.Chosen != "manter");

    // ---- Audio ----

    private Task<string>? PrewarmAudio(MidiRow row)
    {
        if (prepareAudio is null || row.ScorePath is null) return null;
        if (audioTasks.TryGetValue(row, out var running) && !running.IsFaulted && !running.IsCanceled) return running;
        var task = prepareAudio(row, prepareCancellation.Token);
        audioTasks[row] = task;
        return task;
    }

    /// <summary>Space: plays the song from the selected point (from the start without a map), or stops it.</summary>
    internal async Task TogglePlayAsync()
    {
        if (Current is not { } row) return;
        if (playing) { StopAudio(); status.Text = "Áudio parado."; return; }
        if (PrewarmAudio(row) is not { } preparing) { status.Text = "Esta partitura não tem áudio para ouvir."; return; }
        try
        {
            if (!preparing.IsCompleted) status.Text = "Preparando o áudio…";
            string path = await preparing;
            if (!ReferenceEquals(Current, row) || !IsLoaded && !IsVisible) return;
            double start = StartSecondsOfCurrentPoint();
            player.Open(new Uri(path)); pendingSeek = start > 0 ? start : null; player.Play(); playing = true;
            status.Text = (start > 0 && CurrentPoint is { } point ? $"Ouvindo do {point.MeasureText} ({TimeSpan.FromSeconds(start):m\\:ss})" : "Ouvindo do início") + " · Espaço para parar.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or UriFormatException)
        { audioTasks.Remove(row); status.Text = "Não foi possível preparar o áudio: " + error.Message; }
    }

    /// <summary>Where the current point starts in the audio (0 when there is no point or no map).</summary>
    internal double StartSecondsOfCurrentPoint()
    {
        if (map is null || CurrentPoint is not { Measure: > 0 } point) return 0;
        var occurrence = map.FindOccurrence(point.Measure, null);
        // A breath before the measure, so the ear catches its start.
        return occurrence is null ? 0 : Math.Max(0, occurrence.StartSeconds - .4);
    }

    private void StopAudio() { if (!playing) return; playing = false; player.Stop(); }

    // ---- Navigation and decisions ----

    private static ImageSource? LoadThumbnail(string? path) => LoadBitmap(path, 160);

    private static BitmapImage? LoadBitmap(string? path, int decodeWidth)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreImageCache; image.UriSource = new Uri(path);
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException) { return null; }
    }

    internal void Move(int step) { if (!busy) { status.Text = ""; Show(Index + step); } }
    internal void JumpTo(int index) { if (!busy && index >= 0 && index < rows.Count) { status.Text = ""; Show(index); } }

    /// <summary>Approves the current song at once (callers that do not await); the window itself uses <see cref="ApproveAsync"/>.</summary>
    internal void Approve()
    {
        if (busy || Current is not { } row) return;
        if (HasChordChanges && services.Regenerate is not null) { _ = ApproveAsync(); return; }
        approve(row); Approved++;
        status.Text = $"Aprovada: {title.Text}.";
        Show(Index + 1);
    }

    /// <summary>A: approves and moves on; a song whose chords were changed is generated again instead.</summary>
    internal async Task ApproveAsync()
    {
        if (busy || Current is not { } row) return;
        if (!HasChordChanges || services.Regenerate is not { } regenerate) { Approve(); return; }
        busy = true; status.Text = "Gerando de novo com as cifras escolhidas…";
        try { string message = await regenerate(row); audioTasks.Remove(row); mapTasks.Remove(row); pointCache.Remove(row); Show(Index + 1); status.Text = message; }
        catch (Exception error) when (error is InvalidOperationException or IOException) { status.Text = error.Message; }
        finally { busy = false; }
    }

    internal async Task RedoAsync()
    {
        if (busy || Current is not { } row) return;
        busy = true; status.Text = "Gerando de novo com a próxima melodia candidata…";
        try { string message = await redo(row); audioTasks.Remove(row); mapTasks.Remove(row); pointCache.Remove(row); Show(Index); status.Text = message; }
        catch (Exception error) when (error is InvalidOperationException or IOException) { status.Text = error.Message; }
        finally { busy = false; }
    }

    internal void Edit()
    {
        if (busy || Current is not { } row) return;
        EditRequested = row; Close();
    }

    // ---- Templates ----

    private DataTemplate PointTemplate()
    {
        var template = new DataTemplate(typeof(ReviewSequencePoint));
        var root = new FrameworkElementFactory(typeof(StackPanel)); root.SetValue(MarginProperty, new Thickness(2, 4, 2, 6));
        var line = new FrameworkElementFactory(typeof(DockPanel));
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ReviewSequencePoint.Glyph)));
        glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")); glyph.SetValue(TextBlock.FontSizeProperty, 12.0);
        glyph.SetValue(MarginProperty, new Thickness(0, 2, 8, 0)); glyph.SetValue(DockPanel.DockProperty, Dock.Left); glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        line.AppendChild(glyph);
        var state = new FrameworkElementFactory(typeof(TextBlock));
        state.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ReviewSequencePoint.StateText)));
        state.SetValue(DockPanel.DockProperty, Dock.Right); state.SetValue(TextBlock.FontSizeProperty, 11.0); state.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        state.SetResourceReference(TextBlock.ForegroundProperty, "Ready"); state.SetValue(MarginProperty, new Thickness(6, 0, 0, 0));
        line.AppendChild(state);
        var measure = new FrameworkElementFactory(typeof(TextBlock));
        measure.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ReviewSequencePoint.MeasureText)));
        measure.SetValue(DockPanel.DockProperty, Dock.Left); measure.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); measure.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
        line.AppendChild(measure);
        var head = new FrameworkElementFactory(typeof(TextBlock));
        head.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ReviewSequencePoint.Title)));
        head.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        line.AppendChild(head);
        root.AppendChild(line);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ReviewSequencePoint.Detail)));
        detail.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); detail.SetValue(TextBlock.FontSizeProperty, 11.0); detail.SetValue(OpacityProperty, .75); detail.SetValue(MarginProperty, new Thickness(20, 2, 0, 0));
        root.AppendChild(detail);
        var alternatives = new FrameworkElementFactory(typeof(ItemsControl));
        alternatives.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(ReviewSequencePoint.Options)));
        alternatives.SetValue(MarginProperty, new Thickness(20, 4, 0, 0));
        alternatives.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel))));
        alternatives.SetValue(ItemsControl.ItemTemplateProperty, AlternativeTemplate());
        root.AppendChild(alternatives);
        template.VisualTree = root;
        return template;
    }

    private DataTemplate AlternativeTemplate()
    {
        var template = new DataTemplate(typeof(ReviewAlternativeOption));
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(PaddingProperty, new Thickness(8, 2, 8, 2)); button.SetValue(MarginProperty, new Thickness(0, 0, 6, 4)); button.SetValue(FontSizeProperty, 11.5);
        button.SetValue(MinHeightProperty, 24.0);
        button.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(ReviewAlternativeOption.Label)));
        button.SetValue(ToolTipProperty, "Trocar a cifra por esta alternativa (ou tecle o número indicado); a partitura é gerada de novo ao aprovar");
        button.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(AlternativeClick));
        template.VisualTree = button;
        return template;
    }

    private void AlternativeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReviewAlternativeOption option }) return;
        var point = Points.FirstOrDefault(p => p.Alternatives.Contains(option.Alternative));
        if (point is null) return;
        pointsList.SelectedItem = point;
        ChooseAlternative(option.Key - 1);
    }

    private static Style PointContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 2, 6, 2)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Line")));
        return style;
    }

}
