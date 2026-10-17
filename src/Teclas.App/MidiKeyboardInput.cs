using System.Runtime.InteropServices;

namespace Teclas.App;

/// <summary>Reads Note On messages from a USB/MIDI keyboard through WinMM. Notes arrive on a driver thread.</summary>
public sealed class MidiKeyboardInput : IDisposable
{
    private const uint CallbackFunction = 0x30000, MimData = 0x3C3;
    private delegate void InputProc(nint handle, uint message, nint instance, nint param1, nint param2);
    private readonly InputProc callback;
    private nint handle;
    public event Action<int, int>? NoteOn;

    public static IReadOnlyList<string> Devices()
    {
        var names = new List<string>();
        uint count = midiInGetNumDevs();
        for (uint index = 0; index < count; index++)
        {
            var caps = new MidiInCaps();
            names.Add(midiInGetDevCapsW((nuint)index, ref caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0 ? caps.Name : $"Dispositivo MIDI {index + 1}");
        }
        return names;
    }

    public MidiKeyboardInput(int device)
    {
        // Keep the delegate rooted for the device's lifetime; WinMM calls it from its own thread.
        callback = OnMessage;
        uint error = midiInOpen(out handle, (uint)device, Marshal.GetFunctionPointerForDelegate(callback), 0, CallbackFunction);
        if (error != 0) throw new InvalidOperationException($"Não foi possível abrir o teclado MIDI (código {error}). Confira se outro programa está usando o dispositivo.");
        error = midiInStart(handle);
        if (error != 0) { midiInClose(handle); handle = 0; throw new InvalidOperationException($"O teclado MIDI não iniciou (código {error})."); }
    }

    private void OnMessage(nint device, uint message, nint instance, nint param1, nint param2)
    {
        if (message != MimData) return;
        uint data = (uint)param1;
        int status = (int)(data & 0xF0), pitch = (int)((data >> 8) & 0x7F), velocity = (int)((data >> 16) & 0x7F);
        if (status == 0x90 && velocity > 0) NoteOn?.Invoke(pitch, velocity);
    }

    public void Dispose()
    {
        nint owned = Interlocked.Exchange(ref handle, 0);
        if (owned == 0) return;
        midiInStop(owned); midiInReset(owned); midiInClose(owned);
        GC.KeepAlive(callback);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort Manufacturer, Product; public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Support;
    }
    [DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiInGetDevCapsW(nuint device, ref MidiInCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiInOpen(out nint handle, uint device, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiInStart(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiInStop(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiInReset(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiInClose(nint handle);
}
