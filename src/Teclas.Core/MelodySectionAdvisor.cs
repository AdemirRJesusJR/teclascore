namespace Teclas.Core;

/// <summary>A note as the section advisor sees it: its source and position in quarters.</summary>
public sealed record SectionNote(SourceKey Source, double Start, double End, int Pitch);
/// <summary>A run of measures (1-based, inclusive) whose melody comes from one source.</summary>
public sealed record ProposedSection(int FirstMeasure, int LastMeasure, SourceKey Source);

/// <summary>
/// Proposes where the melody passes from one part to another (voice, then a flute solo, then the voice again), using
/// only the notes that are in the MIDI: each candidate part scores in each measure by the sung syllables that fall on
/// its notes, by playing one note at a time in a melody register, and by the general ranking of melody candidates.
/// The parts are chosen for the whole song at once (Viterbi over measures × parts): every change of part costs
/// <see cref="SwitchCost"/>, so a part takes over only where it wins clearly for long enough. A part that does not play
/// in a measure is heavily penalized there; measures where no candidate plays cost nothing and keep the current part.
/// Runs still shorter than the minimum join a neighbour. When one part covers the whole song, nothing is proposed.
/// </summary>
public static class MelodySectionAdvisor
{
    /// <summary>Cost of changing the melody part between two measures, in score units (one sung syllable is 3).</summary>
    public const double SwitchCost = 4.0;
    private const double SilentPenalty = 8.0;

    public static double MeasureScore(SectionNote[] inside, double start, double end, IReadOnlyList<double> syllables, int rank, int candidates)
    {
        int attacks = inside.Select(n => Math.Round(n.Start, 3)).Distinct().Count();
        double monophony = attacks / (double)inside.Length;
        double mean = inside.Average(n => n.Pitch);
        double register = mean is >= 55 and <= 86 ? 1 : mean < 48 ? -2 : 0;
        int sung = syllables.Count(s => s >= start - 1e-6 && s < end - 1e-6 && inside.Any(n => Math.Abs(n.Start - s) <= .125));
        return sung * 3 + monophony * 2 + register + (candidates - rank) * .4 + Math.Min(attacks, 8) * .1;
    }

    public static IReadOnlyList<ProposedSection> Propose(IReadOnlyList<(double Start, double End)> measures, IReadOnlyList<SectionNote> notes,
        IReadOnlyList<double> syllables, IReadOnlyList<SourceKey> ranking, int minimumRun = 2)
    {
        if (measures.Count == 0 || ranking.Count < 2) return [];
        var candidates = ranking.Take(4).ToList();
        var bySource = notes.Where(n => candidates.Contains(n.Source)).GroupBy(n => n.Source).ToDictionary(g => g.Key, g => g.OrderBy(n => n.Start).ToArray());
        int count = candidates.Count;
        var scores = new double[measures.Count, count];
        var anyPlays = new bool[measures.Count];
        for (int m = 0; m < measures.Count; m++)
        {
            var (start, end) = measures[m];
            for (int rank = 0; rank < count; rank++)
            {
                var inside = bySource.TryGetValue(candidates[rank], out var all)
                    ? all.Where(n => n.Start >= start - 1e-6 && n.Start < end - 1e-6).ToArray() : [];
                if (inside.Length == 0) { scores[m, rank] = double.NaN; continue; }
                scores[m, rank] = MeasureScore(inside, start, end, syllables, rank, count);
                anyPlays[m] = true;
            }
        }
        if (!anyPlays.Any(x => x)) return [];
        // Viterbi: best total score with a cost for each change of part.
        double Emission(int m, int rank) => !anyPlays[m] ? 0 : double.IsNaN(scores[m, rank]) ? -SilentPenalty : scores[m, rank];
        var total = new double[count]; var back = new int[measures.Count, count];
        for (int rank = 0; rank < count; rank++) total[rank] = Emission(0, rank);
        for (int m = 1; m < measures.Count; m++)
        {
            var next = new double[count];
            for (int rank = 0; rank < count; rank++)
            {
                int best = rank; double value = total[rank];
                for (int previous = 0; previous < count; previous++)
                {
                    double candidate = total[previous] - (previous == rank ? 0 : SwitchCost);
                    if (candidate > value + 1e-9) { value = candidate; best = previous; }
                }
                next[rank] = value + Emission(m, rank); back[m, rank] = best;
            }
            total = next;
        }
        int state = 0;
        for (int rank = 1; rank < count; rank++) if (total[rank] > total[state] + 1e-9) state = rank;
        var choice = new SourceKey?[measures.Count];
        for (int m = measures.Count - 1; m >= 0; m--) { choice[m] = candidates[state]; if (m > 0) state = back[m, state]; }
        // Smooth: runs shorter than the minimum join the previous run (or the next one at the start).
        bool changed = true;
        while (changed)
        {
            changed = false;
            var runs = Runs(choice);
            if (runs.Count <= 1) break;
            for (int r = 0; r < runs.Count; r++)
            {
                var (first, last, _) = runs[r];
                if (last - first + 1 >= minimumRun) continue;
                var replacement = r > 0 ? runs[r - 1].Source : runs[r + 1].Source;
                for (int m = first; m <= last; m++) choice[m] = replacement;
                changed = true; break;
            }
        }
        var final = Runs(choice);
        return final.Count < 2 ? [] : final.Select(r => new ProposedSection(r.First + 1, r.Last + 1, r.Source)).ToArray();
    }

    private static List<(int First, int Last, SourceKey Source)> Runs(SourceKey?[] choice)
    {
        var runs = new List<(int, int, SourceKey)>();
        for (int m = 0; m < choice.Length; m++)
        {
            var source = choice[m]!;
            if (runs.Count > 0 && runs[^1].Item3 == source) runs[^1] = (runs[^1].Item1, m, source);
            else runs.Add((m, m, source));
        }
        return runs;
    }
}
