using Teclas.Core;

namespace Teclas.App;

/// <summary>While the score plays, a large keyboard diagram of the chord being played (and the next one) over the preview.</summary>
public partial class MainWindow
{
    private sealed record TimedChord(int Measure, double Quarter, string Name, string Root, string Quality, string? Bass, int Inversion);
    private string? chordFollowScore;
    private IReadOnlyList<TimedChord> chordFollowList = [];
    private IReadOnlyDictionary<int, double> chordFollowStarts = new Dictionary<int, double>();
    private bool chordFollowLoading;
    private readonly KeyboardDiagramPreview chordFollowImages = new();
    internal string CurrentChordName { get; private set; } = "";

    public bool CurrentChordEnabled
    {
        get => settings.CurrentChord;
        set
        {
            if (settings.CurrentChord == value) return;
            settings.CurrentChord = value; Notify(); PersistPreferences();
            if (value && !FollowScorePlayback) Status = "Acorde atual ligado: ligue também Acompanhar (Páginas) para o diagrama seguir a reprodução.";
            UpdateCurrentChord();
        }
    }

    /// <summary>Called with the playback position updates; reads the score's chords once per score.</summary>
    internal void UpdateCurrentChord(double? seconds = null)
    {
        var position = seconds ?? PlaybackPositionSeconds;
        if (!settings.CurrentChord || scorePlaybackMap is not { CanSynchronize: true } map || CurrentScorePath is not { } path || seconds is null && !audioPlaying)
        { ShowCurrentChord(null, null); return; }
        if (!string.Equals(chordFollowScore, path, StringComparison.OrdinalIgnoreCase))
        {
            if (chordFollowLoading) return;
            chordFollowLoading = true;
            int fallback = selectedFile?.EffectiveDiagramInversion ?? settings.DefaultDiagramInversion;
            _ = Task.Run(() => karaokeModel(path)).ContinueWith(task =>
            {
                chordFollowLoading = false;
                if (task.IsCompletedSuccessfully)
                {
                    var model = task.Result; int top = model.Measures.Count == 0 ? 1 : model.Measures.Min(m => m.Staff);
                    chordFollowList = model.Measures.Where(m => m.Staff == top).SelectMany(m => m.Harmonies).Where(h => h.Quarter is not null)
                        .OrderBy(h => h.Measure).ThenBy(h => h.Quarter)
                        .Select(h => new TimedChord(h.Measure, h.Quarter!.Value, h.Root + h.Quality + (h.Bass is { } b ? "/" + b : ""), h.Root, h.Quality, h.Bass, h.DiagramInversion ?? fallback)).ToArray();
                    chordFollowStarts = PracticeSession.MeasureStarts(model);
                }
                else chordFollowList = [];
                chordFollowScore = path;
                if (chordFollowList.Count == 0 && settings.CurrentChord) Status = "Esta partitura não tem cifras para mostrar.";
                UpdateCurrentChord(seconds);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        var occurrence = map.MeasureTimeline.FirstOrDefault(o => position >= o.StartSeconds && position < o.EndSeconds);
        if (occurrence is null || chordFollowList.Count == 0) { ShowCurrentChord(null, null); return; }
        double length = chordFollowStarts.TryGetValue(occurrence.Measure, out double start) && chordFollowStarts.TryGetValue(occurrence.Measure + 1, out double following) ? following - start : 4;
        double quarter = (position - occurrence.StartSeconds) / (occurrence.EndSeconds - occurrence.StartSeconds) * length;
        int index = CurrentChordIndex(chordFollowList.Select(c => (c.Measure, c.Quarter)).ToArray(), occurrence.Measure, quarter);
        ShowCurrentChord(index < 0 ? null : chordFollowList[index], index + 1 < chordFollowList.Count ? chordFollowList[index + 1] : null);
    }

    /// <summary>The chord sounding at a written position: the last one at or before it (-1 before the first).</summary>
    internal static int CurrentChordIndex(IReadOnlyList<(int Measure, double Quarter)> chords, int measure, double quarter)
    {
        int found = -1;
        for (int i = 0; i < chords.Count; i++)
            if (chords[i].Measure < measure || chords[i].Measure == measure && chords[i].Quarter <= quarter + 1e-6) found = i; else break;
        return found;
    }

    private void ShowCurrentChord(TimedChord? current, TimedChord? next)
    {
        CurrentChordName = current?.Name ?? "";
        MainScoreViewer?.SetCurrentChord(current?.Name, current is null ? null : chordFollowImages.GetImage(current.Root, current.Quality, current.Inversion, 0, current.Bass),
            next is null ? "" : "depois: " + next.Name);
    }
}
