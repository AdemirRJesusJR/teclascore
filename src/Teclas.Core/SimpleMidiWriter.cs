using System.Text;

namespace Teclas.Core;

/// <summary>A note in beats (quarter notes) from the start.</summary>
public sealed record WrittenNote(double Start, double Length, int Pitch, int Velocity = 90);
/// <summary>One part of a written MIDI file.</summary>
public sealed record WrittenTrack(string Name, int Channel, int Program, IReadOnlyList<WrittenNote> Notes);

/// <summary>Writes a format 1 MIDI file: tempo track (tempo and meter) followed by one track per part, 480 ticks per beat.</summary>
public static class SimpleMidiWriter
{
    public const int Ppq = 480;

    public static byte[] Write(double bpm, int beatsPerBar, IReadOnlyList<WrittenTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (bpm is < 10 or > 400) throw new ArgumentOutOfRangeException(nameof(bpm));
        int micro = (int)Math.Round(60_000_000 / bpm);
        var tempo = new List<byte>
        {
            0, 0xFF, 0x51, 3, (byte)(micro >> 16), (byte)(micro >> 8), (byte)micro,
            0, 0xFF, 0x58, 4, (byte)Math.Clamp(beatsPerBar, 1, 16), 2, 24, 8,
        };
        var file = new List<byte>();
        file.AddRange("MThd"u8.ToArray()); file.AddRange([0, 0, 0, 6, 0, 1, 0, (byte)(tracks.Count + 1), Ppq >> 8, Ppq & 0xFF]);
        Chunk(file, tempo);
        foreach (var track in tracks)
        {
            int channel = Math.Clamp(track.Channel, 0, 15);
            var body = new List<byte>();
            byte[] name = Encoding.UTF8.GetBytes(track.Name);
            body.Add(0); body.AddRange([0xFF, 0x03]); Vlq(body, name.Length); body.AddRange(name);
            body.AddRange([0, (byte)(0xC0 | channel), (byte)Math.Clamp(track.Program, 0, 127)]);
            var events = track.Notes.Where(n => n.Pitch is >= 0 and <= 127 && n.Length > 0)
                .SelectMany(n => new[]
                {
                    (Tick: Ticks(n.Start), Order: 1, Status: (byte)(0x90 | channel), n.Pitch, Velocity: Math.Clamp(n.Velocity, 1, 127)),
                    (Tick: Math.Max(Ticks(n.Start) + 1, Ticks(n.Start + n.Length)), Order: 0, Status: (byte)(0x80 | channel), n.Pitch, Velocity: 0),
                })
                .OrderBy(e => e.Tick).ThenBy(e => e.Order).ThenBy(e => e.Pitch).ToList();
            long last = 0;
            foreach (var e in events) { Vlq(body, (int)(e.Tick - last)); last = e.Tick; body.AddRange([e.Status, (byte)e.Pitch, (byte)e.Velocity]); }
            Chunk(file, body);
        }
        return file.ToArray();
    }

    private static long Ticks(double beats) => (long)Math.Round(Math.Max(0, beats) * Ppq);

    private static void Chunk(List<byte> file, List<byte> body)
    {
        body.AddRange([0, 0xFF, 0x2F, 0]);
        file.AddRange("MTrk"u8.ToArray());
        file.AddRange([(byte)(body.Count >> 24), (byte)(body.Count >> 16), (byte)(body.Count >> 8), (byte)body.Count]);
        file.AddRange(body);
    }

    private static void Vlq(List<byte> output, int value)
    {
        var bytes = new Stack<byte>(); bytes.Push((byte)(value & 0x7F));
        while ((value >>= 7) > 0) bytes.Push((byte)(0x80 | value & 0x7F));
        output.AddRange(bytes);
    }
}
