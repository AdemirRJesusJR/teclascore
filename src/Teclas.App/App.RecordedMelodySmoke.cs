using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Melody from a recording for a MIDI without melody (stub aligner, no engine): the offer in the Melodia step, the
    // refusal that writes nothing, the copy as a new card with the recorded track chosen, and the "Revisar" ceiling.
    private async void VerifyRecordedMelodyUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "recorded-melody-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "recorded melody smoke timed out." })); Shutdown(1); };
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

            // 1. A MIDI with only the accompaniment: the Melodia step offers a melody from a recording.
            string midi = Path.Combine(work, "Só acompanhamento.mid"); File.WriteAllBytes(midi, AccompanimentMidi());
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(midi)));
            window.ImportForSmoke(midi); Flush();
            var original = window.Files.Single(r => r.FullPath == Path.GetFullPath(midi));
            original.ScoreTitle = "Canção de teste"; original.ScoreArtist = "Grupo"; original.Collection = "Lote 02";
            library.SelectedItem = original; Flush();
            Check(original.MelodyWarning.Length > 0 && window.RecordedMelodyVisibility == Visibility.Visible && window.RecordedMelodyHint.Contains("O MIDI original não é alterado")
                && window.FindName("RecordedMelodyButton") is Button { Content: "Melodia de uma gravação…" }, "offer-appears-without-melody " + window.RecordedMelodyHint);

            // 2. A recording that does not fit is refused: a visible failure, no new card, the original untouched.
            string recording = Path.Combine(work, "voz.mp3"); File.WriteAllBytes(recording, [0]);
            string? failure = null;
            window.showFailure = (_, message) => failure = message;
            window.recordedMelodyPicker = () => recording;
            window.audioMelodyAligner = (_, _, _, _) => throw new InvalidOperationException("A gravação não combina com este MIDI. Nada foi gravado.");
            Invoke(window, "RecordedMelodyClick", window, new RoutedEventArgs());
            for (int i = 0; i < 100 && (failure is null || !window.IsIdle); i++) { await Task.Delay(30); Flush(); }
            Check(failure is not null && failure.Contains("Nada foi gravado") && failure.Contains("original não foi alterado") && window.Files.Count == 1 && !original.HasOperationError,
                "refusal-writes-nothing " + failure);

            // 3. A fitting recording: the copy enters the library as its own card with the recorded track chosen.
            string? requested = null;
            window.audioMelodyAligner = (source, audio, target, _) =>
            {
                requested = target;
                File.WriteAllBytes(target, WithRecordedTrack(File.ReadAllBytes(source)));
                return Task.FromResult(new JsonObject { ["correlation"] = .62, ["offset_seconds"] = 1.25, ["tempo_factor"] = 1.031, ["transpose_semitones"] = 0,
                    ["method"] = "linear", ["compatible_share"] = .78, ["track"] = 3, ["track_name"] = RecordedMelody.TrackName });
            };
            Invoke(window, "RecordedMelodyClick", window, new RoutedEventArgs());
            for (int i = 0; i < 100 && (window.Files.Count < 2 || !window.IsIdle); i++) { await Task.Delay(30); Flush(); }
            var copy = window.Files.SingleOrDefault(r => r != original);
            Check(copy is not null && requested is not null && copy.FullPath == Path.GetFullPath(requested)
                && Path.GetDirectoryName(copy.FullPath)!.EndsWith(RecordedMelody.Folder, StringComparison.Ordinal)
                && Path.GetFileName(copy.FullPath).Contains("(melodia de gravação)"), "copy-is-a-new-card " + requested + " " + window.Status);
            var recorded = copy!.Tracks.SingleOrDefault(t => t.Source.Name == RecordedMelody.TrackName);
            Check(recorded is not null && recorded.IsMelody && copy.Tracks.Count(t => t.IsMelody) == 1 && recorded.Instrument == RecordedMelody.TrackName && copy.MelodyFromRecording,
                "recorded-track-is-the-melody");
            Check(copy.ScoreTitle == "Canção de teste" && copy.ScoreArtist == "Grupo" && copy.Collection == "Lote 02", "copy-keeps-the-song-properties");
            Check(library.SelectedItem == copy && window.RecordedMelodyVisibility == Visibility.Collapsed && window.Status.Contains("o MIDI original não foi alterado")
                && window.Status.Contains("62%"), "copy-is-opened-with-a-summary " + window.Status);
            Check(window.Files.Contains(original) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(midi))) == hash && !original.MelodyFromRecording, "original-is-untouched");

            // 4. A score from the recorded melody is "Revisar" at best and the guided review names it.
            string score = Path.Combine(work, "gerada.mscz"); File.WriteAllBytes(score, [0]);
            copy.ScorePath = score; copy.PageCount = 2; copy.HasPendingScoreProperties = false;
            var rating = window.RateScore(copy);
            Check(rating is { Label: "Revisar" } && rating.Score <= ScoreConfidence.RecordedMelodyCeiling && rating.Reasons.Any(r => r.Contains("gravação")), "rating-is-at-most-revisar " + rating?.Score);
            Check(new ScoreReviewPoint { Kind = "melody_from_recording" }.KindLabel == "Melodia de gravação", "guided-review-label");

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

    /// <summary>Tempo track, block chords (track 1) and a bass (track 2): no track looks like a melody.</summary>
    private static byte[] AccompanimentMidi()
    {
        var chords = new List<byte> { 0, 0xC0, 0 }; var bass = new List<byte> { 0, 0xC1, 33 };
        int[][] progression = [[48, 52, 55], [53, 57, 60], [55, 59, 62], [48, 52, 55]];
        for (int bar = 0; bar < 8; bar++)
        {
            var chord = progression[bar % 4];
            foreach (var pitch in chord) chords.AddRange([0, 0x90, (byte)pitch, 80]);
            chords.AddRange([0x8F, 0x00]); chords.AddRange([0x80, (byte)chord[0], 0]); chords.AddRange([0, 0x80, (byte)chord[1], 0, 0, 0x80, (byte)chord[2], 0]);
            bass.AddRange([0, 0x91, (byte)(chord[0] - 12), 90, 0x8F, 0x00, 0x81, (byte)(chord[0] - 12), 0]);
        }
        var tempo = MidiTrack([0, 0xFF, 0x51, 3, 0x07, 0xA1, 0x20]);
        return [.. "MThd"u8, 0, 0, 0, 6, 0, 1, 0, 3, 1, 224, .. tempo, .. MidiTrack(chords), .. MidiTrack(bass)];
    }

    /// <summary>Like the engine's copy: the original tracks plus one named "Melodia (gravação)" on channel 4.</summary>
    private static byte[] WithRecordedTrack(byte[] source)
    {
        var name = Encoding.UTF8.GetBytes(RecordedMelody.TrackName);
        var body = new List<byte> { 0, 0xFF, 0x03, (byte)name.Length }; body.AddRange(name); body.AddRange([0, 0xC3, 73]);
        for (int i = 0; i < 16; i++) body.AddRange([0, 0x93, (byte)(72 + i % 5), 90, 0x83, 0x60, 0x83, (byte)(72 + i % 5), 0]);
        var result = source.ToArray();
        int tracks = (result[10] << 8 | result[11]) + 1; result[10] = (byte)(tracks >> 8); result[11] = (byte)tracks;
        return [.. result, .. MidiTrack(body)];
    }
}
