using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // No renderer is executed and no media URI is opened. The cache is primed with verified synthetic pages.
    private async void VerifyScoreAudioUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "score-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        MainWindow? window = null;
        var checks = new Dictionary<string, bool>();
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var trace = PresentationTraceSources.DataBindingSource; var previousLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning; trace.Listeners.Add(listener);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
        timeout.Tick += (_, _) =>
        { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Score audio smoke timed out." })); Shutdown(1); };
        timeout.Start();
        object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, hidden)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, hidden)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, hidden)!.SetValue(window, value);
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() { Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window?.UpdateLayout(); }
        async Task Wait(Func<bool> predicate)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException("Score audio transition failed. " + window?.Status);
            Flush();
        }
        Task Idle() => Wait(() => window!.IsIdle && !Field<bool>("preparingScoreSynchronization"));
        void LoadedState(bool playing)
        {
            Invoke("ResetPlayback", false);
            Set("playbackScoreIdentity", Invoke("SelectedScoreIdentity"));
            Set("audioLoaded", true); Set("loadedAudioPath", "synthetic-player-never-opened");
            Set("audioPlaying", playing); Set("playbackDuration", 5d);
        }
        try
        {
            string source = Path.Combine(work, "score.mscz"), page1 = Path.Combine(work, "page-1.png"), page2 = Path.Combine(work, "page-2.png");
            string renderer = Path.Combine(work, "synthetic-renderer.exe"), settingsPath = Path.Combine(work, "settings.json");
            V018Score(source); V018Preview(page1); V018Preview(page2);
            await File.WriteAllTextAsync(renderer, "Synthetic identity only; never execute.");
            string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            new Settings { MuseScorePath = renderer, FollowScorePlayback = false, OutputDirectory = Path.Combine(work, "output") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            window.Show(); Flush();
            var row = new MidiRow { FullPath = source, NativeSourceSha256 = hash, ScorePath = source, PreviewPath = page1, PageCount = 2, ScoreTitle = "Fixture" };
            window.Files.Add(row); ((ListBox)window.FindName("Library")).SelectedItem = row;
            window.SelectPageForSmoke("library", 2); Flush();
            var viewer = (ScoreViewer)window.FindName("MainScoreViewer");
            var pageImage = (Image)viewer.FindName("PageImage");
            var cache = Field<ScorePlaybackMapCache>("scoreMapCache");
            var timeline = new[] { new ScorePlaybackOccurrence(0, 1, 0, 2, null), new ScorePlaybackOccurrence(1, 2, 2, 4, null) };
            var map = new ScorePlaybackMap(source, hash, 1, [page1, page2],
                [new(1, new(1, 0, 0, 1, 1)), new(2, new(2, 0, 0, 1, 1))], [], timeline, [], true) { MeasureTimeline = timeline };
            await cache.GetOrPrepareAsync(source, renderer, 1, _ => Task.FromResult(map), null, CancellationToken.None);
            int audioRequests = 0, overlays = 0;
            window.PropertyChanged += (_, _) => { if (window.IsAudioOperation && window.OperationVisibility == Visibility.Visible) overlays++; };
            Set("scoreAudioPreparation", new Func<ScoreStudyAudio, string, double, IProgress<string>?, CancellationToken, Task<ScoreAudio>>(
                (_, _, _, _, token) =>
                {
                    token.ThrowIfCancellationRequested(); audioRequests++;
                    // Uri construction rejects this sentinel BEFORE MediaPlayer.Open; no device or renderer is used.
                    return Task.FromResult(new ScoreAudio("", TimeSpan.FromSeconds(4)));
                }));
            Invoke("PlayScoreClick", window, new RoutedEventArgs()); await Idle();
            Check(audioRequests == 1 && cache.CacheHits == 0, "follow-disabled-starts-audio-without-preparing-map");
            Check(!Field<bool>("audioLoaded") && !Field<bool>("audioPlaying") && !Field<bool>("audioOpening"), "synthetic-open-failure-clears-player-state");
            window.FollowScorePlayback = true;
            Invoke("PlayScoreClick", window, new RoutedEventArgs()); await Idle();
            Check(audioRequests == 2 && cache.CacheHits == 1, "follow-enabled-reuses-verified-map-before-audio-open");
            window.FollowScorePlayback = false;
            Invoke("PrepareScoreSynchronizationClick", window, new RoutedEventArgs()); await Idle();
            await Wait(() => pageImage.Source is not null && viewer.PageCount == 2);
            Check(ReferenceEquals(Field<ScorePlaybackMap?>("scorePlaybackMap"), map) && cache.CacheHits == 2, "explicit-prepare-still-works-with-follow-disabled");
            var firstBitmap = pageImage.Source;
            viewer.CurrentPage = 2; await Wait(() => viewer.CurrentPage == 2 && pageImage.Source is not null && !ReferenceEquals(firstBitmap, pageImage.Source));
            var bitmap = pageImage.Source;
            Invoke("PrepareScoreSynchronizationClick", window, new RoutedEventArgs()); await Idle();
            Check(viewer.CurrentPage == 2 && ReferenceEquals(bitmap, pageImage.Source), "same-active-map-preserves-current-page-and-bitmap");

            LoadedState(false); window.FollowScorePlayback = true; await Idle();
            Check(Field<bool>("audioLoaded") && !Field<bool>("audioPlaying") && ReferenceEquals(Field<ScorePlaybackMap?>("scorePlaybackMap"), map),
                "enabling-follow-after-a-paused-load-prepares-map-without-starting-sound");
            long beforeDuplicate = cache.CacheHits; window.FollowScorePlayback = true; await Idle();
            Check(cache.CacheHits == beforeDuplicate, "same-follow-value-cannot-start-reentrant-preparation");
            window.FollowScorePlayback = false; LoadedState(true);
            window.FollowScorePlayback = true; await Idle();
            Check(Field<bool>("audioPlaying") && Field<bool>("audioLoaded"), "enabling-follow-during-playback-preserves-the-playing-state");
            Invoke("StopPlayback", "Synthetic check complete");
            Check(!Field<bool>("audioPlaying"), "synthetic-playback-state-can-stop-after-follow-preparation");
            Check(cache.Preparations == 1, "all-ui-map-requests-reuse-one-primed-preparation-without-running-renderer");
            Check(overlays == 0, "score-audio-and-map-preparation-never-show-modal-overlay");
            Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))) == hash, "score-content-remains-unchanged");
            Check(Field<string?>("persistencePath") == settingsPath, "settings-are-explicitly-isolated");
            Flush(); Check(string.IsNullOrWhiteSpace(bindingOutput.ToString()), "no-binding-warnings");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, cachePreparations = cache.Preparations,
                cacheHits = cache.CacheHits, audioRequests, nativeProcesses = 0, mediaUrisOpened = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString(), bindings = bindingOutput.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); trace.Listeners.Remove(listener); trace.Switch.Level = previousLevel; }
    }
}
