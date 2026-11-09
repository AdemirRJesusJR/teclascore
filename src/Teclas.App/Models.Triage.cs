using System.Windows;

namespace Teclas.App;

/// <summary>
/// What the Mesa de triagem (the table view of the library) shows per song: the chosen melody, the key, the
/// confidence as a number and the situation (Pronta, Para revisar, Para gerar, Precisa de você, Falhou) with a short
/// reason. The main window fills <see cref="TriageNeed"/> with the autopilot's verdict for songs without a melody.
/// </summary>
public sealed partial class MidiRow
{
    private string triageNeed = "";

    /// <summary>Why the autopilot cannot choose the melody of this song ("" when it can or when it is not needed).</summary>
    public string TriageNeed { get => triageNeed; set { if (Set(ref triageNeed, value ?? "")) NotifyTriage(); } }

    /// <summary>"Pista 2 · Piano" (several joined by " + "), "Só cifras", "Perfil revisado" or "—".</summary>
    public string MelodyChoiceText
    {
        get
        {
            if (UsesNativeScoreWorkflow) return IsNativeScore ? "Partitura MuseScore" : "Editada no editor";
            if (ChordsOnly) return "Só cifras";
            if (UseReviewedProfile) return "Perfil revisado";
            var chosen = Tracks.Where(t => t.IsMelody).ToArray();
            if (chosen.Length == 0) return "—";
            return string.Join(" + ", chosen.Select(t => $"Pista {t.Track} · {t.Instrument}"));
        }
    }

    public string MelodyChoiceToolTip
    {
        get
        {
            var chosen = Tracks.Where(t => t.IsMelody).ToArray();
            if (chosen.Length == 0) return ChordsOnly ? "Partitura só com cifras e diagramas, sem melodia." : UseReviewedProfile ? "Melodia e cifras do perfil revisado." : "Nenhuma pista escolhida como melodia.";
            return string.Join("\n", chosen.Select(t => $"Pista {t.Track}, canal {t.Channel}: {t.Instrument} ({t.Count} notas, {t.Range})"))
                + (AutoMelody ? AutoMelodyConfident ? "\nEscolhida automaticamente (escolha clara)." : "\nEscolhida automaticamente (escolha incerta)." : "");
        }
    }

    /// <summary>The key of the score (after the transposition), when the chords were analyzed.</summary>
    public string KeyText => MainWindow.KeyName(this, TransposeSemitones);

    /// <summary>The confidence as a number for sorting: -1 without a score, 101 when approved.</summary>
    public int ConfidenceScore => ScorePath is null ? -1 : Approved ? 101 : Confidence?.Score ?? -1;
    public string ConfidenceText => ScorePath is null ? "" : Approved ? "✓" : Confidence is { } c ? $"{c.Score}%" : "";
    public string PagesText => ScorePath is null || PageCount <= 0 ? "" : PageCount.ToString();

    /// <summary>Pronta · Para revisar · Para gerar · Precisa de você · Falhou (plus Na fila / Gerando…).</summary>
    public string TriageStatus => TriageInfo().Status;
    /// <summary>The short reason under the situation: the review reasons, the error or what the song is waiting for.</summary>
    public string TriageReason => TriageInfo().Reason;
    /// <summary>Order for the Situação column: what needs the user first, then failures, review, to generate, ready.</summary>
    public int TriageOrder => TriageInfo().Order;
    /// <summary>ready · review · accent · need · error: the color of the situation chip.</summary>
    public string TriageTone => TriageInfo().Tone;
    public string TriageToolTip => TriageReason.Length == 0 ? TriageStatus : TriageStatus + ": " + TriageReason;

    private (string Status, string Reason, int Order, string Tone) TriageInfo()
    {
        if (QueueState == QueueCardState.Generating) return ("Gerando…", "na fila de conversões em segundo plano", 3, "accent");
        if (QueueState == QueueCardState.Waiting) return ("Na fila", "aguardando a vez na fila de conversões", 3, "accent");
        if (Category == Teclas.Core.DashboardCategory.Error)
            return ("Falhou", Document is null && !UsesNativeScoreWorkflow ? "não foi possível ler o MIDI" : UsesNativeScoreWorkflow ? "a partitura não está no disco" : Status.Length > 0 ? Shorten(Status) : "a conversão falhou; abra a música para ver o motivo", 1, "error");
        if (ScorePath is not null)
        {
            if (Approved) return ("Pronta", "aprovada na revisão", 5, "ready");
            if (HasPendingScoreProperties) return ("Para gerar", "alterações pendentes: gere de novo para atualizar a partitura", 3, "accent");
            if (Confidence is { } c)
                return c.Score >= 85 ? ("Pronta", c.Reasons.Count == 0 ? "nada chamou atenção na análise" : Shorten(string.Join("; ", c.Reasons)), 5, "ready")
                    : ("Para revisar", Shorten(string.Join("; ", c.Reasons)), 2, "review");
            return ("Pronta", "partitura gerada", 5, "ready");
        }
        if (UsesNativeScoreWorkflow) return ("Para gerar", "partitura MuseScore importada", 3, "accent");
        if (TriageNeed.Length > 0) return ("Precisa de você", Shorten(TriageNeed), 0, "need");
        if (UseReviewedProfile || HasMelodyChoice) return ("Para gerar", "melodia escolhida; ainda sem partitura", 3, "accent");
        return ("Para gerar", "melodia clara pela análise; ainda sem partitura", 3, "accent");
    }

    private static string Shorten(string text)
    {
        text = text.Replace('\n', ' ').Trim().TrimEnd('.');
        return text.Length <= 90 ? text : text[..87].TrimEnd() + "…";
    }

    public Visibility TriageReasonVisibility => TriageReason.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    internal void NotifyTriage()
    {
        foreach (string name in new[] { nameof(MelodyChoiceText), nameof(MelodyChoiceToolTip), nameof(KeyText), nameof(ConfidenceScore), nameof(ConfidenceText),
            nameof(PagesText), nameof(TriageStatus), nameof(TriageReason), nameof(TriageOrder), nameof(TriageTone), nameof(TriageToolTip), nameof(TriageReasonVisibility) })
            Notify(name);
    }
}
