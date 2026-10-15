namespace Teclas.Core;

public sealed record VoicingOccurrence(string Id, string Location, string Root, string Quality,
    int CurrentInversion, bool PreserveChoice = false, int TransposeSemitones = 0);
public readonly record struct VoicingChordKey(string Root, string Quality);
public sealed record VoicingSuggestion(VoicingOccurrence Source, int SuggestedInversion,
    IReadOnlyList<int> BeforePitches, IReadOnlyList<int> AfterPitches, string Limitation)
{
    public bool Changed => SuggestedInversion != Source.CurrentInversion;
    public int Span => AfterPitches.Count == 0 ? 0 : AfterPitches[^1] - AfterPitches[0];
}
public sealed record ComfortableVoicingPlan(IReadOnlyList<VoicingSuggestion> Suggestions,
    int MaximumSpan, double BeforeMovement, double AfterMovement)
{
    public int ChangeCount => Suggestions.Count(s => s.Changed);
    public int PreservedCount => Suggestions.Count(s => s.Source.PreserveChoice);
}

/// <summary>Optimizes a sequence of rendered diagram voicings; never changes harmony or omits notes.</summary>
public static class ComfortableVoicingService
{
    private sealed record Candidate(int Inversion, int[] Pitches);
    private sealed record Entry(VoicingOccurrence Source, int[] Original, Candidate[] Candidates, string Limitation);
    private readonly record struct Cost(double Motion, int Changes) : IComparable<Cost>
    {
        public int CompareTo(Cost other)
        {
            int movement = Motion.CompareTo(other.Motion);
            return movement != 0 ? movement : Changes.CompareTo(other.Changes);
        }
    }

