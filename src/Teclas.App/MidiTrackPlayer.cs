using System.Runtime.InteropServices;
using System.Text;
using Teclas.Core;

namespace Teclas.App;

public sealed class MidiTrackPlayer(Func<IMidiOutputTransport>? outputFactory = null)
    : MidiAuditionPlayer(outputFactory ?? (() => new WinMmMidiOutputTransport())) { }

/// <summary>WinMM MIDI messages are complete status/data DWORDs; this never changes system/device master volume.</summary>
public sealed class WinMmMidiOutputTransport : IMidiOutputTransport
{
    private nint handle;
    public WinMmMidiOutputTransport()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("A escuta MIDI precisa do sintetizador do Windows.");
        uint error = midiOutOpen(out handle, uint.MaxValue, 0, 0, 0); // MIDI_MAPPER, CALLBACK_NULL
        if (error != 0) throw Error(error, "Não foi possível abrir o sintetizador MIDI do Windows. Confira o dispositivo de áudio");
    }
    public void Send(uint message)
    {
        if (handle == 0) throw new InvalidOperationException("O dispositivo MIDI está fechado.");
        uint result = midiOutShortMsg(handle, message);
        if (result != 0) throw Error(result, "O sintetizador MIDI não recebeu a mensagem");
    }
    public void Reset()
    {
        if (handle == 0) return;
        uint result = midiOutReset(handle);
        if (result != 0) throw Error(result, "Não foi possível reiniciar o dispositivo MIDI");
    }
    public void Dispose()
    {
        nint owned = Interlocked.Exchange(ref handle, 0);
        if (owned == 0) return;
        midiOutReset(owned); midiOutClose(owned); GC.SuppressFinalize(this);
    }
    private static InvalidOperationException Error(uint code, string context)
    {
        var details = new StringBuilder(256);
        midiOutGetErrorTextW(code, details, (uint)details.Capacity);
        return new InvalidOperationException($"{context}. Código {code}: {details}");
    }
    [DllImport("winmm.dll")] private static extern uint midiOutOpen(out nint output, uint device, nuint callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiOutShortMsg(nint output, uint message);
    [DllImport("winmm.dll")] private static extern uint midiOutReset(nint output);
    [DllImport("winmm.dll")] private static extern uint midiOutClose(nint output);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiOutGetErrorTextW(uint error, StringBuilder text, uint characters);
}
