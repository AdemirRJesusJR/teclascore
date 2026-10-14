using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private ScorePlaybackMap? scorePlaybackMap;
    private string? scoreMapIdentity;
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
        if (scorePlaybackMap is not null && scoreMapIdentity == identity && scorePlaybackMap.Speed == speed) return;
        try
        {
            var service = new ScorePlaybackMapService(MuseScorePath, Path.Combine(PlaybackCacheDirectory, "score-maps"));
            var map = await service.PrepareAsync(path, speed, CreateOperationProgress(), token);
            token.ThrowIfCancellationRequested();
            if (playbackDisposed || identity != SelectedScoreIdentity()) throw new OperationCanceledException(token);
            if (!map.CanSynchronize)
            {
                ClearScoreSynchronization();
                ScoreSynchronizationStatus = "Acompanhamento indisponível nesta partitura: " + string.Join(" ", map.Limitations);
                return;
            }
            scorePlaybackMap = map; scoreMapIdentity = identity;
            MainScoreViewer.SetPlaybackMap(map);
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
        OnPlaybackSelectionChanged();
        if (!CanPrepareScoreSynchronization || CurrentScorePath is not { } path) return;
        bool resume = audioPlaying, completed = false;
        if (resume) PauseScoreClick(sender, e);
        StopTrackAudition();
        await RunOperation(async () =>
        {
            await PrepareScoreSynchronization(path, ScorePlaybackSpeed, cancellation!.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Status = ScoreSynchronizationStatus;
            completed = true;
        }, operationTitle: "Preparando acompanhamento da partitura");
        if (completed && resume && !operationsDisposed && CurrentScorePath == path && audioLoaded) PlayScoreClick(sender, e);
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
