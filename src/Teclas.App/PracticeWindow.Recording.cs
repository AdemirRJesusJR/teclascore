using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Recording what is played during practice: saved as MIDI in Documentos\TeclaScore\Gravações and played back here.</summary>
public sealed partial class PracticeWindow
{
    private MidiRecording? recording;
    private readonly Stopwatch recordClock = new();
    private readonly DispatcherTimer playbackTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(5) };
    private readonly Stopwatch playbackClock = new();
    private IMidiOutputTransport? playbackOutput;
    private int playbackIndex;
    private Button? recordButton, listenButton;
    /// <summary>Folder for recordings; replaced in smoke tests.</summary>
    internal string RecordingFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TeclaScore", "Gravações");
    internal Func<IMidiOutputTransport?> playbackDevice = () => new WinMmMidiOutputTransport();
    public string? LastRecording { get; private set; }
    public MidiRecording? Recording => recording;
    public bool IsRecording => recordClock.IsRunning;

    private void BuildRecording(Panel footer)
    {
        recordButton = DialogUi.Button("⏺ Gravar", (_, _) => ToggleRecording()); recordButton.Name = "PracticeRecordButton";
        recordButton.ToolTip = "Grava o que você toca no teclado MIDI (ou na tela) e salva em MIDI para ouvir depois";
        listenButton = DialogUi.Button("▶ Ouvir gravação", (_, _) => ToggleListening()); listenButton.IsEnabled = false;
        footer.Children.Insert(0, listenButton); footer.Children.Insert(0, recordButton);
        playbackTimer.Tick += (_, _) => PlaybackTick();
        Closed += (_, _) => { if (IsRecording) ToggleRecording(); StopListening(); playbackOutput?.Dispose(); };
    }

    /// <summary>Every key reaches the recording while it runs (screen clicks are kept as short notes).</summary>
    private void Record(int pitch, int velocity)
    {
        if (recording is null || !recordClock.IsRunning) return;
        recording.Add(recordClock.Elapsed.TotalSeconds, pitch, velocity);
    }

    internal void ToggleRecording()
    {
        if (!IsRecording)
        {
            StopListening();
            recording = new MidiRecording(); recordClock.Restart();
            recordButton!.Content = "⏹ Parar gravação"; listenButton!.IsEnabled = false;
            feedback.Text = "Gravando… toque normalmente. Clique em Parar gravação para salvar.";
            return;
        }
        recordClock.Stop(); recording!.Finish(recordClock.Elapsed.TotalSeconds);
        recordButton!.Content = "⏺ Gravar";
        if (recording.NoteCount == 0) { feedback.Text = "Nada foi tocado durante a gravação."; return; }
        try
        {
            Directory.CreateDirectory(RecordingFolder);
            string name = ScoreFileName.Stem(null, Title.Replace("Praticar · ", "")) + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".mid";
            LastRecording = Path.Combine(RecordingFolder, name);
            File.WriteAllBytes(LastRecording, recording.ToMidi());
            listenButton!.IsEnabled = true;
            feedback.Text = $"Gravação salva: {recording.NoteCount} notas em {recording.DurationSeconds:0} s · {LastRecording}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { feedback.Text = "Não foi possível salvar a gravação: " + error.Message; }
    }

    internal void ToggleListening()
    {
        if (playbackTimer.IsEnabled) { StopListening(); return; }
        if (recording is null || recording.NoteCount == 0) return;
        try { playbackOutput ??= playbackDevice(); } catch (InvalidOperationException error) { feedback.Text = error.Message; return; }
        playbackIndex = 0; playbackClock.Restart(); playbackTimer.Start();
        try { playbackOutput?.Send(0xC0); } catch (InvalidOperationException) { }
        listenButton!.Content = "⏹ Parar";
    }

    /// <summary>Sends the recorded keys at their times; returns how many were sent (smoke tests pass a time).</summary>
    internal int PlaybackTick(double? at = null)
    {
        if (recording is null) return 0;
        double now = at ?? playbackClock.Elapsed.TotalSeconds; int sent = 0;
        while (playbackIndex < recording.Events.Count && recording.Events[playbackIndex].Seconds <= now)
        {
            var e = recording.Events[playbackIndex++];
            try { playbackOutput?.Send((uint)((e.Velocity > 0 ? 0x90 : 0x80) | (e.Pitch << 8) | (Math.Max(e.Velocity, 0) << 16))); sent++; }
            catch (InvalidOperationException) { StopListening(); return sent; }
        }
        if (playbackIndex >= recording.Events.Count) StopListening();
        return sent;
    }

    private void StopListening()
    {
        if (!playbackTimer.IsEnabled) return;
        playbackTimer.Stop(); playbackClock.Stop();
        try { playbackOutput?.Reset(); } catch (InvalidOperationException) { }
        if (listenButton is not null) listenButton.Content = "▶ Ouvir gravação";
    }

    internal void RecordForSmoke(int pitch, int velocity, double seconds) => recording?.Add(seconds, pitch, velocity);
    internal void StartListeningForSmoke(IMidiOutputTransport output) { playbackOutput = output; playbackIndex = 0; playbackClock.Restart(); }
}
