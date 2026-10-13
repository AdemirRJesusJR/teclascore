using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanOrderMelodyTracks => CanSelectTracks && selectedFile?.Tracks.Count(track => track.IsMelody) > 1;
    public string MelodyPrioritySummary
    {
        get
        {
            if (selectedFile is not { Document: not null, UsesNativeScoreWorkflow: false } row) return "";
            var ordered = GetOrderedMelodySources(row);
            if (ordered.Count == 0) return "Nenhuma pista selecionada para a melodia.";
            if (row.UseReviewedProfile) return $"{ordered.Count} pista{(ordered.Count == 1 ? "" : "s")} selecionada{(ordered.Count == 1 ? "" : "s")} · ordem do perfil revisado. Desmarque a revisão para escolher a prioridade manualmente.";
            if (ordered.Count == 1) return "1 pista selecionada · uma pauta de melodia.";
            return $"{ordered.Count} pistas selecionadas · prioridade: " +
                string.Join(" → ", ordered.Take(4).Select(key => $"pista {key.Track + 1}/canal {key.Channel}")) +
                (ordered.Count > 4 ? " → …" : "") + ". Uma pauta de melodia.";
        }
    }

    private void NotifyMelodyPriority()
    {
        Notify(nameof(CanOrderMelodyTracks));
        Notify(nameof(MelodyPrioritySummary));
    }

    private static IReadOnlyList<SourceKey> GetOrderedMelodySources(MidiRow row)
    {
        var selected = row.Tracks.Where(track => track.IsMelody).Select(track => track.Source.Key).Distinct().ToHashSet();
        var preferred = row.MelodySourcePriority.Where(selected.Contains).Distinct();
        return preferred.Concat(selected.OrderBy(key => key.Track).ThenBy(key => key.Channel).ThenBy(key => key.Program))
            .Distinct().ToArray();
    }

    private void MelodyPriorityClick(object sender, RoutedEventArgs e)
    {
        if (!CanOrderMelodyTracks || selectedFile is not { } row || !CommitEdits()) return;
        var byKey = row.Tracks.Where(track => track.IsMelody).ToDictionary(track => track.Source.Key);
        var dialog = new MelodyPriorityWindow(GetOrderedMelodySources(row).Select(key => byKey[key]).ToArray()) { Owner = this };
        if (dialog.ShowDialog() == true) ApplyMelodyPriority(row, dialog.Priority);
    }

    private void ApplyMelodyPriority(MidiRow row, IReadOnlyList<SourceKey> priority)
    {
        if (!IsIdle || selectedFile != row || row.Document is null || row.UsesNativeScoreWorkflow || row.UseReviewedProfile) return;
        var selected = GetOrderedMelodySources(row);
        if (selected.Count < 2) return;
        if (priority is null || priority.Count != selected.Count || priority.Distinct().Count() != priority.Count ||
            !priority.ToHashSet().SetEquals(selected))
            throw new ArgumentException("Ordene exatamente as pistas selecionadas para a melodia.", nameof(priority));
        if (selected.SequenceEqual(priority))
        {
            Status = "A prioridade das pistas foi mantida. Sua análise e seus ajustes continuam disponíveis.";
            return;
        }
        RememberVersion(row);
        row.MelodySourcePriority = priority.Concat(row.MelodySourcePriority).Distinct().ToList();
        InvalidateAnalysis(row);
        RefreshSelection();
        NotifyMelodyPriority();
        PersistPreferences();
        Status = "Prioridade atualizada. Analise os acordes ou gere a partitura para combinar as pistas em uma pauta.";
    }

    private static List<SavedSource> SaveMelodyPriority(IEnumerable<SourceKey> priority) =>
        priority.Distinct().Select(key => new SavedSource { Track = key.Track, Channel = key.Channel, Program = key.Program }).ToList();

    private static List<SourceKey> RestoreMelodyPriority(IEnumerable<SavedSource>? priority) =>
        Settings.NormalizeMelodySourcePriority(priority).Select(source => new SourceKey(source.Track, source.Channel, source.Program)).ToList();
}
