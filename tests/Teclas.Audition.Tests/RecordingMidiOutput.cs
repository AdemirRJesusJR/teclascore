using Teclas.Core;

internal sealed class RecordingMidiOutput : IMidiOutputTransport
{
    private readonly object gate = new();
    private readonly List<uint> messages = [];
    private readonly HashSet<(int Channel, int Pitch)> active = [], released = [];
    private readonly bool[] sustain = new bool[16];
    private int resets;
    private bool disposed;
    public bool FailOnNoteOn { get; init; }
    public uint[] Messages { get { lock (gate) return messages.ToArray(); } }
    public int ResetCount { get { lock (gate) return resets; } }
    public bool IsDisposed { get { lock (gate) return disposed; } }
    public (int Channel, int Pitch)[] Active { get { lock (gate) return active.ToArray(); } }
    public void Send(uint message)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(RecordingMidiOutput));
            int kind = (int)(message & 0xF0), channel = (int)(message & 0x0F);
            int a = (int)((message >> 8) & 127), b = (int)((message >> 16) & 127);
            if (FailOnNoteOn && kind == 0x90 && b > 0) throw new IOException("Synthetic MIDI output failure.");
            messages.Add(message);
            if (kind == 0x90 && b > 0) { active.Add((channel, a)); released.Remove((channel, a)); }
            else if (kind == 0x80 || kind == 0x90 && b == 0)
            {
                if (sustain[channel]) released.Add((channel, a));
                else { active.Remove((channel, a)); released.Remove((channel, a)); }
            }
            else if (kind == 0xB0 && a == 64)
            {
                sustain[channel] = b >= 64;
                if (!sustain[channel])
                    foreach (var key in released.Where(key => key.Channel == channel).ToArray())
                    { active.Remove(key); released.Remove(key); }
            }
            else if (kind == 0xB0 && a is 120 or 123)
            {
                active.RemoveWhere(key => key.Channel == channel); released.RemoveWhere(key => key.Channel == channel);
            }
        }
    }
    public void Reset() { lock (gate) { resets++; active.Clear(); released.Clear(); Array.Clear(sustain); } }
    public void Dispose() { lock (gate) { disposed = true; active.Clear(); released.Clear(); } }
}
