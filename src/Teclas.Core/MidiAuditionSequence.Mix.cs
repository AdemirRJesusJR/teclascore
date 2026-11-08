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
