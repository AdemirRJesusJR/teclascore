namespace Teclas.Core;

/// <summary>Coordinates are normalized to the rendered page; measures are optional.</summary>
public sealed record NativeVisualBounds(double X, double Y, double Width, double Height);
public sealed record NativeVisualReviewIssue(string Id, string Kind, string Severity, int Page,
    int? System, int? Staff, int? Measure, NativeVisualBounds Bounds, string Message, bool CanAutoFix)
{
    public string Position => $"Página {Page}" + (Measure is int m ? $" · compasso {m}" : System is int s ? $" · linha {s}" : "");
}
public sealed record NativeVisualReviewReport(string SourcePath, string SourceSha256,
    NativeScoreMetadata Metadata, NativeScoreResult Original, IReadOnlyList<NativeVisualReviewIssue> Issues,
    IReadOnlyList<string> Limitations, bool CanPrepare);
public sealed record NativeVisualReviewPrepared(NativeScoreResult Result, NativeVisualReviewReport After,
    IReadOnlyList<string> Changes);
