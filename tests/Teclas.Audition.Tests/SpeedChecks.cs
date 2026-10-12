using System.Diagnostics;
using Teclas.Core;

internal static class SpeedChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var document = MidiReader.Read(MidiFixtures.File(0,
        [
            new MidiFixtures.Event(0, 0xC0, 24), new MidiFixtures.Event(0, 0xB0, 7, 33), new MidiFixtures.Event(0, 0xB0, 11, 77), new MidiFixtures.Event(0, 0xE0, 16, 70),
            new MidiFixtures.Event(0, 0xB0, 64, 127), new MidiFixtures.Event(0, 0x90, 64, 91),
            new MidiFixtures.Event(9600, 0x80, 64, 0), new MidiFixtures.Event(19200, 0xB0, 64, 0)
        ]));
        var sequence = MidiAuditionSequence.Create(document, new(0, 1, 24));
        var originalEvents = sequence.Events.ToArray();
        var transport = new RecordingMidiOutput();
        using var player = new MidiAuditionPlayer(() => transport);
        int failures = 0;
        player.PlaybackFailed += (_, _) => failures++;
        check(player.PlaybackRate == 1, "speed-default-is-original-tempo");
        player.PlaybackRate = .5;
        player.Play(sequence);
        check(player.PlaybackRate == .5 && player.Duration == sequence.Duration && transport.Active.Any(note => note.Pitch == 64),
            "speed-half-retained-on-play-with-original-duration");
        double halfRate = await MeasureRate(player);
        check(Math.Abs(halfRate - .5) < .04, "speed-half-advances-half-content-time");

        int messagesBefore = transport.Messages.Length, resetsBefore = transport.ResetCount;
        var beforeChange = player.Position;
        var changeClock = Stopwatch.StartNew();
        player.PlaybackRate = 1.5;
        var afterChange = player.Position;
        check(afterChange >= beforeChange && (afterChange - beforeChange).TotalSeconds <= changeClock.Elapsed.TotalSeconds * 1.5 + .002,
            "speed-live-change-keeps-content-position-continuous");
        check(transport.Messages.Length == messagesBefore && transport.ResetCount == resetsBefore && transport.Active.Any(note => note.Pitch == 64),
            "speed-live-change-does-not-retrigger-release-or-unmute-notes");
        double fastRate = await MeasureRate(player);
        check(Math.Abs(fastRate - 1.5) < .04, "speed-fast-advances-one-and-half-content-time");
        check(sequence.Events.SequenceEqual(originalEvents) && transport.Messages.Where(On).All(message => A(message) == 64 && B(message) == 91)
            && transport.Messages.Last(message => (message & 0xf0) == 0xe0) == MidiAuditionSequence.Message(14, 1, 16, 70)
            && B(transport.Messages.Last(message => (message & 0xf0) == 0xb0 && A(message) == 7)) == 33
            && B(transport.Messages.Last(message => (message & 0xf0) == 0xb0 && A(message) == 11)) == 77,
            "speed-keeps-sequence-pitch-velocity-bend-and-volume-unchanged");

        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -.5, .2499, 1.5001 })
        {
            bool rejected = false;
            try { player.PlaybackRate = invalid; } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected && player.PlaybackRate == 1.5 && player.IsPlaying && failures == 0,
                "speed-invalid-value-rejected-without-interruption-" + invalid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        player.Pause();
        var pausedPosition = player.Position;
        int pausedMessages = transport.Messages.Length;
        player.PlaybackRate = .25;
        await Task.Delay(60);
        check(player.IsPaused && player.Position == pausedPosition && transport.Active.Length == 0 && transport.Messages.Length == pausedMessages,
            "speed-changing-rate-while-paused-keeps-silence-and-position");
        player.Seek(TimeSpan.FromSeconds(12));
        await Task.Delay(60);
        check(player.Position == TimeSpan.FromSeconds(12) && player.IsPaused && transport.Active.Length == 0,
            "speed-paused-seek-uses-original-content-seconds");
        player.Resume();
        check(player.PlaybackRate == .25 && player.IsPlaying && transport.Active.Any(note => note.Pitch == 64),
            "speed-resume-restores-pedal-held-note-at-selected-rate");
        double quarterRate = await MeasureRate(player);
        check(Math.Abs(quarterRate - .25) < .04 && player.Position >= TimeSpan.FromSeconds(12),
            "speed-quarter-advances-from-seek-position");
        player.Stop();
        int stoppedMessages = transport.Messages.Length;
        await Task.Delay(100);
        check(!player.IsPlaying && !player.IsPaused && player.Position == TimeSpan.Zero && transport.Active.Length == 0
            && transport.Messages.Length == stoppedMessages && player.PlaybackRate == .25,
            "speed-stop-after-slowing-cancels-notes-and-retains-rate");
        var switched = MidiAuditionSequence.Create(MidiFixtures.FormatZero(), new(0, 2, 40));
        player.Play(switched);
        check(player.PlaybackRate == .25 && transport.Active.All(note => note.Channel == 1 && note.Pitch == 72),
            "speed-source-switch-retains-rate-without-previous-notes");
        player.Stop(); player.PlaybackRate = 1.5; player.Play(switched);
        var finish = Stopwatch.StartNew();
        while (player.IsPlaying && finish.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
        check(!player.IsPlaying && player.Position == switched.Duration && player.Duration == switched.Duration
            && player.PlaybackRate == 1.5 && transport.Active.Length == 0, "speed-natural-end-retains-original-duration-and-silences-notes");
    }

    private static async Task<double> MeasureRate(MidiAuditionPlayer player)
    {
        double start = player.Position.TotalSeconds;
        var watch = Stopwatch.StartNew();
        await Task.Delay(220);
        double end = player.Position.TotalSeconds;
        return (end - start) / watch.Elapsed.TotalSeconds;
    }
    private static bool On(uint message) => (message & 0xf0) == 0x90 && B(message) > 0;
    private static int A(uint message) => (int)((message >> 8) & 127);
    private static int B(uint message) => (int)((message >> 16) & 127);
}
