using Teclas.Core;

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}

var empty = DashboardMetrics.Calculate([]);
Check(empty.Total == 0 && empty.Pages == 0 && empty.CompletionPercent == 0, "Empty session is zero, not NaN.");

var reviewed = new DashboardItem(true, true, true, false);
Check(reviewed.Category == DashboardCategory.Ready, "Reviewed profile is eligible for the batch.");
Check((reviewed with { UsesReviewedProfile = false }).Category == DashboardCategory.Review,
    "Manual source selection disables batch eligibility.");
Check((reviewed with { HasOperationError = true }).Category == DashboardCategory.Error,
    "Failed conversion belongs to failures instead of claiming batch readiness.");
Check((reviewed with { HasReviewedProfile = false }).Category == DashboardCategory.Review,
    "Unknown, partial and accompaniment files need manual review.");
Check((reviewed with { HasAvailableScore = true, HasOperationError = true }).Category == DashboardCategory.Generated,
    "An earlier existing score remains available after a failed new attempt.");
Check((reviewed with { IsReadable = false, HasAvailableScore = true }).Category == DashboardCategory.Error,
    "An unreadable input is classified as invalid even with stale output metadata.");

var session = DashboardMetrics.Calculate([
    reviewed,
    reviewed with { UsesReviewedProfile = false },
    reviewed with { HasReviewedProfile = false },
    reviewed with { HasOperationError = true },
    reviewed with { HasAvailableScore = true, PageCount = 5 },
    reviewed with { IsReadable = false, HasAvailableScore = true, PageCount = 7 },
    reviewed with { HasAvailableScore = false, PageCount = 9 }
]);
Check(session.Total == 7 && session.Ready == 2 && session.Review == 2 && session.Generated == 1 && session.Error == 2,
    "Mixed library has four non-overlapping categories.");
Check(session.Pages == 5, "Only available generated scores contribute pages.");
Check(Math.Abs(session.CompletionPercent - 100d / 7) < 0.00001, "Completion is generated divided by all imported files.");
Check(session.Percent(-1) == 0 && session.Percent(100) == 100, "Progress visuals stay within 0–100.");

for (int mask = 0; mask < 32; mask++)
{
    var item = new DashboardItem((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
        (mask & 8) != 0, (mask & 16) != 0, -5);
    var metrics = DashboardMetrics.Calculate([item]);
    Check(metrics.Total == metrics.Ready + metrics.Review + metrics.Generated + metrics.Error && metrics.Pages == 0,
        $"Every state belongs to exactly one category with a non-negative page count: {mask}.");
}

// ---- The engine report beside a generated score (review in sequence 2.0) ----
{
    using var document = System.Text.Json.JsonDocument.Parse("""
        {"origin_removed_quarters": 4, "harmonic_warnings": ["Armadura sugerida com confiança limitada"], "quality": {"score": 88, "issues": ["letra apertada"]},
         "bars": [{"start": 0, "end": 3840}, {"start": 3840, "end": 7680}, {"start": 7680, "end": 11520}, {"start": 11520, "end": 15360}, {"start": 15360, "end": 19200}],
         "notes": [{"start": 0, "end": 1920, "pitch": 60}, {"start": 7680, "end": 9600, "pitch": 62}, {"start": 15360, "end": 16000, "pitch": 64}]}
        """);
    var report = ScoreValidationReport.Parse(document.RootElement);
    Check(report.HarmonicWarnings.Count == 1 && report.QualityScore == 88 && report.QualityIssues.SequenceEqual(["letra apertada"]), "Warnings and quality are read from the report.");
    Check(report.MeasureCount == 5 && report.EmptyMeasures.SequenceEqual([2, 4]) && report.OriginQuarters == 4 && report.Bars.Count == 5 && report.Bars[1] == (4, 8), "Bars, empty measures and the origin offset are read.");
    Check(report.EmptyRanges().SequenceEqual([(2, 2), (4, 4)]), "Empty ranges leave out the first and the last measure.");
    Check(report.MeasureAtMidiQuarter(4) == 1 && report.MeasureAtMidiQuarter(9) == 2 && report.MeasureAtMidiQuarter(23.9) == 5 && report.MeasureAtMidiQuarter(1) is null && report.MeasureAtMidiQuarter(40) == 5,
        "A chord position in MIDI quarters finds its score measure after the removed origin.");
    var merged = ScoreValidationReport.Parse(System.Text.Json.JsonDocument.Parse("""{"bars": [{"start": 0, "end": 3840}, {"start": 3840, "end": 7680}, {"start": 7680, "end": 11520}, {"start": 11520, "end": 15360}], "notes": []}""").RootElement);
    Check(merged.EmptyMeasures.SequenceEqual([1, 2, 3, 4]) && merged.EmptyRanges().Count == 0, "A score with no melody at all is one intro-to-end run, not a review point.");
    Check(ScoreValidationReport.Read(Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N") + ".mscz")) == ScoreValidationReport.Empty, "A missing report reads as empty.");
    Check(ScoreValidationReport.Parse(System.Text.Json.JsonDocument.Parse("[]").RootElement) == ScoreValidationReport.Empty, "A report that is not an object reads as empty.");
}
Console.WriteLine($"PASS: {passed} dashboard checks.");
