namespace Teclas.Core;

/// <summary>The steps a new MIDI goes through before its first score is generated.</summary>
public enum ScoreFlowStep { Melody = 0, Analysis = 1, Chords = 2, Generate = 3 }

/// <summary>Block: cannot continue until it is fixed. Confirm: continue only after the user confirms. Ok: informative.</summary>
public enum ScoreFlowLevel { Block, Confirm, Ok }

/// <summary>One item of the "Para continuar" list. <see cref="Action"/> names the app command that resolves it.</summary>
public sealed record ScoreFlowCheck(string Id, ScoreFlowStep Step, ScoreFlowLevel Level, string Message,
    string? Action = null, string? ActionLabel = null, bool Confirmed = false, string? ConfirmLabel = "Confirmar")
{
    public bool Pending => Level == ScoreFlowLevel.Block || Level == ScoreFlowLevel.Confirm && !Confirmed;
    /// <summary>
    /// A confirmation that is a real decision, shown like a block until decided: either the action (one click) or a
    /// conscious "keep it" that the app confirms again before accepting.
    /// </summary>
    public bool Critical { get; init; }
}

/// <param name="GuideNotes">The track barely changes pitch: a karaoke guide note, not the sung line.</param>
public sealed record ScoreFlowTrack(string Name, string Hint, bool IsMelody, bool IsPercussion, int Notes, bool GuideNotes = false);

public sealed record ScoreFlowChord(double Quarter, string Name);

/// <summary>Everything the rules need, gathered by the app from the selected MIDI.</summary>
public sealed record ScoreFlowInput
{
    public bool ChordsOnly { get; init; }
    public IReadOnlyList<ScoreFlowTrack> Tracks { get; init; } = [];
    /// <summary>Played melody range (MIDI pitches, after transposition), null when unknown.</summary>
    public (int Low, int High)? MelodyRange { get; init; }
    public int KeyboardKeys { get; init; } = 61;
    /// <summary>The lyrics-carrying track when it is clearly not among the chosen ones.</summary>
    public string? LyricsTrack { get; init; }
    /// <summary>Share of the syllables on the chosen melody, and on <see cref="LyricsTrack"/>; null when unknown.</summary>
    public double? ChosenLyricsShare { get; init; }
    public double? LyricsTrackShare { get; init; }
    public bool HeardMarked { get; init; }
    public bool HasAnalysis { get; init; }
    public int ReviewPoints { get; init; }
    public int BlockingReviewPoints { get; init; }
    public string KeyName { get; init; } = "";
    public IReadOnlyList<ScoreFlowChord> Chords { get; init; } = [];
    public int DoubtfulChords { get; init; }
    public IReadOnlySet<string> Confirmed { get; init; } = new HashSet<string>();
}

public static class ScoreFlowRules
{
    public const int PianoLow = 21, PianoHigh = 108; // A0–C8, the 88-key piano
    /// <summary>Chord changes closer than this (in quarters) crowd the keyboard diagrams.</summary>
    public const double CrowdedChordGap = 1.0;

    /// <summary>Suggestion labels that do not look like a melody and need the user's confirmation.</summary>
    public static readonly IReadOnlySet<string> DoubtfulMelodyHints = new HashSet<string>(StringComparer.Ordinal)
        { "Baixo provável", "Acompanhamento provável", "Poucos dados" };

