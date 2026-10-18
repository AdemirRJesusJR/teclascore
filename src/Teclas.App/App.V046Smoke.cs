using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    private sealed class FakeNoteSource : IDisposableNoteSource
    {
        public event Action<int>? NoteOn;
        public event Action<double>? PitchHeard;
        public double Level { get; set; }
        public bool Disposed { get; private set; }
        public void Strike(int pitch) => NoteOn?.Invoke(pitch);
        public void Sing(double pitch) => PitchHeard?.Invoke(pitch);
        public void Dispose() => Disposed = true;
    }

    // Left-hand styles and slash-chord bass, streaming teacher with measure questions and exercises, chords from audio,
    // microphone practice, record an idea and the check after optical recognition. No engine, MuseScore, network or audio device.
    private async void VerifyV046Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v046-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.46 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Invoke(object target, string name, params object?[] args) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            var library = (ListBox)window.FindName("Library");
            string score = Path.Combine(work, "versos.mscz"); WriteVerseScore(score);
            var model = NativeMusicEditor.Open(score, CancellationToken.None);

            // 1. Slash chords keep their bass; left-hand styles; patterns follow the player's pace and wait when they stop.
            var chordSteps = PracticeSession.ChordsFromScore(model);
            Check(chordSteps[0] is { Label: "G", Bass: "B" } && AccompanimentFollower.Voice(chordSteps[0])[0] == 47 && AccompanimentFollower.Voice(chordSteps[1])[0] == 36, "slash-chord-plays-its-bass");
            foreach (var style in Enum.GetValues<AccompanimentStyle>())
            {
                var bar = AccompanimentPattern.Bar(style, [43, 50, 55, 59]);
                bool balanced = bar.Where(e => e.On).GroupBy(e => e.Pitch).All(g => g.Count() == bar.Count(e => !e.On && e.Pitch == g.Key));
                Check(balanced && bar.All(e => e.Beat >= 0 && e.Beat < AccompanimentPattern.BeatsPerBar(style, 4)), "pattern-is-balanced-" + style);
            }
            Check(AccompanimentPattern.Bar(AccompanimentStyle.Arpeggio, [43, 50, 55, 59]).Count(e => e.On && e.Pitch != 43) == 8
                && AccompanimentPattern.BeatsPerBar(AccompanimentStyle.Waltz, 4) == 3, "arpeggio-in-eighths-and-waltz-in-three");
            var output = new RecordingMidiOutput(); double now = 0;
            var practice = new PracticeWindow("Versos", PracticeSession.FromScore(model), chordSteps, true, 60) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            practice.followOutputFactory = () => output; practice.Show(); Flush();
            practice.followNow = () => now;
            practice.FollowMe = true; practice.FollowStyle = AccompanimentStyle.Arpeggio; Flush();
            practice.Play(67); practice.FollowTick();
            int NotesOn() => output.Messages.Count(m => (m & 0xFF) == 0x91 && (m >> 16 & 0x7F) > 0);
            int first = NotesOn();
            now = 1.6; practice.FollowTick(); // at ♩ = 60 that is beat 1.6: three more eighths
            Check(first == 2 && NotesOn() == 5 && output.Messages.Any(m => (m & 0xFF) == 0x91 && (m >> 8 & 0x7F) == 47), $"arpeggio-runs-at-the-players-pace ({first}, {NotesOn()})");
            now = 7; practice.FollowTick();
            Check(practice.FollowSounding.Count == 0, "accompaniment-waits-when-the-player-stops");
            now = 8; practice.Play(65); practice.FollowTick();
            Check(practice.FollowSounding.Count > 0, "next-note-restarts-the-bar");
            practice.Close(); Flush();
            Check(output.Messages.Count(m => (m & 0xFF) == 0x91 && (m >> 16 & 0x7F) > 0) == output.Messages.Count(m => (m & 0xFF) == 0x81), "every-key-is-released");

            // 4. Microphone: synthetic piano notes are recognized one by one, repeated notes included; the practice window takes them.
            var detector = new MicrophoneNoteDetector(44100);
            var heard = new List<int>();
            foreach (int pitch in new[] { 60, 64, 67, 67, 72 }) heard.AddRange(detector.Feed(PianoTone(pitch, .5)));
            Check(heard.SequenceEqual([60, 64, 67, 67, 72]), "microphone-hears-each-key " + string.Join(",", heard));
            Check(detector.Feed(new float[44100]).Count == 0, "silence-is-not-a-note");
            var previousAvailable = PracticeWindow.microphoneAvailable; PracticeWindow.microphoneAvailable = () => true;
            var fake = new FakeNoteSource();
            var micPractice = new PracticeWindow("Versos", PracticeSession.FromScore(model), chordSteps, false, 80) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            micPractice.microphoneFactory = () => fake; micPractice.Show(); Flush();
            var inputs = (ComboBox)typeof(PracticeWindow).GetField("devices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(micPractice)!;
            inputs.SelectedItem = PracticeWindow.MicrophoneChoice; Flush(); await Task.Delay(300); Flush();
            fake.Strike(55); Flush(); await Task.Delay(50); Flush();
            Check(micPractice.UsesMicrophone && micPractice.Session.Index == 1 && micPractice.Session.AnyOctave, "microphone-plays-the-practice");
            micPractice.Close(); Flush(); PracticeWindow.microphoneAvailable = previousAvailable;
            Check(fake.Disposed, "microphone-is-released");
            if (MicrophoneNoteInput.Available)
            {
                // The real device: opens, listens for a moment and closes without blocking (sounds around are ignored).
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var opening = Task.Run(() => new MicrophoneNoteInput());
                if (await Task.WhenAny(opening, Task.Delay(5000)) != opening) checks["real-microphone-did-not-answer-skipped"] = true; // the app shows a message instead
                else
                {
                    try
                    {
                        var real = await opening;
                        await Task.Delay(600);
                        await Task.Run(real.Dispose);
                        Check(watch.Elapsed < TimeSpan.FromSeconds(8), $"real-microphone-opens-and-closes ({watch.ElapsedMilliseconds} ms)");
                    }
                    catch (InvalidOperationException) { checks["real-microphone-unavailable-skipped"] = true; }
                }

            }
            else checks["no-microphone-skipped"] = true;
            // A microphone that never answers does not freeze the practice window.
            PracticeWindow.microphoneAvailable = () => true;
            var stuck = new PracticeWindow("Versos", PracticeSession.FromScore(model), chordSteps, false, 80) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            stuck.microphoneFactory = () => { Thread.Sleep(Timeout.Infinite); return fake; }; stuck.microphoneTimeout = TimeSpan.FromMilliseconds(300);
            stuck.Show(); Flush();
            var stuckInputs = (ComboBox)typeof(PracticeWindow).GetField("devices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stuck)!;
            stuckInputs.SelectedItem = PracticeWindow.MicrophoneChoice; Flush();
            await Task.Delay(700); Flush();
            var feedbackText = ((TextBlock)typeof(PracticeWindow).GetField("feedback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stuck)!).Text;
            Check(feedbackText.Contains("não respondeu") && stuck.Play(67) != PracticeResult.Ignored, "silent-microphone-does-not-freeze-practice");
            stuck.Close(); PracticeWindow.microphoneAvailable = previousAvailable;

            // 3. Chords from the audio: an unclear melody becomes a chords-only song and the chords heard are reported.
            string recording = Path.Combine(work, "banda.mp3"); File.WriteAllBytes(recording, [0]);
            window.audioTranscriber = (_, midi, _) => { File.WriteAllBytes(midi, TranscriptionMidi());
                return Task.FromResult(new JsonObject { ["melody_notes"] = 6, ["bpm"] = 92.0, ["melody_clear"] = false, ["chords"] = new JsonArray("C", "Am", "F", "G") }); };
            var created = await window.TranscribeAudioAsync([recording]); Flush();
            var band = window.Files.Single(r => created.Contains(r.FullPath));
            Check(band.ChordsOnly && window.Status.Contains("acordes C Am F G") && window.Status.Contains("só cifras"), "unclear-melody-gives-a-chord-score " + window.Status);

            // 5. Record an idea: keys and pedal become notes; the engine's answer (tempo, 3/4, chords) creates the song.
            var idea = new RecordIdeaWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }; idea.Show(); Flush();
            idea.Press(60, 80, 5); // ignored: not recording
            idea.Toggle();
            idea.Press(60, 80, 0); idea.Press(48, 90, .02); idea.Pedal(true, .3); idea.Release(48, .4); idea.Release(60, .5);
            idea.Press(62, 80, .7); idea.Pedal(false, 1.2); idea.Release(62, 1.3); idea.Press(64, 70, 1.4); idea.Release(64, 1.9); idea.Press(65, 70, 2); idea.Release(65, 2.4);
            idea.Toggle(); Flush();
            var recorded = idea.Notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
            Check(recorded.Count == 5 && recorded.First(n => n.Pitch == 48).End == 1.2 && recorded.First(n => n.Pitch == 60).End == 1.2 && recorded.First(n => n.Pitch == 62).End == 1.3,
                "keys-and-pedal-become-notes " + string.Join(" ", recorded.Select(n => $"{n.Pitch}:{n.Start}-{n.End}")));
            idea.Close();
            IReadOnlyList<RecordedNote>? sent = null;
            window.ideaTranscriber = (notes, midi, _) => { sent = notes; File.WriteAllBytes(midi, TranscriptionMidi());
                return Task.FromResult(new JsonObject { ["melody_notes"] = 16, ["bpm"] = 84.0, ["meter"] = 3, ["melody_clear"] = true, ["chords"] = new JsonArray("G", "D", "Em", "C") }); };
            string? ideaMidi = await window.TranscribeIdeaAsync(recorded); Flush();
            var ideaRow = window.Files.SingleOrDefault(r => r.FullPath == ideaMidi);
            Check(sent?.Count == 5 && ideaRow is { Collection: "Ideias gravadas", ChordsOnly: false } && ideaRow.Tracks.Single(t => t.IsMelody).Source.Key.Track == 1
                && window.Status.Contains("♩ ≈ 84") && window.Status.Contains("compasso 3/4") && window.Status.Contains("acordes G D Em C"), "idea-becomes-a-song " + window.Status);

            // 2 and 6. Teacher: answers stream in, the measure question names the measure, the context lists each measure,
            // and an exercise from the teacher becomes a library song.
            var chunks = new List<string>();
            var teacher = new TeacherWindow("Versos", async (q, onText, _) => { foreach (string piece in new[] { "Toque ", "devagar ", "no compasso." }) { onText(piece); chunks.Add(piece); await Task.Yield(); } return "Toque devagar no compasso."; })
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            teacher.Show(); Flush();
            teacher.MeasureNumber = 7;
            string? asked = null;
            var probe = new TeacherWindow("Versos", (q, onText, _) => { asked = q; return Task.FromResult("ok"); }) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            probe.MeasureNumber = 7; probe.Show(); Flush();
            var measureChip = FindButtons(probe).First(b => b.Content as string == "Explicar este compasso");
            measureChip.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(50); Flush();
            Check(asked?.Contains("compasso 7") == true, "measure-question-names-the-measure " + asked);
            probe.Close();
            await teacher.AskAsync("Como estudo?"); Flush();
            Check(chunks.Count == 3 && teacher.ConversationText.Contains("Toque devagar no compasso.") && teacher.Answers == 1, "answer-streams-in");
            CaptureFolderWindow(teacher, Path.Combine(reports, "teacher-v046.png")); teacher.Close();
            Check(Teclas.App.MainWindow.MeasureSummary(model).Contains("1: G/B | Sol4(2) Fá4(2)") && Teclas.App.MainWindow.MeasureSummary(model).Contains("2: C | Mi4(4)"), "context-lists-each-measure " + Teclas.App.MainWindow.MeasureSummary(model));
            const string exerciseJson = """
                {"title":"Arpejos de Sol e Dó","explanation":"Toque devagar, mão direita sozinha.","tempo":60,"beatsPerBar":4,
                 "steps":[{"chord":"G","notes":[67],"beats":1},{"chord":"G","notes":[71],"beats":1},{"chord":"G","notes":[74],"beats":2},
                          {"chord":"C","notes":[72],"beats":1},{"chord":"C","notes":[76],"beats":1},{"chord":"C","notes":[79],"beats":2},{"chord":"Xyz","notes":[200],"beats":4}]}
                """;
            var exercise = TeacherExercise.Parse(exerciseJson);
            var parsed = MidiReader.Read(exercise.ToMidi());
            Check(exercise.RightHand.Count == 6 && parsed.Notes.Count(n => n.Track == 1) == 6 && parsed.Notes.Where(n => n.Track == 2).Select(n => n.Pitch % 12).Distinct().Order().SequenceEqual([0, 2, 4, 7, 11])
                && exercise.Bpm == 60, "exercise-json-becomes-a-midi");

            var row = window.Files.First(); row.ScorePath = score; library.SelectedItem = null; Flush(); library.SelectedItem = row; Flush();
            TeacherWindow? shown = null; window.teacherDialog = w => { shown = w; return true; };
            window.teacherClient = (context, q, _) => Task.FromResult("Resposta.");
            window.teacherExerciseClient = (context, kind, _) => Task.FromResult(exerciseJson);
            Invoke(window, "TeacherClick", window, new RoutedEventArgs());
            for (int i = 0; i < 100 && shown is null; i++) { await Task.Delay(20); Flush(); }
            Check(shown is not null, "teacher-opens");
            shown!.ShowActivated = false; shown.ShowInTaskbar = false; shown.Opacity = 0; shown.Show(); Flush();
            await shown.MakeExerciseAsync("Arpejos", TeacherWindow.Exercises[1].Kind); Flush();
            var exerciseRow = window.Files.SingleOrDefault(r => r.Collection == "Exercícios do professor");
            Check(exerciseRow is { ScoreTitle: "Arpejos de Sol e Dó" } && shown.StatusText.Contains("Arpejos de Sol e Dó") && File.Exists(exerciseRow.FullPath), "exercise-enters-the-library " + shown.StatusText);
            shown.Close();

            // 7. After optical recognition: the measure with a missing beat and the out-of-range note are listed.
            string misread = Path.Combine(work, "lido.mscz"); WriteMisreadScore(misread);
            var suspects = RecognitionAudit.Check(NativeMusicEditor.Open(misread, CancellationToken.None));
            Check(suspects.Select(s => s.Number).SequenceEqual([2, 3]) && suspects[0].Reason.Contains("3 de 4 tempos") && suspects[1].Reason.Contains("grave demais"), "misread-measures-are-found " + string.Join(" | ", suspects.Select(s => s.Number + ":" + s.Reason)));
            Window? review = null; window.recognitionReviewDialog = w => { review = w; return true; };
            int listed = await window.ReviewRecognizedAsync([misread, score]);
            var reviewText = review is null ? "" : ((TextBox)LogicalTreeHelper.FindLogicalNode(review, "RecognitionReviewText")).Text;
            Check(listed == 2 && reviewText.Contains("Compasso 2: 3 de 4 tempos") && window.Status.Contains("2 compasso(s) para conferir"), "review-window-lists-them " + reviewText);
            review?.Close();

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button button) yield return button;
            foreach (var inner in FindButtons(child)) yield return inner;
        }
    }

    /// <summary>A struck piano key: fundamental and partials with a fast decay, 44.1 kHz.</summary>
    private static float[] PianoTone(int pitch, double seconds)
    {
        const int rate = 44100; var samples = new float[(int)(rate * seconds)];
        double frequency = 440 * Math.Pow(2, (pitch - 69) / 12.0);
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)rate, value = 0;
            for (int k = 1; k <= 5; k++) value += Math.Sin(2 * Math.PI * frequency * k * t) / Math.Pow(k, 1.6);
            samples[i] = (float)(.4 * value * Math.Exp(-t * 4) * Math.Min(1, t * 400));
        }
        return samples;
    }

    /// <summary>Four 4/4 measures: whole, half + quarter (a beat missing), a whole note far too low, whole.</summary>
    private static void WriteMisreadScore(string path)
    {
        string Note(int pitch, string duration) => $"<Chord><durationType>{duration}</durationType><Note><pitch>{pitch}</pitch><tpc>14</tpc></Note></Chord>";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false));
        writer.Write("<museScore version='4.70'><Score><Division>480</Division><metaTag name='workTitle'>Lido</metaTag><Part><Staff id='1'><StaffType group='pitched'/></Staff>" +
            "<Instrument id='piano'><Channel><program value='0'/></Channel></Instrument></Part><Staff id='1'>" +
            "<Measure><voice><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>" + Note(60, "whole") + "</voice></Measure>" +
            "<Measure><voice>" + Note(62, "half") + Note(64, "quarter") + "</voice></Measure>" +
            "<Measure><voice>" + Note(20, "whole") + "</voice></Measure>" +
            "<Measure><voice>" + Note(60, "whole") + "</voice></Measure>" +
            "</Staff></Score></museScore>");
    }
}
