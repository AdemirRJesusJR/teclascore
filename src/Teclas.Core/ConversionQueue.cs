using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Teclas.Core;

public enum ConversionQueueStatus { Pending, Running, Paused, Failed, Completed }
public sealed record ConversionQueueRecipe(string SourcePath, string PythonPath, string EngineDirectory,
    string MuseScorePath, string OutputDirectory, JsonObject AnalyzeOptions, JsonObject GenerateOptions,
    string? ExistingAnalysisPath = null)
{
    public string? SourceSha256 { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>App data kept beside the job (attachment.json), outside the frozen recipe, the queue file and the engine
    /// request: for example the library card to restore when the conversion completes.</summary>
    [JsonIgnore] public JsonObject? Attachment { get; init; }
}
public sealed record ConversionQueueResult(string ScorePath, string PreviewPath, string ReviewDirectory,
    int Pages, string Message)
{
    public JsonObject Data { get; init; } = new();
    public string? AnalysisPath { get; init; }
}
/// <param name="ErrorKind">The engine's cause of the last failure (repeat_jump, diagram_layout…), null when unknown.</param>
public sealed record ConversionQueueItem(string Id, string DisplayName, string OriginalSourcePath,
    string SnapshotPath, string SourceSha256, string Workspace, ConversionQueueStatus Status,
    int Attempts, string Stage, string? Error, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ConversionQueueResult? Result, ConversionQueueRecipe Recipe, string? ErrorKind = null);
/// <summary>A light view of a queue item for lists and progress: no recipe or result JSON is copied.</summary>
public sealed record ConversionQueueSummaryItem(string Id, string DisplayName, string OriginalSourcePath, string SourceSha256,
    string Workspace, ConversionQueueStatus Status, int Attempts, string Stage, string? Error, string? ErrorKind,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ScorePath, string? PreviewPath, int Pages);
/// <summary>Result of adding several recipes at once, aligned with the recipes: an item or the reason it was not added.</summary>
public sealed record ConversionQueueBatch(IReadOnlyList<ConversionQueueItem?> Items, IReadOnlyList<Exception?> Errors)
{
    public int Added => Items.Count(item => item is not null);
}
public sealed record ConversionQueueProgress(string ItemId, string Stage, int Completed, int Total);
public interface IConversionQueueWorker
{
    Task<JsonObject> RunAsync(ConversionQueueRecipe recipe, JsonObject request, string workspace,
        IProgress<string>? progress, CancellationToken token);
}
