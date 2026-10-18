namespace Teclas.Core;

/// <summary>A note as the section advisor sees it: its source and position in quarters.</summary>
public sealed record SectionNote(SourceKey Source, double Start, double End, int Pitch);
/// <summary>A run of measures (1-based, inclusive) whose melody comes from one source.</summary>
public sealed record ProposedSection(int FirstMeasure, int LastMeasure, SourceKey Source);

/// <summary>
/// Proposes where the melody passes from one part to another (voice, then a flute solo, then the voice again), measure
/// by measure, using only the notes that are in the MIDI: each candidate part scores by the sung syllables that fall on
/// its notes, by playing one note at a time in a melody register, and by the general ranking of melody candidates.
/// Short switches (one measure) are smoothed away. Measures where no candidate plays keep the previous part. When one
/// part covers the whole song, nothing is proposed.
/// </summary>
public static class MelodySectionAdvisor
{
    public static IReadOnlyList<ProposedSection> Propose(IReadOnlyList<(double Start, double End)> measures, IReadOnlyList<SectionNote> notes,
        IReadOnlyList<double> syllables, IReadOnlyList<SourceKey> ranking, int minimumRun = 2)
    {
        if (measures.Count == 0 || ranking.Count < 2) return [];
        var candidates = ranking.Take(4).ToList();
        var bySource = notes.Where(n => candidates.Contains(n.Source)).GroupBy(n => n.Source).ToDictionary(g => g.Key, g => g.OrderBy(n => n.Start).ToArray());
        var choice = new SourceKey?[measures.Count];
        for (int m = 0; m < measures.Count; m++)
        {
            var (start, end) = measures[m];
            double best = double.NegativeInfinity; SourceKey? winner = null;
            for (int rank = 0; rank < candidates.Count; rank++)
            {
                if (!bySource.TryGetValue(candidates[rank], out var all)) continue;
                var inside = all.Where(n => n.Start >= start - 1e-6 && n.Start < end - 1e-6).ToArray();
                if (inside.Length == 0) continue;
                int attacks = inside.Select(n => Math.Round(n.Start, 3)).Distinct().Count();
                double monophony = attacks / (double)inside.Length;
                double mean = inside.Average(n => n.Pitch);
                double register = mean is >= 55 and <= 86 ? 1 : mean < 48 ? -2 : 0;
                int sung = syllables.Count(s => s >= start - 1e-6 && s < end - 1e-6 && inside.Any(n => Math.Abs(n.Start - s) <= .125));
                double score = sung * 3 + monophony * 2 + register + (candidates.Count - rank) * .4 + Math.Min(attacks, 8) * .1;
                if (score > best) { best = score; winner = candidates[rank]; }
            }
            choice[m] = winner ?? (m > 0 ? choice[m - 1] : null);
        }
        for (int m = 0; m < measures.Count && choice[m] is null; m++) choice[m] = choice.FirstOrDefault(c => c is not null);
        if (choice.All(c => c is null)) return [];
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
