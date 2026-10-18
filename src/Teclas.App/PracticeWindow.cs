using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public enum PracticeMode { Notes, Chords, Both }

/// <summary>
/// Practice with a MIDI keyboard or the on-screen keys. Two kinds of steps (score notes, or chord symbols voiced like
/// the diagrams) and two ways to play them: wait mode, which advances only on the right keys, and in-time mode,
/// which runs the score at a chosen tempo with a click and scores each key by its timing.
/// </summary>
public sealed partial class PracticeWindow : Window
{
    private const int Low = 36, High = 96; // C2–C7
    private const double CountInBeats = 4;
    private readonly IReadOnlyList<PracticeStep> noteSteps, chordSteps;
    private PracticeSession session;
    private TimedPracticeSession? timed;
    private readonly Canvas keyboard;
    private readonly Dictionary<int, Rectangle> keys = [];
    private readonly TextBlock expected, progress, feedback;
    private readonly ComboBox devices, mode;
    private readonly CheckBox inTime, octave, withMusic, repeatUntilRight;
    private readonly ComboBox speedChoice;
    private readonly TextBox passageFrom, passageTo;
    private readonly Slider tempo;
    private readonly TextBlock tempoLabel;
    private readonly Button start, back, skip;
    private readonly DispatcherTimer clock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Stopwatch watch = new();
    private readonly Stopwatch studied = new();
    private MidiKeyboardInput? input;
    private IMidiOutputTransport? click;
    private int lastBeat = int.MinValue;
    private int? lastPressed; private bool lastCorrect;
    /// <summary>Opens the click output for in-time practice; null keeps it silent (smoke tests).</summary>
    internal Func<IMidiOutputTransport?> clickOutput = () => new WinMmMidiOutputTransport();
    public PracticeSession Session => session;
    public TimedPracticeSession? Timed => timed;
    public PracticeMode Mode => mode.SelectedIndex switch { 1 => PracticeMode.Chords, 2 => PracticeMode.Both, _ => PracticeMode.Notes };
    public bool InTime => inTime.IsChecked == true;
    public double Bpm => Math.Round(tempo.Value);
    public TimeSpan StudyTime => studied.Elapsed;
    /// <summary>Goals shown while practicing: minutes per day (with the minutes already studied today) and the song's target tempo.</summary>
    public int DailyGoalMinutes { get; init; }
    public double MinutesBeforeToday { get; init; }
    public int? TempoGoal { get; init; }
    internal bool DailyGoalCelebrated { get; private set; }
    private string GoalText
    {
        get
        {
            var parts = new List<string>();
            if (DailyGoalMinutes > 0)
            {
                double today = MinutesBeforeToday + studied.Elapsed.TotalMinutes;
                if (today >= DailyGoalMinutes && !DailyGoalCelebrated) { DailyGoalCelebrated = true; feedback.Text += " Meta do dia cumprida!"; }
                parts.Add($"hoje {Math.Floor(today):0} de {DailyGoalMinutes} min{(today >= DailyGoalMinutes ? " ✓" : "")}");
            }
            if (TempoGoal is { } goal) parts.Add($"meta ♩ = {goal}");
            return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
        }
    }
    /// <summary>Every attempt in this window, for the study history.</summary>
    public List<StudyRecord> Attempts { get; } = [];

    public PracticeWindow(string title, IReadOnlyList<PracticeStep> steps, bool anyOctave)
        : this(title, steps, [], anyOctave, 80) { }

