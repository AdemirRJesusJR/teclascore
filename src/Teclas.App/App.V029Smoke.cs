using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // In-time practice, chord practice and study history. No engine, MuseScore or MIDI device runs.
    private void VerifyV029Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v029-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        PracticeWindow? window = null;
        try
        {
            // Timing: at 60 bpm the window is ±0.25 quarter and "on time" is ±0.125.
            PracticeStep Step(double start, params int[] pitches) => new(1 + (int)(start / 4), start % 4, pitches) { Start = start };
            var timed = new TimedPracticeSession([Step(0, 60), Step(1, 62), Step(2, 64)], 60);
            Check(timed.Tolerance == .25, "tolerance-is-a-quarter-second");
            Check(timed.Press(60, .05) == TimedPracticeResult.OnTime, "on-time-key");
            Check(timed.Press(61, .5) == TimedPracticeResult.Wrong, "wrong-key");
            Check(timed.Press(62, 1.2) == TimedPracticeResult.Late && new TimedPracticeSession([Step(1, 62)], 60).Press(62, .8) == TimedPracticeResult.Early, "late-and-early-keys");
            timed.Advance(3);
            Check(timed.Missed == 1 && timed.IsFinished && timed.Score == 37.5, "missed-key-and-score");
            Check(new TimedPracticeSession([Step(0, 48)], 60).Press(72, 0) == TimedPracticeResult.OnTime && new TimedPracticeSession([Step(0, 48)], 60, anyOctave: false).Press(72, 0) == TimedPracticeResult.Wrong, "octave-option-in-time");

            // Chords from the score: voiced like the diagrams, repeats merged, measure starts from content length.
            MusicHarmony Chord(int measure, double quarter, string root, string quality, int? inversion = null) => new($"h{measure}-{quarter}", 1, measure, 1, quarter, root, quality, true, "", true, inversion);
            MusicEvent Rest(int measure, double length) => new($"e{measure}", 1, measure, 1, 0, "Rest", "measure", 0, [], true, "") { DurationQuarter = length };
            var model = new NativeMusicModel("x.mscz", "", null!, [
                new MusicMeasure(1, 1, "", [Rest(1, 3)], [Chord(1, 0, "A", "m"), Chord(1, 2, "A", "m"), Chord(1, 2.5, "G", "7", 0)]),
                new MusicMeasure(1, 2, "", [Rest(2, 3)], [Chord(2, 0, "F", ""), Chord(2, 1, "X", "")]),
                new MusicMeasure(1, 3, "", [], [Chord(3, 0, "C", "13")])], []);
            var chords = PracticeSession.ChordsFromScore(model, 2);
            Check(chords.Select(c => c.Label).SequenceEqual(new[] { "Am", "G7", "F" }), "chord-steps-skip-repeats-and-unknown-symbols");
            Check(chords[0].Pitches.SequenceEqual(KeyboardDiagramVoicing.Pitches(9, "m", 2)) && chords[1].Pitches.SequenceEqual(KeyboardDiagramVoicing.Pitches(7, "7", 0)), "chord-steps-use-the-diagram-position");
            Check(chords[2].Start == 3 && PracticeSession.MeasureStarts(model)[3] == 6, "measure-starts-follow-the-content");

            // Window: chord practice waits; in-time practice scores and records attempts.
            var notes = new[] { Step(0, 60), Step(1, 64), Step(2, 67) };
            window = new PracticeWindow("Prova", notes, chords, true, 60) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            window.clickOutput = () => null;
            window.Show(); Flush();
            Check(window.Mode == PracticeMode.Notes && !window.InTime && window.Session.Steps.Count == 3, "starts-with-notes-in-wait-mode");
            window.Choose(PracticeMode.Chords, false, 60); Flush();
            foreach (int pitch in chords[0].Pitches) window.Play(pitch);
            Check(window.Session.Steps.Count == 3 && window.Session.Index == 1 && window.Timed is null, "chord-practice-advances-on-the-whole-chord");
            window.Choose(PracticeMode.Notes, true, 60); Flush();
            Check(window.Timed is { Bpm: 60 } && window.Attempts.Count == 1 && window.Attempts[0].Mode == "acordes", "changing-mode-records-the-attempt");
            Check(window.Play(60) == PracticeResult.Ignored, "in-time-waits-for-start");
            window.Play(60, 0); window.Play(64, 1.2); window.Tick(3);
            Check(window.Timed is { IsFinished: true, OnTime: 1, OffTime: 1, Missed: 1 } && window.Attempts.Count == 2, "in-time-attempt-finishes");
            var last = window.Attempts[^1];
            Check(last.Mode == "notas no tempo" && last.Bpm == 60 && last.Completed && last.Score == 50, "in-time-record");
            CaptureFolderWindow(window, Path.Combine(reports, "practice-in-time.png"));
            window.Close(); window = null;

            // History: summaries per song and persistence.
            var records = new List<StudyRecord>
            {
                new() { Song = "a.mid", Title = "Asa Branca", Date = new(2026, 10, 1), Seconds = 600, Score = 70, Completed = true, Bpm = 80, Mode = "notas no tempo", Hits = 7 },
                new() { Song = "a.mid", Title = "Asa Branca", Date = new(2026, 10, 2), Seconds = 300, Score = 60, Hits = 6, Misses = 4 },
                new() { Song = "b.mid", Title = "Xote", Date = new(2026, 10, 3), Seconds = 120, Score = 90, Hits = 9, Misses = 1 }
            };
            var summary = StudyHistory.Summaries(records);
            Check(summary.Count == 2 && summary[1] is { Title: "Asa Branca", Sessions: 2, Best: 70, Last: 60, Completed: 1, FastestBpm: 80 } && summary[1].TimeText == "15 min", "history-summarizes-per-song");
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { StudyHistory = records, PracticeBpm = 500 }.Save(settingsPath);
            var loaded = Settings.Load(settingsPath);
            Check(loaded.StudyHistory.Count == 3 && loaded.PracticeBpm == 200, "history-and-tempo-are-saved");
            Check(StudyHistory.Normalize(Enumerable.Range(0, StudyHistory.Limit + 5).Select(i => new StudyRecord { Date = new DateTime(2026, 1, 1).AddMinutes(i) })) is { Count: StudyHistory.Limit } kept && kept[0].Date.Minute == 5, "history-keeps-the-newest");
            var history = new StudyHistoryWindow(records) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            history.Show(); Flush();
            Check(history.Songs.Count == 2, "history-window-lists-songs");
            CaptureFolderWindow(history, Path.Combine(reports, "study-history.png"));
            history.Close();
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
    }
}
