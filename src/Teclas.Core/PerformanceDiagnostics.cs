using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Teclas.Core;

// Only fixed vocabulary and numeric counts can enter diagnostic files. Never add paths,
// user text, exception messages, process arguments, titles, or source hashes here.
public enum PerformanceOperation
{
    AppOperation, AppImport, AppAnalyze, AppGenerate, AppPlayback, AppPdfBatch, AppScoreUpdate, AppBackup, AppOther,
    EngineAnalyze, EngineGenerate, EngineMeasureMap, EngineOther,
    ScoreExportNative, ScoreExportPreview, ScoreExportAudio, ScoreExportPdf,
    SyntheticMidiRead, SyntheticHarmonyAnalysis, SyntheticAudioConversion, SyntheticLayout, SyntheticMetadata,
    ScoreExportMidi
}
public enum PerformancePhase { Total, Process, Conversion }
public enum PerformanceProcessKind { App, Worker, MuseScore }
public enum PerformanceOutcome { Success, Failure, Cancelled, Timeout }
public enum PerformanceCount { Items, Notes, Chords, Pages, Bytes }

public sealed record PerformanceRecord(
    DateTimeOffset StartedUtc, PerformanceOperation Operation, PerformancePhase Phase,
    PerformanceProcessKind ProcessKind, PerformanceOutcome Outcome, double DurationMilliseconds,
    double? CpuMilliseconds, long? PeakWorkingSetBytes, int ResourceSamples,
    IReadOnlyDictionary<PerformanceCount, long> Counts);

public sealed record PerformanceReport(int SchemaVersion, DateTimeOffset GeneratedUtc,
    IReadOnlyList<PerformanceRecord> Records, int UnreadableFiles, bool StorageAvailable)
{
    public string CpuScope => "Process CPU delta from available samples during measurement; may be partial if the final sample is unavailable. App includes concurrent app work; excludes child processes.";
    public string MemoryScope => "Maximum observed process lifetime peak working set; not memory allocated by this operation. Null means unavailable.";
    public string DurationScope => "Wall-clock duration; overlapping operations and different processes must not be summed.";
}

/// <summary>Optional app-wide recorder. Explicit constructor injection keeps tests isolated.</summary>
public static class PerformanceDiagnostics
{
    private static LocalPerformanceRecorder? recorder;
    public static LocalPerformanceRecorder? Recorder
    {
        get => Volatile.Read(ref recorder);
        set => Volatile.Write(ref recorder, value);
    }
    public static PerformanceMeasurement Begin(PerformanceOperation operation,
        PerformanceProcessKind processKind = PerformanceProcessKind.App,
        PerformancePhase phase = PerformancePhase.Total) =>
        Recorder?.Begin(operation, processKind, phase) ?? PerformanceMeasurement.Disabled;
}

/// <summary>Best-effort, bounded local JSON storage. Diagnostic I/O never changes a musical result.</summary>
public sealed class LocalPerformanceRecorder
{
    public const int MaximumFileBytes = 1024 * 1024;
    private readonly string directory;
    private readonly int maxRecordsPerFile;
    private readonly int maxFiles;
    private readonly object gate = new();
    private long generation;
    private bool storageAvailable = true;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, MaxDepth = 12,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public LocalPerformanceRecorder(string directory, int maxRecordsPerFile = 128, int maxFiles = 4)
    {
        this.directory = directory;
        this.maxRecordsPerFile = Math.Clamp(maxRecordsPerFile, 1, 1024);
        this.maxFiles = Math.Clamp(maxFiles, 1, 8);
    }

