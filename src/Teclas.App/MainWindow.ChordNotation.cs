using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>"Escrita das cifras" (Settings): the user's chord notation, a preview, and applying it to the generated scores.</summary>
public partial class MainWindow
{
    public sealed record NotationOption(string Value, string Label);
    public IReadOnlyList<NotationOption> Major7Options { get; } = [new("maj7", "Cmaj7"), new("7M", "C7M"), new("Δ", "CΔ")];
    public IReadOnlyList<NotationOption> HalfDiminishedOptions { get; } = [new("m7b5", "Bm7b5"), new("m7(b5)", "Bm7(b5)"), new("ø", "Bø")];
    public IReadOnlyList<NotationOption> DiminishedOptions { get; } = [new("dim", "Bdim"), new("°", "B°")];
    public IReadOnlyList<NotationOption> Add9Options { get; } = [new("add9", "Cadd9"), new("(9)", "C(9)")];
    public IReadOnlyList<NotationOption> AccidentalOptions { get; } = [new("auto", "Pelo tom da música"), new("flats", "Sempre bemóis (Bb)"), new("sharps", "Sempre sustenidos (A#)")];

    internal static ChordNotationPreference ToPreference(SavedChordNotation? saved) => saved is null ? ChordNotationPreference.Default
        : new ChordNotationPreference(saved.Major7, saved.HalfDiminished, saved.Diminished, saved.Add9, saved.Accidentals).Normalized();
    private ChordNotationPreference Notation => ToPreference(settings.ChordNotation);
    private void SetNotation(ChordNotationPreference value)
    {
        var normalized = value.Normalized();
        settings.ChordNotation = new SavedChordNotation { Major7 = normalized.Major7, HalfDiminished = normalized.HalfDiminished, Diminished = normalized.Diminished, Add9 = normalized.Add9, Accidentals = normalized.Accidentals };
        ChordNotation.Current = normalized;
        foreach (var name in new[] { nameof(NotationMajor7), nameof(NotationHalfDiminished), nameof(NotationDiminished), nameof(NotationAdd9), nameof(NotationAccidentals), nameof(NotationPreview), nameof(NotationApplyHint) }) Notify(name);
        PersistPreferences();
    }
    public string NotationMajor7 { get => Notation.Major7; set => SetNotation(Notation with { Major7 = value }); }
    public string NotationHalfDiminished { get => Notation.HalfDiminished; set => SetNotation(Notation with { HalfDiminished = value }); }
    public string NotationDiminished { get => Notation.Diminished; set => SetNotation(Notation with { Diminished = value }); }
    public string NotationAdd9 { get => Notation.Add9; set => SetNotation(Notation with { Add9 = value }); }
    public string NotationAccidentals { get => Notation.Accidentals; set => SetNotation(Notation with { Accidentals = value }); }

    /// <summary>A few chords as they will be written.</summary>
    public string NotationPreview => "Exemplo: " + string.Join("  ·  ", new[] { ("C", "maj7"), ("B", "m7b5"), ("F#", "dim"), ("A#", ""), ("G", "add9"), ("D", "maj9") }
        .Select(c => ChordNotation.Root(c.Item1, Notation) + ChordNotation.Display(c.Item2, Notation)));

    /// <summary>Generated scores that can be made again from their MIDI with the new notation.</summary>
    internal IReadOnlyList<MidiRow> NotationTargets() => Files.Where(r => r.ScorePath is not null && !r.UsesNativeScoreWorkflow && r.Document is not null
        && (r.HasMelodyChoice || r.UseReviewedProfile || r.ChordsOnly)).ToList();

    public string NotationApplyHint
    {
        get
        {
            int count = NotationTargets().Count, native = Files.Count(r => r.ScorePath is not null && r.UsesNativeScoreWorkflow);
            return (count == 0 ? "As próximas partituras já saem nesta escrita." : $"As próximas partituras já saem nesta escrita. Para as {count} já geradas, use Aplicar à biblioteca (gera de novo pela fila).") +
                   (native > 0 ? $" {native} partitura(s) editada(s) no MuseScore mantêm a escrita atual." : "");
        }
    }

    /// <summary>The engine field for generation requests (absent for the default notation).</summary>
    internal void AddNotation(JsonObject request)
    {
        if (ChordNotation.EngineOption(Notation) is { } option) request["chordNotation"] = JsonSerializer.SerializeToNode(option);
    }

    private async void ApplyNotationClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var targets = NotationTargets();
        if (targets.Count == 0) { Status = "Nenhuma partitura gerada a partir de MIDI para atualizar."; return; }
        var confirm = SimpleDialog("Aplicar a escrita das cifras", $"{targets.Count} partitura(s) serão geradas de novo pela fila com a escrita escolhida ({NotationPreview.Replace("Exemplo: ", "")}). As versões anteriores ficam no histórico de cada música.", "Aplicar");
        if (autopilotDialog(confirm) != true) return;
        await ApplyNotationToLibraryAsync(targets);
    }

    internal async Task<int> ApplyNotationToLibraryAsync(IReadOnlyList<MidiRow> targets)
    {
        IReadOnlyDictionary<MidiRow, string> failures = new Dictionary<MidiRow, string>();
        await RunOperation(async () =>
        {
            Status = $"Atualizando a escrita das cifras em {targets.Count} partitura(s)…";
            failures = autopilotGenerate is { } generate ? await generate(targets, cancellation!.Token) : await GenerateWithAdjustmentsAsync(targets, cancellation!.Token);
        }, operationTitle: "Escrita das cifras");
        int done = targets.Count - failures.Count;
        RefreshSelection(); PersistPreferences();
        Status = $"Escrita das cifras aplicada a {done} partitura(s)" + (failures.Count > 0 ? $"; {failures.Count} falharam: {string.Join("; ", failures.Select(f => f.Key.Name + " — " + f.Value))}." : ".");
        return done;
    }
}
