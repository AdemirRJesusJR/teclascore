using System.Diagnostics;
using System.Security.Cryptography;
using Teclas.Core;

internal static class ScorePlaybackMapCacheChecks
{
    internal static async Task<object> Run(string work, Action<bool, string> check)
    {
        string directory = Path.Combine(work, "map-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "partitura com acento ç.mscz"), renderer = Path.Combine(directory, "renderer.exe");
        await File.WriteAllTextAsync(source, "AAAA"); await File.WriteAllTextAsync(renderer, "synthetic renderer; never executed");
        int factoryCalls = 0;
        string Hash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        async Task<ScorePlaybackMap> Prepare(double speed, CancellationToken token)
        {
            int number = Interlocked.Increment(ref factoryCalls);
            await Task.Delay(40, token); // Known simulated preparation cost, not a MuseScore timing claim.
            string page = Path.Combine(directory, $"page-{number}.png");
            await File.WriteAllBytesAsync(page, new byte[32], token);
            var occurrence = new ScorePlaybackOccurrence(0, 1, 0, 1, null);
            return new(source, Hash(), speed, [page], [new(1, new(1, 0, 0, 1, 1))], [], [occurrence], [], true)
                { MeasureTimeline = [occurrence] };
        }
        Task<ScorePlaybackMap> Get(ScorePlaybackMapCache cache, double speed = 1, CancellationToken token = default) =>
            cache.GetOrPrepareAsync(source, renderer, speed, request => Prepare(speed, request), null, token);
        static async Task<bool> Throws<T>(Func<Task> action) where T : Exception
        { try { await action(); return false; } catch (T) { return true; } }

        var cache = new ScorePlaybackMapCache();
        var watch = Stopwatch.StartNew(); var first = await Get(cache); watch.Stop();
        double coldMilliseconds = watch.Elapsed.TotalMilliseconds;
        var hitMilliseconds = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            watch.Restart(); var hit = await Get(cache); watch.Stop(); hitMilliseconds.Add(watch.Elapsed.TotalMilliseconds);
            check(ReferenceEquals(first, hit), "Map cache reuses the exact verified timeline and page bindings");
        }
        check(factoryCalls == 1 && cache.Preparations == 1 && cache.CacheHits == 5,
            "Six requests require only one simulated export, without absolute timing gates");

        // Content must invalidate even if size and timestamp were deliberately preserved.
        DateTime timestamp = File.GetLastWriteTimeUtc(source);
        await File.WriteAllTextAsync(source, "BBBB"); File.SetLastWriteTimeUtc(source, timestamp);
        var changed = await Get(cache);
        check(factoryCalls == 2 && changed.SourceSha256 != first.SourceSha256, "Changed source contents invalidate a same-size same-date cache entry");
        var slow = await Get(cache, .5);
        check(factoryCalls == 3 && slow.Speed == .5, "Different study speeds use separate verified maps");
        await File.AppendAllTextAsync(renderer, " updated");
        var updatedRenderer = await Get(cache);
        check(factoryCalls == 4 && !ReferenceEquals(changed, updatedRenderer), "Renderer changes invalidate the map cache");
        await File.AppendAllTextAsync(updatedRenderer.PreviewPages[0], "changed page");
        var changedPage = await Get(cache);
        check(factoryCalls == 5 && !ReferenceEquals(changedPage, updatedRenderer), "A modified preview page is regenerated");
        File.Delete(changedPage.PreviewPages[0]);
        var missingPage = await Get(cache);
        check(factoryCalls == 6 && File.Exists(missingPage.PreviewPages[0]), "A missing preview page is regenerated");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); int before = factoryCalls;
            check(await Throws<OperationCanceledException>(() => Get(cache, token: cancelled.Token)) && factoryCalls == before,
                "Cancellation is honored even for a prepared cache entry");
        }
        int beforeFailure = factoryCalls;
        var failing = new ScorePlaybackMapCache();
        check(await Throws<IOException>(() => failing.GetOrPrepareAsync(source, renderer, 1,
            _ => throw new IOException("Simulated preparation failure"), null, CancellationToken.None)), "Failed preparation propagates without caching a map");
        await Get(failing);
        check(factoryCalls == beforeFailure + 1 && failing.Preparations == 2 && failing.CacheHits == 0,
            "Preparation retries normally after failure");
        var cancelledPreparation = new ScorePlaybackMapCache();
        using (var cancellation = new CancellationTokenSource())
        {
            check(await Throws<OperationCanceledException>(() => cancelledPreparation.GetOrPrepareAsync(source, renderer, 1, async token =>
            { var map = await Prepare(1, token); cancellation.Cancel(); return map; }, null, cancellation.Token)),
                "Cancellation after the factory returns cannot publish a cache entry");
        }
        int beforeRetry = factoryCalls; await Get(cancelledPreparation);
        check(factoryCalls == beforeRetry + 1 && cancelledPreparation.CacheHits == 0, "Cancelled preparation is retried instead of reused");

        var wrongMap = new ScorePlaybackMapCache();
        check(await Throws<IOException>(() => wrongMap.GetOrPrepareAsync(source, renderer, 1,
            async token => (await Prepare(1, token)) with { SourceSha256 = new string('0', 64) }, null, CancellationToken.None)),
            "A factory result for another score hash is rejected");
        check(await Throws<IOException>(() => wrongMap.GetOrPrepareAsync(source, renderer, 1, async token =>
        { var map = await Prepare(1, token); await File.WriteAllTextAsync(source, "CCCC", token); return map; }, null, CancellationToken.None)),
            "A source changed during preparation cannot enter the cache");

        var bounded = new ScorePlaybackMapCache(capacity: 2);
        var normal = await Get(bounded); await Get(bounded, .5); await Get(bounded); await Get(bounded, .75);
        int beforeEviction = factoryCalls; await Get(bounded);
        check(factoryCalls == beforeEviction && bounded.CacheHits == 2, "Recently used entries survive LRU eviction");
        await Get(bounded, .5);
        check(factoryCalls == beforeEviction + 1, "Least recently used map is evicted at the entry limit");
        check(File.Exists(normal.PreviewPages[0]), "Eviction never deletes pages still used by a viewer");
        var large = new ScorePlaybackMapCache(maximumElements: 2);
        int beforeLarge = factoryCalls; await Get(large); await Get(large);
        check(factoryCalls == beforeLarge + 2 && large.CacheHits == 0, "Maps above the element budget are returned without retention");

        var concurrent = new ScorePlaybackMapCache();
        int beforeConcurrent = factoryCalls;
        var requests = await Task.WhenAll(Get(concurrent), Get(concurrent), Get(concurrent));
        check(factoryCalls == beforeConcurrent + 1 && requests.All(map => ReferenceEquals(map, requests[0])) && concurrent.CacheHits == 2,
            "Concurrent matching requests prepare only one map");
        int beforeRendererMissing = factoryCalls; File.Delete(renderer);
        check(await Throws<FileNotFoundException>(() => Get(concurrent)) && factoryCalls == beforeRendererMissing,
            "A removed renderer cannot silently reuse an obsolete entry");
        check(await Throws<ArgumentOutOfRangeException>(() => Get(concurrent, .33)), "Unsupported speeds never enter the map cache");
        check(await File.ReadAllTextAsync(source) == "CCCC", "Cache operations never alter the original score");

        return new
        {
            kind = "synthetic-map-cache", simulatedPreparationDelayMilliseconds = 40,
            requests = 6, factoryCallsWithoutCache = 6, factoryCallsWithCache = 1, actualMuseScoreProcesses = 0,
            coldMilliseconds, hitMilliseconds,
            medianHitMilliseconds = hitMilliseconds.Order().ElementAt(hitMilliseconds.Count / 2),
            note = "Synthetic timings include hashing/file validation. Counts are deterministic; latency is informative, not a physical audio or MuseScore benchmark."
        };
    }
}