    public PerformanceMeasurement Begin(PerformanceOperation operation,
        PerformanceProcessKind processKind = PerformanceProcessKind.App,
        PerformancePhase phase = PerformancePhase.Total)
    {
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(processKind) || !Enum.IsDefined(phase))
            return PerformanceMeasurement.Disabled;
        lock (gate) return new(this, generation, operation, processKind, phase);
    }

    public PerformanceReport ReadReport()
    {
        lock (gate)
        {
            List<PerformanceRecord> records = [];
            int unreadable = 0;
            for (int index = 0; index < maxFiles; index++)
            {
                try { records.AddRange(ReadFile(FilePath(index))); }
                catch (Exception) { unreadable++; }
            }
            return new(1, DateTimeOffset.UtcNow, records.OrderBy(record => record.StartedUtc).ToArray(),
                unreadable, storageAvailable && unreadable == 0);
        }
    }

    public bool ExportReport(string destination)
    {
        try { AtomicWrite(destination, JsonSerializer.SerializeToUtf8Bytes(ReadReport(), JsonOptions)); return true; }
        catch (Exception) { return false; }
    }

    public bool Clear()
    {
        lock (gate)
        {
            // Operations started before a clear may finish afterward; do not recreate their records.
            generation++;
            bool success = true;
            for (int index = 0; index < 8; index++)
                try { File.Delete(FilePath(index)); } catch (Exception) { success = false; }
            storageAvailable = success;
            return success;
        }
    }

    internal void Append(PerformanceRecord record, long startedGeneration)
    {
        lock (gate)
        {
            if (startedGeneration != generation || !Valid(record)) return;
            try
            {
                Directory.CreateDirectory(directory);
                PerformanceRecord[] current;
                try { current = ReadFile(FilePath(0)); } catch (Exception) { current = []; }
                var data = JsonSerializer.SerializeToUtf8Bytes(current.Append(record).ToArray(), JsonOptions);
                if (current.Length >= maxRecordsPerFile || data.Length > MaximumFileBytes)
                {
                    for (int index = maxFiles - 1; index > 0; index--)
                        if (File.Exists(FilePath(index - 1))) File.Move(FilePath(index - 1), FilePath(index), true);
                    data = JsonSerializer.SerializeToUtf8Bytes(new[] { record }, JsonOptions);
                }
                AtomicWrite(FilePath(0), data);
                storageAvailable = true;
            }
            catch (Exception) { storageAvailable = false; }
        }
    }

    private string FilePath(int index) => Path.Combine(directory, $"performance-{index}.json");

    private PerformanceRecord[] ReadFile(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException();
        // Bounded even if another process grows the file after the Length check.
        byte[] buffer = new byte[MaximumFileBytes + 1];
        int length = 0, count;
        while (length < buffer.Length && (count = stream.Read(buffer, length, buffer.Length - length)) != 0) length += count;
        if (length > MaximumFileBytes) throw new InvalidDataException();
        var records = JsonSerializer.Deserialize<PerformanceRecord[]>(buffer.AsSpan(0, length), JsonOptions);
        if (records is null || records.Length > maxRecordsPerFile || records.Any(record => !Valid(record))) throw new InvalidDataException();
        return records;
    }

    private static bool Valid(PerformanceRecord? record) => record is not null &&
        Enum.IsDefined(record.Operation) && Enum.IsDefined(record.Phase) && Enum.IsDefined(record.ProcessKind) && Enum.IsDefined(record.Outcome) &&
        record.StartedUtc.Year >= 2000 && record.StartedUtc.Year <= 2200 &&
        double.IsFinite(record.DurationMilliseconds) && record.DurationMilliseconds is >= 0 and <= 604800000 &&
        (record.CpuMilliseconds is null || double.IsFinite(record.CpuMilliseconds.Value) && record.CpuMilliseconds is >= 0 and <= 1e12) &&
        (record.PeakWorkingSetBytes is null or >= 0 and <= 1099511627776) && record.ResourceSamples is >= 0 and <= 10000000 &&
        record.Counts is not null && record.Counts.Count <= Enum.GetValues<PerformanceCount>().Length &&
        record.Counts.All(pair => Enum.IsDefined(pair.Key) && pair.Value is >= 0 and <= 1000000000000);

    private static void AtomicWrite(string path, byte[] data)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(data); stream.Flush(); }
            File.Move(temporary, fullPath, true);
        }
        finally { try { File.Delete(temporary); } catch (Exception) { } }
    }
}

