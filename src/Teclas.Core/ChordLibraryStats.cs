namespace Teclas.Core;

/// <summary>The chords of one song in the key it is printed (transposition applied), and whether it was already studied.</summary>
public sealed record SongChords(string Id, string Title, IReadOnlySet<ChordShape> Shapes, bool Studied);
/// <summary>A chord of the library: in how many songs it appears, how many unstudied songs it completes, and whether it is known.</summary>
public sealed record ChordToLearn(ChordShape Shape, string Name, int Songs, int Unlocks, bool Known);
/// <summary>A song with the same chords as another; Transpose ≠ 0 means the same chords in another key.</summary>
public sealed record SimilarSong(SongChords Song, double Score, int Transpose);

/// <summary>
/// Chord statistics over the library. A chord counts as known when it is in a song already studied or was drilled on its
/// own. "Unlocks" counts the songs not yet studied whose only unknown chord is this one: learning it makes them playable.
/// </summary>
public static class ChordLibraryStats
{
    private static readonly string[] Roots = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    public static string Name(ChordShape shape) => Roots[((shape.PitchClass % 12) + 12) % 12] + shape.Quality;

    public static IReadOnlyList<ChordToLearn> ToLearn(IReadOnlyList<SongChords> songs, IReadOnlySet<ChordShape> drilled)
    {
        var known = songs.Where(song => song.Studied).SelectMany(song => song.Shapes).Concat(drilled).ToHashSet();
        var unlocks = new Dictionary<ChordShape, int>();
        foreach (var song in songs.Where(song => !song.Studied))
        {
            var missing = song.Shapes.Where(shape => !known.Contains(shape)).ToArray();
            if (missing.Length == 1) unlocks[missing[0]] = unlocks.GetValueOrDefault(missing[0]) + 1;
        }
        return songs.SelectMany(song => song.Shapes).Distinct()
            .Select(shape => new ChordToLearn(shape, Name(shape), songs.Count(song => song.Shapes.Contains(shape)), unlocks.GetValueOrDefault(shape), known.Contains(shape)))
            .OrderBy(chord => chord.Known).ThenByDescending(chord => chord.Unlocks).ThenByDescending(chord => chord.Songs).ThenBy(chord => chord.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Songs whose chords overlap with this one (Jaccard similarity, at least <paramref name="minimum"/>), also in other
    /// keys: the same progression a few semitones away counts, with the shift reported. The printed key wins a tie.
    /// </summary>
    public static IReadOnlyList<SimilarSong> Similar(SongChords song, IReadOnlyList<SongChords> library, double minimum = .5)
    {
        if (song.Shapes.Count < 2) return [];
        var result = new List<SimilarSong>();
        foreach (var other in library)
        {
            if (other.Id == song.Id || other.Shapes.Count < 2) continue;
            SimilarSong? best = null;
            foreach (int shift in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6 })
            {
                var moved = other.Shapes.Select(shape => shape with { PitchClass = ((shape.PitchClass + shift) % 12 + 12) % 12 }).ToHashSet();
                double score = (double)moved.Count(song.Shapes.Contains) / moved.Union(song.Shapes).Count();
                if (best is null || score > best.Score + 1e-9) best = new(other, score, -shift);
            }
            if (best is not null && best.Score >= minimum) result.Add(best);
        }
        return result.OrderByDescending(item => item.Score).ThenBy(item => Math.Abs(item.Transpose)).ThenBy(item => item.Song.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
