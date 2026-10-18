namespace Teclas.Core;

/// <summary>A part of a MIDI as the rule sees it: where it plays (channel), with which sound (program) and its name.</summary>
public sealed record RuleTrack(SourceKey Key, string Instrument, int Notes);

/// <summary>
/// Choices learned from one song that can be repeated on songs with the same arrangement: same folder or collection
/// (decided by the caller) and the same parts — the melody's channel and sound present exactly once, and most of the
/// other parts matching too (70% or more of the channel/sound pairs in common).
/// </summary>
public static class CollectionRule
{
    public const double MinimumOverlap = .7;

    private static (int Channel, int Program) Signature(RuleTrack track) => (track.Key.Channel, track.Key.Program);

    /// <summary>The part of the target that matches the source's melody part, when the arrangement is similar; else null.</summary>
    public static RuleTrack? Match(IReadOnlyList<RuleTrack> source, RuleTrack melody, IReadOnlyList<RuleTrack> target)
    {
        var candidates = target.Where(t => Signature(t) == Signature(melody)).ToList();
        if (candidates.Count != 1) return null;
        var a = source.Where(t => t.Notes > 0).Select(Signature).ToHashSet();
        var b = target.Where(t => t.Notes > 0).Select(Signature).ToHashSet();
        if (a.Count == 0 || b.Count == 0) return null;
        double overlap = a.Intersect(b).Count() / (double)a.Union(b).Count();
        return overlap >= MinimumOverlap ? candidates[0] : null;
    }
}
