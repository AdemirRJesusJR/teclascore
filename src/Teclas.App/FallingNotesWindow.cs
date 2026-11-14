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

/// <summary>
/// The drawing: keyboard at the bottom, blocks above it falling onto it, keys lit while their note sounds. In the Notas
/// caindo window the blocks fall at 90 pixels per quarter over a 110 px keyboard; as the piano roll under the score
/// (<see cref="VisibleSpan"/> set) the panel shows that many seconds above a keyboard sized to the panel, with the bar
/// lines, the end of the score and the chord being played.
/// </summary>
public sealed class FallingNotesView : FrameworkElement
{
    private const double PixelsPerQuarter = 90, WindowKeyboardHeight = 110;
    private static readonly Brush MelodyBrush = Frozen(Color.FromRgb(86, 160, 255)), ChordBrush = Frozen(Color.FromRgb(242, 189, 98)),
        LitMelody = Frozen(Color.FromRgb(150, 196, 255)), LitChord = Frozen(Color.FromRgb(250, 214, 150)),
        BarBrush = Frozen(Color.FromArgb(34, 255, 255, 255)), OctaveBrush = Frozen(Color.FromArgb(40, 255, 255, 255)),
        HintBrush = Frozen(Color.FromArgb(210, 220, 226, 236)), Outline = Frozen(Color.FromArgb(200, 255, 255, 255)), PillBrush = Frozen(Color.FromArgb(150, 0, 0, 0)), Edge = Frozen(Color.FromArgb(90, 255, 255, 255)),
        RollBar = Frozen(Color.FromArgb(18, 255, 255, 255)), RollOctave = Frozen(Color.FromArgb(34, 255, 255, 255)), EndPill = Frozen(Color.FromRgb(38, 38, 40)),
        EndEdge = Frozen(Color.FromRgb(70, 70, 74)), EndText = Frozen(Color.FromRgb(214, 214, 218)), KeyEdge = Frozen(Color.FromRgb(150, 150, 154)), KeyLabel = Frozen(Color.FromRgb(130, 130, 136));
    // Piano roll keys: lightly shaded ivory and glossy black, like the transcription apps.
    private static readonly Brush WhiteKey = Gradient(Color.FromRgb(252, 252, 252), Color.FromRgb(222, 222, 224)),
        BlackKey = Gradient(Color.FromRgb(58, 58, 62), Color.FromRgb(6, 6, 8));
    private (int Low, int High) range;
    private FallingNote[] ordered = [];
    private HashSet<FallingNote> labelled = [];
    private double longest;
    private bool showMelody = true, showChords = true;
    private string hint = "";
    private int minimumOctaves = 2;
    private bool fullKeyboard;
    public IReadOnlyList<FallingNote> Notes { get; private set; } = [];
    public double Position { get; private set; }
    public IReadOnlyList<int> LitKeys { get; private set; } = [];
    /// <summary>Piano roll: the stretch of music (in the notes' unit) shown above the keyboard; null keeps the window's fixed scale.</summary>
    public double? VisibleSpan { get; init; }
    /// <summary>Piano roll: the slowest fall (pixels per unit), so short notes stay visible in a low panel, which then shows less ahead.</summary>
    public double MinimumScale { get; init; }
    /// <summary>Fewest octaves on the keyboard (the piano roll draws a wider one, like a real keyboard).</summary>
    public int MinimumOctaves { get => minimumOctaves; init { minimumOctaves = value; range = RangeFor(Notes); } }
    /// <summary>Piano roll: the whole 88-key keyboard (A0–C8), as Songscription draws it, whatever the notes.</summary>
    public bool FullKeyboard { get => fullKeyboard; init { fullKeyboard = value; range = RangeFor(Notes); } }
    /// <summary>Piano roll: writes the chord being played at the left, over the keyboard.</summary>
    public bool ShowChordName { get; init; }
    public IReadOnlyList<double> BarLines { get; private set; } = [];
    public double? EndMarker { get; private set; }
    public string ChordName { get; private set; } = "";
    /// <summary>A short message in the middle (nothing to show yet).</summary>
    public string Hint { get => hint; set { hint = value ?? ""; InvalidateVisual(); } }

    public FallingNotesView(IReadOnlyList<FallingNote> notes) => Load(notes);

    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Brush Gradient(Color top, Color bottom) { var brush = new LinearGradientBrush(top, bottom, 90); brush.Freeze(); return brush; }
    private (int Low, int High) RangeFor(IReadOnlyList<FallingNote> notes) => fullKeyboard ? (21, 108) : FallingNotes.KeyRange(notes, minimumOctaves);
    /// <summary>First and last keys drawn.</summary>
    public (int Low, int High) KeyboardRange => range;

