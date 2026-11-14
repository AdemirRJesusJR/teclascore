namespace Teclas.Core;

/// <summary>
/// The piano roll under the score: the falling-note blocks (written quarters) placed on the playback clock (seconds)
/// through the measure timeline of the score's audio. A measure played twice by a repeat comes down twice, and a tempo
/// change stretches its measures, so the blocks reach the keyboard exactly when the audio sounds them.
/// </summary>
public static class PianoRollTimeline
{
    /// <summary>
    /// Blocks in seconds (<see cref="FallingNote.Start"/> and <see cref="FallingNote.Length"/>) for each played measure
    /// of <paramref name="occurrences"/>. A note that crosses into the next played measure stays one block; the same
    /// note reached again after a repeat is a new block.
    /// </summary>
    public static IReadOnlyList<FallingNote> Build(IReadOnlyList<FallingNote> notes, IReadOnlyDictionary<int, double> measureStarts,
        IReadOnlyList<ScorePlaybackOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(notes); ArgumentNullException.ThrowIfNull(measureStarts); ArgumentNullException.ThrowIfNull(occurrences);
        if (notes.Count == 0 || measureStarts.Count == 0 || occurrences.Count == 0) return [];
        var measures = measureStarts.OrderBy(pair => pair.Value).ToArray();
        double end = notes.Max(n => n.Start + n.Length);
        var ranges = new Dictionary<int, (double Start, double End)>();
        for (int i = 0; i < measures.Length; i++)
        {
            double start = measures[i].Value, stop = i + 1 < measures.Length ? measures[i + 1].Value : Math.Max(end, start + 4);
            ranges[measures[i].Key] = (start, stop);
        }
        // The notes sounding in each written measure, found once.
        var inside = ranges.Keys.ToDictionary(measure => measure, _ => new List<int>());
        var starts = measures.Select(pair => pair.Value).ToArray();
        for (int i = 0; i < notes.Count; i++)
        {
            double from = notes[i].Start, to = notes[i].Start + notes[i].Length;
            int index = Array.BinarySearch(starts, from);
            if (index < 0) index = Math.Max(0, ~index - 1);
            for (; index < measures.Length && starts[index] < to; index++)
                if (ranges[measures[index].Key].End > from) inside[measures[index].Key].Add(i);
        }
        var blocks = new List<FallingNote>();
        var last = new Dictionary<int, int>();
        foreach (var occurrence in occurrences.OrderBy(o => o.StartSeconds))
        {
            if (!ranges.TryGetValue(occurrence.Measure, out var range) || range.End <= range.Start || occurrence.EndSeconds <= occurrence.StartSeconds) continue;
            double scale = (occurrence.EndSeconds - occurrence.StartSeconds) / (range.End - range.Start);
            foreach (int i in inside[occurrence.Measure])
            {
                var note = notes[i];
                double from = Math.Max(note.Start, range.Start), to = Math.Min(note.Start + note.Length, range.End);
                if (to - from <= 1e-9) continue;
                double start = occurrence.StartSeconds + (from - range.Start) * scale, stop = occurrence.StartSeconds + (to - range.Start) * scale;
                // The continuation of a note held over the bar line joins its block.
                if (from > note.Start + 1e-9 && last.TryGetValue(i, out int previous) && Math.Abs(blocks[previous].Start + blocks[previous].Length - start) < 1e-3)
                    blocks[previous] = blocks[previous] with { Length = stop - blocks[previous].Start };
                else { last[i] = blocks.Count; blocks.Add(note with { Start = start, Length = stop - start }); }
            }
        }
        return blocks.OrderBy(n => n.Start).ThenBy(n => n.Chord).ThenBy(n => n.Pitch).ToArray();
    }

    /// <summary>The chord name sounding at a time (the lowest chord block holding a label), or "" between chords.</summary>
    public static string ChordAt(IReadOnlyList<FallingNote> blocks, double seconds)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        FallingNote? found = null;
        foreach (var block in blocks)
        {
            if (block.Start > seconds) break;
            if (block.Chord && block.Label.Length > 0 && seconds < block.Start + block.Length && (found is null || block.Start > found.Start || block.Start == found.Start && block.Pitch < found.Pitch)) found = block;
        }
        return found?.Label ?? "";
    }
}
