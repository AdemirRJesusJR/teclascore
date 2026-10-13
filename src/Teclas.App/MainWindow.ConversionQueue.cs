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
        return conversionQueue ??= new ConversionQueueService(Path.Combine(
            persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!, "conversion-queue"));
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
            return JsonNode.DeepEquals(config?["melody_sources"], Sources(row)) &&
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
            options["chords"] = new JsonArray(row.Chords.Select(chord => (JsonNode)new JsonObject
            {
                ["quarter"] = chord.Quarter, ["root"] = chord.Root, ["quality"] = chord.Quality,
                ["diagramInversion"] = chord.DiagramInversion
            }).ToArray());
        }
        return options;
    }

    private JsonObject QueueRevision(MidiRow row) => new()
    {
        ["sourceSha256"] = row.SourceSha256, ["useReviewedProfile"] = row.UseReviewedProfile,
        ["melodySources"] = Sources(row), ["generation"] = QueueGenerationOptions(row),
        ["scorePath"] = row.ScorePath, ["hasMusicalEdits"] = row.HasMusicalEdits,
        ["defaultInversion"] = row.DefaultDiagramInversion, ["inversion"] = row.DiagramInversion
    };

    private async Task EnqueueConversion(MidiRow row, CancellationToken token)
    {
        RequireMidiWorkflow(row); ValidateSettings(true);
        if (row.Document is null || (!row.UseReviewedProfile && !row.Tracks.Any(track => track.IsMelody)))
            throw new InvalidOperationException("Escolha as pistas de melodia antes de adicionar à fila.");
        var options = QueueGenerationOptions(row);
        var snapshot = CaptureMidi(row);
        // History is kept in the library; each queue recipe only needs the selected version.
        var snapshotJson = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
        snapshotJson.Remove(nameof(SavedMidi.Versions));
        options["librarySnapshot"] = snapshotJson;
        options["libraryRevision"] = QueueRevision(row);
        var recipe = new ConversionQueueRecipe(row.FullPath, PythonPath, engineDirectory, MuseScorePath, OutputDirectory,
            new JsonObject { ["useReviewedProfile"] = row.UseReviewedProfile, ["melodySources"] = Sources(row) }, options,
            HasCurrentMelodyAnalysis(row) && !row.ReviewPoints.Any(point => point.Blocking) ? row.AnalysisPath : null)
        { SourceSha256 = row.SourceSha256, DisplayName = row.ScoreTitle };
        await GetConversionQueue().EnqueueAsync(recipe, token);
    }

    private async void EnqueueConversionClick(object sender, RoutedEventArgs e)
    {
        if (!CanEnqueueConversion || selectedFile is not { } row || !CommitEdits()) return;
        bool added = false;
        await RunOperation(async () =>
        {
            await EnqueueConversion(row, cancellation!.Token); added = true;
            Status = "Partitura adicionada à fila com as opções atuais.";
        }, operationTitle: "Adicionando à fila", operationItem: row.Name);
        if (added) ShowConversionQueue();
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
            new ConversionQueueWindow(service, ReconcileQueueResults) { Owner = this }.ShowDialog();
            ReconcileQueueResults(); RefreshSelection(); PersistPreferences();
        }
        catch (Exception error) { Status = "Não foi possível abrir a fila: " + error.Message; }
    }

    private void ReconcileQueueResults()
    {
        if (conversionQueue is null) return;
        bool changed = false;
        foreach (var item in conversionQueue.Items.Where(item => item.Status == ConversionQueueStatus.Completed && item.Result is not null))
        {
            var row = Files.FirstOrDefault(row => string.Equals(row.FullPath, item.OriginalSourcePath, StringComparison.OrdinalIgnoreCase));
            var result = item.Result!;
            if (row is null || row.Versions.Any(version => string.Equals(version.ScorePath, result.ScorePath, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                if (!File.Exists(result.ScorePath)) continue;
                var snapshot = item.Recipe.GenerateOptions["librarySnapshot"]?.Deserialize<SavedMidi>();
                if (snapshot is null) continue;
                var analysis = File.Exists(result.AnalysisPath) ? JsonNode.Parse(File.ReadAllText(result.AnalysisPath!)) : null;
                var chords = snapshot.Chords;
                if (chords.Count == 0 && analysis?["chords"] is JsonArray analyzedChords)
                    chords = analyzedChords.Deserialize<List<SavedChord>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                var version = new SavedScoreVersion
                {
                    SourceSha256 = item.SourceSha256, UseReviewedProfile = snapshot.UseReviewedProfile,
                    MelodySources = snapshot.MelodySources, MelodySourcePriority = snapshot.MelodySourcePriority,
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
                    JsonNode.DeepEquals(QueueRevision(row), item.Recipe.GenerateOptions["libraryRevision"]);
                RememberVersion(row);
                row.Versions.Add(version); changed = true;
                if (activate)
                {
                    RestoreVersion(row, version);
                    row.Status = $"Gerada pela fila · {result.Pages} páginas";
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
