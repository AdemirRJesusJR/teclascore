using System.Collections;
using System.IO;
using System.Reflection;

namespace Teclas.App;

/// <summary>
/// Coalesced, off-thread saving of the session. Many quick changes become one write: each request (re)starts a short
/// delay (500 ms, never more than 2 s after the first pending change); then the owner's thread (the UI) takes a cheap
/// snapshot of the preferences and a background task serializes and writes it, one write at a time and in order.
/// <see cref="Flush"/> writes what is pending right away and waits for the write in progress: the app calls it when
/// closing and before anything that reads or replaces settings.json, and <see cref="Settings.Load"/> calls it for the
/// same file, so the file on disk is always current when someone reads it.
/// </summary>
public sealed class SettingsAutosave : IDisposable
{
    private static readonly List<WeakReference<SettingsAutosave>> Registry = [];
    private readonly object sync = new();
    private readonly Func<Settings?> capture;
    private readonly SynchronizationContext? owner;
    private readonly Action<Exception>? failed;
    private readonly Timer timer;
    private readonly TimeSpan delay, maximumDelay;
    private Task writing = Task.CompletedTask;
    private DateTime firstPendingUtc;
    private bool pending, disposed;
    private int requests, captures, writes;

    public string Path { get; }
    public bool IsPending { get { lock (sync) return pending; } }
    /// <summary>Requests received, snapshots taken and files written (diagnostics and tests).</summary>
    public int Requests => Volatile.Read(ref requests);
    public int Captures => Volatile.Read(ref captures);
    public int Writes => Volatile.Read(ref writes);

    /// <param name="capture">Runs on the owner's thread; returns an independent snapshot to write (see <see cref="Snapshot"/>), or null to skip.</param>
    /// <param name="owner">The UI context the snapshot is taken on; null runs it on the calling or timer thread (tests).</param>
    public SettingsAutosave(string path, Func<Settings?> capture, SynchronizationContext? owner = null, TimeSpan? delay = null,
        TimeSpan? maximumDelay = null, Action<Exception>? failed = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.owner = owner; this.failed = failed;
        this.delay = delay ?? TimeSpan.FromMilliseconds(500);
        this.maximumDelay = maximumDelay ?? TimeSpan.FromSeconds(2);
        timer = new Timer(_ => Elapsed(), null, Timeout.Infinite, Timeout.Infinite);
        lock (Registry) { Registry.RemoveAll(entry => !entry.TryGetTarget(out _)); Registry.Add(new(this)); }
    }

    /// <summary>Asks for a save soon; repeated requests within the delay become one write.</summary>
    public void Request()
    {
        lock (sync)
        {
            if (disposed) return;
            requests++;
            var now = DateTime.UtcNow;
            if (!pending) { pending = true; firstPendingUtc = now; }
            var due = delay;
            var latest = firstPendingUtc + maximumDelay - now;
            if (latest < due) due = latest < TimeSpan.Zero ? TimeSpan.Zero : latest;
            timer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Takes the pending snapshot now (on the owner's thread) and waits until everything is on disk. Throws the write error, if any.</summary>
    public void Flush()
    {
        bool take;
        lock (sync) { take = pending; pending = false; timer.Change(Timeout.Infinite, Timeout.Infinite); }
        if (take) OnOwner(CaptureAndQueue, synchronous: true);
        Task last; lock (sync) last = writing;
        last.GetAwaiter().GetResult();
    }

    /// <summary>Flushes every live autosave of this file (called by <see cref="Settings.Load"/> before reading it).</summary>
    public static void FlushFor(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); } catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        SettingsAutosave[] targets;
        lock (Registry) targets = Registry.Select(entry => entry.TryGetTarget(out var target) ? target : null).OfType<SettingsAutosave>()
            .Where(target => string.Equals(target.Path, full, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var target in targets)
        {
            try { target.Flush(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { target.failed?.Invoke(error); }
        }
    }

    private void Elapsed()
    {
        lock (sync) { if (!pending || disposed) return; pending = false; }
        OnOwner(CaptureAndQueue, synchronous: false);
    }

    private void OnOwner(Action action, bool synchronous)
    {
        if (owner is null || SynchronizationContext.Current == owner) { action(); return; }
        try
        {
            if (synchronous) owner.Send(_ => action(), null); else owner.Post(_ => action(), null);
        }
        catch (InvalidOperationException) { action(); } // The UI is shutting down: take the snapshot here.
    }

    private void CaptureAndQueue()
    {
        Settings? snapshot;
        try { snapshot = capture(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException) { failed?.Invoke(error); return; }
        if (snapshot is null) return;
        Interlocked.Increment(ref captures);
        lock (sync)
        {
            writing = writing.ContinueWith(_ =>
            {
                snapshot.Save(Path);
                Interlocked.Increment(ref writes);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            var current = writing;
            current.ContinueWith(task => Report(task.Exception!.GetBaseException()), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private void Report(Exception error)
    {
        if (failed is null) return;
        if (owner is null) failed(error);
        else try { owner.Post(_ => failed(error), null); } catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        lock (sync) { disposed = true; timer.Change(Timeout.Infinite, Timeout.Infinite); }
        timer.Dispose();
        lock (Registry) Registry.RemoveAll(entry => !entry.TryGetTarget(out var target) || ReferenceEquals(target, this));
    }

    /// <summary>
    /// An independent copy for writing on another thread, cheap enough for the UI thread: the preferences and each
    /// library card are copied member by member, and their lists and dictionaries are new collections, so adding or
    /// removing items on the UI thread cannot disturb the serialization. Deeper items (chords, saved versions, study
    /// records) are created whole and never changed afterwards, so they are shared. New preferences are copied
    /// automatically (no list of fields to keep up to date).
    /// </summary>
    public static Settings Snapshot(Settings settings) => (Settings)Copy(settings, depth: 0);

    /// <summary>Objects up to this depth are copied (0 = Settings, 1 = SavedMidi); collections are always new up to one level more.</summary>
    private const int CopiedDepth = 1;
    private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    private static object Copy(object value, int depth)
    {
        var clone = MemberwiseCloneMethod.Invoke(value, null)!;
        var properties = Properties.GetOrAdd(value.GetType(), type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && !IsImmutable(p.PropertyType)).ToArray());
        foreach (var property in properties)
        {
            if (property.GetValue(clone) is not { } member) continue;
            var copy = CopyMember(member, depth + 1);
            if (!ReferenceEquals(copy, member)) property.SetValue(clone, copy);
        }
        return clone;
    }

    private static object CopyMember(object member, int depth)
    {
        var type = member.GetType();
        if (depth > CopiedDepth + 1) return member;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(type, ((ICollection)member).Count)!;
            foreach (var item in (IEnumerable)member) list.Add(item is null || depth > CopiedDepth || IsImmutable(item.GetType()) ? item : Copy(item, depth));
            return list;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var comparer = type.GetProperty("Comparer")!.GetValue(member);
            var dictionary = (IDictionary)Activator.CreateInstance(type, comparer)!;
            foreach (DictionaryEntry entry in (IDictionary)member)
                dictionary[entry.Key] = entry.Value is null || depth > CopiedDepth || IsImmutable(entry.Value.GetType()) ? entry.Value : Copy(entry.Value, depth);
            return dictionary;
        }
        return depth > CopiedDepth || IsImmutable(type) ? member : Copy(member, depth);
    }

    private static bool IsImmutable(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid) ||
        Nullable.GetUnderlyingType(type) is not null || type.IsValueType;
}
