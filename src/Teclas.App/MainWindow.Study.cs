using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private double loadedAudioSpeed = 1;
    private double pendingPlaybackPosition;
    private double? loopStart, loopEnd;
    private bool loopEnabled;
    public IReadOnlyList<TrackSpeedOption> ScorePlaybackSpeeds { get; } =
    [new(.25, "25%"), new(.5, "50%"), new(.75, "75%"), new(1, "100% · normal"), new(1.25, "125%"), new(1.5, "150%")];
    public double ScorePlaybackSpeed
    {
        get => settings.ScorePlaybackSpeed;
        set
        {
            var speed = ScoreStudyAudio.IsSupportedSpeed(value) ? value : 1;
            if (settings.ScorePlaybackSpeed == speed || IsBusy) return;
            var position = PlaybackPositionSeconds;
            var resume = audioPlaying;
            settings.ScorePlaybackSpeed = speed;
            ResetPlayback(preserveStudy: true);
            pendingPlaybackPosition = position;
            Notify(); PersistPreferences(); NotifyPlaybackState();
            PlaybackStatus = "Velocidade de estudo alterada. A primeira reprodução nessa velocidade prepara um novo áudio.";
            if (resume) PlayScoreClick(this, new RoutedEventArgs());
        }
    }
    public bool CanMarkLoop => CanSeekScore && IsIdle;
    public bool CanLoopScore => loopStart is { } a && loopEnd is { } b && b - a >= .1;
    public bool LoopEnabled
    {
        get => loopEnabled;
        set
        {
            loopEnabled = value && CanLoopScore;
            if (value && !loopEnabled) PlaybackStatus = "Marque o início A e depois o fim B para repetir um trecho.";
            if (loopEnabled && audioLoaded && (PlaybackPositionSeconds < loopStart || PlaybackPositionSeconds >= loopEnd))
                PlaybackPositionSeconds = loopStart!.Value;
            Notify();
        }
    }
    public string LoopStartText => loopStart is { } value ? "A · " + FormatTime(value) : "A · não marcado";
    public string LoopEndText => loopEnd is { } value ? "B · " + FormatTime(value) : "B · não marcado";

    private void MarkLoopStartClick(object sender, RoutedEventArgs e)
    {
        if (!CanMarkLoop) return;
        loopStart = PlaybackPositionSeconds;
        if (loopEnd <= loopStart + .1) { loopEnd = null; loopEnabled = false; }
        NotifyStudyState();
        PlaybackStatus = "Início A marcado. Avance até o fim do trecho e marque B.";
    }
    private void MarkLoopEndClick(object sender, RoutedEventArgs e)
    {
        if (!CanMarkLoop) return;
        var position = PlaybackPositionSeconds;
        if (loopStart is not { } start || position - start < .1)
        { PlaybackStatus = "O fim B deve ficar depois do início A. Marque A e avance na reprodução."; return; }
        loopEnd = position;
        LoopEnabled = true;
        NotifyStudyState();
        PlaybackStatus = "Repetição A–B ativada. Os marcadores usam o tempo da partitura no andamento original.";
    }
    private void ClearLoopClick(object sender, RoutedEventArgs e)
    {
        ResetStudyMarkers();
        PlaybackStatus = "Marcadores A–B removidos.";
    }
    private void ResetStudyMarkers()
    { loopStart = loopEnd = null; loopEnabled = false; pendingPlaybackPosition = 0; NotifyStudyState(); }
    private void NotifyStudyState()
    {
        foreach (var name in new[] { nameof(CanMarkLoop), nameof(CanLoopScore), nameof(LoopEnabled), nameof(LoopStartText), nameof(LoopEndText), nameof(ScorePlaybackSpeed) }) Notify(name);
    }
    private void ClampStudyMarkers()
    {
        // MuseScore's fixed reverb tail has a different length on the original-time axis at each speed.
        if (loopStart >= playbackDuration) { loopStart = loopEnd = null; loopEnabled = false; }
        else if (loopEnd > playbackDuration) loopEnd = playbackDuration;
        if (!CanLoopScore) loopEnabled = false;
        if (loopEnabled && (pendingPlaybackPosition < loopStart || pendingPlaybackPosition >= loopEnd))
            pendingPlaybackPosition = loopStart!.Value;
    }
    private bool RepeatStudyLoop()
    {
        if (!LoopEnabled || !CanLoopScore || !audioLoaded || loopStart >= playbackDuration) return false;
        scorePlayer.Position = TimeSpan.FromSeconds(loopStart!.Value / loadedAudioSpeed);
        scorePlayer.Play(); audioPlaying = true; playbackTimer.Start();
        NotifyPlaybackState(); return true;
    }
}
