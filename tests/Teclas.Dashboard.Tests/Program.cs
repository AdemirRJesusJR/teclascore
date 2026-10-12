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
Console.WriteLine($"PASS: {passed} dashboard checks.");
