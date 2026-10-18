using System.Text.Json.Nodes;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Typing the whole chord chart of a song and applying it measure by measure (then the score is made again).</summary>
public partial class MainWindow
{
    internal Func<TypedChordsWindow, bool?> typedChordsDialog = window => window.ShowDialog();

    /// <summary>Quarters per measure (first time signature) and the start of the first measure with notes, as in the score.</summary>
    internal static (double Measure, double Origin) ChartGrid(MidiRow row)
    {
        double measure = 4, first = 0;
        if (row.Document is { } document)
        {
            if (document.TimeSignatures.Count > 0 && document.TimeSignatures[0] is { Numerator: > 0, Denominator: > 0 } meter) measure = meter.Numerator * 4.0 / meter.Denominator;
            var notes = document.Notes.Where(n => n.Channel != 10).ToList();
            if (notes.Count > 0) first = notes.Min(n => n.Start) / (double)document.Ppq;
        }
        return (measure, ChordChartText.Origin(first, measure));
    }

    /// <summary>A slash chord's bass goes to the engine only when known (typed), so analyzed basses are kept.</summary>
    internal static JsonObject WithBass(JsonObject chord, ChordRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.Bass)) chord["bass"] = row.Bass;
        return chord;
    }

    internal string ChartText(MidiRow row)
    {
        var (measure, origin) = ChartGrid(row);
        return ChordChartText.Format(row.Chords.Select(c => new ChartChord(c.Quarter, c.Root, c.Quality, c.Bass)).ToList(), measure, origin);
    }

    private async void TypedChordsClick(object sender, RoutedEventArgs e)
    {
        if (!CanCustomizeDiagrams || selectedFile is not { } row || !CommitEdits()) return;
        var (measure, origin) = ChartGrid(row);
        var window = new TypedChordsWindow(string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle, ChartText(row), measure, origin) { Owner = this };
        if (typedChordsDialog(window) != true) return;
        await ApplyTypedChordsAsync(row, window.Result);
    }

    /// <summary>Replaces the song's chords with the typed ones (the previous version stays in the history) and generates again.</summary>
    internal async Task ApplyTypedChordsAsync(MidiRow row, IReadOnlyList<ChartChord> chords)
    {
        RequireMidiWorkflow(row);
        RememberVersion(row);
        row.Chords.Clear();
        foreach (var chord in chords) row.Chords.Add(new ChordRow { Quarter = chord.Quarter, Root = chord.Root, Quality = chord.Quality, Bass = chord.Bass, Confidence = 1 });
        row.HasPendingScoreProperties = true; row.Approved = false;
        ResetPlayback(); RefreshSelection(); PersistPreferences();
        if (!CanRegenerate(row)) { Status = $"{chords.Count} acorde(s) digitado(s). Gere a partitura para ver os novos diagramas."; return; }
        await RunOperation(async () => await (diagramGeneration?.Invoke(row) ?? Generate(row)), operationTitle: "Aplicando a cifra digitada", operationItem: row.Name);
        RefreshConfidence(row);
        Status = $"Cifra digitada aplicada: {chords.Count} acorde(s), partitura gerada de novo. A versão anterior está no histórico.";
    }
}
