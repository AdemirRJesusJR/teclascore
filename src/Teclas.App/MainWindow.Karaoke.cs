using System.IO;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Karaoke: while the score plays, the lyric line shows over the preview with the syllable being sung highlighted.</summary>
public partial class MainWindow
{
    private string? karaokeScore;
    private IReadOnlyList<KaraokeSyllable> karaokeSyllables = [];
    private IReadOnlyDictionary<int, double> karaokeStarts = new Dictionary<int, double>();
    private bool karaokeLoading;
    internal Func<string, NativeMusicModel> karaokeModel = path => NativeMusicEditor.Open(path, CancellationToken.None);
    internal IReadOnlyList<KaraokePiece> KaraokeLine { get; private set; } = [];

    public bool KaraokeEnabled
    {
        get => settings.Karaoke;
        set
        {
            if (settings.Karaoke == value) return;
            settings.Karaoke = value; Notify(); PersistPreferences();
            if (value && !FollowScorePlayback) Status = "Karaokê ligado: ligue também Acompanhar (Páginas) para a letra seguir a reprodução.";
            UpdateKaraoke();
        }
    }

    /// <summary>Called with the playback position updates; loads the score's lyrics once per score.</summary>
    internal void UpdateKaraoke(double? seconds = null)
    {
        var position = seconds ?? PlaybackPositionSeconds;
        if (!settings.Karaoke || scorePlaybackMap is not { CanSynchronize: true } map || CurrentScorePath is not { } path || seconds is null && !audioPlaying)
        { ShowKaraoke([]); return; }
        if (!string.Equals(karaokeScore, path, StringComparison.OrdinalIgnoreCase))
        {
            if (karaokeLoading) return;
            karaokeLoading = true;
            _ = Task.Run(() => karaokeModel(path)).ContinueWith(task =>
            {
                karaokeLoading = false;
                if (task.IsCompletedSuccessfully)
                { karaokeSyllables = KaraokeLyrics.FromScore(task.Result); karaokeStarts = PracticeSession.MeasureStarts(task.Result); }
                else karaokeSyllables = [];
                karaokeScore = path;
                if (karaokeSyllables.Count == 0 && settings.Karaoke) Status = "Esta partitura não tem letra para o karaokê.";
                UpdateKaraoke(seconds);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        if (karaokeSyllables.Count == 0) { ShowKaraoke([]); return; }
        var occurrence = map.MeasureTimeline.FirstOrDefault(o => position >= o.StartSeconds && position < o.EndSeconds);
        if (occurrence is null) { ShowKaraoke([]); return; }
        double length = karaokeStarts.TryGetValue(occurrence.Measure, out double start) && karaokeStarts.TryGetValue(occurrence.Measure + 1, out double next) ? next - start : 4;
        double quarter = (position - occurrence.StartSeconds) / (occurrence.EndSeconds - occurrence.StartSeconds) * length;
        ShowKaraoke(KaraokeLyrics.Line(karaokeSyllables, occurrence.Measure, quarter));
    }

    private void ShowKaraoke(IReadOnlyList<KaraokePiece> line)
    {
        KaraokeLine = line;
        MainScoreViewer?.SetKaraoke(line);
    }
}
