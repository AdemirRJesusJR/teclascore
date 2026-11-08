namespace Teclas.Core;

public sealed partial class MidiAuditionSequence
{
    /// <summary>The sources heard together, in the order given (empty for single-source or full-document sequences).</summary>
    public IReadOnlyList<SourceKey> MixedSources { get; private init; } = [];
    /// <summary>For a mix: where each source's first note falls on this sequence's timeline (same order as MixedSources).</summary>
    public IReadOnlyList<TimeSpan> SourceStarts { get; private init; } = [];
    /// <summary>For a mix: when each source has finished sounding on this sequence's timeline.</summary>
    public IReadOnlyList<TimeSpan> SourceEnds { get; private init; } = [];
    /// <summary>A live mix tags every event with its source index, so the player can mute and unmute sources while it plays.</summary>
    public bool IsLiveMix { get; private init; }

    /// <summary>
    /// Several sources on one timeline, starting at the earliest first note among them. A source sharing a
    /// melodic channel with another selected source moves to a free channel so each keeps its own program.
    /// </summary>
    public static MidiAuditionSequence CreateMix(MidiDocument document, IReadOnlyList<SourceKey> sources, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var distinct = sources.Distinct().ToArray();
        if (distinct.Length == 0) throw new ArgumentException("Escolha ao menos uma pista para ouvir.", nameof(sources));
        if (distinct.Length == 1) return Create(document, distinct[0]);
        return Build(document, distinct, live: false, required: distinct, token);
    }

    /// <summary>
    /// Every playable source of the song on one timeline, each event tagged with its source, so tracks can be
    /// switched on and off during playback without stopping it. <paramref name="required"/> sources are placed
    /// first; when the Windows synthesizer runs out of channels the remaining optional sources are left out
    /// (check MixedSources before muting a source by its index).
    /// </summary>
    public static MidiAuditionSequence CreateLiveMix(MidiDocument document, IReadOnlyList<SourceKey> sources, IReadOnlyCollection<SourceKey> required, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var playable = sources.Distinct().Where(source => document.Notes.Any(note => note.Track == source.Track && note.Channel == source.Channel && note.Program == source.Program)).ToList();
        var ordered = playable.Where(required.Contains).Concat(playable.Where(source => !required.Contains(source))).ToArray();
        if (ordered.Length == 0) throw new InvalidOperationException("Esta fonte não contém notas para ouvir.");
        return Build(document, ordered, live: true, required, token);
    }

    private (TimeSpan Start, TimeSpan End)[][]? activity;

    /// <summary>True while a note of this live-mix source is held at <paramref name="at"/> (or just struck, for very short notes).</summary>
    public bool IsSourceSounding(int source, TimeSpan at)
    {
        var spans = ActivityOf(source);
        int index = LastStartAtOrBefore(spans, at);
        return index >= 0 && at < spans[index].End;
    }

    /// <summary>Where the source next plays a note at or after <paramref name="at"/>; null when it has finished.</summary>
    public TimeSpan? NextSourceNote(int source, TimeSpan at)
    {
        var spans = ActivityOf(source);
        int index = LastStartAtOrBefore(spans, at);
        if (index >= 0 && at < spans[index].End) return at;
        return index + 1 < spans.Length ? spans[index + 1].Start : null;
    }

    private static int LastStartAtOrBefore((TimeSpan Start, TimeSpan End)[] spans, TimeSpan at)
    {
        int lo = 0, hi = spans.Length - 1, found = -1;
        while (lo <= hi) { int middle = (lo + hi) / 2; if (spans[middle].Start <= at) { found = middle; lo = middle + 1; } else hi = middle - 1; }
        return found;
    }

    private (TimeSpan Start, TimeSpan End)[] ActivityOf(int source)
    {
        if (source < 0 || source >= MixedSources.Count) return [];
        activity ??= BuildActivity();
        return activity[source];
    }

