using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Shared base: opens the microphone with a time limit when shown and closes it with the window.</summary>
public abstract class MicrophoneWindow : Window
{
    internal Func<IDisposableNoteSource> microphoneFactory = () => new MicrophoneNoteInput();
    internal TimeSpan microphoneTimeout = TimeSpan.FromSeconds(5);
    protected IDisposableNoteSource? Microphone { get; private set; }
    protected readonly TextBlock Status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    public string StatusText => Status.Text;
    public bool Listening => Microphone is not null;

    protected MicrophoneWindow()
    {
        AutomationProperties.SetLiveSetting(Status, AutomationLiveSetting.Polite);
        Loaded += async (_, _) => await OpenAsync();
        Closed += (_, _) => { Microphone?.Dispose(); Microphone = null; };
    }

    internal async Task OpenAsync()
    {
        Status.Text = "Abrindo o microfone…";
        try
        {
            var source = await MicrophoneOpener.OpenAsync(microphoneFactory, microphoneTimeout);
            if (!IsLoaded) { source.Dispose(); return; }
            Microphone = source;
            source.NoteOn += pitch => Dispatcher.InvokeAsync(() => OnNote(pitch));
            source.PitchHeard += pitch => Dispatcher.InvokeAsync(() => OnPitch(pitch));
            Opened();
        }
        catch (InvalidOperationException error) { Status.Text = error.Message; Failed(); }
    }

    protected virtual void Opened() { }
    protected virtual void Failed() { }
    protected virtual void OnNote(int pitch) { }
    protected virtual void OnPitch(double pitch) { }

    protected static Button PrivacyButton()
    {
        var button = DialogUi.Button("Privacidade do microfone", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { }
        });
        button.ToolTip = "Abre a página do Windows que libera o microfone para os aplicativos da área de trabalho";
        return button;
    }
}

/// <summary>Microphone test (Settings): the sound level and the note recognized, live, with help when Windows blocks it.</summary>
public sealed class MicrophoneTestWindow : MicrophoneWindow
{
    private readonly Border meter = new() { Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Border fill = new() { CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    public double MeterShare { get; private set; }
    private readonly TextBlock note = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0), Text = "–" };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    public int NotesHeard { get; private set; }
    public string NoteText => note.Text;

    public MicrophoneTestWindow()
    {
        Title = "Testar microfone"; Width = 520; Height = 380; MinWidth = 420; MinHeight = 320; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(PrivacyButton());
        var close = DialogUi.Button("Fechar", (_, _) => Close(), true); close.IsCancel = true; footer.Children.Add(close);
        var body = new StackPanel(); root.Children.Add(body);
        body.Children.Add(new TextBlock { Text = "Testar microfone", FontSize = 20, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "Toque notas no piano ou cante perto do computador. A barra mostra o volume que chega e a nota reconhecida aparece embaixo. Nada é gravado.",
            TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 6, 0, 4) });
        AutomationProperties.SetName(meter, "Volume do microfone");
        meter.SetResourceReference(Border.BackgroundProperty, "Soft"); fill.SetResourceReference(Border.BackgroundProperty, "Accent"); meter.Child = fill;
        body.Children.Add(meter); body.Children.Add(note); body.Children.Add(Status);
        timer.Tick += (_, _) => { MeterShare = Math.Min(1, (Microphone?.Level ?? 0) * 4); fill.Width = MeterShare * meter.ActualWidth; };
        Closed += (_, _) => timer.Stop();
    }

    protected override void Opened() { Status.Text = "Ouvindo. Toque ou cante uma nota."; timer.Start(); }
    protected override void Failed() => note.Text = "Sem microfone";
    protected override void OnNote(int pitch) { NotesHeard++; note.Text = VoiceRange.NoteName(pitch); Status.Text = $"Microfone funcionando: {NotesHeard} nota(s) reconhecida(s)."; }
}

/// <summary>"Medir cantando": sing your lowest and highest comfortable notes; the held notes give your range.</summary>
public sealed class VoiceRangeWindow : MicrophoneWindow
{
    private readonly VoiceRangeMeter meter = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TextBlock now = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0), Text = "–" };
    private readonly TextBlock range = new() { FontSize = 16, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button use;
    public int? Low => meter.Low;
    public int? High => meter.High;
    /// <summary>Replaces the clock in smoke tests.</summary>
    internal Func<double> seconds;

    public VoiceRangeWindow()
    {
        seconds = () => clock.Elapsed.TotalSeconds;
        Title = "Medir a voz"; Width = 540; Height = 400; MinWidth = 440; MinHeight = 340; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(DialogUi.Button("Recomeçar", (_, _) => { meter.Clear(); Refresh(); }));
        use = DialogUi.Button("Usar estas notas", (_, _) => DialogResult = true, true); use.IsEnabled = false; footer.Children.Add(use);
        var close = DialogUi.Button("Cancelar", (_, _) => Close()); close.IsCancel = true; footer.Children.Add(close);
        var body = new StackPanel(); root.Children.Add(body);
        body.Children.Add(new TextBlock { Text = "Medir a voz", FontSize = 20, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "Cante \"ah\" na nota mais grave que você canta com conforto e segure por um segundo. Depois, a mais aguda confortável. Só contam notas firmes; deslizes e falhas são ignorados. Nada é gravado.",
            TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 6, 0, 4) });
        body.Children.Add(now); body.Children.Add(range); body.Children.Add(Status);
        Refresh();
    }

    protected override void Opened() => Status.Text = "Ouvindo. Comece pela nota mais grave.";

    protected override void OnPitch(double pitch) => Heard(pitch, seconds());

    /// <summary>Adds a pitch heard at a moment (also used by smoke tests).</summary>
    internal void Heard(double? pitch, double at) { meter.Add(pitch, at); Refresh(); }

    private void Refresh()
    {
        now.Text = meter.Holding is { } note ? "Segurando " + VoiceRange.NoteName(note) : "–";
        range.Text = meter.Low is { } low && meter.High is { } high ? $"Sua extensão: de {VoiceRange.NoteName(low)} a {VoiceRange.NoteName(high)}" : "Sua extensão aparece aqui.";
        use.IsEnabled = meter.Low is { } l && meter.High is { } h && h - l >= 5;
    }
}
