using System.Security.Cryptography;

namespace Teclas.Core;

/// <summary>A bounded, in-session cache of verified maps. It never owns or deletes source/page files.</summary>
public sealed class ScorePlaybackMapCache
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly LinkedList<Entry> entries = new();
    private readonly int capacity, maximumElements;
    private long retainedElements, cacheHits, preparations;

    public ScorePlaybackMapCache(int capacity = 4, int maximumElements = 250_000)
    {
        if (capacity is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumElements < 1) throw new ArgumentOutOfRangeException(nameof(maximumElements));
        this.capacity = capacity; this.maximumElements = maximumElements;
    }

    /// <summary>Forgets every prepared map (their page files may be about to be deleted).</summary>
    public void Clear()
    {
        gate.Wait();
        try { entries.Clear(); retainedElements = 0; }
        finally { gate.Release(); }
    }

    public long CacheHits => Interlocked.Read(ref cacheHits);
    public long Preparations => Interlocked.Read(ref preparations);

    public async Task<ScorePlaybackMap> GetOrPrepareAsync(string scorePath, string museScorePath, double speed,
        Func<CancellationToken, Task<ScorePlaybackMap>> prepare, IProgress<string>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        if (!ScoreStudyAudio.IsSupportedSpeed(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            string source = Path.GetFullPath(scorePath);
            var key = new Key(source.ToUpperInvariant(), await HashAsync(source, token).ConfigureAwait(false), speed,
                RendererIdentity(museScorePath));
            var node = entries.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Key == key)
                {
                    if (PagesMatch(node.Value.Pages))
                    {
                        token.ThrowIfCancellationRequested();
                        entries.Remove(node); entries.AddFirst(node);
                        Interlocked.Increment(ref cacheHits);
                        progress?.Report("Acompanhamento já preparado encontrado. Iniciando a reprodução…");
                        return node.Value.Map;
                    }
                    Remove(node);
                    break;
                }
                node = next;
            }

            Interlocked.Increment(ref preparations);
            var map = await prepare(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetFullPath(map.SourcePath), source, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(map.SourceSha256, key.SourceHash, StringComparison.OrdinalIgnoreCase) || map.Speed != speed
                || await HashAsync(source, token).ConfigureAwait(false) != key.SourceHash
                || RendererIdentity(museScorePath) != key.Renderer)
                throw new IOException("A partitura ou o MuseScore mudou durante a preparação do acompanhamento. Tente novamente.");
            var pages = map.PreviewPages.Select(PageIdentity).ToArray();
            if (pages.Length == 0) throw new InvalidDataException("O acompanhamento não contém páginas verificáveis.");
            token.ThrowIfCancellationRequested();
            long elements = (long)map.Measures.Count + map.Segments.Count + map.Timeline.Count + map.MeasureTimeline.Count + pages.Length;
            if (elements <= maximumElements)
            {
                while (entries.Count >= capacity || retainedElements + elements > maximumElements) Remove(entries.Last!);
                entries.AddFirst(new Entry(key, map, pages, elements)); retainedElements += elements;
            }
            return map;
        }
        finally { gate.Release(); }
    }

    private void Remove(LinkedListNode<Entry> node)
    { retainedElements -= node.Value.Elements; entries.Remove(node); }

    private static bool PagesMatch(PageStamp[] pages)
    {
        try { return pages.All(page => PageIdentity(page.Path) == page); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    private static PageStamp PageIdentity(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < 24) throw new IOException("Uma página do acompanhamento não está mais disponível.");
        return new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
    }
    private static string RendererIdentity(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Localize o MuseScore para preparar o acompanhamento da partitura.", path);
        return file.FullName.ToUpperInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
    private sealed record Key(string Source, string SourceHash, double Speed, string Renderer);
    private sealed record PageStamp(string Path, long Length, long LastWriteTicks);
    private sealed record Entry(Key Key, ScorePlaybackMap Map, PageStamp[] Pages, long Elements);
}