    /// <summary>Merged note spans per source; a struck note counts as sounding for at least 150 ms so drums and staccato show up.</summary>
    private (TimeSpan Start, TimeSpan End)[][] BuildActivity()
    {
        var minimum = TimeSpan.FromMilliseconds(150);
        var result = new (TimeSpan Start, TimeSpan End)[MixedSources.Count][];
        var open = Enumerable.Range(0, MixedSources.Count).Select(_ => new Dictionary<int, Stack<TimeSpan>>()).ToArray();
        var spans = Enumerable.Range(0, MixedSources.Count).Select(_ => new List<(TimeSpan Start, TimeSpan End)>()).ToArray();
        foreach (var item in Events)
        {
            if (item.Source < 0 || item.Source >= MixedSources.Count) continue;
            int kind = (int)(item.Message & 0xf0), pitch = (int)((item.Message >> 8) & 127), velocity = (int)((item.Message >> 16) & 127);
            var held = open[item.Source];
            if (kind == 0x90 && velocity > 0)
            {
                if (!held.TryGetValue(pitch, out var starts)) held[pitch] = starts = new();
                starts.Push(item.At);
            }
            else if ((kind == 0x80 || kind == 0x90) && held.TryGetValue(pitch, out var starts) && starts.Count > 0)
            {
                var start = starts.Pop();
                spans[item.Source].Add((start, item.At - start < minimum ? start + minimum : item.At));
            }
        }
        for (int source = 0; source < spans.Length; source++)
        {
            foreach (var starts in open[source].Values) foreach (var start in starts) spans[source].Add((start, Duration));
            var merged = new List<(TimeSpan Start, TimeSpan End)>();
            foreach (var span in spans[source].OrderBy(span => span.Start))
                if (merged.Count > 0 && span.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, span.End > merged[^1].End ? span.End : merged[^1].End);
                else merged.Add(span);
            result[source] = merged.ToArray();
        }
        return result;
    }

    private static MidiAuditionSequence Build(MidiDocument document, IReadOnlyList<SourceKey> distinct, bool live, IReadOnlyCollection<SourceKey> required, CancellationToken token)
    {
        var reserved = distinct.Select(source => source.Channel).ToHashSet();
        var used = new HashSet<int>();
        var placed = new List<(SourceKey Source, int Channel, MidiAuditionSequence Untrimmed, TimeSpan FirstNote)>();
        foreach (var source in distinct)
        {
            token.ThrowIfCancellationRequested();
            int channel = source.Channel;
            // Percussion keeps channel 10: moving it would turn drums into a melodic instrument.
            if (used.Contains(channel) && channel != 10)
            {
                channel = Enumerable.Range(1, 16).FirstOrDefault(candidate => candidate != 10 && !used.Contains(candidate) && !reserved.Contains(candidate));
                // A live mix may borrow the channel of a source that was left out.
                if (channel == 0 && live) channel = Enumerable.Range(1, 16).FirstOrDefault(candidate => candidate != 10 && !used.Contains(candidate));
                if (channel == 0)
                {
                    if (live && !required.Contains(source)) continue;
                    throw new InvalidOperationException("Há pistas demais para ouvir juntas no sintetizador do Windows (16 canais).");
                }
            }
            used.Add(channel);
            placed.Add((source, channel, Create(document, source, trimLeadingSilence: false), Create(document, source).LeadingSilenceTrimmed));
        }
        var trim = placed.Min(item => item.FirstNote);
        var events = new List<(int Order, MidiAuditionEvent Event)>();
        var starts = new List<TimeSpan>(); var ends = new List<TimeSpan>();
        TimeSpan duration = TimeSpan.Zero;
        int order = 0;
        for (int index = 0; index < placed.Count; index++)
        {
            var (_, channel, untrimmed, firstNote) = placed[index];
            int tag = live ? index : -1;
            foreach (var item in untrimmed.Events)
            {
                var at = item.At - trim;
                events.Add((order++, new(at < TimeSpan.Zero ? TimeSpan.Zero : at, (item.Message & ~15u) | (uint)(channel - 1), tag)));
            }
            var end = untrimmed.Duration - trim;
            starts.Add(firstNote - trim); ends.Add(end);
            duration = TimeSpan.FromTicks(Math.Max(duration.Ticks, end.Ticks));
        }
        var ordered = events.OrderBy(item => item.Event.At).ThenBy(item => item.Order).Select(item => item.Event).ToArray();
        string[] warnings = document.HasSystemExclusive ? ["A escuta usa o sintetizador General MIDI do Windows; timbres personalizados por SysEx podem soar diferentes."] : [];
        var channels = placed.Select(item => item.Channel).Distinct().Order().ToArray();
        return new(placed[0].Source, duration, trim, ordered, warnings, channels)
        { MixedSources = placed.Select(item => item.Source).ToArray(), SourceStarts = starts, SourceEnds = ends, IsLiveMix = live };
    }
}
