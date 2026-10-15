namespace Teclas.Core;

/// <summary>Small session cache of immutable playback timelines. A re-read document is a new owner.</summary>
public sealed class MidiPreparationCache(int maximumEntries = 8, int maximumEvents = 250000)
{
    private sealed record Entry(MidiDocument Document, SourceKey? Source, MidiAuditionSequence Sequence);
    private readonly object gate = new();
    private readonly LinkedList<Entry> entries = new();
    private long eventCount, revision;

    public async Task<MidiAuditionSequence> GetOrCreateAsync(MidiDocument document, SourceKey? source,
        Func<CancellationToken, Task<MidiAuditionSequence>> prepare, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(prepare);
        if (maximumEntries < 1 || maximumEvents < 1) throw new InvalidOperationException("Os limites do cache precisam ser positivos.");
        token.ThrowIfCancellationRequested();
        long startedRevision;
        lock (gate)
        {
            var existing = Find(document, source);
            if (existing is not null)
            {
                entries.Remove(existing); entries.AddFirst(existing);
                return existing.Value.Sequence;
            }
            startedRevision = revision;
        }
        var prepared = await prepare(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A preparação não retornou uma sequência MIDI.");
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            // Removal/closing must not reinsert a result that completed after invalidation.
            if (startedRevision != revision || prepared.Events.Count > maximumEvents) return prepared;
            if (Find(document, source) is { } previous) Remove(previous);
            entries.AddFirst(new Entry(document, source, prepared)); eventCount += prepared.Events.Count;
            while (entries.Count > maximumEntries || eventCount > maximumEvents) Remove(entries.Last!);
        }
        return prepared;
    }

    public void Remove(MidiDocument document)
    {
        lock (gate)
        {
            revision++;
            for (var node = entries.First; node is not null;)
            {
                var next = node.Next;
                if (ReferenceEquals(node.Value.Document, document)) Remove(node);
                node = next;
            }
        }
    }

    public void Clear()
    {
        lock (gate) { revision++; entries.Clear(); eventCount = 0; }
    }

    private LinkedListNode<Entry>? Find(MidiDocument document, SourceKey? source)
    {
        for (var node = entries.First; node is not null; node = node.Next)
            if (ReferenceEquals(node.Value.Document, document) && node.Value.Source == source) return node;
        return null;
    }
    private void Remove(LinkedListNode<Entry> node)
    { eventCount -= node.Value.Sequence.Events.Count; entries.Remove(node); }
}
