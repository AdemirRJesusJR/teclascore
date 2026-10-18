using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Teclas.App;

/// <summary>A note played during the recording, in seconds from the start (the sustain pedal extends the end).</summary>
public sealed record RecordedNote(double Start, double End, int Pitch, int Velocity);

/// <summary>
/// "Gravar ideia": records what is played on the MIDI keyboard, freely, without metronome. The tempo, the measures and
/// the note values are worked out afterwards from the player's own pace, so nothing has to be played to a click.
/// </summary>
public sealed class RecordIdeaWindow : Window
{
    private readonly ComboBox devices;
    private readonly Button record, convert;
    private readonly TextBlock status;
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<int, (double Start, int Velocity)> down = [];
    private readonly HashSet<int> held = [];
    private readonly List<RecordedNote> notes = [];
    private MidiKeyboardInput? input;
    private bool pedal;
    public IReadOnlyList<RecordedNote> Notes => notes;
    public bool Recording { get; private set; }

    public RecordIdeaWindow()
    {
        Title = "Gravar ideia"; Width = 620; Height = 330; MinWidth = 500; MinHeight = 300; ResizeMode = ResizeMode.CanResizeWithGrip; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Gravar ideia", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock
        {
            Text = "Toque no teclado MIDI do seu jeito, sem metrônomo: melodia e acordes, com pedal se quiser. Ao transformar, o TeclaScore descobre o andamento pela sua velocidade " +
                   "(acompanhando quando você acelera ou desacelera), o compasso e as figuras das notas, separa a melodia do acompanhamento e lê as cifras.",
            TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 6, 0, 12)
        });
        var line = new StackPanel { Orientation = Orientation.Horizontal }; top.Children.Add(line);
        line.Children.Add(new TextBlock { Text = "Teclado MIDI", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var names = MidiKeyboardInput.Devices();
        devices = new ComboBox { Width = 240, ItemsSource = names.Count > 0 ? names : ["Nenhum teclado MIDI encontrado"], SelectedIndex = 0, IsEnabled = names.Count > 0 };
        AutomationProperties.SetName(devices, "Teclado MIDI");
        devices.SelectionChanged += (_, _) => OpenDevice();
        line.Children.Add(devices);
        status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), FontSize = 15 };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        top.Children.Add(status);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        record = DialogUi.Button("● Gravar", (_, _) => Toggle()); record.Name = "RecordIdeaButton";
        convert = DialogUi.Button("Transformar em partitura", (_, _) => Finish(), true); convert.IsEnabled = false;
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true;
        footer.Children.Add(record); footer.Children.Add(DialogUi.Button("Recomeçar", (_, _) => Restart())); footer.Children.Add(convert); footer.Children.Add(close);
        root.Children.Add(new Border());
        timer.Tick += (_, _) => ShowStatus();
        Loaded += (_, _) => { OpenDevice(); ShowStatus(); };
        Closed += (_, _) => { timer.Stop(); input?.Dispose(); };
    }

    private void OpenDevice()
    {
        input?.Dispose(); input = null;
        if (!devices.IsEnabled) { status.Text = "Conecte um teclado MIDI e abra esta janela de novo."; record.IsEnabled = false; return; }
        try
        {
            input = new MidiKeyboardInput(devices.SelectedIndex);
            input.NoteOn += (pitch, velocity) => Dispatcher.InvokeAsync(() => Press(pitch, velocity, clock.Elapsed.TotalSeconds));
            input.NoteOff += pitch => Dispatcher.InvokeAsync(() => Release(pitch, clock.Elapsed.TotalSeconds));
            input.ControlChange += (controller, value) => { if (controller == 64) Dispatcher.InvokeAsync(() => Pedal(value >= 64, clock.Elapsed.TotalSeconds)); };
        }
        catch (InvalidOperationException error) { status.Text = error.Message; }
    }

    /// <summary>Starts or stops recording. The clock starts with the first key, so silence before playing is not kept.</summary>
    internal void Toggle()
    {
        Recording = !Recording;
        if (Recording) { timer.Start(); record.Content = "■ Parar"; }
        else { clock.Stop(); timer.Stop(); foreach (int pitch in down.Keys.ToArray()) Close(pitch, clock.Elapsed.TotalSeconds); record.Content = "● Continuar"; }
        ShowStatus();
    }

    internal void Press(int pitch, int velocity, double at)
    {
        if (!Recording) return;
        if (!clock.IsRunning) { clock.Start(); if (notes.Count == 0 && down.Count == 0) at = 0; }
        if (down.ContainsKey(pitch)) Close(pitch, at);
        down[pitch] = (at, velocity); held.Remove(pitch);
        ShowStatus();
    }

    internal void Release(int pitch, double at)
    {
        if (!down.ContainsKey(pitch)) return;
        if (pedal) held.Add(pitch); else Close(pitch, at);
    }

    internal void Pedal(bool pressed, double at)
    {
        pedal = pressed;
        if (pressed) return;
        foreach (int pitch in held.ToArray()) Close(pitch, at);
        held.Clear();
    }

    private void Close(int pitch, double at)
    {
        if (!down.Remove(pitch, out var start)) return;
        held.Remove(pitch);
        notes.Add(new RecordedNote(start.Start, Math.Max(start.Start + .05, at), pitch, start.Velocity));
    }

    private void Restart()
    {
        notes.Clear(); down.Clear(); held.Clear(); clock.Reset(); Recording = false; timer.Stop(); record.Content = "● Gravar"; ShowStatus();
    }

    private void ShowStatus()
    {
        int count = notes.Count + down.Count;
        convert.IsEnabled = !Recording && notes.Count >= 4;
        status.Text = Recording
            ? (count == 0 ? "Gravando: comece a tocar quando quiser." : $"Gravando… {clock.Elapsed:m\\:ss} · {count} notas")
            : count == 0 ? (input is null && !devices.IsEnabled ? status.Text : "Clique em Gravar e toque.") : $"Gravação parada: {clock.Elapsed:m\\:ss} · {notes.Count} notas. Transforme em partitura ou continue gravando.";
    }

    private void Finish()
    {
        if (Recording) Toggle();
        if (notes.Count < 4) return;
        DialogResult = true;
    }
}
