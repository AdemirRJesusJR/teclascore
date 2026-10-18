namespace Teclas.Core;

public sealed record MelodySearchHit(string Id, string Title, double Similarity, int Position);

/// <summary>
/// Finds songs by a few played notes. Melodies are compared by their intervals, so any key works; the best matching
/// stretch of each song's top voice gives its similarity (1 = every interval equal, a semitone off counts half).
/// </summary>
public static class MelodySearch
{
    public const int MinimumNotes = 4;

    /// <summary>Top voice of a part: at each attack the highest note; very short grace-like notes are kept as played.</summary>
    public static int[] Contour(IEnumerable<MidiNote> notes) => notes
        .GroupBy(note => note.Start).OrderBy(group => group.Key).Select(group => group.Max(note => note.Pitch)).ToArray();

    public static double Similarity(IReadOnlyList<int> query, IReadOnlyList<int> melody, out int position)
    {
        position = -1;
        if (query.Count < 2 || melody.Count < query.Count) return 0;
        var q = Intervals(query); var m = Intervals(melody);
        double best = 0;
        for (int start = 0; start + q.Length <= m.Length; start++)
        {
            double score = 0;
            for (int i = 0; i < q.Length; i++)
            {
                int difference = Math.Abs(m[start + i] - q[i]);
                score += difference == 0 ? 1 : difference == 1 ? .5 : 0;
            }
            score /= q.Length;
            if (score > best) { best = score; position = start; if (best >= 1) break; }
        }
        return best;
    }

    /// <summary>Songs ordered by similarity, keeping those at or above <paramref name="threshold"/>.</summary>
    public static IReadOnlyList<MelodySearchHit> Search(IReadOnlyList<int> query, IEnumerable<(string Id, string Title, IReadOnlyList<int> Melody)> songs, double threshold = .7, int limit = 10)
    {
        if (query.Count < MinimumNotes) return [];
        return songs.Select(song => (song, score: Similarity(query, song.Melody, out int position), position))
            .Where(item => item.score >= threshold)
            .OrderByDescending(item => item.score).ThenBy(item => item.position)
            .Take(limit).Select(item => new MelodySearchHit(item.song.Id, item.song.Title, Math.Round(item.score, 3), item.position)).ToArray();
    }

    private static int[] Intervals(IReadOnlyList<int> pitches)
    {
        var result = new int[pitches.Count - 1];
        for (int i = 1; i < pitches.Count; i++) result[i - 1] = pitches[i] - pitches[i - 1];
        return result;
    }
}