    /// <summary>New blocks (another score or another playback map), with the measure lines and the end of the music.</summary>
    public void Load(IReadOnlyList<FallingNote> notes, IReadOnlyList<double>? barLines = null, double? end = null)
    {
        Notes = notes; BarLines = barLines ?? []; EndMarker = end;
        ordered = notes.OrderBy(n => n.Start).ToArray();
        longest = ordered.Length == 0 ? 0 : ordered.Max(n => n.Length);
        // The chord name goes on the lowest block of each chord.
        labelled = notes.Where(n => n.Chord && n.Label.Length > 0).GroupBy(n => (n.Start, n.Label)).Select(g => g.MinBy(n => n.Pitch)!).ToHashSet();
        range = RangeFor(notes);
        Show(Position, showMelody, showChords);
    }

    public void Show(double position, bool melody, bool chords)
    {
        Position = position; showMelody = melody; showChords = chords;
        LitKeys = Sounding(position).Select(n => n.Pitch).Distinct().ToArray();
        ChordName = PianoRollTimeline.ChordAt(ordered, position);
        InvalidateVisual();
    }

    /// <summary>Visible notes sounding at a position (blocks are sorted by start, so the search stops early).</summary>
    private IEnumerable<FallingNote> Sounding(double position)
    {
        foreach (var note in ordered)
        {
            if (note.Start > position) yield break;
            if (Visible(note) && position < note.Start + note.Length) yield return note;
        }
    }

    private bool Visible(FallingNote note) => note.Chord ? showChords : showMelody;
    private static bool IsWhite(int pitch) => (pitch % 12) is 0 or 2 or 4 or 5 or 7 or 9 or 11;

