using System.Runtime.InteropServices;

namespace Teclas.Core;

/// <summary>
/// How many conversions (Python + MuseScore) run at the same time. "Automático" (0) uses the processor threads and the
/// free memory: one conversion for every four threads and for every 2 GB free, up to six. A real generation (2 pages,
/// analysis + generation, measured on 08/11/2026 sampling the process tree every 50 ms) peaked at about 290 MB:
/// MuseScore 4.7 rendering and exporting about 250 MB, the Python worker about 70 MB (the analysis alone about 40 MB).
/// 2 GB per conversion is a wide margin for long songs, MuseScore spikes, the app, the audio and the rest of Windows;
/// the processor threads are the usual limit. The user can choose 1 to 8 in Configurações.
/// </summary>
public static class ConversionParallelism
{
    public const int Automatic = 0, Maximum = 8, AutomaticCap = 6;
    /// <summary>Free memory reserved per conversion when choosing automatically.</summary>
    public const double GigabytesPerConversion = 2.0;
    /// <summary>The measured peak of one generation (Python worker + MuseScore), shown in Configurações.</summary>
    public const string MeasuredPeakText = "0,3 GB";

    /// <summary>min(threads / 4, free GB / 2, 6), at least 1.</summary>
    public static int Recommended(int processorCount, double availableMemoryGb)
    {
        int byThreads = Math.Max(0, processorCount) / 4;
        int byMemory = double.IsFinite(availableMemoryGb) ? (int)Math.Floor(Math.Max(0, availableMemoryGb) / GigabytesPerConversion) : 1;
        return Math.Clamp(Math.Min(Math.Min(byThreads, byMemory), AutomaticCap), 1, AutomaticCap);
    }

    /// <summary>Normalizes a saved preference: 0 (automatic) or 1 to 8.</summary>
    public static int Normalize(int setting) => setting is >= 1 and <= Maximum ? setting : Automatic;

    /// <summary>The number of simultaneous conversions for a preference (0 = automatic, measured now).</summary>
    public static int Resolve(int setting) => Normalize(setting) is var value and > 0 ? value : Recommended(Environment.ProcessorCount, AvailableMemoryGb());

    public static string Describe(int setting) => Normalize(setting) == Automatic
        ? $"Automático · {Resolve(Automatic)} agora ({Environment.ProcessorCount} threads, {AvailableMemoryGb():0.#} GB livres)"
        : setting == 1 ? "1 de cada vez" : $"{setting} ao mesmo tempo";

    /// <summary>Free physical memory in GB (Windows); elsewhere what the runtime reports as available.</summary>
    public static double AvailableMemoryGb()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref status)) return status.AvailablePhysical / (1024.0 * 1024 * 1024);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        var info = GC.GetGCMemoryInfo();
        return Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes) / (1024.0 * 1024 * 1024);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
