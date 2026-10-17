using Teclas.Core;

internal static class PracticeChecks
{
    internal static object Run(Action<bool, string> check)
    {
        var steps = new[] { new PracticeStep(1, 0, [60]), new PracticeStep(1, 1, [64, 67]), new PracticeStep(2, 0, [72]) };
        var session = new PracticeSession(steps, anyOctave: false);
        check(session.Press(61) == PracticeResult.Wrong && session.Index == 0 && session.Misses == 1, "wrong key counts but never advances");
        check(session.Press(60) == PracticeResult.StepComplete && session.Index == 1, "right key advances");
        check(session.Press(67) == PracticeResult.Correct && session.Remaining.SequenceEqual([64]), "chord waits for every key");
        check(session.Press(67) == PracticeResult.Wrong, "a chord key already played does not count twice");
        check(session.Press(64) == PracticeResult.StepComplete && session.Index == 2, "chord completes in any order");
        check(session.Press(60) == PracticeResult.Wrong, "other octave is wrong when exact octave is required");
        session.AnyOctave = true;
        check(session.Press(60) == PracticeResult.Finished && session.IsFinished && session.Press(60) == PracticeResult.Ignored, "any octave accepted when enabled; finished ignores keys");
        check(session.Hits == 4 && session.Misses == 3 && Math.Abs(session.Accuracy - 4 / 7.0) < 1e-9, "hits, misses and accuracy");
        session.Advance(-2); check(session.Index == 1 && session.Remaining.Count == 2, "going back restores the whole step");
        session.Restart(); check(session.Index == 0 && session.Hits == 0 && session.Misses == 0, "restart clears the score");
        check(PracticeSession.NoteName(60) == "Dó4" && PracticeSession.NoteName(70) == "Si♭4", "Portuguese note names");
        return new { steps = steps.Length };
    }

    internal static object RunNative(string native, Action<bool, string> check)
    {
        var steps = PracticeSession.FromScore(NativeMusicEditor.Open(native));
        // The native fixture has A4 then C5, one whole note per measure.
        check(steps.Select(step => step.Pitches.Single()).SequenceEqual([69, 72]) && steps.Select(step => step.Measure).SequenceEqual([1, 2]),
            "practice steps come from the saved score in order");
        return new { steps = steps.Count };
    }
}
