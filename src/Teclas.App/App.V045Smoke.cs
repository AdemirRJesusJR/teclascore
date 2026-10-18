using System.IO;
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
    // Audio to score (stub transcriber), lyrics part marker, Windows audio decoding, AI teacher (stub client),
    // accompaniment that follows the player and the Audiveris import (stub). No engine, MuseScore, Audiveris or network.
    private async void VerifyV045Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v045-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.45 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Invoke(object target, string name, params object?[] args) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);
        try
        {
            string settingsPath = Path.Combine(work, "settings.json"); new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            var library = (ListBox)window.FindName("Library");

            // Audio to score: the transcription becomes a library song with the melody track chosen.
            string recording = Path.Combine(work, "gravacao.mp3"); File.WriteAllBytes(recording, [0]);
            window.audioTranscriber = (_, midi, _) =>
            {
                File.WriteAllBytes(midi, TranscriptionMidi());
                return Task.FromResult(new JsonObject { ["melody_notes"] = 16, ["bpm"] = 100.0 });
            };
            var created = await window.TranscribeAudioAsync([recording]); Flush();
            var song = window.Files.SingleOrDefault(r => created.Contains(r.FullPath));
            Check(created.Count == 1 && File.Exists(created[0]) && Path.GetDirectoryName(created[0])!.EndsWith("Transcrições de áudio") && song is not null, "transcription-enters-the-library " + window.Status);
            Check(song!.Collection == "Transcrições de áudio" && song.Tracks.Where(t => t.IsMelody).Select(t => t.Source.Key.Track).SequenceEqual([1]) && window.Status.Contains("♩ ≈ 100"),
                "melody-track-is-chosen " + window.Status);

            // Windows audio decoding (Media Foundation); skipped where the system has no media components.
            string wav = Path.Combine(work, "tom.wav"), decoded = Path.Combine(work, "tom-pcm.wav"); WriteToneWav(wav);
            try
            {
                double seconds = AudioDecoder.ToWav(wav, decoded, CancellationToken.None);
                var bytes = File.ReadAllBytes(decoded);
                Check(Math.Abs(seconds - 1) < .1 && bytes.Take(4).SequenceEqual("RIFF"u8.ToArray()) && bytes.Length > 40_000, $"windows-decodes-audio ({seconds:0.00}s, {bytes.Length} bytes)");
            }
            catch (Exception error) when (error is InvalidDataException or DllNotFoundException or System.Runtime.InteropServices.COMException) { checks["windows-decoder-unavailable-skipped"] = true; }

            // Lyrics: the part under the sung syllables gets the "Canto" mark and the hint offers to switch the melody.
            string karaoke = Path.Combine(work, "karaoke.mid"); File.WriteAllBytes(karaoke, KaraokeMidi());
            window.ImportForSmoke(karaoke); Flush();
            var sung = window.Files.Single(r => r.FullPath == karaoke); library.SelectedItem = sung; Flush();
            foreach (var track in sung.Tracks) track.IsMelody = track.Source.Key.Track == 1;
            Invoke(window, "RefreshSelection"); Flush();
            var voice = sung.Tracks.SingleOrDefault(t => t.Source.Key.Track == 2) ?? throw new InvalidOperationException("tracks " + sung.Status + " " + string.Join(",", sung.Tracks.Select(t => t.Source.Key)));
            Check(voice.LyricsBadge == "🎤 100%" && sung.Tracks.Where(t => t != voice).All(t => t.LyricsBadge == "") && voice.LyricsToolTip.Contains("acompanha o canto"), "lyrics-part-is-marked " + voice.LyricsBadge);
            Check(window.LyricsTrackVisibility == Visibility.Visible && window.LyricsTrackHint.Contains("16 de 16"), "hint-offers-the-sung-part " + window.LyricsTrackHint);
            Invoke(window, "UseLyricsTrackClick", window, new RoutedEventArgs()); Flush();
            Check(voice.IsMelody && sung.Tracks.Count(t => t.IsMelody) == 1 && window.LyricsTrackVisibility == Visibility.Collapsed, "use-this-part-switches-the-melody");

            // AI teacher: the key lives in the Windows Credential Manager; without a key nothing is sent; with the client the
            // song's chord sheet and practice results go as context and the conversation keeps going.
            string target = "TeclaScore/Smoke-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                TeacherKeyStore.Write("sk-ant-teste-0000000000000000000000", target);
                Check(TeacherKeyStore.Read(target) == "sk-ant-teste-0000000000000000000000", "key-is-kept-by-windows");
                TeacherKeyStore.Delete(target);
                Check(TeacherKeyStore.Read(target) is null, "key-is-removed");
            }
            catch (InvalidOperationException) { checks["credential-manager-unavailable-skipped"] = true; }
            string score = Path.Combine(work, "versos.mscz"); WriteVerseScore(score);
            sung.ScorePath = score; library.SelectedItem = null; Flush(); library.SelectedItem = sung; Flush();
            window.teacherKey = () => null;
            TeacherWindow? shown = null; window.teacherDialog = w => { shown = w; return true; };
            Invoke(window, "TeacherClick", window, new RoutedEventArgs()); Flush();
            Check(shown is null && window.CurrentPage == "settings" && window.Status.Contains("chave") && !window.HasTeacherKey, "without-a-key-nothing-is-sent");
            window.SelectPageForSmoke("library", 2); Flush();
            string? sentContext = null;
            window.teacherClient = (context, question, _) => { sentContext = context; return Task.FromResult("Comece pelo compasso 1: " + question.Length); };
            var settings = (Settings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            settings.StudyHistory.Add(new StudyRecord { Song = karaoke, Date = DateTime.Now, Mode = "notas", Score = 87, Steps = 3, StepsDone = 3, Misses = 1, Completed = true });
            Invoke(window, "TeacherClick", window, new RoutedEventArgs());
            for (int i = 0; i < 100 && shown is null; i++) { await Task.Delay(20); Flush(); }
            Check(shown is not null, "teacher-window-opens");
            shown!.ShowActivated = false; shown.ShowInTaskbar = false; shown.Opacity = 0; shown.Show(); Flush();
            await shown.AskAsync(TeacherWindow.Suggestions[0]); Flush();
            await shown.AskAsync("E a mão esquerda?"); Flush();
            Check(shown.Answers == 2 && shown.ConversationText.Contains("Comece pelo compasso 1") && shown.ConversationText.Contains("E a mão esquerda?"), "conversation-continues");
            Check(sentContext is not null && sentContext.Contains("Título:") && sentContext.Contains("Ca") && sentContext.Contains("87% de acerto"), "song-context-is-sent " + sentContext);
            CaptureFolderWindow(shown, Path.Combine(reports, "teacher.png")); shown.Close();
            Check(window.FindName("TeacherKeyBox") is PasswordBox && window.TeacherKeyStatus.Contains("nada é enviado"), "settings-have-the-key-field");

            // Accompaniment that follows: chords and bass change when the player reaches a new chord, at the player's pace.
            var model = NativeMusicEditor.Open(score, CancellationToken.None);
            var follow = new AccompanimentFollower(PracticeSession.ChordsFromScore(model));
            var first = follow.Follow(0, 10); var same = follow.Follow(2, 11.5); var next = follow.Follow(4, 13);
            Check(first is { Label: "G", Off.Count: 0 } && first.On.Contains(43) && first.On.Skip(1).All(p => p is >= 48 and < 72) && same is null && next is { Label: "C" } && next.Off.SequenceEqual(first.On) && next.On.Contains(36),
                "follower-changes-chords " + string.Join(",", first?.On ?? []));
            Check(follow.Bpm is >= 79 and <= 81, "follower-measures-the-pace " + follow.Bpm);
            var output = new RecordingMidiOutput();
            var practice = new PracticeWindow("Versos", PracticeSession.FromScore(model), PracticeSession.ChordsFromScore(model), true, 80) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            practice.followOutputFactory = () => output; practice.Show(); Flush();
            practice.FollowMe = true; Flush();
            practice.Play(67); practice.Play(65); Flush();
            Check(practice.Follower is { Label: "G" } && output.Messages.Count(m => (m & 0xFF) == 0x91 && (m >> 16 & 0x7F) > 0) == first!.On.Count, "playing-starts-the-accompaniment");
            practice.Play(64); Flush();
            Check(practice.Follower!.Label == "C" && output.Messages.Count(m => (m & 0xFF) == 0x81) == first!.On.Count, "next-chord-follows-the-player");
            practice.Close(); Flush();
            Check(output.Messages.Count(m => (m & 0xFF) == 0x81) == first!.On.Count + next!.On.Count, "closing-silences-the-accompaniment");

            // Audiveris: not installed → points to the official page; installed → reads the PDF to MusicXML and imports it.
            Check(AudiverisLocator.Find([Path.Combine(work, "nao-existe.exe")]) is null, "missing-audiveris-is-detected");
            bool prompted = false;
            window.audiverisPath = () => null; window.audiverisMissingPrompt = () => prompted = true;
            Invoke(window, "SheetToScoreClick", window, new RoutedEventArgs()); Flush();
            Check(prompted && window.SheetImportToolTip.Contains("instala à parte") && AudiverisLocator.DownloadUrl.StartsWith("https://github.com/Audiveris/"), "without-audiveris-the-link-is-offered");
            string fakeAudiveris = Path.Combine(work, "Audiveris", "Audiveris.exe"); Directory.CreateDirectory(Path.GetDirectoryName(fakeAudiveris)!); File.WriteAllBytes(fakeAudiveris, [0]);
            Check(AudiverisLocator.Find([Path.Combine(work, "x.exe"), fakeAudiveris]) == fakeAudiveris, "installed-audiveris-is-found");
            window.audiverisPath = () => fakeAudiveris;
            string pdf = Path.Combine(work, "hino.pdf"); File.WriteAllBytes(pdf, "%PDF-1.4"u8.ToArray());
            window.audiverisRunner = (_, input, output2, _) => { File.WriteAllText(Path.Combine(output2, Path.GetFileNameWithoutExtension(input) + ".mxl"), "x"); return Task.CompletedTask; };
            window.musicXmlConverter = (_, mscz, _) => { File.Copy(score, mscz); return Task.CompletedTask; };
            var xml = await window.RecognizeSheetsAsync([pdf]); Flush();
            var scores = await window.ConvertMusicXmlAsync(xml); Flush();
            Check(xml.Count == 1 && xml[0].EndsWith("hino.mxl") && xml[0].Contains("Partituras lidas") && scores.Count == 1 && File.Exists(scores[0]) && window.SheetImportToolTip.Contains("Audiveris instalado"),
                "pdf-is-read-and-converted");

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

    private static byte[] MidiTrack(List<byte> body)
    {
        body.AddRange([0, 0xFF, 0x2F, 0]);
        return [.. "MTrk"u8, (byte)(body.Count >> 24), (byte)(body.Count >> 16), (byte)(body.Count >> 8), (byte)body.Count, .. body];
    }

    /// <summary>Like the engine's transcription: tempo track, melody on track 1 (channel 1), accompaniment on track 2 (channel 2).</summary>
    private static byte[] TranscriptionMidi()
    {
        static byte[] Part(int channel, int basePitch)
        {
            var body = new List<byte> { 0, (byte)(0xC0 | channel), 0 };
            for (int i = 0; i < 16; i++) body.AddRange([0, (byte)(0x90 | channel), (byte)(basePitch + i % 5), 90, 0x83, 0x60, (byte)(0x80 | channel), (byte)(basePitch + i % 5), 0]);
            return MidiTrack(body);
        }
        var tempo = MidiTrack([0, 0xFF, 0x51, 3, 0x09, 0x27, 0xC0]);
        return [.. "MThd"u8, 0, 0, 0, 6, 0, 1, 0, 3, 1, 224, .. tempo, .. Part(0, 72), .. Part(1, 48)];
    }

    /// <summary>Lyrics on the tempo track, 16 syllables on the beats; track 1 plays between the beats, track 2 on them.</summary>
    private static byte[] KaraokeMidi()
    {
        var words = new List<byte>();
        for (int i = 0; i < 16; i++) { words.AddRange(i == 0 ? [0] : [0x83, 0x60]); words.AddRange([0xFF, 0x05, 2, (byte)'l', (byte)'a']); }
        static byte[] Part(int channel, int pitch, bool offBeat)
        {
            var body = new List<byte> { 0, (byte)(0xC0 | channel), 0 };
            if (offBeat) body.AddRange([0x81, 0x70, (byte)(0x90 | channel), (byte)pitch, 0]); // a silent start to shift by half a beat
            for (int i = 0; i < 16; i++) body.AddRange([0, (byte)(0x90 | channel), (byte)(pitch + i % 3), 90, 0x83, 0x60, (byte)(0x80 | channel), (byte)(pitch + i % 3), 0]);
            return MidiTrack(body);
        }
        return [.. "MThd"u8, 0, 0, 0, 6, 0, 1, 0, 3, 1, 224, .. MidiTrack(words), .. Part(0, 60, true), .. Part(1, 67, false)];
    }

    /// <summary>One second of a 440 Hz tone, 44.1 kHz stereo 16-bit PCM.</summary>
    private static void WriteToneWav(string path)
    {
        const int rate = 44100; int frames = rate;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + frames * 4); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)2);
        writer.Write(rate); writer.Write(rate * 4); writer.Write((short)4); writer.Write((short)16); writer.Write("data"u8); writer.Write(frames * 4);
        for (int i = 0; i < frames; i++) { short v = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / rate)); writer.Write(v); writer.Write(v); }
    }
}
