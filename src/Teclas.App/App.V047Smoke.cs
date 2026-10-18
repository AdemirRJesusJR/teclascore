using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Rhythm notes of a recorded idea, microphone test, Brazilian left-hand rhythms, voice range by singing, humming
    // search and the accompaniment in time mode. No engine, MuseScore, audio device or network.
    private async void VerifyV047Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v047-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.47 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        var previousAvailable = PracticeWindow.microphoneAvailable;
        MainWindow? window = null;
        try
        {
            // The main window applies the theme the dialogs use.
            string settingsPath = Path.Combine(work, "settings.json"); new Settings { OutputDirectory = Path.Combine(work, "saida") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            string score = Path.Combine(work, "versos.mscz"); WriteVerseScore(score);
            var model = NativeMusicEditor.Open(score, CancellationToken.None);
            var chordSteps = PracticeSession.ChordsFromScore(model);

            // 1. What the rhythm analysis found is told in the status.
            string notes = Teclas.App.MainWindow.RhythmNotes(new JsonObject { ["triplet_beats"] = 2, ["swing"] = true, ["pickup_beats"] = 1 });
            Check(notes.Contains("2 tempos em tercinas") && notes.Contains("swing") && notes.Contains("anacruse de 1 tempo")
                && Teclas.App.MainWindow.RhythmNotes(new JsonObject { ["triplet_beats"] = 0, ["swing"] = false, ["pickup_beats"] = 0 }) == "", "rhythm-notes-are-reported " + notes);

            // 2. Microphone test: level and note live; a microphone that never answers gives the privacy hint, without freezing.
            var fake = new FakeNoteSource { Level = .2 };
            var test = new MicrophoneTestWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            test.microphoneFactory = () => fake; test.Show();
            for (int i = 0; i < 50 && !test.Listening; i++) { await Task.Delay(20); Flush(); }
            fake.Strike(60); Flush(); await Task.Delay(200); Flush();
            Check(test.Listening && test.NotesHeard == 1 && test.NoteText == "Dó4" && test.StatusText.Contains("funcionando") && test.MeterShare > .7, "microphone-test-shows-the-note " + test.StatusText);
            CaptureFolderWindow(test, Path.Combine(reports, "microphone-test.png")); test.Close(); Flush();
            Check(fake.Disposed, "microphone-test-releases-it");
            var silent = new MicrophoneTestWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            silent.microphoneFactory = () => { Thread.Sleep(Timeout.Infinite); return fake; }; silent.microphoneTimeout = TimeSpan.FromMilliseconds(200);
            silent.Show(); await Task.Delay(500); Flush();
            Check(!silent.Listening && silent.StatusText.Contains("Privacidade") && silent.NoteText == "Sem microfone", "silent-microphone-explains-what-to-check");
            silent.Close();

            // 3. Brazilian rhythms: balanced patterns in their own meters; the baião bass figure.
            foreach (var style in Enum.GetValues<AccompanimentStyle>())
            {
                var bar = AccompanimentPattern.Bar(style, [43, 50, 55, 59]);
                bool balanced = bar.Where(e => e.On).GroupBy(e => e.Pitch).All(g => g.Count() == bar.Count(e => !e.On && e.Pitch == g.Key));
                Check(balanced && bar.Count > 0 && bar.All(e => e.Beat >= 0 && e.Beat < AccompanimentPattern.BeatsPerBar(style, 4)), "pattern-is-balanced-" + style);
            }
            var baiao = AccompanimentPattern.Bar(AccompanimentStyle.Baiao, [43, 50, 55, 59]).Where(e => e.On && e.Pitch < 48).Select(e => e.Beat).ToArray();
            Check(baiao.SequenceEqual([0, .75, 1, 2, 2.75, 3]) && AccompanimentPattern.BeatsPerBar(AccompanimentStyle.Guarania, 4) == 3
                && AccompanimentPattern.Bar(AccompanimentStyle.Sertanejo, [43, 50, 55, 59]).Where(e => e.On && e.Pitch >= 48).All(e => e.Beat % 1 == .5), "brazilian-figures " + string.Join(",", baiao));

            // 4. Voice range by singing: steady notes count, slides and short notes do not; the key is suggested at once.
            var meter = new VoiceRangeMeter(); double t = 0;
            void Sing(double pitch, double seconds) { for (double end = t + seconds; t < end; t += .023) meter.Add(pitch, t); }
            Sing(48.2, .6); Sing(55, .1); Sing(60.4, .15); meter.Add(null, t); Sing(67.8, .6); Sing(74, .2);
            Check(meter.Low == 48 && meter.High == 68, $"steady-notes-make-the-range ({meter.Low}-{meter.High})");
            var voice = new VoiceRangeWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            voice.microphoneFactory = () => fake; voice.Show(); Flush();
            t = 0; for (; t < .7; t += .023) voice.Heard(53.1, t); for (; t < 1.5; t += .023) voice.Heard(69.9, t);
            Check(voice.Low == 53 && voice.High == 70, "voice-window-measures");
            voice.Close();
            var transpose = new TransposeWindow(0, 0, false, (64, 81), 55, 72) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            transpose.Show(); Flush();
            var previousDialog = TransposeWindow.voiceRangeDialog;
            TransposeWindow.voiceRangeDialog = w => { w.Heard(50, 0); w.Heard(50, .5); w.Heard(65, .6); w.Heard(65, 1.1); return true; };
            transpose.MeasureVoice((64, 81)); Flush();
            TransposeWindow.voiceRangeDialog = previousDialog;
            Check(transpose.VoiceLow == 50 && transpose.VoiceHigh == 65 && transpose.Semitones == VoiceRange.Suggest(64, 81, 50, 65).Semitones && transpose.VoiceResult.Length > 0, "sung-range-fills-and-suggests " + transpose.VoiceResult);
            transpose.Close();

            // 5. Humming search: the microphone choice takes sung notes and finds the song by its intervals.
            PracticeWindow.microphoneAvailable = () => true;
            var hum = new FakeNoteSource();
            var search = new MelodySearchWindow([("a", "Asa Branca", [67, 69, 71, 74, 74, 71, 72, 72]), ("b", "Outra", [60, 60, 60, 60, 62, 64])]) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            search.microphoneFactory = () => hum; search.Show(); Flush();
            Field<ComboBox>(search, "devices").SelectedItem = MelodySearchWindow.HumChoice;
            for (int i = 0; i < 50 && search.Hits.Count == 0; i++)
            {
                await Task.Delay(20); Flush();
                if (i == 10) foreach (int p in new[] { 62, 64, 66, 69, 69 }) hum.Strike(p);
            }
            Check(search.UsesMicrophone && search.Hits.FirstOrDefault()?.Title == "Asa Branca", "humming-finds-the-song");
            search.Close(); Flush();
            Check(hum.Disposed, "humming-releases-the-microphone");
            PracticeWindow.microphoneAvailable = previousAvailable;

            // 6. Time mode: the chords follow the clock in the chosen style and stop when the clock stops.
            var output = new RecordingMidiOutput();
            var practice = new PracticeWindow("Versos", PracticeSession.FromScore(model), chordSteps, true, 60) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            practice.followOutputFactory = () => output; practice.clickOutput = () => null; practice.Show(); Flush();
            Field<CheckBox>(practice, "inTime").IsChecked = true;
            practice.FollowMe = true; practice.FollowStyle = AccompanimentStyle.Sertanejo; Flush();
            Check(practice.InTime && practice.Follower is not null, "accompaniment-is-on-in-time-mode");
            int On() => output.Messages.Count(m => (m & 0xFF) == 0x91 && (m >> 16 & 0x7F) > 0);
            practice.TimedFollow(0); int atStart = On();
            practice.TimedFollow(.6); int afterOffbeat = On();
            practice.TimedFollow(4.1);
            bool cChord = output.Messages.Skip(output.Messages.Count - 4).Any(m => (m & 0xFF) == 0x91 && (m >> 8 & 0x7F) == 36);
            Check(atStart == 1 && afterOffbeat == 1 + 3 && output.Messages.Any(m => (m & 0xFF) == 0x91 && (m >> 8 & 0x7F) == 47) && cChord, $"chords-follow-the-clock ({atStart}, {afterOffbeat})");
            practice.PauseTimedFollow();
            Check(practice.FollowSounding.Count == 0, "pause-releases-the-chords");
            practice.Close(); Flush();
            Check(On() == output.Messages.Count(m => (m & 0xFF) == 0x81), "every-key-is-released-in-time-mode");

            Check(window.FindName("MicrophoneTestButton") is Button, "settings-have-the-microphone-test");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            PracticeWindow.microphoneAvailable = previousAvailable;
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
