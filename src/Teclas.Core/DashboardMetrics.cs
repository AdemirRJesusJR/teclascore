namespace Teclas.Core;

public enum DashboardCategory { Ready, Review, Generated, Error }

/// <summary>Facts about an imported MIDI, independent of the desktop presentation.</summary>
public sealed record DashboardItem(
    bool IsReadable,
    bool HasReviewedProfile,
    bool UsesReviewedProfile,
    bool HasAvailableScore,
    bool HasOperationError = false,
    int PageCount = 0)
{
    public DashboardCategory Category => !IsReadable ? DashboardCategory.Error
        : HasAvailableScore ? DashboardCategory.Generated
        : HasOperationError ? DashboardCategory.Error
        : HasReviewedProfile && UsesReviewedProfile ? DashboardCategory.Ready
        : DashboardCategory.Review;
}

public sealed record DashboardMetrics(int Total, int Ready, int Review, int Generated, int Error, int Pages)
{
    public double Percent(int count) => Total == 0 ? 0 : Math.Clamp(100d * count / Total, 0, 100);
    public double CompletionPercent => Percent(Generated);

    public static DashboardMetrics Calculate(IEnumerable<DashboardItem> items)
    {
        int total = 0, ready = 0, review = 0, generated = 0, error = 0, pages = 0;
        foreach (var item in items)
        {
            total++;
            switch (item.Category)
            {
                case DashboardCategory.Ready: ready++; break;
                case DashboardCategory.Review: review++; break;
                case DashboardCategory.Generated: generated++; pages += Math.Max(0, item.PageCount); break;
                case DashboardCategory.Error: error++; break;
            }
        }
        return new(total, ready, review, generated, error, pages);
    }
}
