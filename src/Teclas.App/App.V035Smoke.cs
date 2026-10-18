using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Search by playing, both hands, study goals and recording. No MIDI device, engine or MuseScore runs.
    private void VerifyV035Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v035-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        PracticeWindow? practice = null;
        try
        {
            // Melody search: intervals, any key; a semitone off counts half.
            var asaBranca = new[] { 60, 62, 64, 67, 67, 64, 65, 65, 64, 62, 60 };
            var other = new[] { 70, 69, 67, 65, 64, 62, 60, 62, 64, 65 };
            Check(MelodySearch.Contour([new MidiNote(0, 0, 0, 60, 90, 0, 10), new MidiNote(0, 0, 0, 67, 90, 0, 10), new MidiNote(0, 0, 0, 62, 90, 20, 30)]).SequenceEqual(new[] { 67, 62 }), "contour-takes-the-top-voice");
            Check(MelodySearch.Similarity([65, 67, 69, 72, 72], asaBranca, out int at) == 1 && at == 0, "search-matches-in-another-key");
            Check(Math.Abs(MelodySearch.Similarity([60, 62, 64, 68, 68], asaBranca, out _) - .875) < .001, "semitone-off-counts-half");
            var hits = MelodySearch.Search([67, 69, 71, 74, 74], [("a", "Asa Branca", asaBranca), ("b", "Outra", other)]);
            Check(hits.Count == 1 && hits[0] is { Id: "a", Similarity: 1 }, "search-ranks-the-right-song");
            var search = new MelodySearchWindow([("a", "Asa Branca", asaBranca), ("b", "Outra", other)]) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            search.Show(); Flush();
            foreach (int pitch in new[] { 55, 57, 59, 62 }) search.Play(pitch);
            Check(search.Hits.FirstOrDefault()?.Id == "a", "search-window-finds-while-playing");
            CaptureFolderWindow(search, Path.Combine(reports, "melody-search.png")); search.Close();

            // Both hands: the chord joins the melody attack at the same moment; a chord alone is its own step.
            PracticeStep S(double start, params int[] pitches) => new(1, start, pitches) { Start = start };
            var melody = new[] { S(0, 72), S(1, 74), S(2, 76) };
            var chords = new[] { S(0, 55, 60, 64) with { Label = "C" }, S(1.5, 55, 59, 62) with { Label = "G" } };
            var both = PracticeSession.Combine(melody, chords);
            Check(both.Count == 4 && both[0].Pitches.SequenceEqual(new[] { 55, 60, 64, 72 }) && both[0].Label == "C" && both[2] is { Start: 1.5, Label: "G" } && both[1].Pitches.SequenceEqual(new[] { 74 }), "both-hands-merge-by-time");

            // Practice window: both hands, goals and recording.
            practice = new PracticeWindow("Asa Branca", melody, chords, true, 60) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, DailyGoalMinutes = 15, MinutesBeforeToday = 15, TempoGoal = 60 };
            practice.clickOutput = () => null; practice.RecordingFolder = Path.Combine(work, "gravacoes");
            practice.Show(); Flush();
            practice.Choose(PracticeMode.Both, false, 60); Flush();
            Check(practice.Session.Steps.Count == 4, "both-hands-mode-uses-merged-steps");
            foreach (int pitch in both[0].Pitches) practice.Play(pitch);
            Check(practice.Session.Index == 1 && practice.DailyGoalCelebrated, "daily-goal-is-celebrated");
            practice.Choose(PracticeMode.Notes, true, 60); Flush();
            Check(practice.Attempts.Any(a => a.Mode == "duas mãos"), "both-hands-attempt-is-recorded");
            practice.Play(72, 0); practice.Play(74, 1); practice.Play(76, 2); practice.Tick(3);
            Check(practice.Timed is { Score: 100 } && practice.Attempts[^1].Mode == "notas no tempo", "in-time-pass-for-the-goal");
            // Recording: two keys, saved as MIDI, played back at their times.
            practice.ToggleRecording();
            practice.RecordForSmoke(60, 90, .0); practice.RecordForSmoke(60, 0, .4); practice.RecordForSmoke(64, 80, .5); practice.RecordForSmoke(64, 0, .9);
            practice.ToggleRecording();
            var saved = practice.LastRecording is { } file ? MidiReader.Read(file) : null;
            Check(saved is not null && saved.Notes.Select(n => n.Pitch).SequenceEqual(new[] { 60, 64 }) && saved.Notes[1].Start > saved.Notes[0].Start, "recording-is-saved-as-midi");
            var output = new RecordingMidiOutput();
            practice.StartListeningForSmoke(output);
            Check(practice.PlaybackTick(.45) == 2 && practice.PlaybackTick(5) == 2 && output.Messages.Count == 4, "recording-plays-back-in-order");
            CaptureFolderWindow(practice, Path.Combine(reports, "practice-goals-recording.png"));
            practice.Close(); practice = null;

            // Goals in the history: daily minutes and the selected song's tempo, saved through the callback.
            var records = new List<StudyRecord> { new() { Song = "a", Title = "Asa Branca", Date = DateTime.Now, Seconds = 600, Score = 85, Completed = true, Bpm = 92, Mode = "notas no tempo", Hits = 9 } };
            (int Daily, IReadOnlyDictionary<string, int> Tempos)? saved2 = null;
            var history = new StudyHistoryWindow(records, null, 0, new Dictionary<string, int>(), (daily, tempos) => saved2 = (daily, new Dictionary<string, int>(tempos))) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            history.Show(); Flush();
            history.DailyGoalBox!.Text = "15"; history.TempoGoalBox!.Text = "90"; history.SaveGoals(); Flush();
            Check(saved2 is { Daily: 15 } && saved2.Value.Tempos["a"] == 90 && history.GoalStatus!.Text.Contains("10 de 15 min") && history.GoalStatus.Text.Contains("atingida"), "history-goals-are-saved-and-shown");
            CaptureFolderWindow(history, Path.Combine(reports, "study-goals.png")); history.Close();
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { DailyGoalMinutes = 900, TempoGoals = new() { ["a"] = 90, ["b"] = 5 } }.Save(settingsPath);
            var loaded = Settings.Load(settingsPath);
            Check(loaded.DailyGoalMinutes == 600 && loaded.TempoGoals.Count == 1, "goals-are-normalized-when-saved");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            practice?.Close(); Shutdown(1);
        }
    }
}