/// <summary>Failure by default; callers mark success only after their validation passes.</summary>
public sealed class PerformanceMeasurement : IDisposable
{
    internal static PerformanceMeasurement Disabled { get; } = new();
    private readonly LocalPerformanceRecorder? recorder;
    private readonly long generation;
    private readonly PerformanceOperation operation;
    private readonly PerformancePhase phase;
    private readonly PerformanceProcessKind processKind;
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly object gate = new();
    private readonly Dictionary<PerformanceCount, long> counts = [];
    private Process? process;
    private bool ownsProcess, disposed;
    private Timer? sampler;
    private double cpuBaseline, cpuLast;
    private bool cpuAvailable, cpuBaselineSet;
    private long? peakWorkingSet;
    private int samples;
    private PerformanceOutcome outcome = PerformanceOutcome.Failure;

    private PerformanceMeasurement() { }
    internal PerformanceMeasurement(LocalPerformanceRecorder recorder, long generation,
        PerformanceOperation operation, PerformanceProcessKind processKind, PerformancePhase phase)
    {
        this.recorder = recorder; this.generation = generation; this.operation = operation;
        this.processKind = processKind; this.phase = phase;
        if (processKind == PerformanceProcessKind.App)
            try { process = Process.GetCurrentProcess(); ownsProcess = true; StartSampling(false); } catch (Exception) { }
    }

    /// <summary>Attach the newly started owned child process; no descendant CPU/memory is included.</summary>
    public void TrackProcess(Process value)
    {
        if (recorder is null) return;
        lock (gate)
        {
            if (disposed || process is not null || processKind == PerformanceProcessKind.App) return;
            process = value; StartSampling(true);
        }
    }

    private void StartSampling(bool newChild)
    {
        cpuBaselineSet = newChild;
        Sample();
        try { sampler = new Timer(_ => { lock (gate) { if (!disposed) Sample(); } }, null, 200, 200); }
        catch (Exception) { }
    }

    private void Sample()
    {
        if (process is null) return;
        bool sampled = false;
        try
        {
            cpuLast = process.TotalProcessorTime.TotalMilliseconds;
            if (!cpuBaselineSet) { cpuBaseline = cpuLast; cpuBaselineSet = true; }
            cpuAvailable = true; sampled = true;
        }
        catch (Exception) { }
        try
        {
            long value;
            if (OperatingSystem.IsWindows() && GetProcessMemoryInfo(process.SafeHandle, out var memory, (uint)Marshal.SizeOf<ProcessMemoryCounters>()))
                value = checked((long)memory.PeakWorkingSetSize);
            else { process.Refresh(); value = process.PeakWorkingSet64; }
            if (value > 0) { peakWorkingSet = Math.Max(peakWorkingSet ?? 0, value); sampled = true; }
        }
        catch (Exception) { }
        if (sampled) samples++;
    }

    // Process.PeakWorkingSet64 uses a process-list snapshot, which disappears after exit.
    // On Windows the still-open owned handle can retain the lifetime peak for short jobs.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, out ProcessMemoryCounters counters, uint size);

    public void Success() => SetOutcome(PerformanceOutcome.Success);
    public void Failure() => SetOutcome(PerformanceOutcome.Failure);
    public void Cancelled() => SetOutcome(PerformanceOutcome.Cancelled);
    public void TimedOut() => SetOutcome(PerformanceOutcome.Timeout);
    private void SetOutcome(PerformanceOutcome value) { if (recorder is not null) lock (gate) { if (!disposed) outcome = value; } }
    public void SetCount(PerformanceCount count, long value)
    {
        if (recorder is null || !Enum.IsDefined(count) || value is < 0 or > 1000000000000) return;
        lock (gate) { if (!disposed) counts[count] = value; }
    }

    public void Dispose()
    {
        if (recorder is null) return;
        PerformanceRecord record;
        lock (gate)
        {
            if (disposed) return;
            watch.Stop(); Sample(); disposed = true;
            sampler?.Dispose();
            if (ownsProcess) process?.Dispose();
            record = new(startedUtc, operation, phase, processKind, outcome, watch.Elapsed.TotalMilliseconds,
                cpuAvailable ? Math.Max(0, cpuLast - cpuBaseline) : null, peakWorkingSet, samples,
                new Dictionary<PerformanceCount, long>(counts));
        }
        recorder.Append(record, generation);
    }
}
