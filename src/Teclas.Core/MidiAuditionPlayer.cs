using System.Diagnostics;

namespace Teclas.Core;

public interface IMidiOutputTransport : IDisposable
{
    void Send(uint message);
    void Reset();
}

public sealed class MidiPlaybackErrorEventArgs(string message, long generation = 0) : EventArgs
{ public string Message { get; } = message; public long Generation { get; } = generation; }

/// <summary>Schedules a selected source or complete MIDI document. The transport can be replaced by a silent test recorder.</summary>
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
    private HashSet<int> muted = [];
    private long playbackGeneration;
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
    public long PlaybackGeneration { get { lock (gate) return playbackGeneration; } }
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
    public void Play(MidiAuditionSequence value, TimeSpan position) => Play(value, position, []);

    /// <summary>Starts a live mix with some of its sources muted (indexes into MixedSources).</summary>
    public void Play(MidiAuditionSequence value, TimeSpan position, IEnumerable<int> mutedSources)
    {
        ArgumentNullException.ThrowIfNull(value);
        var nextMuted = mutedSources.ToHashSet();
        Change(() =>
        {
            SilenceLocked(); sequence = value; muted = nextMuted; offset = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, value.Duration.Ticks)); nextEvent = 0;
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
    public void Stop(bool releaseOutput = false)
    {
        bool changed;
        lock (gate)
        {
            if (disposed) return;
            playbackGeneration++;
            changed = playing || paused || offset != TimeSpan.Zero;
            playing = false; paused = false; clock.Reset(); offset = TimeSpan.Zero; nextEvent = 0;
            timer.Change(Timeout.Infinite, Timeout.Infinite); SilenceLocked();
            if (releaseOutput) { try { output?.Dispose(); } catch { } output = null; }
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>Sources of a live mix that are currently silent (indexes into MixedSources).</summary>
    public IReadOnlyCollection<int> MutedSources { get { lock (gate) return muted.ToArray(); } }

    /// <summary>
    /// Switches live-mix sources on and off without stopping playback: a muted source releases its sounding
    /// notes at once, and a source switched back on re-sounds the notes it holds at this moment. Controller and
    /// program events keep flowing for muted sources, so their channel is ready the moment they return.
    /// </summary>
    public void SetMutedSources(IEnumerable<int> sources)
    {
        var next = sources.ToHashSet();
        Change(() =>
        {
            var silenced = next.Except(muted).ToArray();
            var revived = muted.Except(next).ToArray();
            muted = next;
            // While paused everything is already silent; Resume restores only the audible sources.
            if (!playing || output is null || sequence is not { IsLiveMix: true }) return;
            foreach (int source in silenced) ReleaseSourceLocked(source);
            foreach (int source in revived) ReviveSourceLocked(source);
        });
    }

    private bool IsMutedNoteOn(MidiAuditionEvent item) =>
        item.Source >= 0 && (item.Message & 0xf0) == 0x90 && ((item.Message >> 16) & 127) > 0 && muted.Contains(item.Source);

    /// <summary>The notes a source holds at the scheduling point, ignoring whether it was muted when they began.</summary>
    private (int Channel, MidiAuditionVoices Voices, uint? Sustain, uint? Sostenuto) SourceStateLocked(int source)
    {
        var voices = new MidiAuditionVoices();
        int channel = 0; uint? sustain = null, sostenuto = null;
        for (int index = 0; index < nextEvent; index++)
        {
            var item = sequence!.Events[index];
            if (item.Source != source) continue;
            uint message = item.Message;
            channel = (int)(message & 15) + 1;
            voices.Apply(message);
            if ((message & 0xf0) == 0xb0 && ((message >> 8) & 127) == 64) sustain = message;
            if ((message & 0xf0) == 0xb0 && ((message >> 8) & 127) == 66) sostenuto = message;
        }
        return (channel, voices, sustain, sostenuto);
    }

    private void ReleaseSourceLocked(int source)
    {
        var (channel, voices, sustain, sostenuto) = SourceStateLocked(source);
        if (channel == 0 || voices.Voices.Count == 0) return;
        // Lift the pedals so the note-offs take effect, then put them back for when the source returns.
        output!.Send(MidiAuditionSequence.Message(11, channel, 64)); output.Send(MidiAuditionSequence.Message(11, channel, 66));
        foreach (var voice in voices.Voices) output.Send(MidiAuditionSequence.Message(8, channel, voice.Pitch));
        if (sustain is { } pedal) output.Send(pedal);
        if (sostenuto is { } middle) output.Send(middle);
    }

    private void ReviveSourceLocked(int source)
    {
        var (channel, voices, _, _) = SourceStateLocked(source);
        if (channel == 0) return;
        foreach (var voice in voices.Voices)
        {
            output!.Send(MidiAuditionSequence.Message(9, channel, voice.Pitch, voice.Velocity));
            // A note already released but held by the pedal keeps ringing only through the pedal.
            if (!voice.Down) output.Send(MidiAuditionSequence.Message(8, channel, voice.Pitch));
        }
    }

    private void Change(Action action)
    {
        Exception? failure = null; long generation;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            generation = ++playbackGeneration;
            try { action(); }
            catch (Exception ex) { FailLocked(); failure = ex; }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (failure is not null)
        {
            string message = "Não foi possível reproduzir esta fonte MIDI. " + failure.Message;
            PlaybackFailed?.Invoke(this, new(message, generation));
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
        bool changed = false; string? failure = null; long generation = 0;
        lock (gate)
        {
            if (disposed || !playing || sequence is null || output is null) return;
            try
            {
                TimeSpan position = PositionLocked();
                while (nextEvent < sequence.Events.Count && sequence.Events[nextEvent].At <= position)
                {
                    var item = sequence.Events[nextEvent++];
                    if (!IsMutedNoteOn(item)) output.Send(item.Message);
                }
                if (position >= sequence.Duration)
                {
                    offset = sequence.Duration; clock.Reset(); playing = false; paused = false;
                    timer.Change(Timeout.Infinite, Timeout.Infinite); SilenceLocked(); changed = true;
                }
            }
            catch (Exception ex) { generation = playbackGeneration; FailLocked(); failure = "A reprodução MIDI foi interrompida. " + ex.Message; changed = true; }
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
        if (failure is not null) PlaybackFailed?.Invoke(this, new(failure, generation));
    }

    private void RestoreLocked(TimeSpan position, bool restoreNotes)
    {
        if (sequence is null || output is null) return;
        var channels = sequence.Channels.ToDictionary(channel => channel, _ => new MidiAuditionVoices());
        var pressure = new Dictionary<(int Channel, int Pitch), uint>();
        nextEvent = 0;
        while (nextEvent < sequence.Events.Count && sequence.Events[nextEvent].At <= position)
        {
            var item = sequence.Events[nextEvent++];
            if (IsMutedNoteOn(item)) continue;
            uint message = item.Message;
            int channel = (int)(message & 15) + 1;
            channels[channel].Apply(message);
            if ((message & 0xf0) == 0xa0) pressure[(channel, (int)((message >> 8) & 127))] = message;
            else if (MidiAuditionSequence.IsControl(message)) output.Send(message);
        }
        if (!restoreNotes) return;
        foreach (var (channel, voices) in channels)
        {
            int program = voices.Program, bankMsb = voices.ProgramBankMsb, bankLsb = voices.ProgramBankLsb;
            void SelectProgram(int requestedProgram, int msb, int lsb)
            {
                output.Send(MidiAuditionSequence.Message(11, channel, 0, msb));
                output.Send(MidiAuditionSequence.Message(11, channel, 32, lsb));
                output.Send(MidiAuditionSequence.Message(12, channel, requestedProgram));
                program = requestedProgram; bankMsb = msb; bankLsb = lsb;
            }
            void RestoreVoice(MidiAuditionVoices.Voice voice)
            {
                if (program != voice.Program || bankMsb != voice.BankMsb || bankLsb != voice.BankLsb)
                    SelectProgram(voice.Program, voice.BankMsb, voice.BankLsb);
                output.Send(MidiAuditionSequence.Message(9, channel, voice.Pitch, voice.Velocity));
            }
            bool hasSostenuto = voices.Voices.Any(v => v.Sostenuto);
            if (hasSostenuto)
            {
                output.Send(MidiAuditionSequence.Message(11, channel, 66));
                foreach (var voice in voices.Voices.Where(v => v.Sostenuto)) RestoreVoice(voice);
                output.Send(MidiAuditionSequence.Message(11, channel, 66, 127));
            }
            foreach (var voice in voices.Voices.Where(v => !hasSostenuto || !v.Sostenuto)) RestoreVoice(voice);
            foreach (var voice in voices.Voices.Where(v => !v.Down)) output.Send(MidiAuditionSequence.Message(8, channel, voice.Pitch));
            foreach (var voice in voices.Voices) if (pressure.TryGetValue((channel, voice.Pitch), out uint message)) output.Send(message);
            // Held notes can originate before a program change. Restore those timbres, then the
            // current program and pending bank selection for future events on this channel.
            if (program != voices.Program || bankMsb != voices.ProgramBankMsb || bankLsb != voices.ProgramBankLsb)
                SelectProgram(voices.Program, voices.ProgramBankMsb, voices.ProgramBankLsb);
            output.Send(MidiAuditionSequence.Message(11, channel, 0, voices.BankMsb));
            output.Send(MidiAuditionSequence.Message(11, channel, 32, voices.BankLsb));
        }
    }
    private void SilenceLocked()
    {
        if (output is null) return;
        foreach (int channel in sequence?.Channels ?? [])
        {
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
            disposed = true; playbackGeneration++; playing = false; paused = false; clock.Reset(); timer.Dispose();
            SilenceLocked(); try { output?.Dispose(); } catch { } output = null;
        }
        GC.SuppressFinalize(this);
    }
}
