using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Rules learned per collection: the melody part, transposition and diagram position chosen for one song can be
/// repeated on the songs of the same folder or collection that have the same arrangement and no melody choice yet.
/// </summary>
public partial class MainWindow
{
    private static IReadOnlyList<RuleTrack> RuleTracks(MidiRow row) => row.Tracks.Select(t => new RuleTrack(t.Source.Key, t.Instrument, t.Source.Count)).ToArray();

    /// <summary>Songs that would receive the selected song's choices, with the part that would become their melody.</summary>
    internal IReadOnlyList<(MidiRow Row, TrackRow Track)> CollectionRuleTargets(MidiRow source)
    {
        if (source.UsesNativeScoreWorkflow || source.Document is null || source.ChordsOnly) return [];
        var melody = source.Tracks.Where(t => t.IsMelody).ToList();
        if (melody.Count != 1) return [];
        string? folder = Path.GetDirectoryName(source.FullPath);
        var sourceTracks = RuleTracks(source);
        var key = new RuleTrack(melody[0].Source.Key, melody[0].Instrument, melody[0].Source.Count);
        var result = new List<(MidiRow, TrackRow)>();
        foreach (var row in Files)
        {
            if (row == source || row.UsesNativeScoreWorkflow || row.Document is null || row.HasMelodyChoice || row.UseReviewedProfile) continue;
            bool sameGroup = string.Equals(Path.GetDirectoryName(row.FullPath), folder, StringComparison.OrdinalIgnoreCase)
                || source.Collection.Length > 0 && string.Equals(row.Collection, source.Collection, StringComparison.OrdinalIgnoreCase);
            if (!sameGroup) continue;
            if (CollectionRule.Match(sourceTracks, key, RuleTracks(row)) is { } match && row.Tracks.FirstOrDefault(t => t.Source.Key == match.Key) is { } track)
                result.Add((row, track));
        }
        return result;
    }

    public string CollectionRuleHint
    {
        get
        {
            if (selectedFile is not { } row) return "";
            var targets = CollectionRuleTargets(row);
            if (targets.Count == 0) return "";
            var melody = row.Tracks.First(t => t.IsMelody);
            string extras = (row.TransposeSemitones != 0 ? $", transposição {row.TransposeSemitones:+0;-0}" : "") + $", diagramas na {row.DefaultDiagramInversion + 1}ª posição";
            return $"{targets.Count} música(s) da mesma {(row.Collection.Length > 0 ? "coleção ou pasta" : "pasta")} têm as mesmas pistas e ainda não têm melodia. " +
                   $"Aplicar a mesma escolha: melodia em {melody.Instrument} (canal {melody.Channel}){extras}?";
        }
    }

    public Visibility CollectionRuleVisibility => CollectionRuleHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Applies the selected song's choices to the similar songs; returns how many changed.</summary>
    internal int ApplyCollectionRule(MidiRow source)
    {
        var targets = CollectionRuleTargets(source);
        foreach (var (row, track) in targets)
        {
            foreach (var other in row.Tracks) other.IsMelody = other == track;
            row.TransposeSemitones = source.TransposeSemitones;
            row.DefaultDiagramInversion = source.DefaultDiagramInversion;
            row.AutoMelody = true; row.AutoMelodyConfident = true;
        }
        if (targets.Count > 0) { PersistPreferences(); libraryDirty = true; RefreshDashboard(); }
        return targets.Count;
    }

    private void ApplyCollectionRuleClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || selectedFile is not { } row || !CommitEdits()) return;
        int count = ApplyCollectionRule(row);
        Status = count == 0 ? "Nenhuma música parecida para receber a escolha." :
            $"Escolha aplicada a {count} música(s) parecida(s). Gere todas de uma vez com o Piloto automático ou pela fila.";
        NotifyCollectionRule();
    }

    private void NotifyCollectionRule() { Notify(nameof(CollectionRuleHint)); Notify(nameof(CollectionRuleVisibility)); }
}
