namespace Teclas.Core;

public sealed record MidiAuditionEvent(TimeSpan At, uint Message);

public sealed class MidiAuditionSequence
{
    private sealed record OrderedEvent(long Tick, int Track, int Order, int Priority, uint Message);
    public SourceKey Source { get; }
    public TimeSpan Duration { get; }
    public TimeSpan LeadingSilenceTrimmed { get; }
    public IReadOnlyList<MidiAuditionEvent> Events { get; }
    public IReadOnlyList<string> Warnings { get; }

    private MidiAuditionSequence(SourceKey source, TimeSpan duration, TimeSpan trim, IReadOnlyList<MidiAuditionEvent> events, IReadOnlyList<string> warnings)
    { Source = source; Duration = duration; LeadingSilenceTrimmed = trim; Events = events; Warnings = warnings; }

    public static uint Message(int kind, int channel, int first, int second = 0)
    {
        if (kind is < 8 or > 14 || channel is < 1 or > 16 || first is < 0 or > 127 || second is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(first), "Mensagem MIDI inválida.");
        return (uint)((kind << 4) | (channel - 1) | (first << 8) | (second << 16));
    }

    public static MidiAuditionSequence Create(MidiDocument document, SourceKey source, bool trimLeadingSilence = true)
    {
        if (document.Ppq <= 0) throw new InvalidDataException("O MIDI precisa usar uma resolução musical PPQ válida.");
        var notes = document.Notes.Where(n => n.Track == source.Track && n.Channel == source.Channel && n.Program == source.Program).ToArray();
        if (notes.Length == 0) throw new InvalidOperationException("Esta fonte não contém notas para ouvir.");
        long firstTick = trimLeadingSilence ? notes.Min(note => note.Start) : 0;
        long lastNoteTick = notes.Max(note => note.End);
        var pitches = notes.Select(note => note.Pitch).ToHashSet();
        var ordered = new List<OrderedEvent>();
        foreach (var note in notes)
        {
            ordered.Add(new(note.Start, note.Track, note.StartOrder, 1, Message(9, source.Channel, note.Pitch, note.Velocity)));
            ordered.Add(new(Math.Max(note.Start, note.End), note.Track, note.EndOrder, 0, Message(8, source.Channel, note.Pitch)));
        }
        foreach (var control in document.ChannelEvents.Where(e => e.Channel == source.Channel))
        {
            if (control.Kind == 12 && control.Data1 != source.Program || control.Kind == 10 && !pitches.Contains(control.Data1)) continue;
            ordered.Add(new(control.Tick, control.Track, control.Order, 0, Message(control.Kind, control.Channel, control.Data1, control.Data2)));
        }
        ordered = ordered.OrderBy(e => e.Tick).ThenBy(e => e.Track).ThenBy(e => e.Order).ThenBy(e => e.Priority).ToList();
        // Keep a final pedal-held note until its actual release, even when that is later than note-off.
        var voices = new MidiAuditionVoices();
        long endTick = Math.Max(lastNoteTick, document.EndTick);
        foreach (var e in ordered)
        {
            voices.Apply(e.Message);
            if (e.Tick >= lastNoteTick && voices.Voices.Count == 0) { endTick = e.Tick; break; }
        }
        var tempo = new MidiTempoMap(document.Ppq, document.TempoChanges);
        TimeSpan trim = tempo.At(firstTick);
        var events = new List<MidiAuditionEvent>();
        foreach (uint message in InitialMessages(source)) events.Add(new(TimeSpan.Zero, message));
        foreach (var e in ordered.Where(e => e.Tick < firstTick && IsControl(e.Message))) events.Add(new(TimeSpan.Zero, e.Message));
        // A source is defined by its GM program; another instrument on the same channel must not replace it.
        events.Add(new(TimeSpan.Zero, Message(12, source.Channel, source.Program)));
        foreach (var e in ordered.Where(e => e.Tick >= firstTick && e.Tick <= endTick)) events.Add(new(tempo.At(e.Tick) - trim, e.Message));
        TimeSpan end = tempo.At(endTick) - trim;
        // Release any pedal left down by a malformed/unfinished source and leave a short natural release tail.
        events.Add(new(end, Message(11, source.Channel, 64)));
        events.Add(new(end, Message(11, source.Channel, 66)));
        events.Add(new(end, Message(11, source.Channel, 123)));
        string[] warnings = document.HasSystemExclusive ? ["A escuta usa o sintetizador General MIDI do Windows; timbres personalizados por SysEx podem soar diferentes."] : [];
        return new(source, end + TimeSpan.FromMilliseconds(250), trim, events.ToArray(), warnings);
    }

