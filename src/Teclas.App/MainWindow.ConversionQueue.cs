using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private ConversionQueueService? conversionQueue;
    public bool CanEnqueueConversion => CanGenerate && selectedFile is { UsesNativeScoreWorkflow: false };
    private ConversionQueueService GetConversionQueue()
    {
        if (!persistenceEnabled) throw new InvalidOperationException("A fila precisa de uma sessão salva.");
        // Simultaneous conversions: Configurações › Conversões simultâneas (automatic by processor threads and free memory).
        conversionQueue ??= new ConversionQueueService(Path.Combine(
            persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!, "conversion-queue"), conversionQueueWorker);
        if (!conversionQueue.IsRunning) conversionQueue.MaxParallel = ConversionParallelism.Resolve(settings.ConversionParallelism);
        return conversionQueue;
    }

    private bool HasCurrentMelodyAnalysis(MidiRow row)
    {
        if (!File.Exists(row.AnalysisPath) || !Directory.Exists(row.Workspace)) return false;
        try
        {
            var data = JsonNode.Parse(File.ReadAllText(row.AnalysisPath!))!;
            if (!string.Equals(data["sha256"]?.GetValue<string>(), row.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(data["source"]?.GetValue<string>(), row.FullPath, StringComparison.OrdinalIgnoreCase) ||
                data["reviewedProfile"]?.GetValue<bool>() != row.UseReviewedProfile) return false;
            if (row.UseReviewedProfile) return true;
            var config = data["config"];
            if ((config?["chords_only"]?.GetValue<bool>() ?? false) != row.ChordsOnly) return false;
            return JsonNode.DeepEquals(config?["melody_sources"], Sources(row)) &&
                JsonNode.DeepEquals(config?["melody_sections"] ?? new JsonArray(), MelodySections(row)) &&
                (GetOrderedMelodySources(row).Count < 2 || config?["merge_mode"]?.GetValue<string>() == "priority");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }

    private JsonObject QueueGenerationOptions(MidiRow row)
    {
        var options = new JsonObject
        {
            ["scoreTitle"] = row.ScoreTitle, ["scoreSubtitle"] = row.ScoreSubtitle,
            ["scoreComposer"] = row.ScoreComposer, ["scoreArtist"] = row.ScoreArtist,
            ["diagramInversion"] = row.EffectiveDiagramInversion, ["transposeSemitones"] = row.TransposeSemitones
        };
        if (row.Chords.Count > 0)
        {
            options["keyFifths"] = row.KeyFifths; options["keyMinor"] = row.KeyMinor;
            options["chords"] = new JsonArray(row.Chords.Select(chord => (JsonNode)WithBass(new JsonObject
            {
                ["quarter"] = chord.Quarter, ["root"] = chord.Root, ["quality"] = chord.Quality,
                ["diagramInversion"] = chord.DiagramInversion
            }, chord)).ToArray());
            if (row.SimplifiedChords) options["chords"] = ChordSimplifier.SimplifyAll((JsonArray)options["chords"]!);
        }
        else if (chordPreferences.Count > 0)
            options["chordPreferences"] = new JsonArray(chordPreferences.Select(item => (JsonNode)new JsonObject
            {
                ["pitchClass"] = item.Key.PitchClass, ["quality"] = item.Key.Quality, ["inversion"] = item.Value
            }).ToArray());
        if (row.SimplifiedChords && row.Chords.Count == 0) options["simplifyChords"] = true;
        if (row.FoldRepeats) options["foldRepeats"] = true;
        if (!row.IncludeLyrics) options["includeLyrics"] = false;
        else if (row.LyricsMode != "auto") options["lyricsMode"] = row.LyricsMode;
        if (row.ChordStaff && !row.ChordsOnly) options["chordStaff"] = true;
        if (settings.NoteNames == "solfejo") options["noteNames"] = "solfejo";
        AddPresentation(options);
        AddNotation(options);
        if (SecondVoiceSources(row) is { Count: > 0 } secondVoice && !row.ChordsOnly) options["secondVoiceSources"] = secondVoice;
        if (ScoreStyleRequest() is { } scoreStyle) options["scoreStyle"] = scoreStyle;
        return options;
    }

    private JsonObject AnalyzeOptions(MidiRow row)
    {
        var options = new JsonObject { ["useReviewedProfile"] = row.UseReviewedProfile, ["melodySources"] = Sources(row), ["melodySections"] = MelodySections(row) };
        if (row.ChordsOnly && !row.UseReviewedProfile) { options["chordsOnly"] = true; options["melodySections"] = new JsonArray(); }
        return options;
    }

    private JsonObject QueueRevision(MidiRow row) => new()
    {
        ["sourceSha256"] = row.SourceSha256, ["useReviewedProfile"] = row.UseReviewedProfile,
        ["melodySources"] = Sources(row), ["melodySections"] = MelodySections(row), ["generation"] = QueueGenerationOptions(row),
        ["scorePath"] = row.ScorePath, ["hasMusicalEdits"] = row.HasMusicalEdits,
        ["defaultInversion"] = row.DefaultDiagramInversion, ["inversion"] = row.DiagramInversion
    };

    /// <summary>
    /// Freezes the card as a queue recipe. The library snapshot and revision travel as the job's attachment (outside
    /// queue.json and the engine request), so saving the queue stays small however many songs wait in it.
    /// </summary>
    private ConversionQueueRecipe BuildQueueRecipe(MidiRow row, string outputDirectory)
    {
        RequireMidiWorkflow(row);
        if (row.Document is null || (!row.UseReviewedProfile && !row.HasMelodyChoice))
            throw new InvalidOperationException("Escolha as pistas de melodia antes de adicionar à fila.");
        var snapshot = CaptureMidi(row);
        // History is kept in the library; each queue recipe only needs the selected version.
        var snapshotJson = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
        snapshotJson.Remove(nameof(SavedMidi.Versions));
        return new ConversionQueueRecipe(row.FullPath, PythonPath, engineDirectory, MuseScorePath, outputDirectory,
            AnalyzeOptions(row), QueueGenerationOptions(row),
            HasCurrentMelodyAnalysis(row) && !row.ReviewPoints.Any(point => point.Blocking) ? row.AnalysisPath : null)
        {
            SourceSha256 = row.SourceSha256, DisplayName = row.ScoreTitle,
            Attachment = new JsonObject { ["librarySnapshot"] = snapshotJson, ["libraryRevision"] = QueueRevision(row) }
        };
    }

    private async Task EnqueueConversion(MidiRow row, CancellationToken token)
    {
        var failures = new Dictionary<MidiRow, string>();
        if ((await EnqueueConversionsAsync([row], token, failures)).Count == 0)
            throw new InvalidOperationException(failures.GetValueOrDefault(row) ?? "Não foi possível adicionar à fila.");
    }

    /// <summary>Adds the song to the queue without opening it; a running background queue takes it too.</summary>
    private async void EnqueueConversionClick(object sender, RoutedEventArgs e)
    {
        if (!CanEnqueueNow || selectedFile is not { } row || !CommitEdits()) return;
        bool added = false;
        await RunOperation(async () =>
        {
            await EnqueueConversion(row, cancellation!.Token); added = true;
        }, operationTitle: "Adicionando à fila", operationItem: row.Name);
        if (!added) return;
        int pending = conversionQueue?.Summaries.Count(item => item.Status == ConversionQueueStatus.Pending) ?? 1;
        if (IsBackgroundQueueRunning) { StartBackgroundQueue(); Status = $"{row.Name} entrou na fila em segundo plano."; }
        else Status = $"{row.Name} adicionada à fila ({pending} pendente(s)). Inicie em Gerar › Fila de conversões, ou use Gerar e próxima para gerar em segundo plano.";
        PersistPreferences();
    }

    /// <summary>Puts the rows in the queue with one save and starts the background queue (Lote, Melodias em lote, músicas marcadas).</summary>
    private async Task<int> EnqueueRowsAsync(IReadOnlyList<MidiRow> rows, CancellationToken token, List<string> failed)
    {
        var failures = new Dictionary<MidiRow, string>();
        var added = await EnqueueConversionsAsync(rows, token, failures);
        foreach (var (row, error) in failures) failed.Add((string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle) + ": " + error);
        return added.Count;
    }

    private void ConversionQueueClick(object sender, RoutedEventArgs e)
    {
        if (IsIdle && CommitEdits()) ShowConversionQueue();
    }

    private void ShowConversionQueue()
    {
        try
        {
            StopTrackAudition(); ResetPlayback();
            var service = GetConversionQueue();
            ReconcileQueueResults();
            var queue = new ConversionQueueWindow(service, ReconcileQueueResults) { Owner = this };
            queue.ShowDialog();
            ReconcileQueueResults(); RefreshSelection(); PersistPreferences();
            // Opening a song from the summary selects its card in the library.
            if (queue.OpenSource is { } source && Files.FirstOrDefault(row => string.Equals(row.FullPath, source, StringComparison.OrdinalIgnoreCase)) is { } card)
            { SetCurrentPage("library"); Library.SelectedItem = card; Library.ScrollIntoView(card); }
        }
        catch (Exception error) { Status = "Não foi possível abrir a fila: " + error.Message; }
    }

    private void ReconcileQueueResults()
    {
        if (conversionQueue is null) return;
        bool changed = false;
        // The light summaries say what finished; the full item and its attachment are read only for new results.
        foreach (var summary in conversionQueue.Summaries.Where(item => item.Status == ConversionQueueStatus.Completed && item.ScorePath is not null && !reconciledQueueItems.Contains(item.Id)))
        {
            var row = Files.FirstOrDefault(row => string.Equals(row.FullPath, summary.OriginalSourcePath, StringComparison.OrdinalIgnoreCase));
            if (row is null) continue;
            if (row.Versions.Any(version => string.Equals(version.ScorePath, summary.ScorePath, StringComparison.OrdinalIgnoreCase))) { reconciledQueueItems.Add(summary.Id); continue; }
            if (conversionQueue.FindItem(summary.Id) is not { Result: { } result } item) continue;
            try
            {
                if (!File.Exists(result.ScorePath)) continue;
                var attachment = conversionQueue.ReadAttachment(item.Id);
                var snapshot = (attachment?["librarySnapshot"] ?? item.Recipe.GenerateOptions["librarySnapshot"])?.Deserialize<SavedMidi>();
                var revision = attachment?["libraryRevision"] ?? item.Recipe.GenerateOptions["libraryRevision"];
                if (snapshot is null) continue;
                var analysis = File.Exists(result.AnalysisPath) ? JsonNode.Parse(File.ReadAllText(result.AnalysisPath!)) : null;
                var chords = snapshot.Chords;
                if (chords.Count == 0 && analysis?["chords"] is JsonArray analyzedChords)
                {
                    // The queue applied the remembered positions when generating; keep the card in step with the score.
                    var withPositions = (JsonArray)analyzedChords.DeepClone();
                    if (item.Recipe.GenerateOptions["chordPreferences"] is JsonArray preferences)
                        DiagramPositionPropagation.ApplyPreferences(withPositions, preferences, snapshot.TransposeSemitones);
                    chords = withPositions.Deserialize<List<SavedChord>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                }
                var version = new SavedScoreVersion
                {
                    SourceSha256 = item.SourceSha256, UseReviewedProfile = snapshot.UseReviewedProfile,
                    MelodySources = snapshot.MelodySources, MelodySourcePriority = snapshot.MelodySourcePriority,
                    MelodySections = Settings.CloneMelodySections(snapshot.MelodySections),
                    ScorePath = result.ScorePath, PreviewPath = result.PreviewPath, PageCount = result.Pages,
                    CreatedAt = item.UpdatedAt, Title = snapshot.ScoreTitle ?? "", Subtitle = snapshot.ScoreSubtitle,
                    Composer = snapshot.ScoreComposer ?? "", Artist = snapshot.ScoreArtist ?? "",
                    MetadataOrganizationNotice = snapshot.MetadataOrganizationNotice,
                    DefaultInversion = snapshot.DefaultDiagramInversion, Inversion = snapshot.DiagramInversion,
                    TransposeSemitones = snapshot.TransposeSemitones,
                    KeyFifths = item.Recipe.GenerateOptions["keyFifths"]?.GetValue<int>() ?? analysis?["keyFifths"]?.GetValue<int>() ?? snapshot.KeyFifths,
                    KeyMinor = item.Recipe.GenerateOptions["keyMinor"]?.GetValue<bool>() ?? analysis?["keyMinor"]?.GetValue<bool>() ?? snapshot.KeyMinor,
                    Chords = chords,
                    // Rebased snapshot analysis cannot be used directly with the original MIDI.
                    // A future generation reanalyzes while preserving these reviewed chord choices.
                    AnalysisPath = result.AnalysisPath, Workspace = item.Workspace
                };
                bool activate = !row.UsesNativeScoreWorkflow && string.Equals(row.SourceSha256, item.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
                    JsonNode.DeepEquals(QueueRevision(row), revision);
                RememberVersion(row);
                row.Versions.Add(version); changed = true; reconciledQueueItems.Add(item.Id);
                if (activate)
                {
                    RestoreVersion(row, version);
                    row.Status = $"Gerada pela fila · {result.Pages} páginas"; row.Approved = false;
                    // Keep review information visible, without making a stale analysis reusable.
                    if (analysis is not null) ParseReviewPoints(row, JsonSerializer.SerializeToElement(analysis));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { Status = "A conversão foi salva. Não foi possível atualizar a biblioteca: " + error.Message; }
        }
        if (changed) { RefreshSelection(); PersistPreferences(); }
    }
}
