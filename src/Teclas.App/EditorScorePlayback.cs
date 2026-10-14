using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Plays an immutable editor snapshot without changing the library player or user settings.
/// Construct and call on the WPF dispatcher thread. Stop closes the current audio and cancels preparation.
/// </summary>
public sealed class EditorScorePlayback : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer positionTimer;
    private readonly ScoreStudyAudio audioService;
    private readonly ScorePlaybackMapService mapService;
    private MediaPlayer? player;
    private CancellationTokenSource? requestCancellation;
    private CancellationTokenRegistration cancellationRegistration;
    private TaskCompletionSource<bool>? pendingOpen;
    private long generation;
    private double speed = 1, duration, volume = .85;
    private double rangeStart;
    private double? rangeEnd;
    private bool playing, paused, hasAudio, ended, disposed;

    public EditorScorePlayback(string museScorePath,string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(museScorePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        audioService = new ScoreStudyAudio(museScorePath,Path.GetFullPath(cacheDirectory));
        mapService = new ScorePlaybackMapService(museScorePath,Path.Combine(Path.GetFullPath(cacheDirectory),"maps"));
        positionTimer = new DispatcherTimer(DispatcherPriority.Background,dispatcher)
            { Interval = TimeSpan.FromMilliseconds(20) };
        positionTimer.Tick += (_, _) =>
        {
            if (playing && rangeEnd is { } end && PositionSeconds >= end && player is not null)
            {
                player.Pause(); player.Position = TimeSpan.FromSeconds(end / speed);
                playing = false; paused = true; positionTimer.Stop();
            }
            Changed();
        };
    }

    public event EventHandler? StateChanged;
    public event EventHandler<string>? PlaybackFailed;
    public bool IsPlaying => playing;
    public bool IsPaused => paused;
    public bool HasAudio => hasAudio;
    public double DurationSeconds => duration;
    public double PositionSeconds
    {
        get
        {
            dispatcher.VerifyAccess();
            return !hasAudio || player is null ? 0 : ended ? duration
                : Math.Clamp(player.Position.TotalSeconds * speed,0,duration);
        }
    }
    public double Volume
    {
        get => volume;
        set
        {
            VerifyAlive();
            if(!double.IsFinite(value) || value is < 0 or > 1)throw new ArgumentOutOfRangeException(nameof(value));
            volume = value;
            if(player is not null)player.Volume = value;
        }
    }

    public Task<ScorePlaybackMap> PrepareMapAsync(string path,double playbackSpeed,IProgress<string>? progress,CancellationToken token) =>
        mapService.PrepareAsync(path,playbackSpeed,progress,token);

    public Task PlayAsync(string draftPath,double playbackSpeed,IProgress<string>? progress,CancellationToken token) =>
        PlayRangeAsync(draftPath,playbackSpeed,0,null,progress,token);

    public async Task PlayRangeAsync(string draftPath,double playbackSpeed,double startSeconds,double? endSeconds,IProgress<string>? progress,CancellationToken token)
    {
        VerifyAlive();
        if (!double.IsFinite(startSeconds) || startSeconds < 0 || endSeconds is { } invalidEnd && (!double.IsFinite(invalidEnd) || invalidEnd <= startSeconds))
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        StopCore();
        long requestedGeneration = generation;
        // Keep the linked source alive so cancellation remains effective
        // even after PlayAsync has finished opening the audio.
        var activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestCancellation = activeCancellation;
        var requestToken = activeCancellation.Token;
        cancellationRegistration = requestToken.Register(() => QueueCancellation(requestedGeneration));
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(draftPath);
            if(!ScoreStudyAudio.IsSupportedSpeed(playbackSpeed))throw new ArgumentOutOfRangeException(nameof(playbackSpeed));
            EnsureCurrent(requestedGeneration,requestToken);
            var audio = await audioService.PrepareAsync(draftPath,playbackSpeed,progress,requestToken);
            EnsureCurrent(requestedGeneration,requestToken);
            speed = playbackSpeed;
            duration = audio.Duration.TotalSeconds * speed;
            var requestedPlayer = new MediaPlayer { Volume = volume, SpeedRatio = 1 };
            player = requestedPlayer;
            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingOpen = opened;
            Exception? openFailure = null;
            requestedPlayer.MediaOpened += (_, _) =>
            {
                if(IsCurrent(requestedGeneration,requestedPlayer) && !requestToken.IsCancellationRequested)
                    opened.TrySetResult(true);
            };
            requestedPlayer.MediaFailed += (_, args) =>
            {
                if(!IsCurrent(requestedGeneration,requestedPlayer) || requestToken.IsCancellationRequested)return;
                var failure = new InvalidOperationException(
                    "Não foi possível reproduzir o rascunho. Confira o dispositivo de som do Windows. " + args.ErrorException.Message,
                    args.ErrorException);
                if(pendingOpen is not null) { openFailure = failure; opened.TrySetException(failure); }
                else FailPlayback(failure.Message);
            };
            requestedPlayer.MediaEnded += (_, _) =>
            {
                if(!IsCurrent(requestedGeneration,requestedPlayer) || requestToken.IsCancellationRequested)return;
                playing = paused = false; ended = true;
                positionTimer.Stop(); Changed();
            };
            progress?.Report("Abrindo o áudio do rascunho…");
            EnsureCurrent(requestedGeneration,requestToken);
            requestedPlayer.Open(new Uri(Path.GetFullPath(audio.Path)));
            try { await opened.Task.WaitAsync(TimeSpan.FromSeconds(20),requestToken); }
            catch(TimeoutException error)
            {
                throw new TimeoutException("O Windows não abriu o áudio do rascunho em vinte segundos. Confira o dispositivo de som e tente novamente.",error);
            }
            EnsureCurrent(requestedGeneration,requestToken);
            if(!ReferenceEquals(player,requestedPlayer))throw new OperationCanceledException(requestToken);
            if(openFailure is not null)throw openFailure;
            pendingOpen = null;
            if(requestedPlayer.NaturalDuration.HasTimeSpan)
                duration = requestedPlayer.NaturalDuration.TimeSpan.TotalSeconds * speed;
            if (startSeconds >= duration) throw new InvalidOperationException("O trecho selecionado não está dentro do áudio preparado.");
            rangeStart = startSeconds; rangeEnd = endSeconds is { } requestedEnd ? Math.Min(requestedEnd,duration) : null;
            hasAudio = true; ended = false; paused = false;
            requestedPlayer.Position = TimeSpan.FromSeconds(rangeStart / speed);
            requestedPlayer.Play();
            EnsureCurrent(requestedGeneration,requestToken);
            playing = true; positionTimer.Start(); Changed();
        }
        catch(OperationCanceledException)
        {
            if(generation == requestedGeneration)StopCore();
            throw;
        }
        catch(Exception error)
        {
            if(disposed || generation != requestedGeneration || requestToken.IsCancellationRequested)
                throw new OperationCanceledException("A reprodução anterior foi cancelada.",error,requestToken);
            StopCore();
            PlaybackFailed?.Invoke(this,error.Message);
            throw;
        }
    }

    public void Pause()
    {
        VerifyAlive();
        if(!hasAudio || !playing || player is null)return;
        try { player.Pause(); playing = false; paused = true; positionTimer.Stop(); Changed(); }
        catch(Exception error) { FailPlayback(error.Message); }
    }

    public void Resume()
    {
        VerifyAlive();
        if(!hasAudio || player is null || playing)return;
        try
        {
            if(ended || PositionSeconds >= (rangeEnd ?? duration))player.Position = TimeSpan.FromSeconds(rangeStart / speed);
            ended = false; player.Play(); playing = true; paused = false;
            positionTimer.Start(); Changed();
        }
        catch(Exception error) { FailPlayback(error.Message); }
    }

    public void Stop()
    {
        dispatcher.VerifyAccess();
        if(!disposed)StopCore();
    }

    public void Seek(double originalSeconds)
    {
        VerifyAlive();
        if(!double.IsFinite(originalSeconds))throw new ArgumentOutOfRangeException(nameof(originalSeconds));
        if(!hasAudio || player is null)return;
        try
        {
            player.Position = TimeSpan.FromSeconds(Math.Clamp(originalSeconds,0,duration)/speed);
            ended = false; Changed();
        }
        catch(Exception error) { FailPlayback(error.Message); }
    }

    private bool IsCurrent(long expected,MediaPlayer candidate) =>
        !disposed && expected == generation && ReferenceEquals(candidate,player);
    private void EnsureCurrent(long expected,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(disposed || expected != generation)throw new OperationCanceledException(token);
    }
    private void QueueCancellation(long expected)
    {
        if(dispatcher.HasShutdownStarted)return;
        try
        {
            dispatcher.InvokeAsync(() =>
            {
                if(!disposed && generation == expected)StopCore();
            },DispatcherPriority.Send);
        }
        catch(InvalidOperationException) { /* Dispatcher is shutting down; Dispose closes its player. */ }
    }
    private void FailPlayback(string message)
    {
        StopCore();
        PlaybackFailed?.Invoke(this,message);
    }
    private void StopCore()
    {
        generation++;
        positionTimer.Stop();
        var previousOpen = pendingOpen; pendingOpen = null;
        var previousCancellation = requestCancellation; requestCancellation = null;
        var previousPlayer = player; player = null;
        playing = paused = hasAudio = ended = false; duration = 0; speed = 1;
        rangeStart = 0; rangeEnd = null;
        previousOpen?.TrySetCanceled();
        // A token callback only posts to the dispatcher, so registration disposal cannot
        // wait on a worker which is itself waiting synchronously for this UI thread.
        cancellationRegistration.Dispose(); cancellationRegistration = default;
        previousCancellation?.Cancel(); previousCancellation?.Dispose();
        previousPlayer?.Close();
        Changed();
    }
    private void VerifyAlive()
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed,this);
    }
    private void Changed() => StateChanged?.Invoke(this,EventArgs.Empty);

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if(disposed)return;
        disposed = true; StopCore(); GC.SuppressFinalize(this);
    }
}
