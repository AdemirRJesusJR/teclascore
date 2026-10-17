using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>The score audio and its measure map, prepared by MuseScore for one score.</summary>
public interface IPracticeAccompaniment : IDisposable
{
    Task<ScorePlaybackMap> PrepareMapAsync(double speed, IProgress<string>? progress, CancellationToken token);
    Task PlayAsync(double speed, double startSeconds, IProgress<string>? progress, CancellationToken token);
    double PositionSeconds { get; }
    bool IsPlaying { get; }
    void Stop();
}

/// <summary>Accompaniment backed by the editor player (study audio and maps cached per speed).</summary>
public sealed class ScorePracticeAccompaniment(string scorePath, EditorScorePlayback playback) : IPracticeAccompaniment
{
    public Task<ScorePlaybackMap> PrepareMapAsync(double speed, IProgress<string>? progress, CancellationToken token) => playback.PrepareMapAsync(scorePath, speed, progress, token);
    public Task PlayAsync(double speed, double startSeconds, IProgress<string>? progress, CancellationToken token) =>
        playback.PlayRangeAsync(scorePath, speed, startSeconds, null, progress, token);
    public double PositionSeconds => playback.PositionSeconds;
    public bool IsPlaying => playback.IsPlaying;
    public void Stop() => playback.Stop();
    public void Dispose() => playback.Dispose();
}

public sealed partial class PracticeWindow
{
    private readonly IPracticeAccompaniment? accompaniment;
    private readonly IReadOnlyDictionary<int, double> measureStarts;
    private readonly Dictionary<double, (ScorePlaybackMap Map, PracticeTimeline Timeline)> maps = [];
    private ScoreViewer? viewer;
    private PracticeTimeline? timeline;
    private CancellationTokenSource? preparing;
    private bool musicRunning;
    private int passageAttempt = 1;
    private readonly DispatcherTimer restart = new() { Interval = TimeSpan.FromSeconds(1.5) };
    public static IReadOnlyList<double> MusicSpeeds { get; } = [.5, .75, 1];

    public bool WithMusic => withMusic.IsChecked == true && InTime && accompaniment is not null;
    public double MusicSpeed => speedChoice.SelectedItem is double speed ? speed : 1;
    public int PassageAttempt => passageAttempt;
    public (int From, int To)? Passage
    {
        get
        {
            bool hasFrom = int.TryParse(passageFrom.Text, out int from), hasTo = int.TryParse(passageTo.Text, out int to);
            if (!hasFrom && !hasTo) return null;
            if (!hasFrom) from = 1; if (!hasTo) to = int.MaxValue;
            return from <= to ? (from, to) : (to, from);
        }
    }

    /// <summary>Steps of the chosen kind, limited to the passage.</summary>
    private IReadOnlyList<PracticeStep> PassageSteps => Passage is { } range
        ? CurrentSteps.Where(step => step.Measure >= range.From && step.Measure <= range.To).ToArray() : CurrentSteps;

    /// <summary>Prepares the measure map for a speed once; the visual follow and the music clock use it.</summary>
    private async Task<PracticeTimeline?> EnsureTimeline(double speed, CancellationToken token)
    {
        if (accompaniment is null) return null;
        if (!maps.TryGetValue(speed, out var entry))
        {
            var map = await accompaniment.PrepareMapAsync(speed, new Progress<string>(text => { if (!musicRunning) feedback.Text = text; }), token);
            entry = (map, new PracticeTimeline(map.MeasureTimeline, measureStarts));
            maps[speed] = entry;
        }
        timeline = entry.Timeline;
        if (viewer is not null && !ReferenceEquals(viewer.PlaybackMap, entry.Map)) viewer.SetPlaybackMap(entry.Map.CanSynchronize ? entry.Map : null);
        return entry.Timeline;
    }

