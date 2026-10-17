namespace Teclas.Core;

public sealed partial class MidiAuditionSequence
{
    /// <summary>The sources heard together, in the order given (empty for single-source or full-document sequences).</summary>
    public IReadOnlyList<SourceKey> MixedSources { get; private init; } = [];

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
        var trim = distinct.Select(source => Create(document, source).LeadingSilenceTrimmed).Min();
        var reserved = distinct.Select(source => source.Channel).ToHashSet();
        var used = new HashSet<int>();
        var channels = new List<int>();
        var events = new List<(TimeSpan At, int Order, uint Message)>();
        TimeSpan duration = TimeSpan.Zero;
        int order = 0;
        foreach (var source in distinct)
        {
            token.ThrowIfCancellationRequested();
            int channel = source.Channel;
            // Percussion keeps channel 10: moving it would turn drums into a melodic instrument.
            if (used.Contains(channel) && channel != 10)
            {
                channel = Enumerable.Range(1, 16).FirstOrDefault(candidate => candidate != 10 && !used.Contains(candidate) && !reserved.Contains(candidate));
                if (channel == 0) throw new InvalidOperationException("Há pistas demais para ouvir juntas no sintetizador do Windows (16 canais).");
            }
            used.Add(channel);
            if (!channels.Contains(channel)) channels.Add(channel);
            var untrimmed = Create(document, source, trimLeadingSilence: false);
            foreach (var item in untrimmed.Events)
            {
                var at = item.At - trim;
                events.Add((at < TimeSpan.Zero ? TimeSpan.Zero : at, order++, (item.Message & ~15u) | (uint)(channel - 1)));
            }
            duration = TimeSpan.FromTicks(Math.Max(duration.Ticks, (untrimmed.Duration - trim).Ticks));
        }
        var ordered = events.OrderBy(item => item.At).ThenBy(item => item.Order).Select(item => new MidiAuditionEvent(item.At, item.Message)).ToArray();
        string[] warnings = document.HasSystemExclusive ? ["A escuta usa o sintetizador General MIDI do Windows; timbres personalizados por SysEx podem soar diferentes."] : [];
        return new(distinct[0], duration, trim, ordered, warnings, channels.Order().ToArray()) { MixedSources = distinct };
    }
}
