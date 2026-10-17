using Teclas.Core;
using static MidiFixtures;

internal static class MixChecks
{
    private static int Channel(uint message) => (int)(message & 15) + 1;
    private static bool On(uint message) => (message & 0xF0) == 0x90 && ((message >> 16) & 127) > 0;
    private static int A(uint message) => (int)((message >> 8) & 127);

    internal static void Run(Action<bool, string> check)
    {
        // FormatZero: channel 1 switches from program 0 (note 60) to program 24 (note 67 at 0.5 s); channel 2 program 40; drums on 10.
        var zero = FormatZero();
        var piano = new SourceKey(0, 1, 0); var guitar = new SourceKey(0, 1, 24); var violin = new SourceKey(0, 2, 40); var drums = new SourceKey(0, 10, 0);
        var mix = MidiAuditionSequence.CreateMix(zero, [piano, guitar]);
        var ons = mix.Events.Where(item => On(item.Message)).ToArray();
        int guitarChannel = Channel(ons.Single(item => A(item.Message) == 67).Message);
        check(ons.Any(item => A(item.Message) == 60 && Channel(item.Message) == 1 && item.At == TimeSpan.Zero) && guitarChannel != 1 && guitarChannel != 10,
            "mix-moves-a-shared-melodic-channel-to-a-free-one");
        check(mix.Events.Any(item => (item.Message & 0xF0) == 0xC0 && Channel(item.Message) == guitarChannel && A(item.Message) == 24)
            && !mix.Events.Any(item => (item.Message & 0xF0) == 0xC0 && Channel(item.Message) == 1 && A(item.Message) == 24),
            "each-mixed-source-keeps-its-own-program");
        check(mix.Channels.SequenceEqual(new[] { 1, guitarChannel }.Order()) && mix.MixedSources.SequenceEqual([piano, guitar]), "mix-reports-channels-and-sources");
        check(Math.Abs(ons.Single(item => A(item.Message) == 67).At.TotalSeconds - 0.5) < 1e-6, "mix-keeps-each-source-at-its-time");

        var withDrums = MidiAuditionSequence.CreateMix(zero, [violin, drums]);
        check(withDrums.Events.Where(item => On(item.Message)).Any(item => Channel(item.Message) == 10) && withDrums.Channels.Contains(10) && withDrums.Channels.Contains(2),
            "percussion-stays-on-channel-10");
        check(MidiAuditionSequence.CreateMix(zero, [violin]).MixedSources.Count == 0, "single-source-uses-the-normal-sequence");

        var late = MidiReader.Read(File(0,
        [
            new Event(0, 0xC0, 0), new Event(0, 0xC1, 73),
            new Event(960, 0x90, 60, 90), new Event(1440, 0x80, 60, 0),
            new Event(1440, 0x91, 79, 90), new Event(1920, 0x81, 79, 0)
        ]));
        var lateMix = MidiAuditionSequence.CreateMix(late, [new(0, 1, 0), new(0, 2, 73)]);
        var lateOns = lateMix.Events.Where(item => On(item.Message)).ToArray();
        check(Math.Abs(lateMix.LeadingSilenceTrimmed.TotalSeconds - 1.0) < 1e-6 && lateOns[0].At == TimeSpan.Zero
            && Math.Abs(lateOns[1].At.TotalSeconds - 0.5) < 1e-6, "mix-starts-at-the-earliest-first-note");
        check(lateMix.Duration > lateOns[1].At, "mix-lasts-until-the-latest-source-ends");

        var output = new RecordingMidiOutput();
        using var player = new MidiAuditionPlayer(() => output);
        player.Play(mix, TimeSpan.FromSeconds(0.6));
        check(player.IsPlaying && output.Active.Any(note => note.Pitch == 67), "player-plays-a-mix-from-an-offset");
        player.Stop(releaseOutput: true);
        check(output.Active.Length == 0, "stopping-a-mix-silences-every-channel");
        try { MidiAuditionSequence.CreateMix(zero, []); check(false, "empty-mix-accepted"); }
        catch (ArgumentException) { check(true, "empty-mix-rejected"); }
    }
}