    /// <summary>Left edge and width of every key: white keys side by side, black keys between them.</summary>
    private Dictionary<int, (double X, double Width)> Keys(double width)
    {
        int whites = Enumerable.Range(range.Low, range.High - range.Low + 1).Count(IsWhite);
        double white = width / Math.Max(1, whites), x = 0;
        var keys = new Dictionary<int, (double, double)>();
        for (int pitch = range.Low; pitch <= range.High; pitch++)
        {
            if (IsWhite(pitch)) { keys[pitch] = (x, white); x += white; }
            else keys[pitch] = (x - white * .3, white * .6);
        }
        return keys;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        bool roll = VisibleSpan is not null;
        double keyboardHeight = roll ? Math.Clamp(Math.Round(height * .22), 30, 90) : WindowKeyboardHeight, keyboardTop = height - keyboardHeight;
        double scale = VisibleSpan is { } span && span > 0 ? Math.Max(MinimumScale, Math.Max(1, keyboardTop) / span) : PixelsPerQuarter, ahead = Math.Max(0, keyboardTop) / scale;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var keys = Keys(width);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var octave = new Pen(roll ? RollOctave : OctaveBrush, 1);
        for (int pitch = range.Low; pitch <= range.High; pitch++)
            if (pitch % 12 == 0) dc.DrawLine(octave, new Point(keys[pitch].X, 0), new Point(keys[pitch].X, keyboardTop));
        var bar = new Pen(roll ? RollBar : BarBrush, 1);
        foreach (double line in BarLines)
            if (line >= Position && line <= Position + ahead) { double y = keyboardTop - (line - Position) * scale; dc.DrawLine(bar, new Point(0, y), new Point(width, y)); }
        // Chords first, the melody on top (slightly inset with an outline) so a shared key still shows both.
        foreach (var note in ordered.Where(n => Visible(n) && n.Start <= Position + ahead && n.Start >= Position - longest && n.Start + n.Length >= Position).OrderBy(n => n.Chord ? 0 : 1))
        {
            if (!keys.TryGetValue(note.Pitch, out var key)) continue;
            double bottom = keyboardTop - (note.Start - Position) * scale, top = bottom - note.Length * scale;
            if (bottom < 0 || top > keyboardTop) continue;
            double inset = note.Chord ? Math.Min(1, key.Width / 8) : Math.Min(4, key.Width / 6);
            var block = new Rect(key.X + inset, Math.Max(0, top) + 1, Math.Max(2, key.Width - 2 * inset), Math.Max(2, Math.Min(bottom, keyboardTop) - Math.Max(0, top) - 2));
            // The outline only on blocks tall and wide enough to show it: on a short note it would hide the color.
            double radius = Math.Min(roll ? 3 : 4, Math.Min(block.Height, block.Width) / 2);
            dc.DrawRoundedRectangle(note.Chord ? ChordBrush : MelodyBrush, note.Chord || block.Height < 7 || block.Width < 8 ? null : new Pen(Outline, 1), block, radius, radius);
            if (labelled.Contains(note) && block.Height > 18)
            {
                var label = new FormattedText(note.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), roll ? 10 : 13, Brushes.Black, dip);
                // On the narrow keys of the full keyboard the name goes beside the block when it does not fit inside.
                if (label.Width + 4 <= block.Width) dc.DrawText(label, new Point(block.X + 2, block.Bottom - label.Height - 2));
                else if (roll) { label.SetForegroundBrush(ChordBrush); dc.DrawText(label, new Point(block.Right + 2, block.Bottom - label.Height - 1)); }
            }
        }
        if (EndMarker is { } end && end >= Position && end <= Position + ahead)
        {
            double y = keyboardTop - (end - Position) * scale;
            if (roll)
            {
                // A small pill with a lock resting on the end of the music, as Songscription marks it.
                var lockGlyph = new FormattedText("", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 10, EndText, dip);
                var label = new FormattedText("Fim da partitura", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, EndText, dip);
                double pillWidth = lockGlyph.Width + label.Width + 26, pillHeight = Math.Max(label.Height, lockGlyph.Height) + 6;
                var pill = new Rect(width / 2 - pillWidth / 2, Math.Max(2, y - pillHeight - 4), pillWidth, pillHeight);
                dc.DrawRoundedRectangle(EndPill, new Pen(EndEdge, 1), pill, pillHeight / 2, pillHeight / 2);
                dc.DrawText(lockGlyph, new Point(pill.X + 10, pill.Y + (pillHeight - lockGlyph.Height) / 2));
                dc.DrawText(label, new Point(pill.X + 16 + lockGlyph.Width, pill.Y + (pillHeight - label.Height) / 2));
            }
            else
            {
                dc.DrawLine(new Pen(Edge, 1) { DashStyle = DashStyles.Dash }, new Point(0, y), new Point(width, y));
                var label = new FormattedText("Fim da partitura", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, HintBrush, dip);
                var pill = new Rect(width / 2 - label.Width / 2 - 10, Math.Max(2, y - label.Height - 8), label.Width + 20, label.Height + 6);
                dc.DrawRoundedRectangle(PillBrush, new Pen(Edge, 1), pill, pill.Height / 2, pill.Height / 2);
                dc.DrawText(label, new Point(pill.X + 10, pill.Y + 3));
            }
        }
        var lit = LitKeys.ToHashSet();
        // A key lights amber only when no melody note is on it (the melody wins).
        var melodyLit = Sounding(Position).Where(n => !n.Chord).Select(n => n.Pitch).ToHashSet();
        var border = roll ? new Pen(KeyEdge, .6) : new Pen(Brushes.Black, 1);
        double labelSize = roll ? Math.Clamp(keyboardHeight / 8, 8, 10) : 11;
        foreach (int pitch in Enumerable.Range(range.Low, range.High - range.Low + 1).Where(IsWhite))
        {
            var (x, w) = keys[pitch];
            var face = lit.Contains(pitch) ? (melodyLit.Contains(pitch) ? LitMelody : LitChord) : roll ? WhiteKey : Brushes.White;
            dc.DrawRectangle(face, border, new Rect(x, keyboardTop, w, keyboardHeight));
            // The roll names only middle C (its keys are narrow); the window names every C.
            if (pitch % 12 == 0 && (roll ? pitch == 60 : w > labelSize * 1.6))
                dc.DrawText(new FormattedText(KeyboardDiagramPreview.CLabel + (pitch / 12 - 1), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), labelSize, roll ? KeyLabel : Brushes.DimGray, dip),
                    new Point(x + (roll ? 1 : 3), height - labelSize - (roll ? 5 : 7)));
        }
        foreach (int pitch in Enumerable.Range(range.Low, range.High - range.Low + 1).Where(p => !IsWhite(p)))
        {
            var (x, w) = keys[pitch];
            var face = lit.Contains(pitch) ? (melodyLit.Contains(pitch) ? MelodyBrush : ChordBrush) : roll ? BlackKey : Brushes.Black;
            var rect = new Rect(x, keyboardTop, w, keyboardHeight * (roll ? .6 : .62));
            if (roll) dc.DrawRoundedRectangle(face, null, rect, Math.Min(2, w / 4), Math.Min(2, w / 4)); else dc.DrawRectangle(face, null, rect);
        }
        if (ShowChordName && ChordName.Length > 0 && keyboardTop > 24)
        {
            // The chord being played: small, at the left, just above the keys.
            var name = new FormattedText(ChordName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 14, Brushes.White, dip);
            dc.DrawText(name, new Point(10, keyboardTop - name.Height - 4));
        }
        if (hint.Length > 0)
        {
            var text = new FormattedText(hint, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, HintBrush, dip) { MaxTextWidth = Math.Max(40, width - 40), TextAlignment = TextAlignment.Center };
            dc.DrawText(text, new Point(20, Math.Max(4, keyboardTop / 2 - text.Height / 2)));
        }
    }
}