    public static ComfortableVoicingPlan Suggest(IReadOnlyList<VoicingOccurrence> sequence, int maximumSpan = 12)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (maximumSpan is < 5 or > 12) throw new ArgumentOutOfRangeException(nameof(maximumSpan), "Escolha um alcance entre 5 e 12 semitons.");
        if (sequence.Count > 100000) throw new ArgumentException("A sequência contém cifras demais para uma única revisão.", nameof(sequence));
        var entries = sequence.Select(source => Create(source, maximumSpan)).ToArray();
        int[] choices = new int[entries.Length];
        // An unsupported chord breaks the chain: there is no evidenced position to optimize through it.
        for (int start = 0; start < entries.Length;)
        {
            if (entries[start].Original.Length == 0) { start++; continue; }
            int end = start + 1;
            while (end < entries.Length && entries[end].Original.Length > 0) end++;
            Optimize(entries, choices, start, end);
            start = end;
        }
        var results = entries.Select((entry, index) =>
        {
            var selected = entry.Candidates[choices[index]];
            return new VoicingSuggestion(entry.Source, selected.Inversion, entry.Original, selected.Pitches, entry.Limitation);
        }).ToArray();
        double before = 0, after = 0;
        for (int i = 1; i < results.Length; i++)
        {
            before += Movement(results[i - 1].BeforePitches, results[i].BeforePitches);
            after += Movement(results[i - 1].AfterPitches, results[i].AfterPitches);
        }
        return new(results, maximumSpan, before, after);
    }

    /// <summary>Written chord identity: accidental spelling is preserved; quality is case-sensitive.</summary>
    public static VoicingChordKey GetChordGroupKey(VoicingOccurrence source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(NormalizeRoot(source.Root).ToUpperInvariant(), (source.Quality ?? "").Trim());
    }

    /// <summary>
    /// Assigns one position to every repetition of a written chord, using all original transitions.
    /// Deterministic bounded coordinate descent is an approximation, not a global optimum claim.
    /// Groups with manual choices or different existing positions remain entirely unchanged.
    /// </summary>
    public static ComfortableVoicingPlan SuggestGrouped(IReadOnlyList<VoicingOccurrence> sequence, int maximumSpan = 12)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (maximumSpan is < 5 or > 12) throw new ArgumentOutOfRangeException(nameof(maximumSpan), "Escolha um alcance entre 5 e 12 semitons.");
        if (sequence.Count > 100000) throw new ArgumentException("A sequência contém cifras demais para uma única revisão.", nameof(sequence));
        var entries = sequence.Select(source => Create(source with
        { Root = NormalizeRoot(source.Root), Quality = (source.Quality ?? "").Trim() }, maximumSpan) with { Source = source }).ToArray();
        var groups = sequence.Select((source, index) => (Key: GetChordGroupKey(source), Index: index))
            .GroupBy(item => item.Key).Select(group => group.Select(item => item.Index).ToArray()).ToArray();
        var groupAt = new int[entries.Length];
        var choices = entries.Select(entry => entry.Source.CurrentInversion).ToArray();
        var pitches = entries.Select(entry => entry.Original).ToArray();
        var limitations = entries.Select(entry => entry.Limitation).ToArray();
        var allowed = new int[groups.Length][];
        var edges = new int[groups.Length][];
        for (int group = 0; group < groups.Length; group++)
        {
            var indices = groups[group];
            foreach (int index in indices) groupAt[index] = group;
            bool mixed = indices.Select(index => entries[index].Source.CurrentInversion).Distinct().Skip(1).Any();
            bool manual = indices.Any(index => entries[index].Source.PreserveChoice);
            bool unknown = indices.Any(index => entries[index].Original.Length == 0);
            allowed[group] = mixed || manual || unknown ? [] : Enumerable.Range(0, 3)
                .Where(inversion => indices.All(index => entries[index].Candidates.Any(candidate => candidate.Inversion == inversion))).ToArray();
            if (mixed || manual)
            {
                string protectedReason = mixed
                    ? "Posições diferentes já existentes para esta cifra: grupo preservado. Escolha uma posição no diálogo de diagramas para unificar."
                    : "Escolha individual preservada em todas as ocorrências desta cifra.";
                foreach (int index in indices) limitations[index] = protectedReason
                    + (entries[index].Original.Length == 0 ? " Cifra não reconhecida; alcance não estimado."
                        : entries[index].Original[^1] - entries[index].Original[0] > maximumSpan ? " Excede o alcance selecionado." : "");
            }
            if (allowed[group].Length > 0)
            {
                int selected = allowed[group].Contains(choices[indices[0]]) ? choices[indices[0]] : allowed[group][0];
                foreach (int index in indices)
                {
                    choices[index] = selected;
                    pitches[index] = entries[index].Candidates.Single(candidate => candidate.Inversion == selected).Pitches;
                }
            }
            edges[group] = indices.SelectMany(index => new[] { index, index + 1 })
                .Where(edge => edge > 0 && edge < entries.Length).Distinct().Order().ToArray();
        }
        // Each pass visits every original edge at most twice per candidate. The hard cap
        // bounds long songs; strict lexicographic improvements avoid cycles and arbitrary churn.
        for (int pass = 0; pass < 24; pass++)
        {
            bool changed = false;
            for (int group = 0; group < groups.Length; group++)
            {
                if (allowed[group].Length < 2) continue;
                var indices = groups[group]; int current = choices[indices[0]], best = current;
                var candidates = allowed[group].ToDictionary(inversion => inversion,
                    inversion => indices.ToDictionary(index => index, index => entries[index].Candidates.Single(candidate => candidate.Inversion == inversion).Pitches));
                Cost Evaluate(int inversion)
                {
                    var proposed = candidates[inversion];
                    double motion = edges[group].Sum(edge => Movement(
                        groupAt[edge - 1] == group ? proposed[edge - 1] : pitches[edge - 1],
                        groupAt[edge] == group ? proposed[edge] : pitches[edge]));
                    return new(motion, indices.Count(index => entries[index].Source.CurrentInversion != inversion));
                }
                var bestCost = Evaluate(current);
                foreach (int inversion in allowed[group])
                {
                    var cost = Evaluate(inversion);
                    if (cost.Motion < bestCost.Motion - .000000001 || Math.Abs(cost.Motion - bestCost.Motion) <= .000000001 && cost.Changes < bestCost.Changes)
                    { best = inversion; bestCost = cost; }
                }
                if (best == current) continue;
                foreach (int index in indices) { choices[index] = best; pitches[index] = candidates[best][index]; }
                changed = true;
            }
            if (!changed) break;
        }
        var results = entries.Select((entry, index) => new VoicingSuggestion(entry.Source, choices[index], entry.Original, pitches[index], limitations[index])).ToArray();
        double before = 0, after = 0;
        for (int index = 1; index < results.Length; index++)
        {
            before += Movement(results[index - 1].BeforePitches, results[index].BeforePitches);
            after += Movement(results[index - 1].AfterPitches, results[index].AfterPitches);
        }
        return new(results, maximumSpan, before, after);
    }

    private static string NormalizeRoot(string? root)
    {
        string value = (root ?? "").Trim().Replace('♯', '#').Replace('♭', 'b');
        return value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].Replace('B', 'b');
    }

    // Corresponding pitch-height positions, resampled when chord sizes differ. This is a comparison
    // metric, not fingering or a claim about physical comfort. It includes bass and upper-note motion.
    public static double Movement(IReadOnlyList<int> first, IReadOnlyList<int> second)
    {
        if (first.Count == 0 || second.Count == 0) return 0;
        int count = Math.Max(first.Count, second.Count);
        double SumAt(IReadOnlyList<int> notes, int index) => notes[(int)Math.Round((double)index * (notes.Count - 1) / Math.Max(1, count - 1))];
        double total = 0;
        for (int i = 0; i < count; i++) total += Math.Abs(SumAt(first, i) - SumAt(second, i));
        return total / count;
    }

    private static Entry Create(VoicingOccurrence source, int maximumSpan)
    {
        if (source.CurrentInversion is < 0 or > 2) throw new ArgumentException("A posição atual precisa estar entre 0 e 2.", nameof(source));
        if (source.TransposeSemitones is < -48 or > 48) throw new ArgumentException("Transposição fora do intervalo suportado.", nameof(source));
        if (!KeyboardDiagramVoicing.TryRootPitchClass(source.Root, out int root) || !KeyboardDiagramVoicing.SupportsQuality(source.Quality))
            return new(source, [], [new(source.CurrentInversion, [])], "Cifra não reconhecida: posição mantida sem estimativa de alcance.");
        root += source.TransposeSemitones;
        int[] current = KeyboardDiagramVoicing.Pitches(root, source.Quality, source.CurrentInversion);
        int span = current[^1] - current[0];
        if (source.PreserveChoice)
            return new(source, current, [new(source.CurrentInversion, current)], span > maximumSpan
                ? "Escolha individual preservada; excede o alcance selecionado." : "Escolha individual preservada.");
        var candidates = Enumerable.Range(0, 3).Select(inversion => new Candidate(inversion, KeyboardDiagramVoicing.Pitches(root, source.Quality, inversion)))
            .Where(candidate => candidate.Pitches[^1] - candidate.Pitches[0] <= maximumSpan).ToArray();
        return candidates.Length == 0
            ? new(source, current, [new(source.CurrentInversion, current)], "Nenhuma das três posições cabe no alcance; posição mantida, sem retirar notas.")
            : new(source, current, candidates, "");
    }

    private static void Optimize(Entry[] entries, int[] choices, int start, int end)
    {
        var costs = new Cost[end - start][];
        var parents = new int[end - start][];
        for (int at = start; at < end; at++)
        {
            var entry = entries[at]; int row = at - start;
            costs[row] = new Cost[entry.Candidates.Length]; parents[row] = new int[entry.Candidates.Length];
            for (int candidate = 0; candidate < entry.Candidates.Length; candidate++)
            {
                var current = entry.Candidates[candidate]; int changed = current.Inversion == entry.Source.CurrentInversion ? 0 : 1;
                if (row == 0) { costs[row][candidate] = new(0, changed); continue; }
                Cost best = new(double.PositiveInfinity, int.MaxValue); int parent = 0;
                for (int previous = 0; previous < entries[at - 1].Candidates.Length; previous++)
                {
                    var prior = costs[row - 1][previous];
                    var next = new Cost(prior.Motion + Movement(entries[at - 1].Candidates[previous].Pitches, current.Pitches), prior.Changes + changed);
                    if (next.CompareTo(best) < 0) { best = next; parent = previous; }
                }
                costs[row][candidate] = best; parents[row][candidate] = parent;
            }
        }
        int chosen = 0;
        for (int i = 1; i < costs[^1].Length; i++) if (costs[^1][i].CompareTo(costs[^1][chosen]) < 0) chosen = i;
        for (int at = end - 1; at >= start; at--) { choices[at] = chosen; chosen = parents[at - start][chosen]; }
    }
}
