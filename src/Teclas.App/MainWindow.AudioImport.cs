using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Audio to score: a recording is decoded by Windows, transcribed by the engine (Basic Pitch) into a MIDI with melody
/// and accompaniment tracks, saved in the output folder and added to the library with the melody already chosen.
/// </summary>
public partial class MainWindow
{
    /// <summary>Test hook: replaces decoding + engine (audio path, MIDI target) → report.</summary>
    internal Func<string, string, CancellationToken, Task<JsonObject>>? audioTranscriber = null;
    internal Func<string[]?>? audioFilesPicker = null;
    /// <summary>Shows a failure that must not go unnoticed (the status line alone is easy to miss).</summary>
    internal Action<string, string> showFailure = (title, message) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>
    /// Transcription needs numpy and onnxruntime, which come with the TeclaScore's own Python. A Python chosen in
    /// Settings for MuseScore work may not have them, so the private one is used whenever it is installed.
    /// </summary>
    internal string TranscriptionPython
    {
        get
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "python.exe");
            return File.Exists(bundled) ? bundled : PythonPath;
        }
    }

    private async void AudioToScoreClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        string[]? files = audioFilesPicker is { } pick ? pick() : null;
        if (audioFilesPicker is null)
        {
            var dialog = new OpenFileDialog { Title = "Escolha gravações para transcrever", Multiselect = true,
                Filter = "Áudio (MP3, WAV, M4A, WMA, FLAC)|" + string.Join(";", AudioDecoder.Extensions.Select(x => "*" + x)) };
            files = dialog.ShowDialog(this) == true ? dialog.FileNames : null;
        }
        if (files is { Length: > 0 }) await TranscribeAudioAsync(files);
    }

    /// <summary>Transcribes each recording and imports the MIDIs; returns the MIDI paths created.</summary>
    internal async Task<IReadOnlyList<string>> TranscribeAudioAsync(IReadOnlyList<string> files)
    {
        var created = new List<string>(); var reports = new List<string>(); var unclear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await RunOperation(async () =>
        {
            if (audioTranscriber is null) ValidateSettings(false);
            var token = cancellation!.Token;
            string folder = Path.Combine(await PrepareOutputDirectoryAsync(token), "Transcrições de áudio");
            Directory.CreateDirectory(folder);
            var taken = new HashSet<string>(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                SetOperationItems(i, files.Count, name); Status = $"Transcrevendo {name}… (leva alguns segundos por minuto de áudio)";
                string midi = Path.Combine(folder, ScoreFileName.Unique(ScoreFileName.Stem(null, name), ".mid", taken));
                JsonObject report;
                if (audioTranscriber is { } transcribe) report = await transcribe(files[i], midi, token);
                else
                {
                    string job = Path.Combine(DataFolder, "jobs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
                    try
                    {
                        string wav = Path.Combine(job, "audio.wav");
                        await Task.Run(() => AudioDecoder.ToWav(files[i], wav, token), token);
                        report = await new EngineClient(TranscriptionPython, engineDirectory).RunAsync(
                            new JsonObject { ["mode"] = "transcribeAudio", ["workspace"] = job, ["inputMidi"] = "", ["inputAudio"] = wav, ["outputMidi"] = midi },
                            job, CreateOperationProgress(), token);
                    }
                    finally { try { Directory.Delete(job, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                }
                created.Add(midi);
                var chords = report["chords"] is JsonArray heard ? heard.Select(c => c?.GetValue<string>()).OfType<string>().ToList() : [];
                bool clear = report["melody_clear"]?.GetValue<bool>() ?? true;
                if (!clear) unclear.Add(midi);
                string harmony = chords.Count == 0 ? "" : $", acordes {string.Join(" ", chords.Take(8))}{(chords.Count > 8 ? " …" : "")}";
                reports.Add($"{name}: {report["melody_notes"]?.GetValue<int>() ?? 0} notas na melodia{(clear ? "" : " (pouco clara: só cifras)")}, ♩ ≈ {report["bpm"]?.GetValue<double>() ?? 0:0}{harmony}");
            }
        }, operationTitle: "Transcrevendo áudio");
        if (created.Count == 0)
        {
            showFailure("Áudio para partitura", "Não foi possível transcrever o áudio.\n\n" + Status);
            return created;
        }
        await ImportAndOpenLibraryAsync(created);
        foreach (var row in Files.Where(row => created.Contains(row.FullPath, StringComparer.OrdinalIgnoreCase)))
        {
            // The transcription writes the melody on track 1 and the accompaniment on track 2.
            foreach (var track in row.Tracks) track.IsMelody = track.Source.Key.Track == 1;
            row.Collection = row.Collection.Length > 0 ? row.Collection : "Transcrições de áudio";
            // A melody that did not come out clearly still gives the chords heard in the sound: a chords-only score.
            if (unclear.Contains(row.FullPath)) row.ChordsOnly = true;
        }
        RefreshSelection(); PersistPreferences();
        Status = "Transcrição pronta (" + string.Join("; ", reports) + "). Ouça a melodia na aba Melodia, ajuste se preciso e gere a partitura.";
        return created;
    }
}
