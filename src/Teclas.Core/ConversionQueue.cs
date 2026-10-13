using System.Text.Json.Nodes;

namespace Teclas.Core;

public enum ConversionQueueStatus { Pending, Running, Paused, Failed, Completed }
public sealed record ConversionQueueRecipe(string SourcePath, string PythonPath, string EngineDirectory,
    string MuseScorePath, string OutputDirectory, JsonObject AnalyzeOptions, JsonObject GenerateOptions,
    string? ExistingAnalysisPath = null)
{
    public string? SourceSha256 { get; init; }
    public string? DisplayName { get; init; }
}
public sealed record ConversionQueueResult(string ScorePath, string PreviewPath, string ReviewDirectory,
    int Pages, string Message)
{
    public JsonObject Data { get; init; } = new();
    public string? AnalysisPath { get; init; }
}
public sealed record ConversionQueueItem(string Id, string DisplayName, string OriginalSourcePath,
    string SnapshotPath, string SourceSha256, string Workspace, ConversionQueueStatus Status,
    int Attempts, string Stage, string? Error, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ConversionQueueResult? Result, ConversionQueueRecipe Recipe);
public sealed record ConversionQueueProgress(string ItemId, string Stage, int Completed, int Total);
public interface IConversionQueueWorker
{
    Task<JsonObject> RunAsync(ConversionQueueRecipe recipe, JsonObject request, string workspace,
        IProgress<string>? progress, CancellationToken token);
}
