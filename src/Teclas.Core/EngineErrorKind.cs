using System.Text.Json;
using System.Text.Json.Nodes;

namespace Teclas.Core;

/// <summary>
/// The cause of an engine failure (the <c>kind</c> of the engine's NeedsReview: repeat_jump, diagram_layout, …), read
/// tolerantly: newer workers send it with the error message, older ones only write it to the diagnostic file, and
/// older queue results have none (null = unknown cause).
/// </summary>
public static class EngineErrorKind
{
    private static readonly string[] Fields = ["errorKind", "failureKind", "reviewKind", "reason"];

    public static string? Of(Exception? error) => error switch
    {
        EngineOperationException engine => Of(engine.Details),
        AggregateException aggregate => aggregate.InnerExceptions.Select(Of).FirstOrDefault(kind => kind is not null),
        _ => null
    };

    public static string? Of(JsonObject? details)
    {
        if (details is null) return null;
        foreach (string field in Fields)
            if (Text(details[field]) is { } value) return value;
        // The message envelope uses "kind" for its own type ("error"); any other value is the cause.
        if (Text(details["kind"]) is { } kind && kind != "error") return kind;
        if (details["error"] is JsonObject nested && Of(nested) is { } inner) return inner;
        if (Text(details["diagnosticPath"]) is { } path) return FromDiagnostic(path);
        return null;
    }

    /// <summary>Failures that a new attempt without folding the repeats can fix: repeats/jumps and page or diagram layout.</summary>
    public static bool IsRepeatOrLayout(string? kind) => kind is { Length: > 0 } value &&
        (value.StartsWith("repeat", StringComparison.Ordinal) || value.EndsWith("_layout", StringComparison.Ordinal) ||
         value is "diagram_collision" or "page_overflow" or "system_overflow");

    /// <summary>A short Portuguese name of the cause, for grouping failures in reports.</summary>
    public static string Describe(string? kind) => kind switch
    {
        null or "" => "causa não informada",
        "repeat_jump" => "sinais de repetição (D.S./Coda)",
        "diagram_layout" => "espaço dos diagramas",
        "diagram_collision" => "diagramas personalizados coincidentes",
        "instrument_label_layout" => "nomes dos instrumentos",
        "instrument_source" => "pista de origem inválida",
        "rhythm_collision" => "ritmo para revisar",
        "transpose_range" => "transposição fora da extensão",
        var other when other.StartsWith("repeat", StringComparison.Ordinal) => "repetições",
        var other => other.Replace('_', ' ')
    };

    private static string? Text(JsonNode? node)
    {
        try { return node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string? FromDiagnostic(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return null;
            return JsonNode.Parse(File.ReadAllBytes(path)) is JsonObject document ? Text(document["kind"]) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return null; }
    }
}
