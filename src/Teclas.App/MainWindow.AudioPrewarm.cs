using System.IO;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    // After a score is generated, its audio is prepared quietly so the first Play starts without waiting for MuseScore.
    private Task? audioPrewarm;
    private string? audioPrewarmKey;
    private CancellationTokenSource? audioPrewarmCancellation;
    internal Func<string, double, bool, CancellationToken, Task>? audioPrewarmOverride = null;
    internal int AudioPrewarmCount { get; private set; }

    private static string PrewarmKey(string path, double speed, bool metronome) => $"{Path.GetFullPath(path)}|{speed}|{metronome}";

    private void QueueAudioPrewarm(string? scorePath)
    {
        if (playbackDisposed || scorePath is null || !File.Exists(scorePath) || !File.Exists(MuseScorePath) && audioPrewarmOverride is null) return;
        audioPrewarmCancellation?.Cancel();
        var cancellation = audioPrewarmCancellation = new CancellationTokenSource();
        double speed = ScorePlaybackSpeed; bool metronome = MetronomeEnabled || CountInEnabled;
        audioPrewarmKey = PrewarmKey(scorePath, speed, metronome); AudioPrewarmCount++;
        audioPrewarm = audioPrewarmOverride is { } run ? run(scorePath, speed, metronome, cancellation.Token) : Task.Run(async () =>
        {
            var service = new ScoreStudyAudio(MuseScorePath, PlaybackCacheDirectory);
            var audio = await service.PrepareAsync(scorePath, speed, null, cancellation.Token);
            if (!metronome) return;
            var beats = await service.PrepareBeatsAsync(scorePath, speed, null, cancellation.Token);
            if (beats.Count > 0) await service.PrepareMetronomeAudioAsync(audio, beats, null, cancellation.Token);
        }, cancellation.Token);
    }

    /// <summary>Lets Play reuse a preparation already running for the same score and options, instead of starting a second one.</summary>
    private async Task AwaitAudioPrewarm(string scorePath, CancellationToken token)
    {
        if (audioPrewarm is not { IsCompleted: false } running
            || audioPrewarmKey != PrewarmKey(scorePath, ScorePlaybackSpeed, MetronomeEnabled || CountInEnabled)) return;
        try { await running.WaitAsync(token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception) when (!token.IsCancellationRequested) { /* Play prepares the audio itself and reports any error. */ }
    }

    private void CancelAudioPrewarm() { audioPrewarmCancellation?.Cancel(); audioPrewarmCancellation = null; }
}
