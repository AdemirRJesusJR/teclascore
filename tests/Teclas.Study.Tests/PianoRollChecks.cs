using Teclas.Core;

internal static class PianoRollChecks
{
    internal static object Run(Action<bool, string> check)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        // Three 4/4 measures written once; the audio plays 1-2, repeats 1-2 and goes on to 3 at half the speed.
        var starts = new Dictionary<int, double> { [1] = 0, [2] = 4, [3] = 8 };
        var notes = new[]
        {
            new FallingNote(0, 1, 60, false),
            new FallingNote(3, 2, 64, false),       // held over the bar line 1 → 2
            new FallingNote(7, 2, 72, false),       // held over the bar line 2 → 3 (the end of the repeat)
            new FallingNote(8, 4, 67, false),
            new FallingNote(0, 8, 55, true, "C"), new FallingNote(0, 8, 64, true, "C"),
            new FallingNote(8, 4, 55, true, "G"), new FallingNote(8, 4, 59, true, "G"),
        };
        var timeline = new[]
        {
            new ScorePlaybackOccurrence(0, 1, 0, 2, null), new ScorePlaybackOccurrence(1, 2, 2, 4, null),
            new ScorePlaybackOccurrence(2, 1, 4, 6, null), new ScorePlaybackOccurrence(3, 2, 6, 8, null),
            new ScorePlaybackOccurrence(4, 3, 8, 12, null),
        };
        var blocks = PianoRollTimeline.Build(notes, starts, timeline);
        FallingNote[] Of(int pitch, bool chord = false) => blocks.Where(b => b.Pitch == pitch && b.Chord == chord).ToArray();
        check(Of(60) is [{ } a, { } b] && Near(a.Start, 0) && Near(a.Length, .5) && Near(b.Start, 4) && Near(b.Length, .5),
            "piano roll: a repeated measure comes down again at its second pass");
        check(Of(64) is [{ } c, { } d] && Near(c.Start, 1.5) && Near(c.Length, 1) && Near(d.Start, 5.5) && Near(d.Length, 1),
            "piano roll: a note held over the bar line stays one block");
        check(Of(72) is [{ } e, { } f] && Near(e.Start, 3.5) && Near(e.Length, .5) && Near(f.Start, 7.5) && Near(f.Length, 1.5),
            "piano roll: a note held into the measure after the repeat continues only where the music goes on");
        check(Of(67) is [{ } g] && Near(g.Start, 8) && Near(g.Length, 4), "piano roll: a slower tempo stretches the blocks");
        check(Of(55, true).Length == 3 && Of(55, true).Where(x => x.Label == "C").All(x => Near(x.Length, 4)) && Of(64, true).Length == 2,
            "piano roll: chord blocks are held through each pass");
        check(blocks.Zip(blocks.Skip(1)).All(p => p.First.Start <= p.Second.Start), "piano roll: blocks are in time order");
        check(PianoRollTimeline.ChordAt(blocks, 1) == "C" && PianoRollTimeline.ChordAt(blocks, 5) == "C" && PianoRollTimeline.ChordAt(blocks, 9) == "G"
            && PianoRollTimeline.ChordAt(blocks, 12.5) == "" && PianoRollTimeline.ChordAt(blocks, -1) == "", "piano roll: the chord being played");
        check(PianoRollTimeline.Build(notes, starts, []).Count == 0 && PianoRollTimeline.Build([], starts, timeline).Count == 0
            && PianoRollTimeline.Build(notes, starts, [new ScorePlaybackOccurrence(0, 9, 0, 2, null)]).Count == 0,
            "piano roll: nothing to place without a timeline, notes or a known measure");
        check(FallingNotes.KeyRange(notes) == (48, 83) && FallingNotes.KeyRange(notes, 4) == (48, 95)
            && FallingNotes.KeyRange([], 4) == (36, 83) && FallingNotes.KeyRange([]) == (48, 71),
            "piano roll: a wider keyboard keeps the notes in the middle");
        return new { blocks = blocks.Count };
    }
}
