using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    // Starts the music player slightly before the count-in ends, covering the Windows player's start-up delay.
    // Each computer and headset differs, so the lead is adjustable in Configurações.
    private double CountInLeadSeconds => Math.Clamp(settings.CountInLeadMilliseconds, 0, 300) / 1000.0;
    public int CountInLeadMilliseconds
    {
        get => Math.Clamp(settings.CountInLeadMilliseconds, 0, 300);
        set
        {
            int clamped = Math.Clamp((int)Math.Round(value / 10.0) * 10, 0, 300);
            if (settings.CountInLeadMilliseconds == clamped) return;
            settings.CountInLeadMilliseconds = clamped; Notify(); Notify(nameof(CountInLeadText)); PersistPreferences();
        }
    }
    public string CountInLeadText => CountInLeadMilliseconds == 0 ? "Sem antecipação" : $"Música começa {CountInLeadMilliseconds} ms antes do fim da contagem";
    private IReadOnlyList<MetronomeBeat>? loadedBeats;
    private CancellationTokenSource? countInCancellation;
    private DispatcherTimer? countInDisplay;
    private bool countingIn;
    internal Func<ScoreStudyAudio, string, double, IProgress<string>?, CancellationToken, Task<IReadOnlyList<MetronomeBeat>>> metronomeBeatsPreparation =
        (service, path, speed, progress, token) => service.PrepareBeatsAsync(path, speed, progress, token);
    internal Func<ScoreStudyAudio, ScoreAudio, IReadOnlyList<MetronomeBeat>, IProgress<string>?, CancellationToken, Task<ScoreAudio>> metronomeAudioPreparation =
        (service, audio, beats, progress, token) => service.PrepareMetronomeAudioAsync(audio, beats, progress, token);
    internal Func<byte[], CancellationToken, Task> countInPlayback = PlayCountInWaveAsync;

    public bool IsCountingIn => countingIn;
    public bool MetronomeEnabled
    {
        get => settings.ScoreMetronome;
        set
        {
            if (settings.ScoreMetronome == value) return;
            if (IsBusy) { Notify(); return; }
            settings.ScoreMetronome = value; Notify(); PersistPreferences();
            if (audioLoaded) ReloadScoreAudioKeepingPosition(value
                ? "Metrônomo ligado. A primeira reprodução prepara o áudio com os cliques."
                : "Metrônomo desligado.");
            else PlaybackStatus = value ? "Metrônomo ligado para a próxima reprodução." : "Metrônomo desligado.";
        }
    }
    public bool CountInEnabled
    {
        get => settings.ScoreCountIn;
        set
        {
            if (settings.ScoreCountIn == value) return;
            if (IsBusy) { Notify(); return; }
            settings.ScoreCountIn = value; Notify(); PersistPreferences();
            if (value && audioLoaded && loadedBeats is null) ReloadScoreAudioKeepingPosition("Contagem ligada. A próxima reprodução localiza os tempos dos compassos.");
            else PlaybackStatus = value ? "Contagem de um compasso antes de reproduzir." : "Contagem desligada.";
        }
    }

    private void ReloadScoreAudioKeepingPosition(string message)
    {
        var position = PlaybackPositionSeconds;
        var resume = audioPlaying;
        ResetPlayback(preserveStudy: true);
        pendingPlaybackPosition = position;
        PlaybackStatus = message; NotifyPlaybackState();
        if (resume) PlayScoreClick(this, new RoutedEventArgs());
    }

    private async Task<(ScoreAudio Audio, IReadOnlyList<MetronomeBeat>? Beats, string? Warning)> PrepareMetronome(
        ScoreStudyAudio service, string scorePath, double speed, ScoreAudio audio, CancellationToken token)
    {
        if (!MetronomeEnabled && !CountInEnabled) return (audio, null, null);
        try
        {
            var beats = await metronomeBeatsPreparation(service, scorePath, speed, CreateOperationProgress(), token);
            if (beats.Count == 0) return (audio, null, "Metrônomo e contagem indisponíveis: a partitura não informou os tempos dos compassos.");
            if (MetronomeEnabled) audio = await metronomeAudioPreparation(service, audio, beats, CreateOperationProgress(), token);
            return (audio, beats, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Playback stays available; the clicks are an aid, not part of the score.
            return (audio, null, "Metrônomo e contagem indisponíveis nesta reprodução: " + error.Message);
        }
    }

    /// <summary>Starts the prepared player, after one bar of clicks when the count-in is on.</summary>
    private void StartScorePlayer(string status)
    {
        if (CountInEnabled && loadedBeats is { Count: > 0 } beats &&
            ScoreMetronome.CountInAt(beats, PlaybackPositionSeconds / loadedAudioSpeed) is { } countIn)
        {
            _ = PlayCountInAsync(countIn, status);
            return;
        }
        scorePlayer.Play(); audioPlaying = true; playbackTimer.Start();
        PlaybackStatus = status; NotifyPlaybackState();
    }

    private async Task PlayCountInAsync(MetronomeCountIn countIn, string status)
    {
        CancelCountIn();
        var cancellation = new CancellationTokenSource();
        countInCancellation = cancellation; countingIn = true;
        var player = scorePlayer;
        player.Position = TimeSpan.FromSeconds(countIn.StartSeconds);
        int shown = 1;
        PlaybackStatus = CountInText(countIn.Beats, shown);
        var display = new DispatcherTimer { Interval = TimeSpan.FromSeconds(countIn.IntervalSeconds) };
        display.Tick += (_, _) => { if (++shown <= countIn.Beats) PlaybackStatus = CountInText(countIn.Beats, shown); else display.Stop(); };
        countInDisplay = display; display.Start();
        NotifyPlaybackState();
        bool completed = false;
        try
        {
            await countInPlayback(ScoreMetronome.CreateCountInWave(countIn, CountInLeadSeconds), cancellation.Token);
            completed = !cancellation.IsCancellationRequested;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // Without a count-in device the music still starts; the user hears the reason in the status.
            completed = !cancellation.IsCancellationRequested;
            Status = "Não foi possível tocar a contagem: " + error.Message;
        }
        finally
        {
            display.Stop();
            if (ReferenceEquals(countInCancellation, cancellation)) { countInCancellation = null; countingIn = false; countInDisplay = null; }
            cancellation.Dispose();
        }
        if (!completed || playbackDisposed || !audioLoaded || !ReferenceEquals(player, scorePlayer)) { NotifyPlaybackState(); return; }
        scorePlayer.Play(); audioPlaying = true; playbackTimer.Start();
        PlaybackStatus = status; NotifyPlaybackState();
    }

    private static string CountInText(int beats, int current) =>
        "Contagem · " + string.Join("  ", Enumerable.Range(1, beats).Select(n => n <= current ? n.ToString() : "·"));

    private void CancelCountIn()
    {
        countInDisplay?.Stop(); countInDisplay = null;
        var pending = countInCancellation; countInCancellation = null;
        countingIn = false;
        pending?.Cancel();
    }

    private static Task PlayCountInWaveAsync(byte[] wave, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(wave, writable: false);
        using var player = new SoundPlayer(stream);
        player.Load();
        using var registration = token.Register(player.Stop);
        player.PlaySync();
        token.ThrowIfCancellationRequested();
    }, token);
}
