namespace Teclas.Core;

/// <summary>One side of a printed sheet: the page shown on the left and on the right (null = blank), zero-based.</summary>
public sealed record BookletSide(int? Left, int? Right);

/// <summary>
/// Saddle-stitch order: pages padded with blanks to a multiple of four; sheet k shows (last−2k, 2k) on the front and
/// (2k+1, last−2k−1) on the back, so the folded stack reads 1, 2, 3… Printed on both sides, flipping on the short edge.
/// </summary>
public static class BookletImposition
{
    public static IReadOnlyList<BookletSide> Sides(int pages)
    {
        if (pages <= 0) return [];
        int total = (pages + 3) / 4 * 4;
        int? Page(int index) => index < pages ? index : null;
        var sides = new List<BookletSide>();
        for (int sheet = 0; sheet < total / 4; sheet++)
        {
            sides.Add(new(Page(total - 1 - 2 * sheet), Page(2 * sheet)));
            sides.Add(new(Page(2 * sheet + 1), Page(total - 2 - 2 * sheet)));
        }
        return sides;
    }
}
