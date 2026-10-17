namespace Teclas.Core;

public sealed record MelodyBatchProposal(SourceKey? Source, bool Confident, double Score, string Reason);

/// <summary>Picks one melody source per MIDI for batch conversion, but only marks it confident when the choice is clear.</summary>
public static class MelodyBatchAdvisor
{
    public const double MinimumScore = 60, MinimumLead = 10;

    public static MelodyBatchProposal Propose(IReadOnlyList<MelodySourceSuggestion> suggestions)
    {
        var ranked = suggestions.OrderBy(s => s.Rank).ToArray();
        if (ranked.Length == 0) return new(null, false, 0, "Nenhuma pista com notas.");
        var top = ranked[0]; var second = ranked.Length > 1 ? ranked[1] : null;
        if (top.Label != "Boa candidata")
            return new(top.Key, false, top.Score, "Nenhuma pista parece conter a melodia; ouça antes de escolher.");
        if (top.Score < MinimumScore)
            return new(top.Key, false, top.Score, $"A melhor pista tem pontuação baixa ({top.Score:0}); confira ouvindo.");
        if (second is not null && top.Score - second.Score < MinimumLead)
            return new(top.Key, false, top.Score, $"Duas pistas parecidas ({top.Score:0} e {second.Score:0}); compare antes de escolher.");
        if (top.Reason.Contains("linha semelhante", StringComparison.OrdinalIgnoreCase))
            return new(top.Key, false, top.Score, "Há outra pista com linha semelhante; compare o timbre.");
        return new(top.Key, true, top.Score, second is null ? "Única pista candidata." : $"Melhor pista com folga ({top.Score:0} contra {second.Score:0}).");
    }
}
