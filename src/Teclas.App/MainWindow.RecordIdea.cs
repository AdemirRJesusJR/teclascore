using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Record an idea: what was played on the MIDI keyboard goes to the engine, which finds the tempo from the player's
/// pace, the meter and the note values, and writes a MIDI (melody, accompaniment, chords) that enters the library.
/// </summary>
public partial class MainWindow
{
    internal Func<RecordIdeaWindow, bool?> recordIdeaDialog = window => window.ShowDialog();
    /// <summary>Test hook: replaces the engine (notes, MIDI target) → report.</summary>
    internal Func<IReadOnlyList<RecordedNote>, string, CancellationToken, Task<JsonObject>>? ideaTranscriber = null;

    private async void RecordIdeaClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var window = new RecordIdeaWindow { Owner = this };
        if (recordIdeaDialog(window) == true) await TranscribeIdeaAsync(window.Notes);
    }

    /// <summary>Turns the recorded notes into a library song; returns the MIDI path, or null when it failed.</summary>
    internal async Task<string?> TranscribeIdeaAsync(IReadOnlyList<RecordedNote> notes)
    {
        string? midi = null; JsonObject? report = null;
        await RunOperation(async () =>
        {
            if (ideaTranscriber is null) ValidateSettings(false);
            var token = cancellation!.Token;
            string folder = Path.Combine(await PrepareOutputDirectoryAsync(token), "Ideias gravadas");
            Directory.CreateDirectory(folder);
            var taken = new HashSet<string>(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
            string target = Path.Combine(folder, ScoreFileName.Unique("Ideia " + DateTime.Now.ToString("yyyy-MM-dd HH'h'mm"), ".mid", taken));
            Status = "Descobrindo o andamento, o compasso e as figuras pelo seu jeito de tocar…";
            if (ideaTranscriber is { } transcribe) report = await transcribe(notes, target, token);
            else
            {
                string job = Path.Combine(DataFolder, "jobs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
                try
                {
                    string input = Path.Combine(job, "notas.json");
                    await File.WriteAllTextAsync(input, JsonSerializer.Serialize(notes.Select(n => new { start = n.Start, end = n.End, pitch = n.Pitch, velocity = n.Velocity })), token);
                    report = await new EngineClient(PythonPath, engineDirectory).RunAsync(
                        new JsonObject { ["mode"] = "transcribeNotes", ["workspace"] = job, ["inputMidi"] = "", ["inputNotes"] = input, ["outputMidi"] = target },
                        job, CreateOperationProgress(), token);
                }
                finally { try { Directory.Delete(job, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            midi = target;
        }, operationTitle: "Transformando a ideia em partitura");
        if (midi is null || report is null) return null;
        await ImportAndOpenLibraryAsync([midi]);
        bool clear = report["melody_clear"]?.GetValue<bool>() ?? true;
        foreach (var row in Files.Where(r => string.Equals(r.FullPath, midi, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var track in row.Tracks) track.IsMelody = track.Source.Key.Track == 1;
            row.Collection = "Ideias gravadas";
            if (!clear) row.ChordsOnly = true;
        }
        RefreshSelection(); PersistPreferences();
        var chords = report["chords"] is JsonArray heard ? heard.Select(c => c?.GetValue<string>()).OfType<string>().ToList() : [];
        int meter = report["meter"]?.GetValue<int>() ?? 4;
        Status = $"Ideia transformada: ♩ ≈ {report["bpm"]?.GetValue<double>() ?? 0:0} (pelo seu ritmo), compasso {meter}/4, {report["melody_notes"]?.GetValue<int>() ?? 0} notas na melodia" +
                 (chords.Count > 0 ? $", acordes {string.Join(" ", chords.Take(8))}{(chords.Count > 8 ? " …" : "")}" : "") +
                 (clear ? "" : " (sem melodia clara: só cifras)") + RhythmNotes(report) + ". Confira e gere a partitura.";
        return midi;
    }

    /// <summary>What the rhythm analysis found beyond the plain grid: triplets, a swung feel and a pickup.</summary>
    internal static string RhythmNotes(JsonObject report)
    {
        var notes = new List<string>();
        if (report["triplet_beats"]?.GetValue<int>() is > 0 and var triplets) notes.Add(triplets == 1 ? "1 tempo em tercina" : $"{triplets} tempos em tercinas");
        if (report["swing"]?.GetValue<bool>() == true) notes.Add("balanço (swing) escrito em colcheias retas");
        if (report["pickup_beats"]?.GetValue<int>() is > 0 and var pickup) notes.Add(pickup == 1 ? "anacruse de 1 tempo" : $"anacruse de {pickup} tempos");
        return notes.Count == 0 ? "" : "; " + string.Join(", ", notes);
    }
}
