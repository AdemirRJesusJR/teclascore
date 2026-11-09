using Teclas.Core;

namespace Teclas.App;

/// <summary>What kind of thing a review point asks to look at.</summary>
public enum ReviewSequenceKind { Chord, EmptyMeasures, Rhythm, Lyrics, Harmony, General }

/// <summary>A chord alternative with the key that chooses it ("1 · Am").</summary>
public sealed record ReviewAlternativeOption(int Key, ChordAlternative Alternative)
{
    public string Label => $"{Key} · {Alternative.Label}";
}

/// <summary>
/// One place to look at in the review in sequence: a doubtful chord with its alternatives, a run of measures without
/// melody, a rhythm or lyrics passage the analysis flagged, or a general warning (no measure).
/// </summary>
public sealed class ReviewSequencePoint : Observable
{
    public ReviewSequenceKind Kind { get; init; }
    /// <summary>First and last score measure (1-based); 0 for a general warning.</summary>
    public int Measure { get; init; }
    public int MeasureEnd { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public ChordRow? Chord { get; init; }
    public IReadOnlyList<ChordAlternative> Alternatives { get; init; } = [];
    public IReadOnlyList<ReviewAlternativeOption> Options => Alternatives.Select((alternative, index) => new ReviewAlternativeOption(index + 1, alternative)).ToArray();
    private string chosen = "";
    /// <summary>The alternative chosen with 1/2 ("Am"), or "manter" when the current chord was kept.</summary>
    public string Chosen { get => chosen; set { if (Set(ref chosen, value ?? "")) { Notify(nameof(Done)); Notify(nameof(StateText)); } } }
    public bool Done => Chosen.Length > 0;
    public bool HasMeasure => Measure > 0;
    public string MeasureText => Measure == 0 ? "" : MeasureEnd > Measure ? $"c. {Measure}–{MeasureEnd}" : $"c. {Measure}";
    public string StateText => Chosen.Length == 0 ? "" : Chosen == "manter" ? "✓ mantida" : "✓ " + Chosen;
    public string Glyph => Kind switch
    {
        ReviewSequenceKind.Chord => "", ReviewSequenceKind.EmptyMeasures => "", ReviewSequenceKind.Rhythm => "",
        ReviewSequenceKind.Lyrics => "", ReviewSequenceKind.Harmony => "", _ => ""
    };
}

public static class ReviewSequencePoints
{
    /// <summary>The points of a song, in score order: chords with doubt first in their measures, then the other kinds; general warnings last.</summary>
    public static IReadOnlyList<ReviewSequencePoint> Build(MidiRow row, ScoreValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(row); ArgumentNullException.ThrowIfNull(report);
        var points = new List<ReviewSequencePoint>();
        var meter = row.Meter;
        foreach (var chord in row.Chords)
        {
            var alternatives = ChordAlternative.Parse(chord.Alternatives);
            bool doubtful = chord.Confidence > 0 && chord.Confidence < ScoreConfidence.DoubtfulChord;
            if (!doubtful && alternatives.Count == 0) continue;
            int measure = report.MeasureAtMidiQuarter(chord.Quarter) ?? (meter is not null ? Math.Max(1, meter.MeasureAt(chord.Quarter).Number - RemovedMeasures(report, meter)) : 0);
            string label = chord.Root + chord.Quality;
            string title = chord.Confidence > 0 ? $"Cifra {label} ({chord.Confidence:P0})" : $"Cifra {label}";
            string detail = alternatives.Count > 0 ? "Alternativas: " + string.Join(", ", alternatives.Select(a => a.Label)) + (doubtful ? " · confiança baixa" : "") : "Confiança baixa: ouça o acompanhamento neste compasso";
            points.Add(new() { Kind = ReviewSequenceKind.Chord, Measure = measure, MeasureEnd = measure, Title = title, Detail = detail, Chord = chord, Alternatives = alternatives });
        }
        foreach (var point in row.ReviewPoints)
        {
            var kind = point.Kind.StartsWith("chord", StringComparison.Ordinal) || point.Kind.StartsWith("harmony", StringComparison.Ordinal) || point.Kind.StartsWith("key", StringComparison.Ordinal)
                ? ReviewSequenceKind.Harmony : point.Kind.StartsWith("lyrics", StringComparison.Ordinal) ? ReviewSequenceKind.Lyrics : ReviewSequenceKind.Rhythm;
            // Chord points of the analysis repeat the chord rows above when a chord row sits in the same measure.
            if (kind == ReviewSequenceKind.Harmony && points.Any(p => p.Kind == ReviewSequenceKind.Chord && p.Measure == point.Measure)) continue;
            points.Add(new() { Kind = kind, Measure = point.Measure, MeasureEnd = point.Measure, Title = point.KindLabel, Detail = point.Message.Length > 0 ? point.Message : "Confira este compasso na partitura." });
        }
        foreach (var (from, to) in report.EmptyRanges())
            points.Add(new() { Kind = ReviewSequenceKind.EmptyMeasures, Measure = from, MeasureEnd = to, Title = to > from ? "Compassos sem melodia" : "Compasso sem melodia", Detail = "A melodia escolhida não toca aqui: confira se é um trecho instrumental ou se faltam notas." });
        var ordered = points.Where(p => p.Measure > 0).OrderBy(p => p.Measure).ThenBy(p => p.Kind).ToList();
        foreach (string warning in report.HarmonicWarnings) ordered.Add(new() { Kind = ReviewSequenceKind.General, Title = "Harmonia", Detail = warning });
        foreach (string issue in report.QualityIssues) ordered.Add(new() { Kind = ReviewSequenceKind.General, Title = "Escrita", Detail = issue });
        return ordered;
    }

    /// <summary>Without the report's bars, the chord's MIDI measure minus the measures the score removed at the start.</summary>
    private static int RemovedMeasures(ScoreValidationReport report, SongMeter meter)
    {
        if (report.OriginQuarters <= 0 || meter.Measures.Count == 0) return 0;
        return meter.Measures.Count(m => m.EndQuarter <= report.OriginQuarters + 1e-6);
    }
}
