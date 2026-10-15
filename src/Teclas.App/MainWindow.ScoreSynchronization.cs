using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private readonly ScorePlaybackMapCache scoreMapCache = new();
    private ScorePlaybackMap? scorePlaybackMap;
    private string? scoreMapIdentity;
    private bool preparingScoreSynchronization;
    private string scoreSynchronizationStatus = "Prepare o acompanhamento para clicar nos compassos e acompanhar a reprodução.";
    public string ScoreSynchronizationStatus
    { get => scoreSynchronizationStatus; private set { scoreSynchronizationStatus = value; Notify(); } }
    public bool CanPrepareScoreSynchronization => IsIdle && SelectedScoreExists;
    public bool FollowScorePlayback
    {
        get => settings.FollowScorePlayback;
        set
        {
            if (settings.FollowScorePlayback == value) return;
            settings.FollowScorePlayback = value; Notify(); PersistPreferences();
            UpdateScoreSynchronization();
            if (value && audioLoaded && IsIdle && !preparingScoreSynchronization
                && (scorePlaybackMap is null || scoreMapIdentity != SelectedScoreIdentity() || scorePlaybackMap.Speed != ScorePlaybackSpeed))
                PrepareScoreSynchronizationClick(this, new RoutedEventArgs());
        }
    }
    private void ClearScoreSynchronization()
    {
        scorePlaybackMap = null; scoreMapIdentity = null;
        MainScoreViewer?.SetPlaybackMap(null);
        ScoreSynchronizationStatus = "Prepare o acompanhamento para clicar nos compassos e acompanhar a reprodução.";
    }
    private void UpdateScoreSynchronization()
    {
        if (scorePlaybackMap is null || scoreMapIdentity != SelectedScoreIdentity()) return;
        MainScoreViewer.UpdatePlaybackPosition(PlaybackPositionSeconds, FollowScorePlayback && audioPlaying);
    }
    private async Task PrepareScoreSynchronization(string path, double speed, CancellationToken token)
    {
        string? identity = SelectedScoreIdentity();
        try
        {
            var service = new ScorePlaybackMapService(MuseScorePath, Path.Combine(PlaybackCacheDirectory, "score-maps"));
            var progress = CreateOperationProgress();
            var map = await scoreMapCache.GetOrPrepareAsync(path, MuseScorePath, speed,
                request => service.PrepareAsync(path, speed, progress, request), progress, token);
            token.ThrowIfCancellationRequested();
            if (playbackDisposed || identity != SelectedScoreIdentity()) throw new OperationCanceledException(token);
            if (!map.CanSynchronize)
            {
                ClearScoreSynchronization();
                ScoreSynchronizationStatus = "Acompanhamento indisponível nesta partitura: " + string.Join(" ", map.Limitations);
                return;
            }
            bool sameMap = ReferenceEquals(scorePlaybackMap, map) && scoreMapIdentity == identity;
            scorePlaybackMap = map; scoreMapIdentity = identity;
            if (!sameMap) MainScoreViewer.SetPlaybackMap(map);
            ScoreSynchronizationStatus = "Clique em um compasso ou ataque para ouvir a partir dali. O destaque segue a reprodução." +
                (map.Limitations.Count > 0 ? " " + string.Join(" ", map.Limitations) : "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or TimeoutException or System.Xml.XmlException)
        {
            ClearScoreSynchronization();
            ScoreSynchronizationStatus = "O áudio continua disponível; não foi possível preparar o acompanhamento: " + error.Message;
        }
    }
    private async void PrepareScoreSynchronizationClick(object sender, RoutedEventArgs e)
    {
        if (preparingScoreSynchronization) return;
        OnPlaybackSelectionChanged();
        if (!CanPrepareScoreSynchronization || CurrentScorePath is not { } path) return;
        preparingScoreSynchronization = true;
        try
        {
            bool resume = audioPlaying, completed = false;
            if (resume) PauseScoreClick(sender, e);
            StopTrackAudition();
            await RunAudioOperation(async () =>
            {
                await PrepareScoreSynchronization(path, ScorePlaybackSpeed, cancellation!.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Status = ScoreSynchronizationStatus;
                completed = true;
            }, operationTitle: "Preparando acompanhamento da partitura");
            if (completed && resume && !operationsDisposed && CurrentScorePath == path && audioLoaded) PlayScoreClick(sender, e);
        }
        finally { preparingScoreSynchronization = false; }
    }
    private void ScorePositionSelected(object? sender, ScorePositionSelectedEventArgs e)
    {
        if (!IsIdle || scorePlaybackMap?.CanSynchronize != true || scoreMapIdentity != SelectedScoreIdentity()) return;
        if (LoopEnabled && (e.StartSeconds < loopStart || e.StartSeconds >= loopEnd))
        { loopEnabled = false; NotifyStudyState(); }
        if (audioLoaded) PlaybackPositionSeconds = e.StartSeconds;
        else pendingPlaybackPosition = e.StartSeconds;
        UpdateScoreSynchronization();
        if (!audioPlaying) PlayScoreClick(this, new RoutedEventArgs());
    }
}
