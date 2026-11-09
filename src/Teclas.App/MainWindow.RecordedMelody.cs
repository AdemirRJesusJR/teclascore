using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Names shared with the engine (engine/audio_melody_align.py).</summary>
internal static class RecordedMelody
{
    /// <summary>The track the alignment adds to the copy of the MIDI.</summary>
    public const string TrackName = "Melodia (gravação)";
    /// <summary>Subfolder of the output folder that keeps the copies and their alignment reports.</summary>
    public const string Folder = "Melodias de gravações";
}

/// <summary>
/// Melody from a recording for a MIDI that lacks it: the recording (ideally the voice alone) is transcribed and aligned
/// with the MIDI by the engine, which writes a new copy of the MIDI with an extra "Melodia (gravação)" track. The copy
/// enters the library as a card of its own, next to the original card (which stays as it was), with that track chosen as
/// the melody; its scores are rated "Revisar" at best and the guided review asks for a check by ear.
/// </summary>
public partial class MainWindow
{
    /// <summary>Test hook: replaces the audio file dialog.</summary>
    internal Func<string?>? recordedMelodyPicker = null;
    /// <summary>Test hook: replaces decoding + engine (original MIDI, recording, copy to write) → alignment report.</summary>
    internal Func<string, string, string, CancellationToken, Task<JsonObject>>? audioMelodyAligner = null;

    /// <summary>A MIDI card without a complete melody (batch diagnosis or no track that looks like one), not itself a copy with a recorded melody.</summary>
    internal static bool OffersRecordedMelody(MidiRow row) =>
        row.Document is not null && !row.UsesNativeScoreWorkflow && !row.HasRecordedMelodyTrack && (row.CatalogLacksMelody || row.MelodyWarning.Length > 0);

    public string RecordedMelodyHint
    {
        get
        {
            if (selectedFile is not { } row || !OffersRecordedMelody(row)) return "";
            string why = row.CatalogLacksMelody ? "O diagnóstico indica que este MIDI não traz a melodia completa." : "Nenhuma pista deste MIDI parece ser a melodia completa.";
            return why + " Com uma gravação da mesma música (de preferência só a voz; a mixagem completa também serve), o TeclaScore transcreve a melodia " +
                "e a alinha a este MIDI numa cópia nova, que entra na biblioteca. O MIDI original não é alterado.";
        }
    }

    public Visibility RecordedMelodyVisibility => RecordedMelodyHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void RecordedMelodyClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits() || selectedFile is not { } row || !OffersRecordedMelody(row)) return;
        string? audio = recordedMelodyPicker is { } pick ? pick() : null;
        if (recordedMelodyPicker is null)
        {
            var dialog = new OpenFileDialog { Title = "Escolha uma gravação da mesma música (de preferência só a voz)",
                Filter = "Áudio (MP3, WAV, M4A, WMA, FLAC)|" + string.Join(";", AudioDecoder.Extensions.Select(x => "*" + x)) };
            audio = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (audio is not null) await MelodyFromRecordingAsync(row, audio);
    }

    /// <summary>Aligns the recording with the card's MIDI and adds the copy to the library; returns its card, or null when refused.</summary>
    internal async Task<MidiRow?> MelodyFromRecordingAsync(MidiRow source, string audio)
    {
        string? created = null; JsonObject? report = null;
        await RunOperation(async () =>
        {
            if (audioMelodyAligner is null) ValidateSettings(false);
            var token = cancellation!.Token;
            string folder = Path.Combine(await PrepareOutputDirectoryAsync(token), RecordedMelody.Folder);
            Directory.CreateDirectory(folder);
            var taken = new HashSet<string>(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
            string name = Path.GetFileNameWithoutExtension(source.FullPath);
            string midi = Path.Combine(folder, ScoreFileName.Unique(ScoreFileName.Stem(null, name + " (melodia de gravação)"), ".mid", taken));
            SetOperationItems(0, 1, Path.GetFileName(audio));
            Status = $"Transcrevendo {Path.GetFileName(audio)} e alinhando a melodia a {source.Name}… (leva alguns segundos por minuto de áudio)";
            if (audioMelodyAligner is { } align) report = await align(source.FullPath, audio, midi, token);
            else
            {
                string job = Path.Combine(DataFolder, "jobs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
                try
                {
                    string wav = Path.Combine(job, "audio.wav");
                    await Task.Run(() => AudioDecoder.ToWav(audio, wav, token), token);
                    report = await new EngineClient(TranscriptionPython, engineDirectory).RunAsync(new JsonObject
                    {
                        ["mode"] = "alignAudioMelody", ["workspace"] = job, ["inputMidi"] = source.FullPath, ["inputAudio"] = wav,
                        ["audioSource"] = Path.GetFullPath(audio), ["outputMidi"] = midi, ["outputReport"] = Path.ChangeExtension(midi, ".alinhamento.json")
                    }, job, CreateOperationProgress(), token);
                }
                finally { try { Directory.Delete(job, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            if (!File.Exists(midi)) throw new InvalidOperationException("O motor não gravou a cópia do MIDI com a melodia.");
            created = midi;
        }, operationTitle: "Melodia de uma gravação");
        if (created is null || report is null)
        {
            showFailure("Melodia de uma gravação", "Não foi possível usar esta gravação. O MIDI original não foi alterado.\n\n" + Status);
            return null;
        }
        await AddCreatedFilesAsync([created]);
        var row = Files.FirstOrDefault(r => string.Equals(r.FullPath, Path.GetFullPath(created), StringComparison.OrdinalIgnoreCase));
        if (row is null) return null;
        foreach (var track in row.Tracks) track.IsMelody = track.Source.Name == RecordedMelody.TrackName;
        row.ChordsOnly = false;
        // The copy is the same song: it keeps the original card's properties.
        row.ScoreTitle = source.ScoreTitle; row.ScoreArtist = source.ScoreArtist; row.ScoreComposer = source.ScoreComposer; row.ScoreSubtitle = source.ScoreSubtitle;
        row.Collection = source.Collection; row.Genre = source.Genre; row.Tags = source.Tags;
        row.DiagramInversion = source.DiagramInversion; row.TransposeSemitones = source.TransposeSemitones;
        row.Status = "Melodia de gravação · ouça e confira antes de aprovar";
        RefreshSelection(); PersistPreferences();
        Status = RecordedMelodySummary(report, Path.GetFileName(created));
        return row;
    }

    private static string RecordedMelodySummary(JsonObject report, string copy)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        double Number(string name) => report[name] is JsonValue value && value.TryGetValue(out double number) ? number : 0;
        int shift = (int)Number("transpose_semitones");
        string transposition = shift == 0 ? "" : $", transposta {(shift > 0 ? "+" : "")}{shift} semitons para o tom do MIDI";
        string method = report["method"]?.GetValue<string>() == "por trechos" ? ", andamento ajustado por trechos" : "";
        return string.Format(culture, "Melodia alinhada: harmonia {0:P0} parecida, início em {1:0.00} s, andamento ×{2:0.000}{3}{4}; {5:P0} das notas caem sobre os acordes. ",
                   Number("correlation"), Number("offset_seconds"), Number("tempo_factor"), method, transposition, Number("compatible_share")) +
               $"A cópia {copy} entrou na biblioteca com a pista \"{RecordedMelody.TrackName}\" marcada; o MIDI original não foi alterado. " +
               "Ouça a melodia junto com a gravação e corrija o que precisar antes de aprovar.";
    }
}
