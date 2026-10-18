namespace Teclas.Core;

/// <summary>A key played during practice: when (seconds from the start of the recording), which key, how hard (0 = released).</summary>
public sealed record RecordedNote(double Seconds, int Pitch, int Velocity);

/// <summary>Keeps what was played and writes it as a standard MIDI file (format 0, piano, ♩ = 120, 960 ticks per second).</summary>
public sealed class MidiRecording
{
    private readonly List<RecordedNote> events = [];
    public IReadOnlyList<RecordedNote> Events => events;
    public int NoteCount => events.Count(e => e.Velocity > 0);
    public double DurationSeconds => events.Count == 0 ? 0 : events[^1].Seconds;

    public void Add(double seconds, int pitch, int velocity)
    {
        if (pitch is < 0 or > 127 || !double.IsFinite(seconds) || seconds < 0) return;
        events.Add(new(Math.Max(seconds, events.Count == 0 ? 0 : events[^1].Seconds), pitch, Math.Clamp(velocity, 0, 127)));
    }

    /// <summary>Closes keys still held at the end, so every note has its release.</summary>
    public void Finish(double seconds)
    {
        var held = new HashSet<int>();
        foreach (var e in events) { if (e.Velocity > 0) held.Add(e.Pitch); else held.Remove(e.Pitch); }
        foreach (int pitch in held) Add(seconds, pitch, 0);
    }

    public byte[] ToMidi()
    {
        const int ppq = 480; // ♩ = 120 → 960 ticks per second
        var track = new List<byte> { 0, 0xFF, 0x51, 3, 0x07, 0xA1, 0x20, 0, 0xC0, 0 };
        long last = 0;
        foreach (var e in events)
        {
            long tick = (long)Math.Round(e.Seconds * 960);
            WriteVariable(track, tick - last); last = tick;
            track.AddRange([(byte)(e.Velocity > 0 ? 0x90 : 0x80), (byte)e.Pitch, (byte)(e.Velocity > 0 ? e.Velocity : 64)]);
        }
        track.AddRange([0, 0xFF, 0x2F, 0]);
        var file = new List<byte>();
        file.AddRange("MThd"u8.ToArray()); file.AddRange([0, 0, 0, 6, 0, 0, 0, 1, ppq >> 8, ppq & 0xFF]);
        file.AddRange("MTrk"u8.ToArray());
        file.AddRange([(byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count]);
        file.AddRange(track);
        return file.ToArray();
    }

    private static void WriteVariable(List<byte> output, long value)
    {
        var bytes = new Stack<byte>(); bytes.Push((byte)(value & 0x7F));
        while ((value >>= 7) > 0) bytes.Push((byte)((value & 0x7F) | 0x80));
        output.AddRange(bytes);
    }
}
