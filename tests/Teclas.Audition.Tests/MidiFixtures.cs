using System.Buffers.Binary;
using System.Text;
using Teclas.Core;

internal static class MidiFixtures
{
    public sealed record Event(long Tick, params byte[] Bytes);
    public static byte[] File(int format, params Event[][] tracks)
    {
        using var file = new MemoryStream();
        void U16(int value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value); file.Write(bytes); }
        void U32(int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); file.Write(bytes); }
        file.Write(Encoding.ASCII.GetBytes("MThd")); U32(6); U16(format); U16(tracks.Length); U16(480);
        foreach (var events in tracks)
        {
            using var track = new MemoryStream();
            long previous = 0;
            foreach (var item in events.OrderBy(item => item.Tick))
            {
                Vlq(track, checked((int)(item.Tick - previous))); track.Write(item.Bytes); previous = item.Tick;
            }
            track.Write([0, 0xFF, 0x2F, 0]);
            file.Write(Encoding.ASCII.GetBytes("MTrk")); U32((int)track.Length); track.Position = 0; track.CopyTo(file);
        }
        return file.ToArray();
    }
    private static void Vlq(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4]; int count = 1; bytes[3] = (byte)(value & 127);
        while ((value >>= 7) > 0) bytes[3 - count++] = (byte)((value & 127) | 128);
        stream.Write(bytes[(4 - count)..]);
    }
    public static MidiDocument FormatZero() => MidiReader.Read(File(0,
    [
        new Event(0, 0xC0, 0), new Event(0, 0xC1, 40), new Event(0, 0xC9, 0),
        new Event(0, 0x90, 60, 101), new Event(0, 0x91, 72, 88), new Event(0, 0x99, 36, 112),
        new Event(480, 0x80, 60, 0), new Event(480, 0x81, 72, 0), new Event(480, 0x89, 36, 0),
        new Event(480, 0xC0, 24), new Event(480, 0x90, 67, 77), new Event(960, 0x80, 67, 0)
    ]));
    public static MidiDocument Held() => MidiReader.Read(File(0,
    [
        new Event(0, 0xC0, 0), new Event(0, 0x90, 60, 96),
        new Event(480, 0x90, 62, 64), new Event(960, 0x80, 62, 0), new Event(9600, 0x80, 60, 0)
    ]));
    public static MidiDocument Pedal() => MidiReader.Read(File(0,
    [new Event(0, 0xB0, 64, 127), new Event(0, 0x90, 60, 95), new Event(480, 0x80, 60, 0), new Event(960, 0xB0, 64, 0)]));
}
