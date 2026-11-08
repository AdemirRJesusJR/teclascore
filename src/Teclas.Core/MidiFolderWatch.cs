using System.Text.Json;

namespace Teclas.Core;

/// <summary>
/// Watches a MIDI folder for new or renamed .mid/.midi/.kar files. A file is delivered only after it stopped changing
/// for the quiet period and can be opened while nobody is writing it, so copies and downloads in progress are never
/// read half-written. Files are only referenced, never opened for writing, copied or moved.
/// </summary>
public sealed class MidiFolderWatcher : IDisposable
{
    private sealed class Pending
    {
        public long Size = -1;
        public DateTime WriteUtc;
        public DateTime FirstSeenUtc, LastChangeUtc;
    }

    private readonly FileSystemWatcher watcher;
    private readonly Timer timer;
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<IReadOnlyList<string>> filesReady;
    private readonly TimeSpan quiet, giveUp;
    private bool disposed;

    public string Folder { get; }
    public bool IncludeSubfolders { get; }

    /// <param name="filesReady">Called on a pool thread with the files that finished arriving.</param>
    /// <param name="watchFailed">Called when Windows lost events (too many at once) or the folder went away; rescan then.</param>
    /// <param name="quietPeriod">How long a file must stay unchanged before it is checked (2 s by default).</param>
    /// <param name="giveUpAfter">A file still changing or locked after this long is dropped (10 min by default).</param>
    public MidiFolderWatcher(string folder, bool includeSubfolders, Action<IReadOnlyList<string>> filesReady,
        Action<Exception>? watchFailed = null, TimeSpan? quietPeriod = null, TimeSpan? giveUpAfter = null)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("Escolha uma pasta com caminho completo.", nameof(folder));
        Folder = Path.GetFullPath(folder);
        if (!Directory.Exists(Folder)) throw new DirectoryNotFoundException("A pasta escolhida não está disponível: " + Folder);
        IncludeSubfolders = includeSubfolders;
        this.filesReady = filesReady;
        quiet = quietPeriod is { } q && q > TimeSpan.Zero ? q : TimeSpan.FromSeconds(2);
        giveUp = giveUpAfter is { } g && g > TimeSpan.Zero ? g : TimeSpan.FromMinutes(10);
        timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(Folder)
        {
            IncludeSubdirectories = includeSubfolders, InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Created += (_, e) => Enqueue(e.FullPath);
        watcher.Changed += (_, e) => Enqueue(e.FullPath);
        watcher.Renamed += (_, e) => Enqueue(e.FullPath);
        watcher.Error += (_, e) => { if (!disposed) watchFailed?.Invoke(e.GetException()); };
        watcher.EnableRaisingEvents = true;
    }

    /// <summary>Number of files still waiting to finish arriving.</summary>
    public int PendingCount { get { lock (gate) return pending.Count; } }

    /// <summary>Adds a file found by a scan (for example one that arrived while the app was closed); it goes through the same wait.</summary>
    public void Enqueue(string path)
    {
        if (!MidiFolderScanner.IsMidi(path)) return;
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        lock (gate)
        {
            if (disposed) return;
            var now = DateTime.UtcNow;
            if (!pending.TryGetValue(full, out var item)) pending[full] = item = new Pending { FirstSeenUtc = now };
            item.LastChangeUtc = now;
            timer.Change(quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Tick()
    {
        var done = new List<string>();
        lock (gate)
        {
            if (disposed) return;
            var now = DateTime.UtcNow;
            foreach (var (path, item) in pending.ToArray())
            {
                if (now - item.LastChangeUtc < quiet) continue;
                var probe = Probe(path);
                if (probe is null) { pending.Remove(path); continue; }
                var (size, written, unlocked) = probe.Value;
                if (size != item.Size || written != item.WriteUtc || !unlocked || size == 0)
                {
                    if (now - item.FirstSeenUtc > giveUp) { pending.Remove(path); continue; }
                    // Still arriving (or first look): check again after another half quiet period.
                    item.Size = size; item.WriteUtc = written;
                    item.LastChangeUtc = now - quiet / 2;
                    continue;
                }
                pending.Remove(path);
                done.Add(path);
            }
            if (pending.Count > 0) timer.Change(TimeSpan.FromTicks(Math.Max(quiet.Ticks / 2, TimeSpan.TicksPerMillisecond * 50)), Timeout.InfiniteTimeSpan);
        }
        if (done.Count > 0) filesReady(done);
    }

    /// <summary>Size, last write and whether the file can be read without anyone writing it; null when gone or hidden.</summary>
    private static (long Size, DateTime WriteUtc, bool Unlocked)? Probe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory)) != 0) return null;
            bool unlocked;
            try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); unlocked = true; }
            catch (IOException) { unlocked = false; }
            info.Refresh();
            return (info.Length, info.LastWriteTimeUtc, unlocked);
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return (0, default, false); }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; pending.Clear(); }
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        timer.Dispose();
    }
}

/// <summary>
/// The MIDIs the watched folder already had (saved when watching starts and when the app closes), so opening the app
/// brings in only the files that arrived meanwhile, and the files that were already there are never imported in bulk.
/// </summary>
public sealed class MidiFolderWatchSnapshot
{
    public string Folder { get; set; } = "";
    public bool IncludeSubfolders { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public List<string> Files { get; set; } = [];

    public bool Matches(string folder, bool includeSubfolders)
    {
        static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try { return IncludeSubfolders == includeSubfolders && string.Equals(Normalize(Folder), Normalize(folder), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Files of the scan that the snapshot did not know.</summary>
    public IReadOnlyList<string> NewFiles(MidiFolderScan scan)
    {
        var known = new HashSet<string>(Files, StringComparer.OrdinalIgnoreCase);
        return scan.Entries.Select(e => e.FullPath).Where(path => !known.Contains(path)).ToArray();
    }

    public static MidiFolderWatchSnapshot? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var snapshot = JsonSerializer.Deserialize<MidiFolderWatchSnapshot>(File.ReadAllText(path));
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Folder)) return null;
            snapshot.Files = (snapshot.Files ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    public void Save(string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
