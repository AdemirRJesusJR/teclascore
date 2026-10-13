using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private readonly MediaPlayer scorePlayer = new();
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer audioOpenTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private string? playbackScoreIdentity;
    private string? loadedAudioPath;
    private bool audioLoaded, audioOpening, audioPlaying, playWhenOpened;
    private double playbackDuration;
    private string playbackStatus = "Gere uma partitura para ouvir aqui no aplicativo.";
    private string PlaybackCacheDirectory => Path.Combine(persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!, "audio-cache");

    public bool CanPlayScore => !IsBusy && !audioOpening && !audioPlaying && SelectedScoreExists;
    public bool CanPauseScore => audioLoaded && audioPlaying;
    public bool CanStopScore => audioLoaded || audioOpening;
    public bool CanExportPdf => !IsBusy && SelectedScoreExists;
    public bool CanSeekScore => audioLoaded && playbackDuration > 0;
    private bool SelectedScoreExists => selectedFile?.HasPendingScoreProperties != true && CurrentScorePath is { } path && File.Exists(path);
    public string PlaybackStatus { get => playbackStatus; private set { playbackStatus = value; Notify(); } }
    public string PlaybackPositionText => FormatTime(PlaybackPositionSeconds);
    public string PlaybackDurationText => FormatTime(playbackDuration);
    public double PlaybackDurationSeconds => playbackDuration;
    public double PlaybackPositionSeconds
    {
        get => audioLoaded ? scorePlayer.Position.TotalSeconds : 0;
        set
        {
            if (!audioLoaded || !double.IsFinite(value)) return;
            scorePlayer.Position = TimeSpan.FromSeconds(Math.Clamp(value, 0, playbackDuration));
            Notify(nameof(PlaybackPositionSeconds)); Notify(nameof(PlaybackPositionText));
        }
    }

    private void InitializePlayback()
    {
        scorePlayer.Volume = .85;
        scorePlayer.MediaOpened += (_, _) =>
        {
            if (loadedAudioPath is null) return;
            audioOpenTimer.Stop();
            audioOpening = false; audioLoaded = true;
            if (scorePlayer.NaturalDuration.HasTimeSpan) playbackDuration = scorePlayer.NaturalDuration.TimeSpan.TotalSeconds;
            if (playWhenOpened)
            {
                scorePlayer.Play(); audioPlaying = true; playbackTimer.Start();
                PlaybackStatus = "Reproduzindo a partitura selecionada.";
            }
            else PlaybackStatus = "Áudio pronto para reproduzir.";
            NotifyPlaybackState();
        };
        scorePlayer.MediaEnded += (_, _) => StopPlayback("Reprodução concluída. Você pode ouvir novamente.");
        scorePlayer.MediaFailed += (_, e) =>
        {
            if (loadedAudioPath is null) return;
            ResetPlayback();
            PlaybackStatus = "Não foi possível reproduzir o áudio. Confira o dispositivo de som do Windows e tente novamente.";
            Status = PlaybackStatus + " " + e.ErrorException.Message;
            NotifyPlaybackState();
        };
        playbackTimer.Tick += (_, _) => { Notify(nameof(PlaybackPositionSeconds)); Notify(nameof(PlaybackPositionText)); };
        audioOpenTimer.Tick += (_, _) =>
        {
            ResetPlayback();
            PlaybackStatus = "O Windows não abriu o áudio. Confira o dispositivo de som e tente novamente.";
            Status = PlaybackStatus; NotifyPlaybackState();
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

    private void ResetPlayback()
    {
        playbackTimer.Stop(); audioOpenTimer.Stop(); scorePlayer.Close();
        loadedAudioPath = null; audioLoaded = false; audioOpening = false; audioPlaying = false; playWhenOpened = false; playbackDuration = 0;
    }
    private void DisposePlayback() { ResetPlayback(); }
    private void StopPlayback(string message = "Reprodução parada.")
    {
        if (audioOpening) { ResetPlayback(); PlaybackStatus = message; NotifyPlaybackState(); return; }
        playWhenOpened = false; audioPlaying = false; playbackTimer.Stop();
        scorePlayer.Stop(); PlaybackStatus = message; NotifyPlaybackState();
    }

    private async void PlayScoreClick(object sender, RoutedEventArgs e)
    {
        OnPlaybackSelectionChanged();
        if (!CanPlayScore || CurrentScorePath is not { } scorePath) return;
        StopTrackAudition();
        if (audioLoaded && loadedAudioPath is not null)
        {
            scorePlayer.Play(); audioPlaying = true; playbackTimer.Start();
            PlaybackStatus = "Reproduzindo a partitura selecionada."; NotifyPlaybackState(); return;
        }
        string? identity = SelectedScoreIdentity();
        await RunOperation(async () =>
        {
            PlaybackStatus = "Preparando áudio… A primeira reprodução pode levar alguns segundos.";
            var exporter = new ScoreExporter(MuseScorePath, PlaybackCacheDirectory);
            try
            {
                var audio = await exporter.PrepareAudioAsync(scorePath, new Progress<string>(text => Status = text), cancellation!.Token);
                if (identity != SelectedScoreIdentity()) return;
                loadedAudioPath = audio.Path; playbackDuration = audio.Duration.TotalSeconds;
                audioOpening = true; playWhenOpened = true;
                audioOpenTimer.Start();
                scorePlayer.Open(new Uri(audio.Path));
                PlaybackStatus = "Abrindo áudio da partitura…";
                Status = "Áudio preparado. A reprodução acontece aqui no aplicativo.";
            }
            catch
            {
                ResetPlayback();
                PlaybackStatus = "Áudio não preparado. Confira a mensagem abaixo e tente novamente.";
                throw;
            }
            finally { NotifyPlaybackState(); }
        });
    }
    private void PauseScoreClick(object sender, RoutedEventArgs e)
    {
        if (!CanPauseScore) return;
        scorePlayer.Pause(); audioPlaying = false; playbackTimer.Stop();
        PlaybackStatus = "Pausado. Reproduzir continua do mesmo ponto."; NotifyPlaybackState();
    }
    private void StopScoreClick(object sender, RoutedEventArgs e) => StopPlayback();

    private async void ExportPdfClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportPdf || CurrentScorePath is not { } scorePath) return;
        var dialog = new SaveFileDialog
        {
            Title = "Salvar partitura em PDF", Filter = "Documento PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(scorePath) + ".pdf", InitialDirectory = Path.GetDirectoryName(scorePath), OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperation(async () =>
        {
            var exporter = new ScoreExporter(MuseScorePath, PlaybackCacheDirectory);
            await exporter.ExportPdfAsync(scorePath, dialog.FileName, new Progress<string>(text => Status = text), cancellation!.Token);
            Status = "PDF salvo com todas as páginas: " + dialog.FileName;
        });
    }
}