    public static IReadOnlyList<ScoreFlowCheck> Evaluate(ScoreFlowInput input)
    {
        var checks = new List<ScoreFlowCheck>();
        bool Confirmed(string id) => input.Confirmed.Contains(id);
        var melody = input.Tracks.Where(t => t.IsMelody).ToArray();

        // 1 · Melodia
        if (!input.ChordsOnly)
        {
            if (melody.Length == 0)
                checks.Add(new("melody-none", ScoreFlowStep.Melody, ScoreFlowLevel.Block,
                    "Marque pelo menos uma pista como melodia, ou ligue Só cifras.", "compare", "Comparar pistas", ConfirmLabel: null));
            foreach (var track in melody.Where(t => t.IsPercussion || t.Hint == "Percussão"))
                checks.Add(new("melody-percussion:" + track.Name, ScoreFlowStep.Melody, ScoreFlowLevel.Block,
                    $"{track.Name} é percussão e não pode ser melodia. Desmarque essa pista.", ConfirmLabel: null));
            foreach (var track in melody.Where(t => t.Hint == "Sem notas válidas" || t.Notes == 0))
                checks.Add(new("melody-empty:" + track.Name, ScoreFlowStep.Melody, ScoreFlowLevel.Block,
                    $"{track.Name} não tem notas válidas para a melodia. Desmarque essa pista.", ConfirmLabel: null));
            if (melody.Length > 0 && input.MelodyRange is { } range)
            {
                string span = $"A melodia vai de {VoiceRange.NoteName(range.Low)} a {VoiceRange.NoteName(range.High)}";
                if (range.Low < PianoLow || range.High > PianoHigh)
                    checks.Add(new("melody-outside-piano", ScoreFlowStep.Melody, ScoreFlowLevel.Block,
                        $"{span} e passa até do piano de 88 teclas ({VoiceRange.NoteName(PianoLow)}–{VoiceRange.NoteName(PianoHigh)}). Notas tão extremas empurram as cifras para o sistema vizinho. Desmarque a pista com notas extremas ou escolha as pistas por trecho.",
                        "sections", "Pistas por trecho", ConfirmLabel: null));
                else if (KeyboardRange.OctaveToFit(range.Low, range.High, input.KeyboardKeys) is null)
                {
                    var (low, high) = KeyboardRange.Of(input.KeyboardKeys);
                    checks.Add(new("melody-keyboard", ScoreFlowStep.Melody, ScoreFlowLevel.Confirm,
                        $"{span} e não cabe no seu teclado de {input.KeyboardKeys} teclas ({VoiceRange.NoteName(low)}–{VoiceRange.NoteName(high)}), nem mudando de oitava. Confira se todas as pistas marcadas são mesmo melodia.",
                        "compare", "Comparar pistas", Confirmed("melody-keyboard"), "Manter assim"));
                }
            }
            // A lyric guide note is not a melody: it plays one or two pitches under the words. It must be confirmed
            // explicitly, and the autopilot never picks it on its own.
            foreach (var track in melody.Where(t => t.GuideNotes))
            {
                string id = "melody-guide:" + track.Name;
                checks.Add(new(id, ScoreFlowStep.Melody, ScoreFlowLevel.Confirm,
                    $"{track.Name} quase não muda de altura: parece a nota-guia da letra, não a voz cantada. Desmarque essa pista e escolha a melodia de verdade (ou ligue Só cifras).",
                    "compare", "Comparar pistas", Confirmed(id), "É melodia"));
            }
            foreach (var track in melody.Where(t => DoubtfulMelodyHints.Contains(t.Hint)))
            {
                string id = "melody-hint:" + track.Name;
                string what = track.Hint switch { "Baixo provável" => "parece baixo", "Acompanhamento provável" => "parece acompanhamento", _ => "tem poucas notas para confirmar que é melodia" };
                checks.Add(new(id, ScoreFlowStep.Melody, ScoreFlowLevel.Confirm,
                    $"{track.Name} está marcada como melodia, mas {what}.", null, null, Confirmed(id), "É melodia"));
            }
            if (input.LyricsTrack is { Length: > 0 } lyrics)
            {
                // The chosen melody leaves out more than half of the words while one part carries 90% of them: a decision.
                bool critical = melody.Length > 0 && input.ChosenLyricsShare is < .5 && input.LyricsTrackShare is >= .9;
                checks.Add(critical
                    ? new("melody-lyrics", ScoreFlowStep.Melody, ScoreFlowLevel.Confirm,
                        $"A melodia escolhida cobre só {input.ChosenLyricsShare:P0} da letra, e {lyrics} acompanha {input.LyricsTrackShare:P0}. Decida: troque com um clique ou mantenha sabendo que a maior parte da letra fica fora da partitura.",
                        "use-lyrics", "Usar a pista da letra", Confirmed("melody-lyrics"), "Manter sem a letra") { Critical = true }
                    : new("melody-lyrics", ScoreFlowStep.Melody, ScoreFlowLevel.Confirm,
                        $"A letra acompanha {lyrics}, que não está marcada. Sem trocar, a maior parte da letra fica fora da partitura.",
                        "use-lyrics", "Usar a pista da letra", Confirmed("melody-lyrics"), "Seguir sem trocar"));
            }
            if (melody.Length > 0)
                checks.Add(new("melody-listen", ScoreFlowStep.Melody, ScoreFlowLevel.Confirm,
                    melody.Length == 1 ? "Ouça a pista marcada antes de seguir." : $"Ouça as {melody.Length} pistas marcadas juntas antes de seguir.",
                    "listen", "Ouvir marcadas", input.HeardMarked || Confirmed("melody-listen"), "Já ouvi"));
        }

        // 2 · Ritmo e acordes
        if (!input.HasAnalysis)
            checks.Add(new("analysis-missing", ScoreFlowStep.Analysis, ScoreFlowLevel.Block,
                "Analise o ritmo e os acordes da melodia escolhida.", "analyze", "Analisar", ConfirmLabel: null));
        else
        {
            if (input.ReviewPoints > 0)
                checks.Add(new("analysis-review", ScoreFlowStep.Analysis, ScoreFlowLevel.Confirm,
                    input.BlockingReviewPoints > 0
                        ? $"A análise marcou {Count(input.ReviewPoints, "ponto", "pontos")} para conferir, {input.BlockingReviewPoints} de ritmo que precisam de revisão."
                        : $"A análise marcou {Count(input.ReviewPoints, "ponto", "pontos")} para conferir (ritmo, cifras ou tonalidade).",
                    "review", "Revisão guiada", Confirmed("analysis-review"), "Conferi"));
            int crowded = CrowdedChanges(input.Chords);
            if (crowded > 0)
                checks.Add(new("analysis-crowded", ScoreFlowStep.Analysis, ScoreFlowLevel.Confirm,
                    $"{Count(crowded, "troca de acorde acontece", "trocas de acorde acontecem")} a menos de um tempo da anterior; os diagramas podem ficar apertados. Confira essas cifras na tabela.",
                    null, null, Confirmed("analysis-crowded"), "Manter"));
        }

        // 3 · Cifras
        if (input.HasAnalysis)
        {
            var duplicates = input.Chords.GroupBy(c => Math.Round(c.Quarter, 3)).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (duplicates.Length > 0)
                checks.Add(new("chords-duplicate", ScoreFlowStep.Chords, ScoreFlowLevel.Block,
                    $"Há {Count(duplicates.Length, "posição", "posições")} com duas cifras ({string.Join(", ", duplicates.Take(3).Select(q => q.ToString("0.###")))}). Exclua uma das linhas repetidas.",
                    ConfirmLabel: null));
            if (input.Chords.Count == 0 && !input.ChordsOnly)
                checks.Add(new("chords-empty", ScoreFlowStep.Chords, ScoreFlowLevel.Confirm,
                    "A análise não encontrou acordes; a partitura sairá sem cifras.", null, null, Confirmed("chords-empty"), "Seguir sem cifras"));
            if (input.DoubtfulChords > 0)
                checks.Add(new("chords-doubtful", ScoreFlowStep.Chords, ScoreFlowLevel.Confirm,
                    $"{Count(input.DoubtfulChords, "cifra tem", "cifras têm")} dúvida. Ouça o trecho e as alternativas.",
                    "chord-review", "Revisar cifras", Confirmed("chords-doubtful"), "Conferi"));
            if (input.KeyName.Length > 0)
                checks.Add(new("chords-key", ScoreFlowStep.Chords, ScoreFlowLevel.Confirm,
                    $"Tom detectado: {input.KeyName}. Confira a armadura acima da tabela.", null, null, Confirmed("chords-key"), "Tom certo"));
        }
        return checks;
    }

    /// <summary>The furthest step the user may open: the first step that still has a pending check, or Generate.</summary>
    public static ScoreFlowStep Reachable(IReadOnlyList<ScoreFlowCheck> checks) =>
        checks.Where(c => c.Pending).Select(c => c.Step).DefaultIfEmpty(ScoreFlowStep.Generate).Min();

    public static bool CanGenerate(IReadOnlyList<ScoreFlowCheck> checks) => !checks.Any(c => c.Pending);

    public static int CrowdedChanges(IReadOnlyList<ScoreFlowChord> chords)
    {
        var ordered = chords.OrderBy(c => c.Quarter).ToArray();
        int count = 0;
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i].Quarter - ordered[i - 1].Quarter is > 0.001 and < CrowdedChordGap) count++;
        return count;
    }

    private static string Count(int n, string one, string many) => n == 1 ? "1 " + one : $"{n} {many}";
}
