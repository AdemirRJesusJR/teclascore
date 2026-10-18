using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Teclas.Core;

namespace Teclas.App;

public partial class ScoreViewer : UserControl
{
    public static readonly DependencyProperty FirstPagePathProperty = DependencyProperty.Register(
        nameof(FirstPagePath), typeof(string), typeof(ScoreViewer), new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty CurrentPageProperty = DependencyProperty.Register(
        nameof(CurrentPage), typeof(int), typeof(ScoreViewer), new PropertyMetadata(1, PageChanged));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(ScoreViewer), new PropertyMetadata(100d, ZoomChanged, CoerceZoom));
    private static readonly DependencyPropertyKey PageCountPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PageCount), typeof(int), typeof(ScoreViewer), new PropertyMetadata(0));
    public static readonly DependencyProperty PageCountProperty = PageCountPropertyKey.DependencyProperty;

    public string? FirstPagePath { get => (string?)GetValue(FirstPagePathProperty); set => SetValue(FirstPagePathProperty, value); }
    public int CurrentPage { get => (int)GetValue(CurrentPageProperty); set => SetValue(CurrentPageProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public int PageCount => (int)GetValue(PageCountProperty);

    private readonly ObservableCollection<ScoreViewerPage> pages = [];
    private CancellationTokenSource? loading;
    private int imageRequest;
    private bool selecting;
    private bool settingZoom;
    private bool loadedOnce;
    private bool thumbnailsVisible = true;
    private string fitMode = "width";
    private BitmapSource? pageBitmap;
    private Window? fullScreen;
    private Window? fullScreenHost;
    private ScorePlaybackMap? playbackMap;
    private ScorePlaybackOccurrence? playbackPosition;
    private ScorePlaybackOccurrence? selectedPosition;
    private double? playbackSeconds;
    private bool followPlayback;
    public ScorePlaybackMap? PlaybackMap => playbackMap;
    public event EventHandler<ScorePositionSelectedEventArgs>? PositionSelected;

    public ScoreViewer()
    {
        InitializeComponent();
        ThumbnailList.ItemsSource = pages;
        RefreshControls();
        Loaded += async (_, _) =>
        {
            if (!loadedOnce) { loadedOnce = true; await ReloadAsync(); }
            else ApplyFit();
        };
        Unloaded += (_, _) =>
        {
            loading?.Cancel();
            imageRequest++;
            loadedOnce = false;
            if (fullScreen is { } window) window.Close();
        };
    }

    private static void SourceChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ScoreViewer)obj;
        viewer.playbackMap = null; viewer.playbackPosition = null; viewer.selectedPosition = null;
        viewer.playbackSeconds = null;
        if (viewer.IsLoaded) _ = viewer.ReloadAsync();
    }

    private static void PageChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ScoreViewer)obj;
        if (viewer.IsInitialized && viewer.PageCount > 0) _ = viewer.ShowPageAsync();
    }

    private static object CoerceZoom(DependencyObject obj, object value) =>
        value is double number && double.IsFinite(number) ? Math.Clamp(number, 25, 300) : 100d;

    private static void ZoomChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ScoreViewer)obj;
        if (!viewer.IsInitialized) return;
        if (!viewer.settingZoom) viewer.fitMode = "manual";
        viewer.ApplyImageSize();
        viewer.RefreshControls();
    }

    /// <summary>Reloads rendered pages, including when the source path itself has not changed.</summary>
    public async Task ReloadAsync()
    {
        loading?.Cancel();
        loading?.Dispose();
        var cancellation = loading = new CancellationTokenSource();
        var token = cancellation.Token;
        imageRequest++;
        pageBitmap = null;
        PageImage.Source = null;
        PositionOverlay.Children.Clear();
        pages.Clear();
        SetValue(PageCountPropertyKey, 0);
        RefreshControls();
        var path = playbackMap?.PreviewPages.FirstOrDefault() ?? FirstPagePath;
        SetMessage(string.IsNullOrWhiteSpace(path)
            ? "Gere ou selecione uma partitura para visualizar suas páginas."
            : "Carregando as páginas da partitura…");
        try
        {
            var files = await Task.Run(() => ScorePageFiles.Discover(path), token);
            if (token.IsCancellationRequested) return;
            foreach (var file in files) pages.Add(new ScoreViewerPage(file, pages.Count + 1));
            SetValue(PageCountPropertyKey, pages.Count);
            if (pages.Count == 0)
            {
                SetMessage(string.IsNullOrWhiteSpace(path)
                    ? "Gere ou selecione uma partitura para visualizar suas páginas."
                    : "As páginas da prévia não foram encontradas. Gere a partitura novamente para restaurar a visualização.");
                RefreshControls();
                return;
            }
            SetCurrentValue(CurrentPageProperty, Math.Clamp(CurrentPage, 1, pages.Count));
            await ShowPageAsync();
            if (token.IsCancellationRequested) return;
            foreach (var page in pages.ToArray())
            {
                token.ThrowIfCancellationRequested();
                BitmapImage? thumbnail;
                try { thumbnail = await Task.Run(() => ReadBitmap(page.Path, 152), token); }
                catch (Exception exception) when (IsImageFailure(exception)) { thumbnail = null; }
                if (token.IsCancellationRequested) return;
                page.Thumbnail = thumbnail;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (IsImageFailure(exception))
        {
            if (!token.IsCancellationRequested)
                SetMessage("Não foi possível ler a prévia. Confira se a pasta está acessível ou gere a partitura novamente.");
        }
    }

    private async Task ShowPageAsync()
    {
        if (pages.Count == 0) return;
        var index = Math.Clamp(CurrentPage - 1, 0, pages.Count - 1);
        if (CurrentPage != index + 1) { SetCurrentValue(CurrentPageProperty, index + 1); return; }
        var request = ++imageRequest;
        var page = pages[index];
        selecting = true;
        ThumbnailList.SelectedIndex = index;
        ThumbnailList.ScrollIntoView(page);
        selecting = false;
        pageBitmap = null;
        PageImage.Source = null;
        SetMessage($"Carregando página {index + 1}…");
        RefreshControls();
        try
        {
            var bitmap = await Task.Run(() => ReadBitmap(page.Path));
            if (request != imageRequest) return;
            pageBitmap = bitmap;
            PageImage.Source = bitmap;
            ViewerMessagePanel.Visibility = Visibility.Collapsed;
            PageScroll.ScrollToTop();
            PageScroll.ScrollToLeftEnd();
            ApplyFit();
            RefreshControls();
            DrawPositions();
            FollowPosition();
        }
        catch (Exception exception) when (IsImageFailure(exception))
        {
            if (request == imageRequest)
                SetMessage($"Não foi possível abrir a página {index + 1}. O arquivo pode estar ausente ou danificado. Você pode continuar pelas outras páginas.");
        }
    }

    private static BitmapImage ReadBitmap(string path, int decodeWidth = 0)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        // Each load uses a fresh stream; the URI cache does not participate.
        // IgnoreImageCache requires a UriSource and fails for stream-only images.
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool IsImageFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or
        System.Security.SecurityException or System.Runtime.InteropServices.COMException or FileFormatException;

    private void SetMessage(string text)
    {
        ViewerMessage.Text = text;
        ViewerMessagePanel.Visibility = Visibility.Visible;
    }

    private void RefreshControls()
    {
        if (PageCounter is null) return;
        PageCounter.Text = PageCount == 0 ? "0 / 0" : $"{CurrentPage} / {PageCount}";
        PreviousPageButton.IsEnabled = PageCount > 0 && CurrentPage > 1;
        NextPageButton.IsEnabled = PageCount > 0 && CurrentPage < PageCount;
        ZoomOutButton.IsEnabled = PageCount > 0 && Zoom > 25;
        ZoomInButton.IsEnabled = PageCount > 0 && Zoom < 300;
        ZoomCombo.IsEnabled = FitWidthButton.IsEnabled = FitPageButton.IsEnabled = FullscreenButton.IsEnabled = PageCount > 0;
        ViewerToolbar.Visibility = PageCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        settingZoom = true;
        try
        {
            var exact = ZoomCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
                double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out var value) && Math.Abs(value - Zoom) < .1);
            ZoomCombo.SelectedItem = exact;
            // The non-editable selection also displays arbitrary fit percentages.
            if (exact is null)
            {
                var item = new ComboBoxItem { Content = $"{Zoom:0}%", Tag = Zoom.ToString(CultureInfo.InvariantCulture) };
                if (ZoomCombo.Items.Count > 9) ZoomCombo.Items.RemoveAt(9);
                ZoomCombo.Items.Add(item);
                ZoomCombo.SelectedItem = item;
            }
        }
        finally { settingZoom = false; }
        ViewerStatus.Text = PageCount == 0 ? "Miniaturas · zoom · tela cheia" :
            $"{PageCount} {(PageCount == 1 ? "página" : "páginas")} · zoom {Zoom:0}% · Ctrl + roda do mouse para ampliar";
        if (playbackMap is not null)
            ViewerStatus.Text += playbackMap.CanSynchronize ? " · clique no ataque ou compasso para selecionar" : " · acompanhamento indisponível: " + playbackMap.Limitations.LastOrDefault();
    }

    private void ApplyImageSize()
    {
        if (pageBitmap is null) return;
        PageImage.Width = pageBitmap.Width * Zoom / 100;
        PageImage.Height = pageBitmap.Height * Zoom / 100;
        PageSurface.Width = PageImage.Width; PageSurface.Height = PageImage.Height;
        DrawPositions();
    }

    private void ApplyFit()
    {
        if (pageBitmap is null) return;
        if (fitMode != "manual")
        {
            // Reserve the themed scrollbar and image margins so fitting does not
            // alternate between showing and hiding a scrollbar on each layout.
            var width = Math.Max(1, PageScroll.ActualWidth - 38);
            var height = Math.Max(1, PageScroll.ActualHeight - 90); // room for the floating bar under the page
            if (width <= 1 || height <= 1) return;
            var zoom = width / pageBitmap.Width * 100;
            if (fitMode == "page") zoom = Math.Min(zoom, height / pageBitmap.Height * 100);
            settingZoom = true;
            try { SetCurrentValue(ZoomProperty, zoom); }
            finally { settingZoom = false; }
        }
        ApplyImageSize();
        RefreshControls();
    }

    private void PreviousPageClick(object sender, RoutedEventArgs e) => CurrentPage = Math.Max(1, CurrentPage - 1);
    private void NextPageClick(object sender, RoutedEventArgs e) => CurrentPage = Math.Min(PageCount, CurrentPage + 1);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => Zoom = Math.Max(25, Zoom - 25);
    private void ZoomInClick(object sender, RoutedEventArgs e) => Zoom = Math.Min(300, Zoom + 25);
    private void ZoomSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!settingZoom && ZoomCombo.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out var value)) Zoom = value;
    }
    private void FitWidthClick(object sender, RoutedEventArgs e) { fitMode = "width"; ApplyFit(); }
    private void FitPageClick(object sender, RoutedEventArgs e) { fitMode = "page"; ApplyFit(); }
    private void PageViewportChanged(object sender, SizeChangedEventArgs e) => ApplyFit();
    private double thumbnailWidth = 112;

    /// <summary>The floating bar stays discreet over the music and becomes solid while the mouse is over the preview.</summary>
    private void PreviewHover(object sender, System.Windows.Input.MouseEventArgs e) =>
        ViewerToolbar.Opacity = PreviewSurface.IsMouseOver || ViewerToolbar.IsKeyboardFocusWithin ? 1 : .72;

    private void ToggleThumbnailsClick(object sender, RoutedEventArgs e)
    {
        // Hiding keeps the width the user dragged, so showing again restores it.
        if (thumbnailsVisible && ThumbnailColumn.ActualWidth > 40) thumbnailWidth = ThumbnailColumn.ActualWidth;
        thumbnailsVisible = !thumbnailsVisible;
        ThumbnailColumn.Width = new GridLength(thumbnailsVisible ? thumbnailWidth : 0);
        ThumbnailSplitterColumn.Width = new GridLength(thumbnailsVisible ? 6 : 0);
        ThumbnailList.Visibility = ThumbnailSplitter.Visibility = thumbnailsVisible ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ThumbnailSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!selecting && ThumbnailList.SelectedIndex >= 0) CurrentPage = ThumbnailList.SelectedIndex + 1;
    }
    private void PageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        Zoom += e.Delta > 0 ? 10 : -10;
        e.Handled = true;
    }
    private void ViewerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && fullScreenHost is not null) { fullScreenHost.Close(); e.Handled = true; }
        else if (e.Key == Key.F11) { OpenFullscreen(); e.Handled = true; }
        else if (e.Key == Key.PageDown && CurrentPage < PageCount) { CurrentPage++; e.Handled = true; }
        else if (e.Key == Key.PageUp && CurrentPage > 1) { CurrentPage--; e.Handled = true; }
    }
    private void FullscreenClick(object sender, RoutedEventArgs e) => OpenFullscreen();

    public void OpenFullscreen()
    {
        if (fullScreenHost is not null) { fullScreenHost.Close(); return; }
        if (PageCount == 0) return;
        if (fullScreen is not null) { fullScreen.Activate(); return; }
        var window = CreateFullscreenWindow();
        window.Show();
        ((ScoreViewer)window.Content).Focus();
    }

    private Window CreateFullscreenWindow()
    {
        var viewer = new ScoreViewer { FirstPagePath = FirstPagePath, CurrentPage = CurrentPage, Margin = new Thickness(20), fitMode = "page" };
        var window = new Window
        {
            Title = "TeclaScore · partitura", Content = viewer, Owner = Window.GetWindow(this),
            WindowStyle = WindowStyle.None, WindowState = WindowState.Maximized,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        window.SetResourceReference(BackgroundProperty, "Background");
        window.SetResourceReference(ForegroundProperty, "Ink");
        viewer.FullscreenButton.Content = "\uE73F";
        viewer.FullscreenButton.ToolTip = "Voltar (Esc ou F11)";
        viewer.fullScreenHost = window;
        viewer.playbackMap = playbackMap;
        viewer.playbackPosition = playbackPosition;
        viewer.selectedPosition = selectedPosition;
        viewer.playbackSeconds = playbackSeconds;
        viewer.followPlayback = followPlayback;
        viewer.PositionSelected += (_, e) => PositionSelected?.Invoke(this, e);
        fullScreen = window;
        window.Closed += (_, _) =>
        {
            fullScreen = null;
            if (viewer.CurrentPage >= 1 && viewer.CurrentPage <= PageCount) CurrentPage = viewer.CurrentPage;
            Focus();
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F11) { window.Close(); e.Handled = true; }
        };
        return window;
    }

    /// <summary>Uses the exact pages exported with this map; never changes the library's preview binding.</summary>
    /// <summary>Moves half a screen down (or up) inside the page; false at the page edge, so the caller turns the page.</summary>
    public bool ScrollHalf(int direction)
    {
        double step = Math.Max(40, PageScroll.ViewportHeight * .5);
        if (direction > 0 && PageScroll.VerticalOffset + PageScroll.ViewportHeight < PageScroll.ExtentHeight - 2)
        { PageScroll.ScrollToVerticalOffset(Math.Min(PageScroll.ScrollableHeight, PageScroll.VerticalOffset + step)); return true; }
        if (direction < 0 && PageScroll.VerticalOffset > 2)
        { PageScroll.ScrollToVerticalOffset(Math.Max(0, PageScroll.VerticalOffset - step)); return true; }
        return false;
    }

    public void SetPlaybackMap(ScorePlaybackMap? map)
    {
        Dispatcher.VerifyAccess();
        playbackMap = map; playbackPosition = selectedPosition = null; playbackSeconds = null; followPlayback = false;
        PositionOverlay.Children.Clear();
        if (fullScreen?.Content is ScoreViewer child) child.SetPlaybackMap(map);
        if (IsLoaded) _ = ReloadAsync();
        else RefreshControls();
    }

    public void UpdatePlaybackPosition(double seconds, bool follow = true)
    {
        Dispatcher.VerifyAccess();
        playbackSeconds = double.IsFinite(seconds) && seconds >= 0 ? seconds : null;
        playbackPosition = playbackSeconds is { } current ? playbackMap?.At(current) : null;
        followPlayback = follow;
        if (fullScreen?.Content is ScoreViewer child) child.UpdatePlaybackPosition(seconds, follow);
        if (follow && playbackPosition is { } position && playbackMap?.Measures.FirstOrDefault(m => m.Measure == position.Measure) is { } measure &&
            measure.Bounds.Page != CurrentPage && measure.Bounds.Page <= PageCount)
            SetCurrentValue(CurrentPageProperty, measure.Bounds.Page);
        DrawPositions();
        if (follow) FollowPosition();
    }

    public void SelectEvent(string eventId)
    {
        var segment = playbackMap?.Segments.FirstOrDefault(s => s.EventIds.Contains(eventId, StringComparer.Ordinal));
        if (segment is null) return;
        selectedPosition = playbackMap!.FindOccurrence(segment.Measure, segment.Id);
        if (segment.Bounds.Page != CurrentPage && segment.Bounds.Page <= PageCount) SetCurrentValue(CurrentPageProperty, segment.Bounds.Page);
        DrawPositions();
    }

    private void PageSelected(object sender, MouseButtonEventArgs e)
    {
        if (playbackMap?.CanSynchronize != true || pageBitmap is null || PageSurface.ActualWidth <= 0 || PageSurface.ActualHeight <= 0) return;
        var point = e.GetPosition(PageSurface);
        double x = point.X / PageSurface.ActualWidth, y = point.Y / PageSurface.ActualHeight;
        e.Handled = TrySelectPosition(x, y);
    }

    /// <summary>Right click on a measure (or on its chord symbols above the staff); 0 means the measure map is not prepared yet.</summary>
    public event EventHandler<int>? MeasureContextRequested;

    private void PageContextRequested(object sender, MouseButtonEventArgs e)
    {
        if (pageBitmap is null || PageSurface.ActualWidth <= 0 || PageSurface.ActualHeight <= 0) return;
        if (playbackMap?.CanSynchronize != true) { MeasureContextRequested?.Invoke(this, 0); e.Handled = true; return; }
        var point = e.GetPosition(PageSurface);
        if (MeasureAt(point.X / PageSurface.ActualWidth, point.Y / PageSurface.ActualHeight) is { } measure)
        { MeasureContextRequested?.Invoke(this, measure); e.Handled = true; }
    }

    /// <summary>Measure under a page point; chord names and diagrams sit above the staff, so the band reaches higher.</summary>
    internal int? MeasureAt(double x, double y) => playbackMap?.Measures
        .Where(m => m.Bounds.Page == CurrentPage && x >= m.Bounds.X && x <= m.Bounds.X + m.Bounds.Width && y >= m.Bounds.Y - .075 && y <= m.Bounds.Y + m.Bounds.Height + .014)
        .OrderBy(m => Math.Abs(y - (m.Bounds.Y + m.Bounds.Height / 2))).Select(m => (int?)m.Measure).FirstOrDefault();

    private bool TrySelectPosition(double x, double y)
    {
        if (playbackMap?.CanSynchronize != true || !double.IsFinite(x) || !double.IsFinite(y)) return false;
        // Native mpos spans all staves of a system. Vertical tolerance permits ledger notes,
        // but clicks outside a real measure never invent a time position.
        var measure = playbackMap.Measures.Where(m => m.Bounds.Page == CurrentPage && x >= m.Bounds.X && x <= m.Bounds.X + m.Bounds.Width &&
            y >= m.Bounds.Y - .014 && y <= m.Bounds.Y + m.Bounds.Height + .014)
            .OrderBy(m => Math.Abs(y - (m.Bounds.Y + m.Bounds.Height / 2))).FirstOrDefault();
        if (measure is null) return false;
        var segments = playbackMap.Segments.Where(s => s.Measure == measure.Measure && s.Bounds.Page == CurrentPage).ToArray();
        var segment = segments.Where(s => x >= s.Bounds.X - .004 && x <= s.Bounds.X + Math.Max(.006, s.Bounds.Width) + .004)
            .OrderBy(s => Math.Abs(x - (s.Bounds.X + s.Bounds.Width / 2))).FirstOrDefault();
        var occurrence = playbackMap.FindOccurrence(measure.Measure, segment?.Id, followPlayback ? playbackSeconds : null);
        if (occurrence is null) return false;
        selectedPosition = occurrence;
        DrawPositions();
        var barOccurrence = playbackMap.MeasureTimeline.FirstOrDefault(t => t.Measure == occurrence.Measure &&
            occurrence.StartSeconds >= t.StartSeconds - .0001 && occurrence.StartSeconds < t.EndSeconds);
        PositionSelected?.Invoke(this, new(measure.Measure, segment?.Id, segment?.EventIds ?? [],
            occurrence.StartSeconds, occurrence.EndSeconds, occurrence.Index)
            { MeasureStartSeconds = barOccurrence?.StartSeconds ?? occurrence.StartSeconds, MeasureEndSeconds = barOccurrence?.EndSeconds ?? occurrence.EndSeconds });
        return true;
    }

    private void DrawPositions()
    {
        PositionOverlay.Children.Clear();
        if (playbackMap is null || pageBitmap is null || !double.IsFinite(PageImage.Width) || !double.IsFinite(PageImage.Height)) return;
        void Draw(ScorePageRegion box, Color color, double opacity, double thickness)
        {
            if (box.Page != CurrentPage) return;
            var rectangle = new Rectangle { Width = Math.Max(2, box.Width * PageImage.Width), Height = Math.Max(3, box.Height * PageImage.Height),
                Fill = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B)),
                Stroke = new SolidColorBrush(color), StrokeThickness = thickness, RadiusX = 3, RadiusY = 3 };
            Canvas.SetLeft(rectangle, box.X * PageImage.Width); Canvas.SetTop(rectangle, box.Y * PageImage.Height);
            PositionOverlay.Children.Add(rectangle);
        }
        if (playbackPosition is { } playing)
        {
            var measure = playbackMap.Measures.FirstOrDefault(m => m.Measure == playing.Measure);
            if (measure is not null) Draw(measure.Bounds, Color.FromRgb(31, 137, 209), .10, 1);
            if (playing.SegmentId is { } id && playbackMap.Segments.FirstOrDefault(s => s.Id == id) is { } segment)
                Draw(segment.Bounds, Color.FromRgb(31, 137, 209), .24, 1.5);
        }
        if (selectedPosition is { } selected)
        {
            var box = selected.SegmentId is { } id ? playbackMap.Segments.FirstOrDefault(s => s.Id == id)?.Bounds :
                playbackMap.Measures.FirstOrDefault(m => m.Measure == selected.Measure)?.Bounds;
            if (box is not null) Draw(box, Color.FromRgb(203, 124, 23), .12, 2);
        }
    }

    private void FollowPosition()
    {
        if (!followPlayback || playbackPosition is null || playbackMap is null || pageBitmap is null) return;
        var measure = playbackMap.Measures.FirstOrDefault(m => m.Measure == playbackPosition.Measure);
        if (measure is null || measure.Bounds.Page != CurrentPage) return;
        double top = measure.Bounds.Y * PageImage.Height + 12, bottom = top + measure.Bounds.Height * PageImage.Height;
        if (top < PageScroll.VerticalOffset + 20 || bottom > PageScroll.VerticalOffset + PageScroll.ViewportHeight - 30)
            PageScroll.ScrollToVerticalOffset(Math.Max(0, top - Math.Min(90, PageScroll.ViewportHeight / 4)));
    }
}

public sealed class ScorePositionSelectedEventArgs(int measure, int? segmentId, IReadOnlyList<string> eventIds,
    double startSeconds, double endSeconds, int occurrenceIndex) : EventArgs
{
    public int Measure { get; } = measure;
    public int? SegmentId { get; } = segmentId;
    public IReadOnlyList<string> EventIds { get; } = eventIds;
    public double StartSeconds { get; } = startSeconds;
    public double EndSeconds { get; } = endSeconds;
    public int OccurrenceIndex { get; } = occurrenceIndex;
    public double MeasureStartSeconds { get; init; } = startSeconds;
    public double MeasureEndSeconds { get; init; } = endSeconds;
}

internal sealed class ScoreViewerPage(string path, int number) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Label { get; } = $"Página {number}";
    private BitmapImage? thumbnail;
    public BitmapImage? Thumbnail
    {
        get => thumbnail;
        set { thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
