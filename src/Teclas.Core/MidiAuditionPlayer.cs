using System.Diagnostics;

namespace Teclas.Core;

public interface IMidiOutputTransport : IDisposable
{
    void Send(uint message);
    void Reset();
}

public sealed class MidiPlaybackErrorEventArgs(string message) : EventArgs
{ public string Message { get; } = message; }

/// <summary>Schedules only one selected MIDI source. The transport can be replaced by a silent test recorder.</summary>
public class MidiAuditionPlayer : IDisposable
{
    private readonly object gate = new();
    private readonly Func<IMidiOutputTransport> outputFactory;
    private readonly Stopwatch clock = new();
    private readonly Timer timer;
    private IMidiOutputTransport? output;
    private MidiAuditionSequence? sequence;
    private TimeSpan offset;
    private double playbackRate = 1.0;
    private int nextEvent;
    private bool playing, paused, disposed;
    public event EventHandler? StateChanged;
    public event EventHandler<MidiPlaybackErrorEventArgs>? PlaybackFailed;
    public MidiAuditionPlayer(Func<IMidiOutputTransport> outputFactory)
    {
        this.outputFactory = outputFactory ?? throw new ArgumentNullException(nameof(outputFactory));
        timer = new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
    }
    public MidiAuditionSequence? Sequence { get { lock (gate) return sequence; } }
    public TimeSpan Position { get { lock (gate) return PositionLocked(); } }
    public TimeSpan Duration { get { lock (gate) return sequence?.Duration ?? TimeSpan.Zero; } }
    public bool IsPlaying { get { lock (gate) return playing; } }
    public bool IsPaused { get { lock (gate) return paused; } }
    /// <summary>Changes only scheduling speed. Position and Duration always use the original MIDI timeline.</summary>
    public double PlaybackRate
    {
        get { lock (gate) return playbackRate; }
        set
        {
            if (!double.IsFinite(value) || value is < 0.25 or > 1.5)
                throw new ArgumentOutOfRangeException(nameof(value), "A velocidade da escuta deve ficar entre 25% e 150%.");
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (value == playbackRate) return;
                // Anchor elapsed content at the old rate before changing the clock scale; sounding notes stay untouched.
                if (playing) { offset = PositionLocked(); clock.Restart(); }
                playbackRate = value;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Play(MidiAuditionSequence value) => Play(value, TimeSpan.Zero);

    /// <summary>Starts at an original-timeline position, restoring active voices without playing skipped notes.</summary>
    public void Play(MidiAuditionSequence value, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(value);
        Change(() =>
        {
            SilenceLocked(); sequence = value; offset = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, value.Duration.Ticks)); nextEvent = 0;
            output ??= outputFactory() ?? throw new InvalidOperationException("O dispositivo MIDI não está disponível.");
            bool canPlay = offset < value.Duration;
            RestoreLocked(offset, canPlay);
            if (canPlay) StartLocked();
            else { clock.Reset(); playing = false; paused = false; timer.Change(Timeout.Infinite, Timeout.Infinite); }
        });
    }
    public void Pause()
    {
        Change(() =>
        {
            if (!playing) return;
            offset = PositionLocked(); clock.Reset(); playing = false; paused = true;
            timer.Change(Timeout.Infinite, Timeout.Infinite); SilenceLocked();
        });
    }
    public void Resume()
    {
        Change(() =>
        {
            if (sequence is null) throw new InvalidOperationException("Escolha uma fonte MIDI para ouvir.");
            if (playing) return;
            if (offset >= sequence.Duration) offset = TimeSpan.Zero;
            output ??= outputFactory() ?? throw new InvalidOperationException("O dispositivo MIDI não está disponível.");
            RestoreLocked(offset, true); StartLocked();
        });
    }
    public void Seek(TimeSpan position)
    {
        Change(() =>
        {
            if (sequence is null) return;
            bool resume = playing;
            clock.Reset(); timer.Change(Timeout.Infinite, Timeout.Infinite); playing = false;
            SilenceLocked(); offset = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, sequence.Duration.Ticks));
            bool canResume = resume && offset < sequence.Duration;
            RestoreLocked(offset, canResume);
            if (canResume) StartLocked(); else paused = true;
        });
    }
    public void Stop()
    {
        bool changed;
        lock (gate)
        {
            if (disposed) return;
            changed = playing || paused || offset != TimeSpan.Zero;
            playing = false; paused = false; clock.Reset(); offset = TimeSpan.Zero; nextEvent = 0;
            timer.Change(Timeout.Infinite, Timeout.Infinite); SilenceLocked();
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Change(Action action)
    {
        Exception? failure = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try { action(); }
            catch (Exception ex) { FailLocked(); failure = ex; }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (failure is not null)
        {
            string message = "Não foi possível reproduzir esta fonte MIDI. " + failure.Message;
            PlaybackFailed?.Invoke(this, new(message));
            throw new InvalidOperationException(message, failure);
        }
    }
    private TimeSpan PositionLocked()
    {
        if (sequence is null) return TimeSpan.Zero;
        double elapsedTicks = playing ? clock.Elapsed.Ticks * playbackRate : 0;
        long remainingTicks = sequence.Duration.Ticks - offset.Ticks;
        return elapsedTicks >= remainingTicks ? sequence.Duration : TimeSpan.FromTicks(offset.Ticks + (long)elapsedTicks);
    }
    private void StartLocked()
    { clock.Restart(); playing = true; paused = false; timer.Change(0, 5); }
    private void Tick(object? state)
    {
        bool changed = false; string? failure = null;
        lock (gate)
        {
            if (disposed || !playing || sequence is null || output is null) return;
            try
            {
                TimeSpan position = PositionLocked();
                while (nextEvent < sequence.Events.Count && sequence.Events[nextEvent].At <= position)
                    output.Send(sequence.Events[nextEvent++].Message);
                if (position >= sequence.Duration)
                {
                    offset = sequence.Duration; clock.Reset(); playing = false; paused = false;
                    timer.Change(Timeout.Infinite, Timeout.Infinite); SilenceLocked(); changed = true;
                }
            }
            catch (Exception ex) { FailLocked(); failure = "A reprodução MIDI foi interrompida. " + ex.Message; changed = true; }
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
        if (failure is not null) PlaybackFailed?.Invoke(this, new(failure));
    }

    private void RestoreLocked(TimeSpan position, bool restoreNotes)
    {
        if (sequence is null || output is null) return;
        var voices = new MidiAuditionVoices();
        var pressure = new Dictionary<int, uint>();
        nextEvent = 0;
        while (nextEvent < sequence.Events.Count && sequence.Events[nextEvent].At <= position)
        {
            uint message = sequence.Events[nextEvent++].Message;
            voices.Apply(message);
            if ((message & 0xf0) == 0xa0) pressure[(int)((message >> 8) & 127)] = message;
            else if (MidiAuditionSequence.IsControl(message)) output.Send(message);
        }
        if (!restoreNotes) return;
        int channel = sequence.Source.Channel;
        bool hasSostenuto = voices.Voices.Any(v => v.Sostenuto);
        if (hasSostenuto)
        {
            output.Send(MidiAuditionSequence.Message(11, channel, 66));
            foreach (var voice in voices.Voices.Where(v => v.Sostenuto)) output.Send(MidiAuditionSequence.Message(9, channel, voice.Pitch, voice.Velocity));
            output.Send(MidiAuditionSequence.Message(11, channel, 66, 127));
        }
        foreach (var voice in voices.Voices.Where(v => !hasSostenuto || !v.Sostenuto)) output.Send(MidiAuditionSequence.Message(9, channel, voice.Pitch, voice.Velocity));
        foreach (var voice in voices.Voices.Where(v => !v.Down)) output.Send(MidiAuditionSequence.Message(8, channel, voice.Pitch));
        foreach (var voice in voices.Voices) if (pressure.TryGetValue(voice.Pitch, out uint message)) output.Send(message);
    }
    private void SilenceLocked()
    {
        if (output is null) return;
        if (sequence is not null)
        {
            int channel = sequence.Source.Channel;
            // Explicit pedal release and note-offs complement midiOutReset on devices with sustained voices.
            TrySend(MidiAuditionSequence.Message(11, channel, 64)); TrySend(MidiAuditionSequence.Message(11, channel, 66));
            for (int pitch = 0; pitch < 128; pitch++) TrySend(MidiAuditionSequence.Message(8, channel, pitch));
            TrySend(MidiAuditionSequence.Message(11, channel, 123)); TrySend(MidiAuditionSequence.Message(11, channel, 120));
        }
        try { output.Reset(); } catch { /* Best effort after device disconnection: closing remains safe. */ }
    }
    private void TrySend(uint message) { try { output?.Send(message); } catch { } }
    private void FailLocked()
    {
        playing = false; paused = false; clock.Reset(); timer.Change(Timeout.Infinite, Timeout.Infinite);
        SilenceLocked(); try { output?.Dispose(); } catch { } output = null;
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; playing = false; paused = false; clock.Reset(); timer.Dispose();
            SilenceLocked(); try { output?.Dispose(); } catch { } output = null;
        }
        GC.SuppressFinalize(this);
    }
}
