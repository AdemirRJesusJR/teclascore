using System.Text.Json.Nodes;

namespace Teclas.Core;

public enum QueueOutcomeLevel { Ready, Review, Failed }

/// <summary>One line of the end-of-queue summary: whether the score came out ready, needs checking, or failed, and why.</summary>
public sealed record QueueOutcome(string ItemId, string Name, string SourcePath, QueueOutcomeLevel Level, string Verdict, string Detail);

/// <summary>Turns finished queue items into a short verdict, from the item state and the saved analysis of each song.</summary>
public static class ConversionQueueSummary
{
    public const double LowConfidence = .65;

    public static IReadOnlyList<QueueOutcome> Build(IEnumerable<ConversionQueueItem> items, Func<string, JsonNode?>? readAnalysis = null)
    {
        readAnalysis ??= path => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        var outcomes = new List<QueueOutcome>();
        foreach (var item in items.OrderBy(item => item.UpdatedAt))
        {
            if (item.Status == ConversionQueueStatus.Failed)
            { outcomes.Add(new(item.Id, item.DisplayName, item.OriginalSourcePath, QueueOutcomeLevel.Failed, "Falhou", item.Error ?? "Erro sem descrição.")); continue; }
            if (item.Status != ConversionQueueStatus.Completed || item.Result is null) continue;
            JsonNode? analysis = null;
            try { if (item.Result.AnalysisPath is { } path) analysis = readAnalysis(path); }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
            int doubtful = analysis?["chords"] is JsonArray chords
                ? chords.Count(chord => (chord?["confidence"]?.GetValue<double>() ?? 1) < LowConfidence) : 0;
            int points = analysis?["reviewPoints"] is JsonArray review ? review.Count : 0;
            int warnings = analysis?["warnings"] is JsonArray list ? list.Count : 0;
            var notes = new List<string>();
            if (doubtful > 0) notes.Add(doubtful == 1 ? "1 cifra com dúvida" : $"{doubtful} cifras com dúvida");
            if (points > 0) notes.Add(points == 1 ? "1 ponto de revisão" : $"{points} pontos de revisão");
            if (warnings > 0) notes.Add(warnings == 1 ? "1 aviso da análise" : $"{warnings} avisos da análise");
            string pages = item.Result.Pages == 1 ? "1 página" : $"{item.Result.Pages} páginas";
            outcomes.Add(notes.Count == 0
                ? new(item.Id, item.DisplayName, item.OriginalSourcePath, QueueOutcomeLevel.Ready, "Pronta", pages)
                : new(item.Id, item.DisplayName, item.OriginalSourcePath, QueueOutcomeLevel.Review, "Conferir", string.Join(" · ", notes) + " · " + pages));
        }
        return outcomes;
    }

    public static string Headline(IReadOnlyList<QueueOutcome> outcomes) =>
        $"{outcomes.Count(o => o.Level == QueueOutcomeLevel.Ready)} prontas · {outcomes.Count(o => o.Level == QueueOutcomeLevel.Review)} para conferir · {outcomes.Count(o => o.Level == QueueOutcomeLevel.Failed)} com falha";
}
