using System.Diagnostics;
using Teclas.Core;

internal static class PreparationCacheChecks
{
    internal static async Task<object> Run(Action<bool, string> check)
    {
        var document = MidiFixtures.FormatZero();
        var original = document.Notes.ToArray();
        var cache = new MidiPreparationCache();
        int fullBuilds = 0, sourceBuilds = 0;
        Task<MidiAuditionSequence> Full(CancellationToken token)
        { fullBuilds++; return Task.FromResult(MidiAuditionSequence.CreateFull(document, token)); }
        var first = await cache.GetOrCreateAsync(document, null, Full, default);
        var again = await cache.GetOrCreateAsync(document, null, Full, default);
        check(ReferenceEquals(first, again) && fullBuilds == 1, "midi-cache-repeated-full-playback-prepares-once");
        var source = document.Sources[0].Key;
        Task<MidiAuditionSequence> Source(CancellationToken _)
        { sourceBuilds++; return Task.FromResult(MidiAuditionSequence.Create(document, source)); }
        var isolated = await cache.GetOrCreateAsync(document, source, Source, default);
        var isolatedAgain = await cache.GetOrCreateAsync(document, source with { }, Source, default);
        check(ReferenceEquals(isolated, isolatedAgain) && !ReferenceEquals(first, isolated) && sourceBuilds == 1,
            "midi-cache-separates-full-file-from-equivalent-isolated-source-keys");
        await cache.GetOrCreateAsync(document with { }, null, Full, default);
        check(fullBuilds == 2, "midi-cache-reread-document-is-not-an-old-timeline-even-with-same-hash");
        cache.Remove(document);
        await cache.GetOrCreateAsync(document, null, Full, default);
        await cache.GetOrCreateAsync(document, source, Source, default);
        check(fullBuilds == 3 && sourceBuilds == 2, "midi-cache-removal-invalidates-all-sources-of-the-document");
        cache.Clear(); await cache.GetOrCreateAsync(document, null, Full, default);
        check(fullBuilds == 4, "midi-cache-clear-releases-prepared-timelines");

        var two = document with { Path = "two.mid" }; var three = document with { Path = "three.mid" };
        var bounded = new MidiPreparationCache(maximumEntries: 2);
        int lruBuilds = 0;
        Task<MidiAuditionSequence> Build(CancellationToken _) { lruBuilds++; return Task.FromResult(first); }
        await bounded.GetOrCreateAsync(document, null, Build, default);
        await bounded.GetOrCreateAsync(two, null, Build, default);
        await bounded.GetOrCreateAsync(document, null, Build, default);
        await bounded.GetOrCreateAsync(three, null, Build, default);
        await bounded.GetOrCreateAsync(document, null, Build, default);
        check(lruBuilds == 3, "midi-cache-bounded-retention-keeps-most-recently-used-song");
        await bounded.GetOrCreateAsync(two, null, Build, default);
        check(lruBuilds == 4, "midi-cache-evicts-least-recently-used-song");
        var limitedEvents = new MidiPreparationCache(maximumEvents: first.Events.Count);
        int before = lruBuilds;
        await limitedEvents.GetOrCreateAsync(document, null, Build, default);
        await limitedEvents.GetOrCreateAsync(two, null, Build, default);
        await limitedEvents.GetOrCreateAsync(document, null, Build, default);
        check(lruBuilds == before + 3, "midi-cache-enforces-total-event-budget");
        var oversized = new MidiPreparationCache(maximumEvents: first.Events.Count - 1);
        before = lruBuilds;
        await oversized.GetOrCreateAsync(document, null, Build, default);
        await oversized.GetOrCreateAsync(document, null, Build, default);
        check(lruBuilds == before + 2, "midi-cache-plays-large-file-without-retaining-oversized-timeline");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        before = fullBuilds;
        try { await cache.GetOrCreateAsync(document, null, Full, cancelled.Token); throw new Exception("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        check(fullBuilds == before, "midi-cache-hit-honors-cancelled-request");
        var pendingCache = new MidiPreparationCache();
        using var pendingCancellation = new CancellationTokenSource();
        var deferred = new TaskCompletionSource<MidiAuditionSequence>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = pendingCache.GetOrCreateAsync(document, null, _ => deferred.Task, pendingCancellation.Token);
        pendingCancellation.Cancel(); deferred.SetResult(first);
        try { await pending; throw new Exception("Late cancellation was ignored."); } catch (OperationCanceledException) { }
        before = fullBuilds;
        await pendingCache.GetOrCreateAsync(document, null, Full, default);
        check(fullBuilds == before + 1, "midi-cache-does-not-retain-cancelled-preparation");
        pendingCache.Clear();
        deferred = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = pendingCache.GetOrCreateAsync(document, null, _ => deferred.Task, default);
        pendingCache.Remove(document); deferred.SetResult(first); await pending;
        before = fullBuilds; await pendingCache.GetOrCreateAsync(document, null, Full, default);
        check(fullBuilds == before + 1, "midi-cache-removal-rejects-late-result-insertion");
        var failedCache = new MidiPreparationCache();
        try { await failedCache.GetOrCreateAsync(document, null, _ => Task.FromException<MidiAuditionSequence>(new IOException("Synthetic failure")), default); }
        catch (IOException) { }
        before = fullBuilds; await failedCache.GetOrCreateAsync(document, null, Full, default);
        check(fullBuilds == before + 1, "midi-cache-failed-preparation-can-be-retried");
        check(document.Notes.SequenceEqual(original) && first.Events.SequenceEqual(again.Events), "midi-cache-preserves-source-and-playback-content");

        var notes = Enumerable.Range(0, 10000).Select(i => new MidiNote(0, 1, 0, 48 + i % 24, 80, i * 120, i * 120 + 100)).ToArray();
        var sample = new MidiDocument("synthetic-measurement.mid", "synthetic", 0, 480, notes, [], []) { EndTick = notes[^1].End };
        var timingCache = new MidiPreparationCache(); int preparations = 0;
        Task<MidiAuditionSequence> Timed(CancellationToken token) { preparations++; return Task.FromResult(MidiAuditionSequence.CreateFull(sample, token)); }
        var timer = Stopwatch.StartNew(); await timingCache.GetOrCreateAsync(sample, null, Timed, default); double coldMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart(); await timingCache.GetOrCreateAsync(sample, null, Timed, default); double warmMs = timer.Elapsed.TotalMilliseconds;
        check(preparations == 1, "midi-cache-10000-note-repeat-skips-sequence-construction");
        return new { notes = notes.Length, preparations, coldMilliseconds = coldMs, cachedMilliseconds = warmMs,
            note = "Synthetic timeline preparation only; excludes device startup. Timing is observational, not a performance threshold." };
    }
}
