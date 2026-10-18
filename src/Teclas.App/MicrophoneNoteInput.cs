using System.Runtime.InteropServices;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Listens to the default microphone (WinMM, 44.1 kHz mono 16-bit) and reports the keys struck on an acoustic piano or
/// keyboard through <see cref="NoteOn"/>, from a background thread. Nothing is recorded or saved.
/// </summary>
public interface IDisposableNoteSource : IDisposable
{
    event Action<int>? NoteOn;
    /// <summary>The pitch heard in each window (fractional MIDI number), for singing; not raised in silence.</summary>
    event Action<double>? PitchHeard;
    /// <summary>Sound level 0..1 of the last window.</summary>
    double Level { get; }
}

/// <summary>Opens a microphone off the interface thread with a time limit (some Windows audio setups never answer).</summary>
public static class MicrophoneOpener
{
    public const string Blocked = "O microfone não respondeu. Confira em Configurações do Windows › Privacidade › Microfone se aplicativos da área de trabalho podem usá-lo, e se há um microfone padrão.";

    public static async Task<IDisposableNoteSource> OpenAsync(Func<IDisposableNoteSource> factory, TimeSpan timeout)
    {
        var opening = Task.Run(factory);
        if (await Task.WhenAny(opening, Task.Delay(timeout)) != opening)
        {
            _ = opening.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            throw new InvalidOperationException(Blocked);
        }
        return await opening;
    }
}

public sealed class MicrophoneNoteInput : IDisposableNoteSource
{
    private const int Rate = 44100, BufferSamples = 1024, Buffers = 6;
    private const uint WaveMapper = unchecked((uint)-1), CallbackEvent = 0x50000, HeaderDone = 1;
    private readonly nint handle;
    private readonly AutoResetEvent ready = new(false);
    private readonly GCHandle[] pins = new GCHandle[Buffers];
    private readonly nint[] headers = new nint[Buffers];
    private readonly short[][] data = new short[Buffers][];
    private readonly Thread thread;
    private readonly MicrophoneNoteDetector detector = new(Rate);
    private volatile bool running = true;
    public event Action<int>? NoteOn;
    public event Action<double>? PitchHeard;
    public double Level => detector.Level;

    public static bool Available => waveInGetNumDevs() > 0;

    public MicrophoneNoteInput()
    {
        var format = new WaveFormat { Tag = 1, Channels = 1, SamplesPerSecond = Rate, AverageBytesPerSecond = Rate * 2, BlockAlign = 2, BitsPerSample = 16 };
        uint error = waveInOpen(out handle, WaveMapper, ref format, ready.SafeWaitHandle.DangerousGetHandle(), 0, CallbackEvent);
        if (error != 0) throw new InvalidOperationException($"Não foi possível abrir o microfone (código {error}). Confira em Configurações do Windows › Privacidade › Microfone se aplicativos da área de trabalho podem usá-lo.");
        int size = Marshal.SizeOf<WaveHeader>();
        for (int i = 0; i < Buffers; i++)
        {
            data[i] = new short[BufferSamples]; pins[i] = GCHandle.Alloc(data[i], GCHandleType.Pinned);
            headers[i] = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new WaveHeader { Data = pins[i].AddrOfPinnedObject(), BufferLength = BufferSamples * 2 }, headers[i], false);
            waveInPrepareHeader(handle, headers[i], (uint)size); waveInAddBuffer(handle, headers[i], (uint)size);
        }
        error = waveInStart(handle);
        if (error != 0) { Release(); throw new InvalidOperationException($"O microfone não começou a ouvir (código {error})."); }
        thread = new Thread(Loop) { IsBackground = true, Name = "Microfone da prática" };
        thread.Start();
    }

    private void Loop()
    {
        int size = Marshal.SizeOf<WaveHeader>();
        var samples = new float[BufferSamples];
        while (running)
        {
            ready.WaitOne(200);
            for (int i = 0; i < Buffers && running; i++)
            {
                var header = Marshal.PtrToStructure<WaveHeader>(headers[i]);
                if ((header.Flags & HeaderDone) == 0) continue;
                int count = (int)Math.Min(BufferSamples, header.BytesRecorded / 2);
                for (int s = 0; s < count; s++) samples[s] = data[i][s] / 32768f;
                foreach (int pitch in detector.Feed(samples.AsSpan(0, count))) NoteOn?.Invoke(pitch);
                if (detector.Pitch is { } heard) PitchHeard?.Invoke(heard);
                header.Flags &= ~HeaderDone; header.BytesRecorded = 0;
                Marshal.StructureToPtr(header, headers[i], false);
                waveInAddBuffer(handle, headers[i], (uint)size);
            }
        }
    }

    public void Dispose()
    {
        if (!running) return;
        running = false; ready.Set();
        thread.Join(1000);
        waveInStop(handle); waveInReset(handle);
        Release();
    }

    private void Release()
    {
        int size = Marshal.SizeOf<WaveHeader>();
        for (int i = 0; i < Buffers; i++)
        {
            if (headers[i] != 0) { waveInUnprepareHeader(handle, headers[i], (uint)size); Marshal.FreeHGlobal(headers[i]); headers[i] = 0; }
            if (pins[i].IsAllocated) pins[i].Free();
        }
        waveInClose(handle);
        ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { public ushort Tag, Channels; public uint SamplesPerSecond, AverageBytesPerSecond; public ushort BlockAlign, BitsPerSample, Size; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader { public nint Data; public uint BufferLength, BytesRecorded; public nint User; public uint Flags, Loops; public nint Next, Reserved; }

    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out nint handle, uint device, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveInStop(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveInReset(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveInClose(nint handle);
}
