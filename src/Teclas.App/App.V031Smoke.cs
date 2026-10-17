using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    /// <summary>Accompaniment with a hand-driven clock: no MuseScore, no audio device.</summary>
    private sealed class FakeAccompaniment(ScorePlaybackMap map) : IPracticeAccompaniment
    {
        public int MapRequests, PlayRequests;
        public double? StartedAt, Speed;
        public double PositionSeconds { get; set; }
        public bool IsPlaying { get; private set; }
        public Task<ScorePlaybackMap> PrepareMapAsync(double speed, IProgress<string>? progress, CancellationToken token) { MapRequests++; return Task.FromResult(map with { Speed = speed }); }
        public Task PlayAsync(double speed, double startSeconds, IProgress<string>? progress, CancellationToken token)
        { PlayRequests++; Speed = speed; StartedAt = startSeconds; PositionSeconds = startSeconds; IsPlaying = true; return Task.CompletedTask; }
        public void Stop() => IsPlaying = false;
        public void Dispose() { }
    }

    // Score following, playing along with the music, passages repeated until right, and the progress chart.
    private async void VerifyV031Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v031-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        PracticeWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.31 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException(what);
            Flush();
        }
        try
        {
            // Timeline: three 4/4 measures of 2 s (♩ = 120), then a repeat back to measure 1 that is not mapped.
            var occurrences = new[] { new ScorePlaybackOccurrence(0, 1, 0, 2, null), new ScorePlaybackOccurrence(1, 2, 2, 4, null),
                new ScorePlaybackOccurrence(2, 3, 4, 6, null), new ScorePlaybackOccurrence(3, 1, 6, 8, null) };
            var starts = new Dictionary<int, double> { [1] = 0, [2] = 4, [3] = 8 };
            var line = new PracticeTimeline(occurrences, starts);
            Check(line.Bpm == 120 && line.ToQuarters(3) == 6 && line.ToSeconds(10) == 5, "timeline-converts-both-ways");
            Check(line.ToQuarters(7) == 14 && line.LeadInSeconds(8) == 2 && line.LeadInSeconds(0) == 0, "timeline-ignores-repeats-and-leads-in-one-measure");

            // Optional native run: the real study audio and measure map of a generated score drive the timeline.
            if (Environment.GetEnvironmentVariable("TECLAS_TEST_MUSESCORE") is { Length: > 0 } museScore && File.Exists(museScore)
                && Environment.GetEnvironmentVariable("TECLAS_TEST_REPERTOIRE_SCORE") is { Length: > 0 } real && File.Exists(real))
            {
                // Short folder: MediaPlayer cannot open audio paths longer than 260 characters.
                string nativeWork = Path.Combine(Path.GetTempPath(), "tcl-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(nativeWork);
                string copy = Path.Combine(nativeWork, "real.mscz"); File.Copy(real, copy);
                var model = NativeMusicEditor.Open(copy, CancellationToken.None);
                using var playback = new EditorScorePlayback(museScore, Path.Combine(nativeWork, "audio"));
                var accompaniment = new ScorePracticeAccompaniment(copy, playback);
                var realMap = await accompaniment.PrepareMapAsync(.5, null, CancellationToken.None);
                var realLine = new PracticeTimeline(realMap.MeasureTimeline, PracticeSession.MeasureStarts(model));
                var firstSteps = PracticeSession.FromScore(model);
                Check(!realLine.IsEmpty && realLine.Bpm is > 40 and < 250 && firstSteps.Count > 0, "native-map-gives-a-timeline");
                await accompaniment.PlayAsync(.5, realLine.LeadInSeconds(firstSteps[0].Start), null, CancellationToken.None);
                double before = realLine.ToQuarters(accompaniment.PositionSeconds);
                await Task.Delay(1500);
                double after = realLine.ToQuarters(accompaniment.PositionSeconds);
                accompaniment.Stop();
                // 1.5 s at half speed of ♩ = Bpm advance about 0.75 × Bpm / 60 quarters.
                double expected = 1.5 * .5 * realLine.Bpm / 60;
                Check(after - before > expected * .5 && after - before < expected * 1.6, $"native-audio-clock-runs-at-study-speed ({after - before:0.00} of {expected:0.00})");
            }

            // Window with a score page and the fake accompaniment.
            string page = Path.Combine(work, "pagina-1.png");
            var bitmap = new RenderTargetBitmap(400, 560, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual(); using (var context = visual.RenderOpen()) context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 400, 560));
            bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(page)) encoder.Save(file);
            var map = new ScorePlaybackMap(page, "", 1, [page], [new(1, new(0, .1, .1, .3, .1)), new(2, new(0, .4, .1, .3, .1)), new(3, new(0, .1, .3, .3, .1))], [], occurrences, [], true) { MeasureTimeline = occurrences };
            var music = new FakeAccompaniment(map);
            PracticeStep Step(int measure, params int[] pitches) => new(measure, 0, pitches) { Start = starts[measure] };
            var notes = new[] { Step(1, 60), Step(2, 62), Step(3, 64) };
            window = new PracticeWindow("Prova", notes, [], true, 60, page, music, starts) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            window.clickOutput = () => null;
            window.Show(); Flush();
            await Wait(() => music.MapRequests >= 1 && window.Viewer?.PlaybackMap is not null, "visual follow was not prepared");
            Check(window.Viewer is not null && music.MapRequests == 1, "score-follows-the-practice");

            // Passage 2–3, repeated until a clean pass.
            window.ChoosePassage("2", "3", repeat: true); Flush();
            Check(window.Session.Steps.Select(s => s.Measure).SequenceEqual(new[] { 2, 3 }), "passage-limits-the-steps");
            window.Play(70); window.Play(62); window.Play(64);
            Check(window.Session.IsFinished && window.Session.Misses == 1, "passage-pass-with-a-mistake");
            await Wait(() => window.PassageAttempt == 2 && window.Session.Index == 0, "passage did not restart");
            Check(window.Attempts.Count == 1, "passage-restarts-after-a-mistake");
            window.Play(62); window.Play(64);
            Check(window.PassageAttempt == 1 && window.Attempts.Count == 2, "clean-pass-ends-the-training");

            // Playing along: the music clock (at 50%) drives the timing; the audio starts one measure before the passage.
            window.ChooseMusic(true, .5); Flush();
            Check(window.WithMusic && window.InTime, "music-turns-on-in-time-mode");
            window.StartOrStop();
            await Wait(() => music.IsPlaying, "music did not start");
            Check(music.StartedAt == 0 && music.Speed == .5 && window.Timed is { Bpm: 60 }, "music-starts-a-measure-early-at-study-speed");
            music.PositionSeconds = 2.02; window.Tick();
            window.Play(62);
            music.PositionSeconds = 6.5; window.Tick();
            Check(window.Timed is { IsFinished: true, OnTime: 1, Missed: 1 } && !music.IsPlaying, "music-clock-scores-and-stops-at-the-end");
            Check(window.Attempts[^1] is { Mode: "notas no tempo", Bpm: 60 }, "music-attempt-is-recorded");
            CaptureFolderWindow(window, Path.Combine(reports, "practice-with-score.png"));
            window.Close(); window = null;

            // Progress chart: one dot per attempt of the selected song.
            var records = new List<StudyRecord>
            {
                new() { Song = "a", Title = "Asa Branca", Date = new(2026, 10, 1), Score = 40, Hits = 4, Misses = 6 },
                new() { Song = "a", Title = "Asa Branca", Date = new(2026, 10, 2), Score = 65, Hits = 6, Misses = 3, Mode = "notas no tempo", Bpm = 70 },
                new() { Song = "a", Title = "Asa Branca", Date = new(2026, 10, 3), Score = 90, Hits = 9, Misses = 1, Mode = "notas no tempo", Bpm = 80 }
            };
            var history = new StudyHistoryWindow(records) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            history.Show(); Flush();
            Check(history.ChartPoints == 3 && history.ChartTitle.Text.Contains("+50 pontos"), "history-chart-shows-progress");
            CaptureFolderWindow(history, Path.Combine(reports, "study-progress.png"));
            history.Close();
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
