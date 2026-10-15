using Teclas.Core;
using static MidiFixtures;

internal static class FullPlaybackChecks
{
    private static int Kind(uint message) => (int)(message & 0xf0);
    private static int Channel(uint message) => (int)(message & 15) + 1;
    private static int A(uint message) => (int)(message >> 8 & 127);
    private static int B(uint message) => (int)(message >> 16 & 127);
    private static bool On(uint message) => Kind(message) == 0x90 && B(message) > 0;

    public static async Task Run(Action<bool, string> check)
    {
        var original = FormatZero();
        var full = MidiAuditionSequence.CreateFull(original);
        check(full.IsFullDocument && full.Channels.SequenceEqual([1, 2, 10]) && full.Events.Count(e => On(e.Message)) == original.Notes.Count,
            "full-midi-keeps-all-tracks-channels-and-note-attacks");
        check(full.Events.Where(e => On(e.Message)).Select(e => (Channel(e.Message), A(e.Message), B(e.Message)))
            .SequenceEqual(new[] { (1, 60, 101), (2, 72, 88), (10, 36, 112), (1, 67, 77) }), "full-midi-preserves-original-pitches-velocities-and-percussion-channel");
        check(full.Events.Any(e => e.At.TotalSeconds == .5 && Kind(e.Message) == 0xc0 && Channel(e.Message) == 1 && A(e.Message) == 24),
            "full-midi-retains-program-changes-within-one-channel");
        var timed = MidiReader.Read(File(1,
            [new(480, 0xff, 0x51, 3, 0x0f, 0x42, 0x40)],
            [new(480, 0x90, 60, 80), new(960, 0x80, 60, 0)],
            [new(480, 0xc1, 40), new(480, 0x91, 60, 90), new(960, 0x81, 60, 0)]));
        var tempoFull = MidiAuditionSequence.CreateFull(timed);
        check(tempoFull.LeadingSilenceTrimmed == TimeSpan.Zero && tempoFull.Events.First(e => On(e.Message)).At.TotalSeconds == .5
            && tempoFull.Duration.TotalSeconds == 1.75, "full-midi-keeps-leading-silence-global-tempo-and-original-duration");
        check(tempoFull.Until(TimeSpan.FromSeconds(1)).IsFullDocument && tempoFull.Until(TimeSpan.FromSeconds(1)).Channels.SequenceEqual([1, 2]),
            "full-midi-range-retains-channel-ownership");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool rejected = false;
            try { MidiAuditionSequence.CreateFull(original, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            check(rejected, "full-midi-preparation-honors-cancellation");
        }
        var changing = MidiReader.Read(File(1,
            [new(0, 0xb0, 0, 1), new(0, 0xb0, 32, 2), new(0, 0xc0, 24), new(0, 0xb0, 7, 77), new(0, 0xb0, 64, 127),
             new(0, 0x90, 60, 91), new(480, 0x80, 60, 0), new(480, 0xb0, 0, 3), new(480, 0xc0, 40),
             new(480, 0x90, 64, 82), new(5000, 0x80, 64, 0), new(5000, 0xb0, 64, 0)],
            [new(0, 0xc1, 56), new(0, 0x91, 60, 99), new(5000, 0x81, 60, 0)],
            [new(0, 0x99, 36, 112), new(5000, 0x89, 36, 0)]));
        var mix = MidiAuditionSequence.CreateFull(changing);
        var output = new RecordingMidiOutput();
        using (var player = new MidiAuditionPlayer(() => output))
        {
            player.Play(mix, TimeSpan.FromSeconds(.75));
            check(output.Active.Select(n => n.Channel).Distinct().Order().SequenceEqual([0, 1, 9]), "full-player-chases-all-active-channels-independently");
            player.Pause();
            check(player.IsPaused && output.Active.Length == 0, "full-player-pause-silences-every-channel");
            int restart = output.Messages.Length;
            player.Resume();
            var resumed = output.Messages.Skip(restart).ToArray();
            check(output.Active.Contains((0, 60)) && output.Active.Contains((0, 64)) && output.Active.Contains((1, 60)) && output.Active.Contains((9, 36)),
                "full-player-resume-keeps-sustained-notes-same-pitch-on-different-channels-and-drums");
            var programs = new Dictionary<int, int>(); var banks = new Dictionary<int, int>();
            var voices = new List<(int Channel, int Pitch, int Program, int Bank)>();
            foreach (uint message in resumed)
            {
                int channel = Channel(message);
                if (Kind(message) == 0xc0) programs[channel] = A(message);
                if (Kind(message) == 0xb0 && A(message) == 0) banks[channel] = B(message);
                if (On(message)) voices.Add((channel, A(message), programs.GetValueOrDefault(channel), banks.GetValueOrDefault(channel)));
            }
            check(voices.Contains((1, 60, 24, 1)) && voices.Contains((1, 64, 40, 3)) && voices.Contains((2, 60, 56, 0)),
                "full-player-resume-restores-held-note-original-program-and-bank-before-current-program");
            check(resumed.Any(m => Kind(m) == 0xb0 && Channel(m) == 1 && A(m) == 7 && B(m) == 77)
                && resumed.Last(m => Kind(m) == 0xc0 && Channel(m) == 1) == MidiAuditionSequence.Message(12, 1, 40),
                "full-player-resume-restores-volume-and-current-program-for-future-notes");
            int stopAt = output.Messages.Length; player.Stop();
            var stopped = output.Messages.Skip(stopAt).ToArray();
            check(new[] { 1, 2, 10 }.All(channel => new[] { 64, 66, 120, 123 }.All(control => stopped.Any(m => Channel(m) == channel
                && Kind(m) == 0xb0 && A(m) == control && B(m) == 0))) && output.Active.Length == 0,
                "full-player-stop-releases-pedals-and-all-notes-on-every-channel");
            player.Play(mix); player.Play(MidiAuditionSequence.Create(original, new(0, 2, 40)));
            check(output.Active.All(n => n.Channel == 1), "full-to-isolated-source-switch-releases-old-channels");
            player.Play(full, full.Duration - TimeSpan.FromMilliseconds(30));
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (player.IsPlaying && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
            check(!player.IsPlaying && output.Active.Length == 0, "full-player-natural-end-silences-all-channels");
        }
        check(output.IsDisposed && output.Active.Length == 0, "full-player-dispose-silences-and-closes-transport");
        int opened = 0; bool replaced = false; MidiPlaybackErrorEventArgs? lateError = null;
        using (var raced = new MidiAuditionPlayer(() => new RecordingMidiOutput { FailOnNoteOn = opened++ == 0 }))
        {
            // The first failure releases the player gate before raising events. Replace the source
            // during StateChanged, reproducing a new owner before the old PlaybackFailed event.
            raced.StateChanged += (_, _) =>
            {
                if (!replaced && !raced.IsPlaying && raced.Sequence is not null)
                { replaced = true; raced.Play(full); }
            };
            raced.PlaybackFailed += (_, error) => lateError = error;
            try { raced.Play(full); } catch (InvalidOperationException) { }
            check(lateError is not null && lateError.Generation < raced.PlaybackGeneration && raced.IsPlaying,
                "failure-generation-is-captured-under-gate-before-a-new-playback-owner");
            long playingGeneration = raced.PlaybackGeneration;
            raced.Stop(releaseOutput: true);
            check(raced.PlaybackGeneration > playingGeneration && !raced.IsPlaying, "stop-invalidates-old-callback-generation-and-releases-device");
        }
        check(original.Notes.Count == 4 && original.ChannelEvents.Count == 4, "full-playback-does-not-mutate-source-document");
    }
}
