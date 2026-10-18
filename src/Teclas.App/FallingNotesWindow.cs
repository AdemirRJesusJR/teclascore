using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Falling notes: the score as blocks descending onto a keyboard, melody and chords in different colors, at an adjustable
/// speed, optionally sounding through the MIDI output. For players who do not read notation fluently yet.
/// </summary>
public sealed class FallingNotesWindow : Window
{
    private readonly FallingNotesView view;
    private readonly Button play;
    private readonly ComboBox speed;
    private readonly CheckBox melody, chords, sound;
    private readonly Stopwatch clock = new();
    private double offset, lastPosition;
    private readonly double bpm;
    private readonly Func<IMidiOutputTransport?> outputFactory;
    private IMidiOutputTransport? output;
    private readonly HashSet<FallingNote> sounding = [];
    public double Position => offset + clock.Elapsed.TotalSeconds * bpm / 60 * (double)(speed.SelectedValue ?? 1.0);
    public bool Playing => clock.IsRunning;
    internal FallingNotesView View => view;
    /// <summary>Tests drive the position themselves; the frame loop then leaves it alone.</summary>
    internal bool Driven { get; set; }

    public FallingNotesWindow(string title, IReadOnlyList<FallingNote> notes, double bpm, Func<IMidiOutputTransport?> outputFactory)
    {
        Title = "Notas caindo · " + title; Width = 1100; Height = 760; MinWidth = 700; MinHeight = 480; DialogUi.Theme(this);
        this.bpm = bpm; this.outputFactory = outputFactory;
        var root = new DockPanel { Margin = new Thickness(14) }; Content = root;
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        play = DialogUi.Button("▶ Tocar", (_, _) => Toggle(), true); play.Name = "FallingPlay"; play.ToolTip = "Tocar ou pausar (Espaço)"; bar.Children.Add(play);
        var restart = DialogUi.Button("⏮ Início", (_, _) => Restart()); restart.ToolTip = "Voltar ao começo (Home)"; bar.Children.Add(restart);
        bar.Children.Add(new TextBlock { Text = "Velocidade", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
        speed = new ComboBox { Width = 110, ItemsSource = new[] { .25, .5, .75, 1.0 }.Select(v => new { Value = v, Label = $"{v * 100:0}%" }).ToArray(), DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = .5, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(speed, "Velocidade das notas caindo");
        speed.SelectionChanged += (_, _) => { offset = lastPosition; if (clock.IsRunning) clock.Restart(); else clock.Reset(); };
        bar.Children.Add(speed);
        melody = new CheckBox { Content = "Melodia", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        chords = new CheckBox { Content = "Acordes", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        sound = new CheckBox { Content = "Som", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Toca as notas pelo sintetizador do Windows quando chegam ao teclado" };
        foreach (var box in new[] { melody, chords, sound }) { box.Click += (_, _) => Silence(); bar.Children.Add(box); }
        bar.Children.Add(new TextBlock { Text = "  Azul: melodia · Âmbar: acordes", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(12, 0, 0, 0) });
        view = new FallingNotesView(notes) { Name = "FallingView" };
        root.Children.Add(new Border { Child = view, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(24, 27, 33)) });
        CompositionTarget.Rendering += Frame;
        Closed += (_, _) => { CompositionTarget.Rendering -= Frame; Silence(); output?.Dispose(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { Toggle(); e.Handled = true; }
            else if (e.Key == Key.Home) { Restart(); e.Handled = true; }
        };
    }

    internal void Toggle()
    {
        if (clock.IsRunning) { offset = Position; clock.Reset(); Silence(); play.Content = "▶ Tocar"; }
        else { clock.Start(); play.Content = "⏸ Pausar"; }
    }

    internal void Restart() { offset = 0; lastPosition = 0; if (clock.IsRunning) clock.Restart(); else clock.Reset(); Silence(); view.Show(0, melody.IsChecked == true, chords.IsChecked == true); }

    /// <summary>Moves the view to a position in quarters (tests and the frame loop).</summary>
    internal void Advance(double position)
    {
        lastPosition = position;
        bool showMelody = melody.IsChecked == true, showChords = chords.IsChecked == true;
        view.Show(position, showMelody, showChords);
        if (sound.IsChecked != true || !clock.IsRunning) return;
        output ??= SafeOutput();
        if (output is null) return;
        foreach (var note in view.Notes)
        {
            if (note.Chord ? !showChords : !showMelody) continue;
            bool on = position >= note.Start && position < note.Start + note.Length;
            if (on && sounding.Add(note)) output.Send((uint)(0x90 | (note.Chord ? 1 : 0)) | (uint)note.Pitch << 8 | (uint)(note.Chord ? 60 : 96) << 16);
            else if (!on && sounding.Remove(note)) output.Send((uint)(0x80 | (note.Chord ? 1 : 0)) | (uint)note.Pitch << 8);
        }
    }

    private IMidiOutputTransport? SafeOutput()
    {
        try { return outputFactory(); }
        catch (InvalidOperationException) { sound.IsChecked = false; return null; }
    }

    private void Silence()
    {
        if (output is not null) foreach (var note in sounding) output.Send((uint)(0x80 | (note.Chord ? 1 : 0)) | (uint)note.Pitch << 8);
        sounding.Clear();
    }

    private void Frame(object? sender, EventArgs e)
    {
        if (Driven) return;
        if (!clock.IsRunning) { if (view.Position != lastPosition) view.Show(lastPosition, melody.IsChecked == true, chords.IsChecked == true); return; }
        Advance(Position);
        if (view.Notes.Count > 0 && Position > view.Notes.Max(n => n.Start + n.Length) + 1) Toggle();
    }
}

/// <summary>The drawing: keyboard at the bottom, blocks above it falling at 90 pixels per quarter, keys lit while their note sounds.</summary>
public sealed class FallingNotesView : FrameworkElement
{
    private const double PixelsPerQuarter = 90, KeyboardHeight = 110;
    private static readonly Brush MelodyBrush = Frozen(Color.FromRgb(86, 160, 255)), ChordBrush = Frozen(Color.FromRgb(242, 189, 98)),
        LitMelody = Frozen(Color.FromRgb(150, 196, 255)), LitChord = Frozen(Color.FromRgb(250, 214, 150));
    private readonly (int Low, int High) range;
    private bool showMelody = true, showChords = true;
    public IReadOnlyList<FallingNote> Notes { get; }
    public double Position { get; private set; }
    public IReadOnlyList<int> LitKeys { get; private set; } = [];

    public FallingNotesView(IReadOnlyList<FallingNote> notes) { Notes = notes; range = FallingNotes.KeyRange(notes); }

    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    public void Show(double position, bool melody, bool chords)
    {
        Position = position; showMelody = melody; showChords = chords;
        LitKeys = Notes.Where(Visible).Where(n => position >= n.Start && position < n.Start + n.Length).Select(n => n.Pitch).Distinct().ToArray();
        InvalidateVisual();
    }

    private bool Visible(FallingNote note) => note.Chord ? showChords : showMelody;
    private static bool IsWhite(int pitch) => (pitch % 12) is 0 or 2 or 4 or 5 or 7 or 9 or 11;

    /// <summary>Left edge and width of a key in pixels.</summary>
    private (double X, double Width) Key(int pitch, double width)
    {
        int whites = Enumerable.Range(range.Low, range.High - range.Low + 1).Count(IsWhite);
        double white = width / whites;
        int before = Enumerable.Range(range.Low, Math.Max(0, pitch - range.Low)).Count(IsWhite);
        return IsWhite(pitch) ? (before * white, white) : (before * white - white * .3, white * .6);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight, keyboardTop = height - KeyboardHeight;
        if (width <= 0 || height <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var line = new Pen(Frozen(Color.FromArgb(40, 255, 255, 255)), 1);
        for (int pitch = range.Low; pitch <= range.High; pitch++)
            if (pitch % 12 == 0) { var (x, _) = Key(pitch, width); dc.DrawLine(line, new Point(x, 0), new Point(x, keyboardTop)); }
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Chords first, the melody on top (slightly inset with an outline) so a shared key still shows both.
        foreach (var note in Notes.OrderBy(n => n.Chord ? 0 : 1))
        {
            if (!Visible(note)) continue;
            double bottom = keyboardTop - (note.Start - Position) * PixelsPerQuarter, top = bottom - note.Length * PixelsPerQuarter;
            if (bottom < 0 || top > keyboardTop) continue;
            var (x, w) = Key(note.Pitch, width);
            double inset = note.Chord ? 1 : 4;
            var block = new Rect(x + inset, Math.Max(0, top) + 1, Math.Max(2, w - 2 * inset), Math.Max(2, Math.Min(bottom, keyboardTop) - Math.Max(0, top) - 2));
            dc.DrawRoundedRectangle(note.Chord ? ChordBrush : MelodyBrush, note.Chord ? null : new Pen(Brushes.White, 1.5), block, 4, 4);
            if (note.Chord && note.Label.Length > 0 && note.Pitch == Notes.Where(n => n.Chord && n.Start == note.Start).Min(n => n.Pitch) && bottom - top > 18)
                dc.DrawText(new FormattedText(note.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13, Brushes.Black, dip), new Point(x + 3, Math.Min(bottom, keyboardTop) - 18));
        }
        var lit = LitKeys.ToHashSet();
        // A key lights amber only when no melody note is on it (the melody wins).
        var melodyLit = Notes.Where(n => !n.Chord && Visible(n) && Position >= n.Start && Position < n.Start + n.Length).Select(n => n.Pitch).ToHashSet();
        var chordLit = lit.Where(p => !melodyLit.Contains(p)).ToHashSet();
        var border = new Pen(Brushes.Black, 1);
        foreach (int pitch in Enumerable.Range(range.Low, range.High - range.Low + 1).Where(IsWhite))
        {
            var (x, w) = Key(pitch, width);
            dc.DrawRectangle(lit.Contains(pitch) ? (chordLit.Contains(pitch) ? LitChord : LitMelody) : Brushes.White, border, new Rect(x, keyboardTop, w, KeyboardHeight));
            if (pitch % 12 == 0)
                dc.DrawText(new FormattedText(KeyboardDiagramPreview.CLabel + (pitch / 12 - 1), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.DimGray, dip), new Point(x + 3, height - 18));
        }
        foreach (int pitch in Enumerable.Range(range.Low, range.High - range.Low + 1).Where(p => !IsWhite(p)))
        {
            var (x, w) = Key(pitch, width);
            dc.DrawRectangle(lit.Contains(pitch) ? (chordLit.Contains(pitch) ? ChordBrush : MelodyBrush) : Brushes.Black, null, new Rect(x, keyboardTop, w, KeyboardHeight * .62));
        }
    }
}
