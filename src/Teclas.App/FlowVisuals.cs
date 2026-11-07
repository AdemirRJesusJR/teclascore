using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Teclas.App;

/// <summary>
/// Where notes sit on the piano: as a thin bar (track rows: the track's range over the 88 keys, the user's keyboard dashed)
/// or as a small keyboard (the "Seu teclado" card: the keys of the user's keyboard with the melody range highlighted).
/// </summary>
public sealed class KeyboardRangeView : FrameworkElement
{
    private static FrameworkPropertyMetadata Render(object value) => new(value, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty ShowKeysProperty = DependencyProperty.Register(nameof(ShowKeys), typeof(bool), typeof(KeyboardRangeView), Render(false));
    public static readonly DependencyProperty LowProperty = DependencyProperty.Register(nameof(Low), typeof(int), typeof(KeyboardRangeView), Render(0));
    public static readonly DependencyProperty HighProperty = DependencyProperty.Register(nameof(High), typeof(int), typeof(KeyboardRangeView), Render(-1));
    public static readonly DependencyProperty KeyboardLowProperty = DependencyProperty.Register(nameof(KeyboardLow), typeof(int), typeof(KeyboardRangeView), Render(36));
    public static readonly DependencyProperty KeyboardHighProperty = DependencyProperty.Register(nameof(KeyboardHigh), typeof(int), typeof(KeyboardRangeView), Render(96));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(KeyboardRangeView), Render(Brushes.RoyalBlue));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof(Brush), typeof(KeyboardRangeView), Render(Brushes.LightSteelBlue));
    public static readonly DependencyProperty OutsideProperty = DependencyProperty.Register(nameof(Outside), typeof(Brush), typeof(KeyboardRangeView), Render(Brushes.DarkOrange));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(KeyboardRangeView), Render(Brushes.Gainsboro));
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(nameof(Line), typeof(Brush), typeof(KeyboardRangeView), Render(Brushes.Gray));
    public static readonly DependencyProperty WhiteKeyProperty = DependencyProperty.Register(nameof(WhiteKey), typeof(Brush), typeof(KeyboardRangeView), Render(Brushes.White));
    public static readonly DependencyProperty BlackKeyProperty = DependencyProperty.Register(nameof(BlackKey), typeof(Brush), typeof(KeyboardRangeView), Render(Brushes.Black));

    public bool ShowKeys { get => (bool)GetValue(ShowKeysProperty); set => SetValue(ShowKeysProperty, value); }
    /// <summary>Lowest and highest note to mark; High below Low marks nothing.</summary>
    public int Low { get => (int)GetValue(LowProperty); set => SetValue(LowProperty, value); }
    public int High { get => (int)GetValue(HighProperty); set => SetValue(HighProperty, value); }
    public int KeyboardLow { get => (int)GetValue(KeyboardLowProperty); set => SetValue(KeyboardLowProperty, value); }
    public int KeyboardHigh { get => (int)GetValue(KeyboardHighProperty); set => SetValue(KeyboardHighProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Highlight { get => (Brush)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public Brush Outside { get => (Brush)GetValue(OutsideProperty); set => SetValue(OutsideProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush Line { get => (Brush)GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public Brush WhiteKey { get => (Brush)GetValue(WhiteKeyProperty); set => SetValue(WhiteKeyProperty, value); }
    public Brush BlackKey { get => (Brush)GetValue(BlackKeyProperty); set => SetValue(BlackKeyProperty, value); }

    private static bool IsBlack(int pitch) => ((pitch % 12) + 12) % 12 is 1 or 3 or 6 or 8 or 10;

    protected override Size MeasureOverride(Size available) => new(
        double.IsInfinity(available.Width) ? 160 : available.Width, ShowKeys ? 48 : 14);

    protected override void OnRender(DrawingContext context)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 4 || height < 2) return;
        if (ShowKeys) RenderKeys(context, width, height); else RenderBar(context, width, height);
    }

    private void RenderBar(DrawingContext context, double width, double height)
    {
        const int first = 21, last = 108;
        double X(double pitch) => Math.Clamp((pitch - first) / (last - first + 1.0), 0, 1) * width;
        double middle = height / 2;
        context.DrawRoundedRectangle(Track, null, new Rect(0, middle - 2.5, width, 5), 2.5, 2.5);
        var keyboard = new Rect(X(KeyboardLow), middle - 5.5, Math.Max(2, X(KeyboardHigh + 1) - X(KeyboardLow)), 11);
        var dashed = new Pen(Line, 1) { DashStyle = new DashStyle([3, 2], 0) };
        context.DrawRoundedRectangle(null, dashed, keyboard, 3, 3);
        if (High < Low) return;
        double left = X(Low), right = Math.Max(left + 3, X(High + 1));
        context.DrawRoundedRectangle(Accent, null, new Rect(left, middle - 2.5, right - left, 5), 2.5, 2.5);
        if (Low < KeyboardLow) context.DrawRoundedRectangle(Outside, null, new Rect(left, middle - 2.5, Math.Max(2, Math.Min(right, X(KeyboardLow)) - left), 5), 2.5, 2.5);
        if (High > KeyboardHigh) { double from = Math.Max(left, X(KeyboardHigh + 1)); context.DrawRoundedRectangle(Outside, null, new Rect(from, middle - 2.5, Math.Max(2, right - from), 5), 2.5, 2.5); }
    }

    private void RenderKeys(DrawingContext context, double width, double height)
    {
        int first = KeyboardLow, last = Math.Max(KeyboardLow + 11, KeyboardHigh);
        if (IsBlack(first)) first--;
        if (IsBlack(last)) last++;
        var whites = Enumerable.Range(first, last - first + 1).Where(p => !IsBlack(p)).ToArray();
        if (whites.Length == 0) return;
        double keyWidth = width / whites.Length;
        var outline = new Pen(Line, 1);
        var index = new Dictionary<int, int>();
        for (int i = 0; i < whites.Length; i++)
        {
            index[whites[i]] = i;
            bool marked = whites[i] >= Low && whites[i] <= High;
            var rect = new Rect(i * keyWidth + 0.5, 0.5, Math.Max(1, keyWidth - 1), height - 1);
            context.DrawRoundedRectangle(marked ? Highlight : WhiteKey, outline, rect, 1.5, 1.5);
        }
        double blackWidth = keyWidth * 0.62, blackHeight = height * 0.6;
        for (int pitch = first + 1; pitch < last; pitch++)
        {
            if (!IsBlack(pitch) || !index.TryGetValue(pitch - 1, out int left)) continue;
            bool marked = pitch >= Low && pitch <= High;
            var rect = new Rect((left + 1) * keyWidth - blackWidth / 2, 0.5, blackWidth, blackHeight);
            context.DrawRoundedRectangle(marked ? Accent : BlackKey, null, rect, 1.2, 1.2);
        }
    }
}

/// <summary>The song as one bar per measure (height = notes of the melody), with the measures to check marked.</summary>
public sealed class MeasureTimeline : FrameworkElement
{
    private static FrameworkPropertyMetadata Render(object? value) => new(value, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(MeasureTimeline), Render(null));
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers), typeof(IReadOnlyList<int>), typeof(MeasureTimeline), Render(null));
    public static readonly DependencyProperty DoneMarkersProperty = DependencyProperty.Register(nameof(DoneMarkers), typeof(IReadOnlyList<int>), typeof(MeasureTimeline), Render(null));
    public static readonly DependencyProperty BarProperty = DependencyProperty.Register(nameof(Bar), typeof(Brush), typeof(MeasureTimeline), Render(Brushes.LightSteelBlue));
    public static readonly DependencyProperty MarkerProperty = DependencyProperty.Register(nameof(Marker), typeof(Brush), typeof(MeasureTimeline), Render(Brushes.DarkOrange));
    public static readonly DependencyProperty DoneProperty = DependencyProperty.Register(nameof(Done), typeof(Brush), typeof(MeasureTimeline), Render(Brushes.SeaGreen));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    /// <summary>Zero-based measures with something to check.</summary>
    public IReadOnlyList<int>? Markers { get => (IReadOnlyList<int>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    /// <summary>Zero-based measures the user already checked.</summary>
    public IReadOnlyList<int>? DoneMarkers { get => (IReadOnlyList<int>?)GetValue(DoneMarkersProperty); set => SetValue(DoneMarkersProperty, value); }
    public Brush Bar { get => (Brush)GetValue(BarProperty); set => SetValue(BarProperty, value); }
    public Brush Marker { get => (Brush)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }
    public Brush Done { get => (Brush)GetValue(DoneProperty); set => SetValue(DoneProperty, value); }

    /// <summary>The zero-based measure under a click.</summary>
    public event EventHandler<int>? MeasureClicked;

    public MeasureTimeline() { Cursor = Cursors.Hand; Focusable = false; }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 300 : available.Width, 40);

    private int MeasureAt(double x) => Values is { Count: > 0 } values ? Math.Clamp((int)(x / Math.Max(1, ActualWidth) * values.Count), 0, values.Count - 1) : -1;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        int measure = MeasureAt(e.GetPosition(this).X);
        if (measure >= 0) { MeasureClicked?.Invoke(this, measure); e.Handled = true; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int measure = MeasureAt(e.GetPosition(this).X);
        ToolTip = measure < 0 ? null : string.Format(CultureInfo.CurrentCulture, "Compasso {0}", measure + 1);
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Values is not { Count: > 0 } values || ActualWidth < 4) return;
        double width = ActualWidth, height = ActualHeight, max = Math.Max(1, values.Max());
        double slot = width / values.Count, gap = slot >= 5 ? Math.Min(3, slot * 0.35) : 0;
        var markers = Markers?.ToHashSet() ?? []; var done = DoneMarkers?.ToHashSet() ?? [];
        for (int i = 0; i < values.Count; i++)
        {
            bool marked = markers.Contains(i), checkedMarker = done.Contains(i);
            double share = values[i] <= 0 ? 0.08 : 0.22 + 0.78 * values[i] / max;
            double barHeight = marked ? height : Math.Max(2, share * (height - 6));
            var brush = checkedMarker ? Done : marked ? Marker : Bar;
            double barWidth = Math.Max(1, slot - gap);
            if (marked) barWidth = Math.Max(barWidth, 3);
            context.DrawRoundedRectangle(brush, null, new Rect(i * slot + (slot - barWidth) / 2, height - barHeight, barWidth, barHeight), Math.Min(2, barWidth / 2), Math.Min(2, barWidth / 2));
        }
    }
}

/// <summary>A ring that fills with the share of the flow already done (0–1).</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RingProperty = DependencyProperty.Register(nameof(Ring), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.RoyalBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Ring { get => (Brush)GetValue(RingProperty); set => SetValue(RingProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnRender(DrawingContext context)
    {
        double size = Math.Min(ActualWidth, ActualHeight), thickness = Math.Max(3, size * 0.09);
        if (size < 8) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2); double radius = (size - thickness) / 2;
        context.DrawEllipse(null, new Pen(Ring, thickness), center, radius, radius);
        double value = Math.Clamp(Value, 0, 1);
        if (value <= 0) return;
        var pen = new Pen(Fill, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 0.999) { context.DrawEllipse(null, pen, center, radius, radius); return; }
        double angle = value * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, value > 0.5, SweepDirection.Clockwise, true));
        context.DrawGeometry(null, pen, new PathGeometry([figure]));
    }
}