    /// <summary>Loads the map at normal speed so the score follows the practice from the start.</summary>
    private async void PrepareVisualFollow()
    {
        if (accompaniment is null || viewer is null) return;
        preparing = new CancellationTokenSource();
        try
        {
            await EnsureTimeline(1, preparing.Token);
            feedback.Text = "A partitura acompanha a prática.";
            Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or System.IO.InvalidDataException or TimeoutException or ArgumentException or System.Xml.XmlException)
        { feedback.Text = "A prática funciona, mas a partitura não pôde acompanhar: " + error.Message; }
    }

    /// <summary>Shows the measure being played in the score (by its first-pass time).</summary>
    private void FollowScore(double? quarter)
    {
        if (viewer is null || timeline is null || timeline.IsEmpty || quarter is not { } at || !double.IsFinite(at)) return;
        double seconds = musicRunning && accompaniment is not null ? accompaniment.PositionSeconds : timeline.ToSeconds(Math.Max(0, at)) + .001;
        viewer.UpdatePlaybackPosition(seconds, follow: true);
    }

    /// <summary>In-time practice with the score audio: the music sets the clock, at the chosen study speed.</summary>
    private async Task StartWithMusic()
    {
        if (accompaniment is null) return;
        var steps = PassageSteps;
        preparing?.Cancel(); preparing = new CancellationTokenSource();
        start.IsEnabled = false;
        try
        {
            feedback.Text = $"Preparando a música em {MusicSpeed:P0}… a primeira vez em cada velocidade leva alguns segundos.";
            var line = await EnsureTimeline(MusicSpeed, preparing.Token);
            if (line is null || line.IsEmpty) { feedback.Text = "Não foi possível ligar a música aos compassos desta partitura. Use o metrônomo."; return; }
            // Map times are at normal speed; the tolerance follows the tempo actually heard.
            if (timed is null || timed.Hits + timed.Missed + timed.Wrong > 0 || timed.Bpm != Math.Round(line.Bpm * MusicSpeed))
                timed = new TimedPracticeSession(steps, Math.Round(line.Bpm * MusicSpeed), AnyOctave);
            double from = steps.Count > 0 ? steps[0].Start : 0;
            await accompaniment.PlayAsync(MusicSpeed, line.LeadInSeconds(from), null, preparing.Token);
            musicRunning = true; clock.Start(); start.Content = "⏸ Parar";
            feedback.Text = "Toque junto com a música.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or System.IO.InvalidDataException or TimeoutException or ArgumentException or System.Xml.XmlException)
        { feedback.Text = "Não foi possível tocar a música: " + error.Message + " Use o metrônomo."; }
        finally { start.IsEnabled = true; }
    }

    private void StopMusic()
    {
        if (!musicRunning) return;
        musicRunning = false;
        try { accompaniment?.Stop(); } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// After a finished pass with "repeat until right": a clean pass (no wrong or missed keys in wait mode, 90 points
    /// in time) ends the training; otherwise the passage starts again.
    /// </summary>
    private void PassageFinished()
    {
        bool mastered = timed is { } run ? run.Score >= 90 : session.Misses == 0;
        if (mastered)
        {
            feedback.Text += passageAttempt == 1 ? " Trecho dominado na primeira tentativa!" : $" Trecho dominado em {passageAttempt} tentativas!";
            passageAttempt = 1; return;
        }
        if (repeatUntilRight.IsChecked != true) return;
        passageAttempt++;
        feedback.Text += $" Vamos de novo: tentativa {passageAttempt}.";
        restart.Start();
    }

    internal ScoreViewer? Viewer => viewer;
    internal void ChoosePassage(string from, string to, bool repeat) { passageFrom.Text = from; passageTo.Text = to; repeatUntilRight.IsChecked = repeat; Reset(); }
    internal void ChooseMusic(bool music, double speed) { inTime.IsChecked = music || InTime; withMusic.IsChecked = music; speedChoice.SelectedItem = speed; Reset(); }
    internal void StartOrStop() => ToggleClock();

    private void RestartPassage()
    {
        restart.Stop();
        int attempt = passageAttempt;
        Reset(keepAttempt: true);
        passageAttempt = attempt;
        if (InTime) ToggleClock();
    }
}
