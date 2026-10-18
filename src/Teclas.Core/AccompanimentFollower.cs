namespace Teclas.Core;

/// <summary>Notes to release and to sound when the accompaniment changes chord.</summary>
public sealed record AccompanimentChange(IReadOnlyList<int> Off, IReadOnlyList<int> On, string Label);

/// <summary>
/// An accompaniment that follows the player: each melody step played moves the score position, and the chord of the
/// cipher at that position sounds (keyboard-diagram voicing in the octave below middle C, root in the bass). The
/// player's pace is measured from the time between steps, so the screen can show the tempo they are really playing.
/// </summary>
public sealed class AccompanimentFollower
{
    public const int ChordFloor = 48, BassFloor = 36;
    private readonly IReadOnlyList<PracticeStep> chords;
    private readonly List<int> sounding = [];
    private (double Start, double Seconds)? previous;
    public string Label { get; private set; } = "";
    /// <summary>The player's tempo in quarter notes per minute, smoothed; null until two steps were played.</summary>
    public double? Bpm { get; private set; }
    public IReadOnlyList<int> Sounding => sounding;

    public AccompanimentFollower(IReadOnlyList<PracticeStep> chordSteps)
    {
        ArgumentNullException.ThrowIfNull(chordSteps);
        chords = chordSteps.Where(step => step.Label.Length > 0 && step.Pitches.Count > 0).OrderBy(step => step.Start).ToArray();
    }

    public bool HasChords => chords.Count > 0;

    /// <summary>The chord in effect at a score position (the last one starting at or before it).</summary>
    public PracticeStep? ChordAt(double start)
    {
        PracticeStep? found = null;
        foreach (var chord in chords) { if (chord.Start <= start + 1e-3) found = chord; else break; }
        return found;
    }

    /// <summary>Called when the player reaches a melody step; returns the notes to change, or null to keep the chord.</summary>
    public AccompanimentChange? Follow(double start, double seconds)
    {
        if (previous is { } last && start > last.Start && seconds > last.Seconds)
        {
            double instant = 60 * (start - last.Start) / (seconds - last.Seconds);
            if (instant is >= 20 and <= 300) Bpm = Bpm is { } bpm ? Math.Round(bpm * .7 + instant * .3, 1) : Math.Round(instant, 1);
        }
        if (previous is { } back && start < back.Start) Bpm = null; // went back: the pace starts again
        previous = (start, seconds);
        if (ChordAt(start) is not { } chord || chord.Label == Label && sounding.Count > 0) return null;
        var on = Voice(chord);
        var change = new AccompanimentChange(sounding.ToArray(), on, chord.Label);
        sounding.Clear(); sounding.AddRange(on); Label = chord.Label;
        return change;
    }

    /// <summary>Releases everything (end, restart or window closed).</summary>
    public IReadOnlyList<int> Stop()
    {
        var off = sounding.ToArray();
        sounding.Clear(); Label = ""; previous = null; Bpm = null;
        return off;
    }

    /// <summary>
    /// The chord's diagram notes moved to start in the octave below middle C, plus the bass: the written bass of a
    /// slash chord (G/B → B), else the root. The bass is always the first (lowest) note.
    /// </summary>
    public static IReadOnlyList<int> Voice(PracticeStep chord)
    {
        int lowest = chord.Pitches.Min();
        int shift = (int)Math.Floor((ChordFloor - lowest) / 12.0 + 1 - 1e-9) * 12;
        if (lowest + shift >= ChordFloor + 12) shift -= 12;
        var notes = chord.Pitches.Select(pitch => pitch + shift).ToList();
        string root = chord.Label.Length > 1 && chord.Label[1] is '#' or 'b' ? chord.Label[..2] : chord.Label[..1];
        if (KeyboardDiagramVoicing.TryRootPitchClass(chord.Bass ?? root, out int pitchClass) ||
            KeyboardDiagramVoicing.TryRootPitchClass(root, out pitchClass)) notes.Insert(0, BassFloor + pitchClass);
        return notes.Distinct().Order().ToArray();
    }
}

/// <summary>How the left hand plays each chord while following the player.</summary>
public enum AccompanimentStyle { Block, Arpeggio, Waltz, Ballad }

/// <summary>One note switching on or off at a beat of the pattern bar.</summary>
public sealed record PatternEvent(double Beat, int Pitch, bool On);

/// <summary>
/// Left-hand patterns over one bar, for a voicing whose first note is the bass. Block holds the chord; Arpeggio walks
/// up and down in eighths over a held bass; Waltz plays bass then two chords (3 beats); Ballad strikes bass and chord on
/// the first and middle beats (the bass moving to the fifth). Every note switched on is switched off inside the bar.
/// </summary>
public static class AccompanimentPattern
{
    public static int BeatsPerBar(AccompanimentStyle style, int meter) => style == AccompanimentStyle.Waltz ? 3 : Math.Clamp(meter, 2, 6);

    public static IReadOnlyList<PatternEvent> Bar(AccompanimentStyle style, IReadOnlyList<int> voicing, int meter = 4)
    {
        if (voicing.Count == 0) return [];
        int bass = voicing[0];
        var chord = voicing.Skip(1).ToArray();
        if (chord.Length == 0) chord = [bass + 12];
        int beats = BeatsPerBar(style, meter);
        var events = new List<PatternEvent>();
        void Note(double at, double length, int pitch) { events.Add(new(at, pitch, true)); events.Add(new(Math.Min(beats - .02, at + length), pitch, false)); }
        void Chord(double at, double length) { foreach (int pitch in chord) Note(at, length, pitch); }
        switch (style)
        {
            case AccompanimentStyle.Arpeggio:
                Note(0, beats - .05, bass);
                var walk = chord.Concat(chord.Reverse().Skip(1).Take(Math.Max(0, chord.Length - 2))).ToArray();
                for (int slot = 0; slot < beats * 2; slot++) Note(slot * .5, .45, walk[slot % walk.Length]);
                break;
            case AccompanimentStyle.Waltz:
                Note(0, .9, bass); Chord(1, .45); Chord(2, .45);
                break;
            case AccompanimentStyle.Ballad:
                double half = beats / 2.0;
                Note(0, half - .05, bass); Chord(0, half - .05);
                Note(half, half - .05, bass + (bass + 7 - 12 >= AccompanimentFollower.BassFloor ? 7 - 12 : 7)); Chord(half, half - .05);
                break;
            default:
                Note(0, beats - .02, bass); Chord(0, beats - .02);
                break;
        }
        return events.OrderBy(e => e.Beat).ThenBy(e => e.On).ToArray();
    }
}
