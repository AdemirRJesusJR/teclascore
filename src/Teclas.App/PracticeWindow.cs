using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Wait-mode practice with a MIDI keyboard or the on-screen keys.</summary>
public sealed class PracticeWindow : Window
{
    private const int Low = 36, High = 96; // C2–C7
    private readonly PracticeSession session;
    private readonly Canvas keyboard;
    private readonly Dictionary<int, Rectangle> keys = [];
    private readonly TextBlock expected, progress, feedback;
    private readonly ComboBox devices;
    private MidiKeyboardInput? input;
    private int? lastPressed; private bool lastCorrect;
    public PracticeSession Session => session;

    public PracticeWindow(string title, IReadOnlyList<PracticeStep> steps, bool anyOctave)
    {
        Title = "Praticar · " + title; Width = 980; Height = 520; MinWidth = 760; MinHeight = 440; DialogUi.Theme(this);
        session = new PracticeSession(steps, anyOctave);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Toque a nota ou o acorde destacado. A música só avança quando você acerta; o andamento é livre.", TextWrapping = TextWrapping.Wrap });
        var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; top.Children.Add(options);
        options.Children.Add(new TextBlock { Text = "Teclado MIDI", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var names = MidiKeyboardInput.Devices();
        devices = new ComboBox { Width = 260, ItemsSource = names.Count > 0 ? names : ["Nenhum teclado MIDI encontrado"], SelectedIndex = 0, IsEnabled = names.Count > 0 };
        AutomationProperties.SetName(devices, "Teclado MIDI");
        devices.SelectionChanged += (_, _) => OpenDevice();
        options.Children.Add(devices);
        var octave = new CheckBox { Content = "Aceitar qualquer oitava", IsChecked = anyOctave, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Conta como certa a mesma nota em outra oitava do teclado." };
        octave.Click += (_, _) => session.AnyOctave = octave.IsChecked == true;
        options.Children.Add(octave);

        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, -4, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(DialogUi.Button("◀ Voltar", (_, _) => { session.Advance(-1); Refresh(); }));
        footer.Children.Add(DialogUi.Button("Pular ▶", (_, _) => { session.Advance(1); Refresh(); }));
        footer.Children.Add(DialogUi.Button("Recomeçar", (_, _) => { session.Restart(); lastPressed = null; Refresh(); }));
        var close = DialogUi.Button("Fechar", (_, _) => Close(), true); close.IsCancel = true; footer.Children.Add(close);

        var middle = new StackPanel { Margin = new Thickness(0, 18, 0, 0) }; root.Children.Add(middle);
        expected = new TextBlock { FontSize = 30, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        progress = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 10) };
        feedback = new TextBlock { FontSize = 13, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(expected, AutomationLiveSetting.Polite);
        keyboard = new Canvas { Height = 130, HorizontalAlignment = HorizontalAlignment.Left };
        middle.Children.Add(expected); middle.Children.Add(progress); middle.Children.Add(keyboard); middle.Children.Add(feedback);
        BuildKeyboard();
        Loaded += (_, _) => { OpenDevice(); Refresh(); };
        Closed += (_, _) => input?.Dispose();
    }

    private void OpenDevice()
    {
        input?.Dispose(); input = null;
        if (!devices.IsEnabled || devices.SelectedIndex < 0) { feedback.Text = "Sem teclado MIDI: clique nas teclas da tela para praticar."; return; }
        try
        {
            input = new MidiKeyboardInput(devices.SelectedIndex);
            input.NoteOn += (pitch, _) => Dispatcher.InvokeAsync(() => Play(pitch));
            feedback.Text = "Teclado conectado. Toque a nota destacada.";
        }
        catch (InvalidOperationException error) { feedback.Text = error.Message + " Você ainda pode clicar nas teclas da tela."; }
    }

    /// <summary>Handles one key, from the MIDI keyboard or the screen.</summary>
    internal PracticeResult Play(int pitch)
    {
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
        Refresh();
        return result;
    }

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
        foreach (var key in keys.Values) key.MouseLeftButtonDown += (sender, _) => Play((int)((Rectangle)sender).Tag);
    }

    private void Refresh()
    {
        var accent = (Brush)FindResource("Accent");
        foreach (var (pitch, key) in keys)
            key.Fill = (pitch % 12) is 1 or 3 or 6 or 8 or 10 ? Brushes.Black : Brushes.White;
        foreach (int pitch in session.Remaining)
        {
            int shown = pitch;
            while (shown < Low) shown += 12;
            while (shown > High) shown -= 12;
            if (keys.TryGetValue(shown, out var key)) key.Fill = accent;
        }
        if (lastPressed is { } last && keys.TryGetValue(last, out var pressed) && !session.Remaining.Contains(last))
            pressed.Fill = lastCorrect ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        if (session.Current is { } step)
        {
            expected.Text = string.Join("  +  ", step.Pitches.Select(PracticeSession.NoteName));
            progress.Text = $"Compasso {step.Measure} · passo {session.Index + 1} de {session.Steps.Count} · {session.Hits} acertos · {session.Misses} erros";
        }
        else
        {
            expected.Text = session.Steps.Count == 0 ? "Esta partitura não tem notas para praticar." : "Música concluída!";
            progress.Text = $"{session.Steps.Count} passos · {session.Hits} acertos · {session.Misses} erros · {session.Accuracy:P0} de precisão";
        }
    }
}
