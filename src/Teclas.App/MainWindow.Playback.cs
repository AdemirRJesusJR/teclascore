using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private MediaPlayer scorePlayer = new();
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer audioOpenTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private string? playbackScoreIdentity;
    private string? loadedAudioPath;
    private bool audioLoaded, audioOpening, audioPlaying, playbackDisposed;
    private TaskCompletionSource<bool>? pendingAudioOpen;
    private Exception? audioOpenFailure;
    private Func<ScoreStudyAudio, string, double, IProgress<string>?, CancellationToken, Task<ScoreAudio>> scoreAudioPreparation =
        (service, path, speed, progress, token) => service.PrepareAsync(path, speed, progress, token);
    private double playbackDuration;
    private string playbackStatus = "Gere uma partitura para ouvir aqui no aplicativo.";
    private string PlaybackCacheDirectory => Path.Combine(persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!, "audio-cache");

    public bool CanPlayScore => !IsBusy && !audioOpening && !audioPlaying && !countingIn && SelectedScoreExists;
    public bool CanPauseScore => audioLoaded && audioPlaying;
    public bool CanStopScore => audioLoaded || audioOpening || countingIn;
    public bool CanExportPdf => !IsBusy && SelectedScoreExists;
    public bool CanSeekScore => audioLoaded && playbackDuration > 0;
    private bool SelectedScoreExists => selectedFile?.HasPendingScoreProperties != true && CurrentScorePath is { } path && File.Exists(path);
    public string PlaybackStatus { get => playbackStatus; private set { playbackStatus = value; Notify(); } }
    public string PlaybackPositionText => FormatTime(PlaybackPositionSeconds);
    public string PlaybackDurationText => FormatTime(playbackDuration);
    public double PlaybackDurationSeconds => playbackDuration;
    public double PlaybackPositionSeconds
    {
        get => audioLoaded ? scorePlayer.Position.TotalSeconds * loadedAudioSpeed : pendingPlaybackPosition;
        set
        {
            if (!audioLoaded || !double.IsFinite(value)) return;
            scorePlayer.Position = TimeSpan.FromSeconds(Math.Clamp(value, 0, playbackDuration) / loadedAudioSpeed);
            Notify(nameof(PlaybackPositionSeconds)); Notify(nameof(PlaybackPositionText));
            UpdateScoreSynchronization();
        }
    }

    private void InitializePlayback()
    {
        InitializeScorePlayer(scorePlayer);
        MainScoreViewer.PositionSelected += ScorePositionSelected;
        playbackTimer.Tick += (_, _) =>
        {
            if (audioPlaying && LoopEnabled && loopEnd is { } end && PlaybackPositionSeconds >= end) RepeatStudyLoop();
            UpdateScoreSynchronization();
            Notify(nameof(PlaybackPositionSeconds)); Notify(nameof(PlaybackPositionText));
        };
        audioOpenTimer.Tick += (_, _) =>
        {
            audioOpenTimer.Stop();
            pendingAudioOpen?.TrySetException(new TimeoutException("O Windows não abriu o áudio. Confira o dispositivo de som e tente novamente."));
        };
    }

    private void InitializeScorePlayer(MediaPlayer player)
    {
        player.Volume = .85;
        player.SpeedRatio = 1;
        player.MediaOpened += (_, _) =>
        {
            if (playbackDisposed || !ReferenceEquals(player, scorePlayer) || loadedAudioPath is null || !audioOpening || pendingAudioOpen is null) return;
            audioOpenTimer.Stop();
            pendingAudioOpen.TrySetResult(true);
        };
        player.MediaEnded += (_, _) =>
        {
            if (playbackDisposed || !ReferenceEquals(player, scorePlayer) || loadedAudioPath is null) return;
            if (!RepeatStudyLoop()) StopPlayback("Reprodução concluída. Você pode ouvir novamente.");
        };
        player.MediaFailed += (_, e) =>
        {
            if (playbackDisposed || !ReferenceEquals(player, scorePlayer) || loadedAudioPath is null) return;
            const string message = "Não foi possível reproduzir o áudio. Confira o dispositivo de som do Windows e tente novamente.";
            if (pendingAudioOpen is { } pending)
            {
                audioOpenTimer.Stop();
                audioOpenFailure = new InvalidOperationException(message + " " + e.ErrorException.Message, e.ErrorException);
                pending.TrySetException(audioOpenFailure);
                return;
            }
            ResetPlayback();
            PlaybackStatus = message;
            Status = PlaybackStatus + " " + e.ErrorException.Message;
            NotifyPlaybackState();
        };
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private void NotifyPlaybackState()
    {
        foreach (var name in new[] { nameof(CanPlayScore), nameof(CanPauseScore), nameof(CanStopScore), nameof(CanExportPdf), nameof(CanSeekScore), nameof(PlaybackPositionSeconds), nameof(PlaybackPositionText), nameof(PlaybackDurationSeconds), nameof(PlaybackDurationText) }) Notify(name);
        NotifyStudyState();
        Notify(nameof(CanPrepareScoreSynchronization));
    }

    private string? SelectedScoreIdentity()
    {
        if (CurrentScorePath is not { } path || !File.Exists(path)) return null;
        try
        {
            var file = new FileInfo(path);
            return file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void OnPlaybackSelectionChanged()
    {
        string? identity = SelectedScoreIdentity();
        if (identity != playbackScoreIdentity)
        {
            ResetPlayback(); playbackScoreIdentity = identity;
            PlaybackStatus = identity is null ? "Gere uma partitura para ouvir aqui no aplicativo." : "Ouça esta partitura sem abrir o editor do MuseScore.";
        }
        NotifyPlaybackState();
    }

    private void ResetPlayback(bool preserveStudy = false)
    {
        playbackTimer.Stop(); audioOpenTimer.Stop();
        CancelCountIn(); loadedBeats = null;
        ClearScoreSynchronization();
        pendingAudioOpen?.TrySetCanceled(); pendingAudioOpen = null;
        audioOpenFailure = null;
        loadedAudioPath = null; audioLoaded = false; audioOpening = false; audioPlaying = false; playbackDuration = 0;
        var previousPlayer = scorePlayer;
        // Each open belongs to its own player so delayed native events cannot complete a later request.
        if (!playbackDisposed)
        {
            scorePlayer = new MediaPlayer();
            InitializeScorePlayer(scorePlayer);
        }
        previousPlayer.Close();
        if (!preserveStudy) ResetStudyMarkers();
    }
    private void DisposePlayback() { playbackDisposed = true; ResetPlayback(); }
    private void StopPlayback(string message = "Reprodução parada.")
    {
        if (audioOpening) { ResetPlayback(); PlaybackStatus = message; NotifyPlaybackState(); return; }
        CancelCountIn();
        audioPlaying = false; playbackTimer.Stop();
        pendingPlaybackPosition = 0;
        scorePlayer.Stop(); UpdateScoreSynchronization(); PlaybackStatus = message; NotifyPlaybackState();
    }

    private async void PlayScoreClick(object sender, RoutedEventArgs e)
    {
        OnPlaybackSelectionChanged();
        if (!CanPlayScore || CurrentScorePath is not { } scorePath) return;
        StopTrackAudition();
        if (audioLoaded && loadedAudioPath is not null)
        {
            if (LoopEnabled && CanLoopScore && (PlaybackPositionSeconds < loopStart || PlaybackPositionSeconds >= loopEnd)) PlaybackPositionSeconds = loopStart!.Value;
            StartScorePlayer("Reproduzindo a partitura selecionada."); return;
        }
        string? identity = SelectedScoreIdentity();
        var requestedSpeed = ScorePlaybackSpeed;
        await RunAudioOperation(async () =>
        {
            var token = cancellation!.Token;
            PlaybackStatus = "Preparando áudio… A primeira reprodução pode levar alguns segundos.";
            var exporter = new ScoreStudyAudio(MuseScorePath, PlaybackCacheDirectory);
            try
            {
                var audio = await scoreAudioPreparation(exporter, scorePath, requestedSpeed, CreateOperationProgress(), token);
                var (playedAudio, beats, metronomeWarning) = await PrepareMetronome(exporter, scorePath, requestedSpeed, audio, token);
                audio = playedAudio;
                if (FollowScorePlayback) await PrepareScoreSynchronization(scorePath, requestedSpeed, token);
                token.ThrowIfCancellationRequested();
                if (playbackDisposed || identity != SelectedScoreIdentity()) throw new OperationCanceledException();
                loadedAudioSpeed = requestedSpeed;
                loadedAudioPath = audio.Path; playbackDuration = audio.Duration.TotalSeconds * loadedAudioSpeed;
                audioOpening = true;
                audioOpenFailure = null;
                var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingAudioOpen = opened;
                using var registration = token.Register(() => opened.TrySetCanceled(token));
                PlaybackStatus = "Abrindo áudio da partitura…";
                Status = "Áudio preparado. Aguardando o reprodutor do Windows…";
                NotifyPlaybackState();
                audioOpenTimer.Start();
                scorePlayer.Open(new Uri(audio.Path));
                await opened.Task;
                token.ThrowIfCancellationRequested();
                if (playbackDisposed || identity != SelectedScoreIdentity() || !audioOpening) throw new OperationCanceledException();
                if (audioOpenFailure is { } error) throw error;
                audioOpening = false; audioLoaded = true;
                if (scorePlayer.NaturalDuration.HasTimeSpan) playbackDuration = scorePlayer.NaturalDuration.TimeSpan.TotalSeconds * loadedAudioSpeed;
                ClampStudyMarkers();
                scorePlayer.Position = TimeSpan.FromSeconds(Math.Clamp(pendingPlaybackPosition, 0, playbackDuration) / loadedAudioSpeed);
                pendingPlaybackPosition = 0;
                pendingAudioOpen = null;
                loadedBeats = beats;
                StartScorePlayer("Reproduzindo a partitura selecionada.");
                Status = metronomeWarning ?? "Áudio preparado. A reprodução acontece aqui no aplicativo.";
            }
            catch (OperationCanceledException)
            {
                ResetPlayback(preserveStudy: true);
                PlaybackStatus = "Preparação do áudio cancelada. Use Reproduzir para tentar novamente.";
                throw;
            }
            catch
            {
                ResetPlayback();
                PlaybackStatus = "Áudio não preparado. Confira a mensagem abaixo e tente novamente.";
                throw;
            }
            finally { NotifyPlaybackState(); }
        }, operationTitle: "Preparando áudio da partitura", operationItem: Path.GetFileNameWithoutExtension(scorePath));
    }
    private void PauseScoreClick(object sender, RoutedEventArgs e)
    {
        if (!CanPauseScore) return;
        scorePlayer.Pause(); audioPlaying = false; playbackTimer.Stop();
        UpdateScoreSynchronization();
        PlaybackStatus = "Pausado. Reproduzir continua do mesmo ponto."; NotifyPlaybackState();
    }
    private void StopScoreClick(object sender, RoutedEventArgs e) => StopPlayback();

    private async void ExportPdfClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportPdf || CurrentScorePath is not { } scorePath) return;
        var dialog = await CreateScoreExportDialogAsync("Salvar partitura em PDF", "Documento PDF|*.pdf",
            ScoreFileName.Stem(selectedFile?.ScoreArtist, selectedFile?.ScoreTitle, scorePath) + ".pdf", ".pdf");
        if (dialog is null || dialog.ShowDialog(this) != true) return;
        await RunOperation(async () =>
        {
            var exporter = new ScoreExporter(MuseScorePath, PlaybackCacheDirectory);
            await exporter.ExportPdfAsync(scorePath, dialog.FileName, CreateOperationProgress(), cancellation!.Token);
            RememberExportDirectory(Path.GetDirectoryName(dialog.FileName)!);
            Status = "PDF salvo com todas as páginas: " + dialog.FileName;
        }, operationTitle: "Salvando partitura em PDF", operationItem: Path.GetFileNameWithoutExtension(scorePath));
    }
}
