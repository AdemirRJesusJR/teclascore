using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

/// <summary>"Montar a melodia por trechos": proposes where the melody passes between parts, for the user to confirm.</summary>
public partial class MainWindow
{
    /// <summary>Test hook: the measure map of a song (instead of asking the engine).</summary>
    internal Func<MidiRow, Task<MelodyMeasureMap>>? measureMapProvider = null;
    internal Func<Window, bool?> autoSectionsDialog = window => window.ShowDialog();

    /// <summary>The proposal for a song: sections by measure, from the real notes, lyrics and melody ranking.</summary>
    internal static IReadOnlyList<SavedMelodySection> ProposeSections(MidiRow row, MelodyMeasureMap map)
    {
        if (row.Document is not { } document) return [];
        double ppq = document.Ppq;
        var notes = document.Notes.Where(n => n.Channel != 10).Select(n => new SectionNote(new SourceKey(n.Track, n.Channel, n.Program), n.Start / ppq, n.End / ppq, n.Pitch)).ToList();
        var syllables = LyricsTrack.Syllables(document).Select(t => t / ppq).ToList();
        var lyrics = LyricsTrack.Coverage(document);
        // Ranking: the part that follows the lyrics first, then the advisor's melody candidates.
        var ranking = lyrics.Where(c => c.Share >= .2).Select(c => c.Source)
            .Concat(row.Tracks.Where(t => t.MelodySuggestion is not null && t.Channel != 10).OrderBy(t => t.MelodySuggestion!.Rank).Select(t => t.Source.Key))
            .Distinct().ToList();
        var proposal = MelodySectionAdvisor.Propose(map.Measures.Select(m => (m.StartQuarter, m.EndQuarter)).ToList(), notes, syllables, ranking);
        return proposal.Select(p => new SavedMelodySection { FirstMeasure = p.FirstMeasure, LastMeasure = p.LastMeasure, Source = new SavedSource { Track = p.Source.Track, Channel = p.Source.Channel, Program = p.Source.Program } }).ToList();
    }

    private async void AutoSectionsClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditMelodySections || selectedFile is not { Document: { } document } row || !CommitEdits()) return;
        MelodyMeasureMap? map = null;
        if (measureMapProvider is { } provide) map = await provide(row);
        else await RunOperation(async () =>
        {
            ValidateSettings(false);
            string workspace = Path.Combine(LocalJobsDirectory, "measure-maps", Guid.NewGuid().ToString("N"));
            var response = await new EngineClient(PythonPath, engineDirectory).RunAsync(new JsonObject { ["mode"] = "measureMap", ["inputMidi"] = row.FullPath, ["workspace"] = workspace },
                workspace, CreateOperationProgress(), cancellation!.Token);
            map = MelodyMeasureMap.Read(response);
        }, operationTitle: "Lendo compassos da música", operationItem: row.ScoreTitle);
        if (map is null || selectedFile != row || row.Document != document) return;
        await ProposeAndApplySectionsAsync(row, map);
    }

    /// <summary>Shows the proposal and applies it when confirmed; returns the sections applied (empty when none).</summary>
    internal Task<IReadOnlyList<SavedMelodySection>> ProposeAndApplySectionsAsync(MidiRow row, MelodyMeasureMap map)
    {
        var sections = ProposeSections(row, map);
        if (sections.Count == 0) { Status = "A melodia não troca de instrumento nesta música: uma só pista basta (escolha-a na aba Melodia)."; return Task.FromResult<IReadOnlyList<SavedMelodySection>>([]); }
        string Name(SavedSource s) => row.Tracks.FirstOrDefault(t => t.Source.Key == new SourceKey(s.Track, s.Channel, s.Program)) is { } t ? $"{t.Instrument} (pista {t.Track}, canal {t.Channel})" : $"pista {s.Track + 1}";
        var list = new TextBox { Text = string.Join("\n", sections.Select(s => $"Compassos {s.FirstMeasure}–{s.LastMeasure}: {Name(s.Source)}")), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, Name = "AutoSectionsText" };
        var confirm = SimpleDialog("Melodia por trechos", "Proposta montada só com as notas do MIDI (nenhuma voz é inventada). Confira ouvindo e ajuste em Trechos se precisar.", "Usar estes trechos", list);
        if (autoSectionsDialog(confirm) != true) return Task.FromResult<IReadOnlyList<SavedMelodySection>>([]);
        var main = sections.GroupBy(s => (s.Source.Track, s.Source.Channel, s.Source.Program)).OrderByDescending(g => g.Sum(s => s.LastMeasure - s.FirstMeasure + 1)).First().Key;
        foreach (var track in row.Tracks) track.IsMelody = track.Source.Key == new SourceKey(main.Track, main.Channel, main.Program);
        ApplyMelodySections(row, map, sections);
        Status = $"Melodia montada em {sections.Count} trecho(s). Analise ou gere a partitura para aplicar.";
        return Task.FromResult(sections);
    }
}
