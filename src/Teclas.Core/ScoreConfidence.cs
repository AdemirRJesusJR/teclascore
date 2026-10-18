namespace Teclas.Core;

/// <summary>What is known about a generated score, to rate how much it needs a human look.</summary>
public sealed record ConfidenceInput(
    IReadOnlyList<double> ChordConfidences,
    int ChordsWithAlternatives,
    int ReviewPoints,
    int BlockingReviewPoints,
    bool MelodyChosenAutomatically,
    bool MelodyChoiceConfident,
    double? LyricsShareOnMelody,
    bool LyricsRequested,
    bool HasError,
    int PageCount);

/// <summary>A 0–100 rating (higher needs less review) and the reasons that lowered it, in words.</summary>
public sealed record ConfidenceRating(int Score, IReadOnlyList<string> Reasons)
{
    public string Label => Score >= 85 ? "Pronta" : Score >= 65 ? "Conferir" : "Revisar";
}

/// <summary>
/// Rates a generated score from the evidence the analysis leaves behind: doubtful chords, review points (rhythm,
/// harmony gaps), an automatic melody choice that was not clear, lyrics that mostly miss the melody, errors. Each
/// problem takes points off with a ceiling, so one kind of issue cannot hide the others. It is a sorting aid for
/// review, not a judgment of the music.
/// </summary>
public static class ScoreConfidence
{
    public const double DoubtfulChord = .65;

    public static ConfidenceRating Rate(ConfidenceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.HasError) return new(0, ["a última geração falhou"]);
        double score = 100; var reasons = new List<string>();
        int doubtful = input.ChordConfidences.Count(c => c > 0 && c < DoubtfulChord);
        int chords = input.ChordConfidences.Count;
        if (chords > 0 && doubtful + input.ChordsWithAlternatives > 0)
        {
            double share = Math.Min(1, (doubtful + input.ChordsWithAlternatives * .5) / chords);
            score -= Math.Min(30, 6 + share * 60);
            reasons.Add(doubtful > 0 ? $"{doubtful} acorde(s) com dúvida" : $"{input.ChordsWithAlternatives} acorde(s) com alternativa");
        }
        if (input.BlockingReviewPoints > 0) { score -= Math.Min(35, 20 + 5 * input.BlockingReviewPoints); reasons.Add($"{input.BlockingReviewPoints} ponto(s) que impedem gerar sem revisão"); }
        int soft = input.ReviewPoints - input.BlockingReviewPoints;
        if (soft > 0) { score -= Math.Min(20, 4 * soft); reasons.Add($"{soft} ponto(s) de revisão"); }
        if (input.MelodyChosenAutomatically && !input.MelodyChoiceConfident) { score -= 20; reasons.Add("melodia escolhida automaticamente sem certeza"); }
        if (input.LyricsRequested && input.LyricsShareOnMelody is { } share2 && share2 < .6)
        { score -= share2 < .3 ? 15 : 8; reasons.Add($"só {share2:P0} da letra cai na melodia"); }
        if (input.PageCount > 6) { score -= 5; reasons.Add($"{input.PageCount} páginas: talvez falte dobrar repetições"); }
        return new((int)Math.Round(Math.Clamp(score, 0, 100)), reasons);
    }
}