    public PracticeWindow(string title, IReadOnlyList<PracticeStep> notes, IReadOnlyList<PracticeStep> chords, bool anyOctave, double bpm,
        string? previewPath = null, IPracticeAccompaniment? accompaniment = null, IReadOnlyDictionary<int, double>? measureStarts = null)
    {
        Title = "Praticar · " + title; Width = 1040; Height = previewPath is null ? 620 : 860; MinWidth = 800; MinHeight = 500; DialogUi.Theme(this);
        noteSteps = notes; chordSteps = chords;
        this.accompaniment = accompaniment; this.measureStarts = measureStarts ?? new Dictionary<int, double>();
        session = new PracticeSession(notes, anyOctave);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Toque o que está destacado. Na espera, a música só avança quando você acerta; no tempo, ela corre com metrônomo e cada tecla ganha pontos pela precisão.", TextWrapping = TextWrapping.Wrap });

        var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; top.Children.Add(options);
        options.Children.Add(Label("Praticar"));
        mode = new ComboBox { Width = 210, ItemsSource = new[] { "Notas da partitura", "Acordes da cifra (mão esquerda)", "Duas mãos (melodia e acordes)" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 16, 6) };
        AutomationProperties.SetName(mode, "O que praticar");
        if (chords.Count == 0) mode.ToolTip = "Esta partitura não tem cifras para praticar acordes.";
        mode.SelectionChanged += (_, _) => { if (Mode != PracticeMode.Notes && chordSteps.Count == 0) { mode.SelectedIndex = 0; feedback!.Text = "Esta partitura não tem cifras para praticar acordes."; return; } Reset(); };
        options.Children.Add(mode);
        inTime = new CheckBox { Content = "No tempo", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6),
            ToolTip = "Corre a partitura no andamento escolhido, com metrônomo e um compasso de contagem, e dá pontos por tecla: no tempo, adiantada ou atrasada." };
        inTime.Click += (_, _) => Reset();
        options.Children.Add(inTime);
        tempo = new Slider { Width = 140, Minimum = 30, Maximum = 200, Value = Math.Clamp(bpm, 30, 200), SmallChange = 1, LargeChange = 5, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6) };
        AutomationProperties.SetName(tempo, "Andamento da prática em batidas por minuto");
        tempoLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 64, Margin = new Thickness(0, 0, 16, 6) };
        tempo.ValueChanged += (_, _) => { tempoLabel.Text = $"♩ = {Bpm:0}"; if (timed is not null && !clock.IsEnabled) Reset(); };
        tempoLabel.Text = $"♩ = {Bpm:0}";
        options.Children.Add(tempo); options.Children.Add(tempoLabel);
        octave = new CheckBox { Content = "Qualquer oitava", IsChecked = anyOctave, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 6),
            ToolTip = "Conta como certa a mesma nota em outra oitava do teclado." };
        octave.Click += (_, _) => { session.AnyOctave = AnyOctave; if (timed is not null) timed.AnyOctave = AnyOctave; };
        options.Children.Add(octave);
        options.Children.Add(Label("Teclado MIDI"));
        var names = MidiKeyboardInput.Devices();
        devices = new ComboBox { Width = 220, ItemsSource = names.Count > 0 ? names : ["Nenhum teclado MIDI encontrado"], SelectedIndex = 0, IsEnabled = names.Count > 0, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(devices, "Teclado MIDI");
        devices.SelectionChanged += (_, _) => OpenDevice();
        options.Children.Add(devices);

        // Second row: study aids. Passage limits the measures; the music needs the score audio (MuseScore).
        var study = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) }; top.Children.Add(study);
        study.Children.Add(Label("Trecho: compassos"));
        passageFrom = new TextBox { Width = 46, Margin = new Thickness(0, 0, 6, 6), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Primeiro compasso do trecho (vazio: desde o início)" };
        passageTo = new TextBox { Width = 46, Margin = new Thickness(6, 0, 16, 6), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Último compasso do trecho (vazio: até o fim)" };
        AutomationProperties.SetName(passageFrom, "Primeiro compasso do trecho"); AutomationProperties.SetName(passageTo, "Último compasso do trecho");
        passageFrom.LostFocus += (_, _) => Reset(); passageTo.LostFocus += (_, _) => Reset();
        study.Children.Add(passageFrom); study.Children.Add(new TextBlock { Text = "a", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) }); study.Children.Add(passageTo);
        repeatUntilRight = new CheckBox { Content = "Repetir até acertar", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 6),
            ToolTip = "Ao terminar o trecho, começa de novo até uma passagem limpa: sem erros na espera, 90 pontos no tempo." };
        study.Children.Add(repeatUntilRight);
        withMusic = new CheckBox { Content = "Tocar junto com a música", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), IsEnabled = accompaniment is not null,
            ToolTip = accompaniment is null ? "Precisa do MuseScore para preparar o áudio da partitura." : "No tempo, a música da partitura toca e marca o tempo (em vez do metrônomo), com um compasso antes do trecho." };
        withMusic.Click += (_, _) => { if (withMusic.IsChecked == true) inTime.IsChecked = true; Reset(); };
        study.Children.Add(withMusic);
        speedChoice = new ComboBox { Width = 80, ItemsSource = MusicSpeeds, SelectedItem = 1.0, ItemStringFormat = "{0:P0}", Margin = new Thickness(0, 0, 0, 6), IsEnabled = accompaniment is not null };
        AutomationProperties.SetName(speedChoice, "Velocidade da música na prática");
        speedChoice.SelectionChanged += (_, _) => Reset();
        study.Children.Add(speedChoice);

        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, -4, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        start = DialogUi.Button("▶ Começar", (_, _) => ToggleClock(), true); start.Name = "PracticeStartButton";
        back = DialogUi.Button("◀ Voltar", (_, _) => { session.Advance(-1); Refresh(); });
        skip = DialogUi.Button("Pular ▶", (_, _) => { session.Advance(1); Refresh(); });
        footer.Children.Add(start); footer.Children.Add(back); footer.Children.Add(skip);
        footer.Children.Add(DialogUi.Button("Recomeçar", (_, _) => Reset()));
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; footer.Children.Add(close);
        BuildRecording(footer);
        BuildExtras(study, footer);

        var middle = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (previewPath is not null)
        {
            // The score above the keyboard follows the step being played.
            var split = new DockPanel(); DockPanel.SetDock(middle, Dock.Bottom); split.Children.Add(middle);
            viewer = new ScoreViewer { FirstPagePath = previewPath, Margin = new Thickness(0, 10, 0, 0) };
            split.Children.Add(viewer); root.Children.Add(split);
        }
        else root.Children.Add(middle);
        expected = new TextBlock { FontSize = 30, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        progress = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 10) };
        feedback = new TextBlock { FontSize = 13, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(expected, AutomationLiveSetting.Polite);
        keyboard = new Canvas { Height = 130, HorizontalAlignment = HorizontalAlignment.Left };
        middle.Children.Add(expected); middle.Children.Add(progress); middle.Children.Add(keyboard); middle.Children.Add(feedback);
        BuildKeyboard();
        clock.Tick += (_, _) => Tick();
        restart.Tick += (_, _) => RestartPassage();
        Loaded += (_, _) => { OpenDevice(); Reset(); studied.Start(); PrepareVisualFollow(); };
        Closed += (_, _) => { restart.Stop(); preparing?.Cancel(); StopMusic(); StopClock(); RecordAttempt(); studied.Stop(); input?.Dispose(); click?.Dispose(); };
    }

    private bool AnyOctave => octave.IsChecked == true;
    private IReadOnlyList<PracticeStep> CurrentSteps => Mode switch
    {
        PracticeMode.Chords => chordSteps,
        PracticeMode.Both => bothSteps ??= PracticeSession.Combine(noteSteps, chordSteps),
        _ => noteSteps
    };
    private IReadOnlyList<PracticeStep>? bothSteps;
    /// <summary>Clock position in quarters: from the music when it plays, else the metronome (negative during the count-in).</summary>
    internal double Position => musicRunning && timeline is not null && accompaniment is not null ? timeline.ToQuarters(accompaniment.PositionSeconds)
        : watch.Elapsed.TotalSeconds * Bpm / 60 - CountInBeats + firstBeat;
    private double firstBeat;

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };

    private void OpenDevice()
    {
        input?.Dispose(); input = null;
        if (!devices.IsEnabled || devices.SelectedIndex < 0) { feedback.Text = "Sem teclado MIDI: clique nas teclas da tela para praticar."; return; }
        try
        {
            input = new MidiKeyboardInput(devices.SelectedIndex);
            input.NoteOn += (pitch, velocity) => Dispatcher.InvokeAsync(() => { Record(pitch, velocity); Play(pitch); });
            input.NoteOff += pitch => Dispatcher.InvokeAsync(() => Record(pitch, 0));
            feedback.Text = "Teclado conectado.";
        }
        catch (InvalidOperationException error) { feedback.Text = error.Message + " Você ainda pode clicar nas teclas da tela."; }
    }

    /// <summary>Starts a new attempt with the chosen steps and mode; the previous one goes to the history.</summary>
    private void Reset() => Reset(keepAttempt: false);

    private void Reset(bool keepAttempt)
    {
        restart.Stop(); StopMusic(); StopClock(); RecordAttempt(); recorded = false;
        if (!keepAttempt) passageAttempt = 1;
        var steps = PassageSteps;
        session = new PracticeSession(steps, AnyOctave);
        timed = InTime ? new TimedPracticeSession(steps, Bpm, AnyOctave) : null;
        attemptStarted = DateTime.Now; attemptMode = Mode; lastPressed = null;
        start.Visibility = InTime ? Visibility.Visible : Visibility.Collapsed;
        back.Visibility = skip.Visibility = InTime ? Visibility.Collapsed : Visibility.Visible;
        tempo.IsEnabled = !WithMusic; start.Content = "▶ Começar";
        if (InTime) feedback.Text = WithMusic ? "Clique em Começar: a música toca um compasso antes do trecho." : "Clique em Começar: há um compasso de contagem antes da primeira nota.";
        if (steps.Count == 0 && CurrentSteps.Count > 0) feedback.Text = "Nenhuma nota nesse trecho. Confira os compassos.";
        Refresh();
    }
    private DateTime attemptStarted = DateTime.Now;
    private PracticeMode attemptMode;

    private void ToggleClock()
    {
        if (timed is null) return;
        if (musicRunning) { StopMusic(); StopClock(); feedback.Text = "Parado. Começar toca de novo a partir de um compasso antes do trecho."; Refresh(); return; }
        if (WithMusic) { if (timed.IsFinished || timed.Hits + timed.Missed + timed.Wrong > 0) Reset(keepAttempt: true); _ = StartWithMusic(); return; }
        if (clock.IsEnabled) { StopClock(); feedback.Text = "Pausado. Recomeçar volta ao início."; Refresh(); return; }
        if (timed.IsFinished) Reset();
        if (timed is null) return;
        try { click ??= clickOutput(); } catch (InvalidOperationException) { click = null; }
        // Start one bar before the first step (rounded down to a beat), so long intros are skipped.
        firstBeat = timed.Steps.Count > 0 ? Math.Max(0, Math.Floor(timed.Steps[0].Start)) : 0;
        if (timed.Position > 0) firstBeat = Math.Floor(timed.Position);
        watch.Restart(); lastBeat = int.MinValue; clock.Start();
        tempo.IsEnabled = false; start.Content = "⏸ Pausar";
    }

    private void StopClock()
    {
        if (!clock.IsEnabled) return;
        clock.Stop(); watch.Stop();
        start.Content = "▶ Continuar"; tempo.IsEnabled = timed is null || timed.Hits + timed.Missed + timed.Wrong == 0;
    }

    /// <summary>Advances the in-time clock: clicks on each beat, closes missed windows, ends after the last step.</summary>
    internal void Tick(double? at = null)
    {
        if (timed is null) return;
        double position = at ?? Position;
        int beat = (int)Math.Floor(position);
        if (beat != lastBeat && at is null && !musicRunning)
        {
            lastBeat = beat;
            bool downbeat = ((beat - (int)firstBeat) % 4 + 4) % 4 == 0;
            try { click?.Send(0x99 | (uint)((downbeat ? 76 : 77) << 8) | (uint)((downbeat ? 110 : 80) << 16)); } catch (InvalidOperationException) { click = null; }
        }
        timed.Advance(position);
        if (timed.IsFinished)
        {
            StopMusic(); StopClock(); start.Content = "▶ Começar";
            feedback.Text = $"Fim! {timed.Score:0} pontos · {timed.OnTime} no tempo · {timed.OffTime} fora do tempo · {timed.Missed} perdidas · {timed.Wrong} erradas.";
            if (TempoGoal is { } goal && timed.Bpm >= goal && timed.Score >= 80) feedback.Text += $" Meta de andamento ♩ = {goal} atingida!";
            RecordAttempt(); PassageFinished();
        }
        Refresh(position);
    }

    /// <summary>Handles one key, from the MIDI keyboard or the screen.</summary>
    internal PracticeResult Play(int pitch) => Play(pitch, null);

    internal PracticeResult Play(int pitch, double? at)
    {
        if (timed is not null)
        {
            if (!clock.IsEnabled && at is null) { feedback.Text = "Clique em Começar para tocar no tempo."; return PracticeResult.Ignored; }
            var timing = timed.Press(pitch, at ?? Position);
            if (timing == TimedPracticeResult.Ignored) return PracticeResult.Ignored;
            lastPressed = pitch; lastCorrect = timing != TimedPracticeResult.Wrong;
            feedback.Text = timing switch
            {
                TimedPracticeResult.OnTime => "No tempo!",
                TimedPracticeResult.Early => "Certa, mas adiantada.",
                TimedPracticeResult.Late => "Certa, mas atrasada.",
                _ => $"{PracticeSession.NoteName(pitch)} não está neste ponto."
            };
            Refresh(at);
            return timing == TimedPracticeResult.Wrong ? PracticeResult.Wrong : PracticeResult.Correct;
        }
        var result = session.Press(pitch);
        if (result == PracticeResult.Ignored) return result;
        lastPressed = pitch; lastCorrect = result != PracticeResult.Wrong;
        feedback.Text = result switch
        {
            PracticeResult.Wrong => $"{PracticeSession.NoteName(pitch)} não está neste ponto. Tente de novo.",
            PracticeResult.Finished => $"Fim da música! {session.Hits} acertos, {session.Misses} erros ({session.Accuracy:P0} de precisão).",
            PracticeResult.StepComplete => "Certo!",
            _ => "Certo — falta completar o acorde."
        };
        if (result == PracticeResult.Finished) { RecordAttempt(); PassageFinished(); }
        Refresh();
        return result;
    }

    /// <summary>Adds the current attempt to <see cref="Attempts"/> once, if any key was played.</summary>
    private void RecordAttempt()
    {
        int hits = timed?.Hits ?? session.Hits, misses = timed is { } t ? t.Missed + t.Wrong : session.Misses;
        if (hits + misses == 0 || recorded) return;
        int total = timed?.Steps.Count ?? session.Steps.Count;
        int done = timed is { } run ? run.Steps.Count(step => step.Start + run.Tolerance < run.Position) : session.Index;
        Attempts.Add(new StudyRecord
        {
            Date = attemptStarted, Seconds = Math.Max(1, (int)(DateTime.Now - attemptStarted).TotalSeconds),
            Mode = attemptMode switch { PracticeMode.Chords => "acordes", PracticeMode.Both => "duas mãos", _ => "notas" } + (timed is null ? "" : " no tempo"),
            Bpm = timed is null ? null : (int)timed.Bpm, Hits = hits, Misses = misses, Steps = total, StepsDone = Math.Min(done, total),
            Score = timed?.Score ?? Math.Round(100.0 * session.Accuracy, 1),
            Completed = timed?.IsFinished ?? session.IsFinished
        });
        recorded = true;
    }
    private bool recorded;

    private void BuildKeyboard()
    {
        const double white = 18, height = 120;
        int whiteIndex = 0;
        var blacks = new List<(int Pitch, double X)>();
        for (int pitch = Low; pitch <= High; pitch++)
        {
            bool isBlack = (pitch % 12) is 1 or 3 or 6 or 8 or 10;
            if (isBlack) { blacks.Add((pitch, whiteIndex * white - white * .3)); continue; }
            var key = new Rectangle { Width = white, Height = height, Stroke = Brushes.Black, StrokeThickness = 1, Fill = Brushes.White, Tag = pitch, Cursor = System.Windows.Input.Cursors.Hand };
            Canvas.SetLeft(key, whiteIndex * white); keyboard.Children.Add(key); keys[pitch] = key; whiteIndex++;
            if (pitch % 12 == 0) { var label = new TextBlock { Text = "Dó" + (pitch / 12 - 1), FontSize = 9, Foreground = Brushes.Gray }; Canvas.SetLeft(label, (whiteIndex - 1) * white + 2); Canvas.SetTop(label, height + 2); keyboard.Children.Add(label); }
        }
        foreach (var (pitch, x) in blacks)
        {
            var key = new Rectangle { Width = white * .6, Height = height * .62, Fill = Brushes.Black, Tag = pitch, Cursor = System.Windows.Input.Cursors.Hand };
            Canvas.SetLeft(key, x); keyboard.Children.Add(key); keys[pitch] = key;
        }
        keyboard.Width = whiteIndex * white;
        foreach (var key in keys.Values)
        {
            key.MouseLeftButtonDown += (sender, _) => { int pitch = (int)((Rectangle)sender).Tag; Record(pitch, 90); Play(pitch); };
            key.MouseLeftButtonUp += (sender, _) => Record((int)((Rectangle)sender).Tag, 0);
        }
    }

    private static string Describe(PracticeStep step) =>
        step.Label.Length > 0 ? step.Label + "   (" + string.Join(" + ", step.Pitches.Select(PracticeSession.NoteName)) + ")"
            : string.Join("  +  ", step.Pitches.Select(PracticeSession.NoteName));

    private void Refresh(double? at = null)
    {
        var accent = TryFindResource("Accent") as Brush ?? Brushes.DodgerBlue;
        foreach (var (pitch, key) in keys)
            key.Fill = (pitch % 12) is 1 or 3 or 6 or 8 or 10 ? Brushes.Black : Brushes.White;
        IReadOnlyList<int> highlight = timed is null ? session.Remaining : timed.Waiting.Count > 0 ? timed.Waiting : timed.Current?.Pitches ?? [];
        foreach (int pitch in highlight)
        {
            int shown = pitch;
            while (shown < Low) shown += 12;
            while (shown > High) shown -= 12;
            if (keys.TryGetValue(shown, out var key)) key.Fill = accent;
        }
        if (lastPressed is { } last && keys.TryGetValue(last, out var pressed) && !highlight.Contains(last))
            pressed.Fill = lastCorrect ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        if (timed is { } run)
        {
            double position = at ?? (clock.IsEnabled ? Position : run.Position);
            if (clock.IsEnabled && position < firstBeat) expected.Text = $"Contagem… {(int)Math.Ceiling(firstBeat - position)}";
            else expected.Text = run.Current is { } next ? Describe(next) : run.Steps.Count == 0 ? "Nada para praticar nesta escolha." : "Fim da música!";
            progress.Text = $"{(run.Current is { } step ? $"Compasso {step.Measure} · " : "")}♩ = {run.Bpm:0}{(musicRunning ? $" · música em {MusicSpeed:P0}" : "")} · {run.Score:0} pontos · {run.OnTime} no tempo · {run.OffTime} fora · {run.Missed} perdidas · {run.Wrong} erradas{AttemptText}{GoalText}";
            FollowScore(position);
            return;
        }
        if (session.Current is { } current)
        {
            expected.Text = Describe(current);
            progress.Text = $"Compasso {current.Measure} · passo {session.Index + 1} de {session.Steps.Count} · {session.Hits} acertos · {session.Misses} erros{AttemptText}{GoalText}";
            FollowScore(current.Start);
        }
        else
        {
            expected.Text = session.Steps.Count == 0 ? "Esta partitura não tem notas para praticar." : "Música concluída!";
            progress.Text = $"{session.Steps.Count} passos · {session.Hits} acertos · {session.Misses} erros · {session.Accuracy:P0} de precisão";
        }
    }

    private string AttemptText => passageAttempt > 1 ? $" · tentativa {passageAttempt}" : "";

    /// <summary>For smoke tests: chooses mode and timing as the user would.</summary>
    internal void Choose(PracticeMode practiceMode, bool timedMode, double bpm)
    {
        tempo.Value = bpm; inTime.IsChecked = timedMode; mode.SelectedIndex = (int)practiceMode;
        Reset();
    }
}