    internal static bool IsControl(uint message) => (message & 0xf0) is >= 0xa0 and <= 0xe0;
    internal static IEnumerable<uint> InitialMessages(SourceKey source)
    {
        int channel = source.Channel;
        yield return Message(11, channel, 121); yield return Message(11, channel, 0); yield return Message(11, channel, 32);
        yield return Message(11, channel, 7, 100); yield return Message(11, channel, 10, 64); yield return Message(11, channel, 11, 127);
        yield return Message(11, channel, 64); yield return Message(11, channel, 66);
        // Restore GM pitch-bend range and tuning between sources; CC121 alone need not reset RPN values.
        foreach (var parameter in new[] { (Number: 0, Value: 2), (Number: 1, Value: 64), (Number: 2, Value: 64) })
        {
            yield return Message(11, channel, 101); yield return Message(11, channel, 100, parameter.Number);
            yield return Message(11, channel, 6, parameter.Value); yield return Message(11, channel, 38);
        }
        yield return Message(11, channel, 101, 127); yield return Message(11, channel, 100, 127);
        yield return Message(14, channel, 0, 64); yield return Message(13, channel, 0);
    }

    private sealed class MidiTempoMap
    {
        private readonly List<(long Tick, double Seconds, int Tempo)> segments = [(0, 0, 500000)];
        private readonly int ppq;
        public MidiTempoMap(int ppq, IReadOnlyList<MidiTempoChange> changes)
        {
            this.ppq = ppq;
            foreach (var change in changes.Where(t => t.Tick >= 0 && t.MicrosecondsPerQuarter > 0).OrderBy(t => t.Tick).ThenBy(t => t.Track).ThenBy(t => t.Order))
            {
                var last = segments[^1];
                double seconds = last.Seconds + (change.Tick - last.Tick) * (double)last.Tempo / ppq / 1_000_000;
                if (change.Tick == last.Tick) segments[^1] = (change.Tick, seconds, change.MicrosecondsPerQuarter);
                else segments.Add((change.Tick, seconds, change.MicrosecondsPerQuarter));
            }
        }
        public TimeSpan At(long tick)
        {
            int lo = 0, hi = segments.Count - 1;
            while (lo < hi) { int middle = (lo + hi + 1) / 2; if (segments[middle].Tick <= tick) lo = middle; else hi = middle - 1; }
            var segment = segments[lo];
            return TimeSpan.FromSeconds(segment.Seconds + (tick - segment.Tick) * (double)segment.Tempo / ppq / 1_000_000);
        }
    }
}

internal sealed class MidiAuditionVoices
{
    internal sealed class Voice(int pitch, int velocity)
    { public int Pitch { get; } = pitch; public int Velocity { get; } = velocity; public bool Down { get; set; } = true; public bool Sostenuto { get; set; } }
    public List<Voice> Voices { get; } = [];
    private bool sustain, sostenuto;
    public void Apply(uint message)
    {
        int kind = (int)((message >> 4) & 15), first = (int)((message >> 8) & 127), second = (int)((message >> 16) & 127);
        if (kind == 9 && second > 0) Voices.Add(new(first, second));
        else if (kind == 8 || kind == 9 && second == 0)
        {
            var voice = Voices.FirstOrDefault(v => v.Pitch == first && v.Down);
            if (voice is not null) voice.Down = false;
        }
        else if (kind == 11)
        {
            switch (first)
            {
                case 64: sustain = second >= 64; break;
                case 66:
                    if (second >= 64 && !sostenuto) foreach (var v in Voices.Where(v => v.Down)) v.Sostenuto = true;
                    if (second < 64) foreach (var v in Voices) v.Sostenuto = false;
                    sostenuto = second >= 64; break;
                case 120: Voices.Clear(); break;
                case 123: foreach (var v in Voices) v.Down = false; break;
                case 121: sustain = false; sostenuto = false; foreach (var v in Voices) v.Sostenuto = false; break;
            }
        }
        Voices.RemoveAll(v => !v.Down && !sustain && !v.Sostenuto);
    }
}
