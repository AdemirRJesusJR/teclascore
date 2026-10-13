using Teclas.Core;
using static MidiFixtures;

internal static class OffsetChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var document = MidiReader.Read(File(0,
            [new Event(0, 0x90, 60, 85), new Event(480, 0x80, 60, 0), new Event(960, 0x90, 67, 90), new Event(1440, 0x80, 67, 0)]));
        var sequence = MidiAuditionSequence.Create(document, new(0, 1, 0), false);
        var output = new RecordingMidiOutput();
        using var player = new MidiAuditionPlayer(() => output);
        player.Play(sequence, TimeSpan.FromSeconds(1.1));
        check(player.IsPlaying && output.Active.Any(note => note.Pitch == 67)
            && !output.Messages.Any(message => (message & 0xF0) == 0x90 && ((message >> 8) & 127) == 60 && ((message >> 16) & 127) > 0),
            "offset-play-restores-target-without-sounding-skipped-first-note");
        check(player.Position >= TimeSpan.FromSeconds(1.1), "offset-play-uses-original-timeline");
        player.Play(sequence, sequence.Duration + TimeSpan.FromSeconds(5));
        check(!player.IsPlaying && !player.IsPaused && player.Position == sequence.Duration && output.Active.Length == 0,
            "offset-past-end-is-silent-and-clamped");
        player.Play(sequence, TimeSpan.FromSeconds(-1));
        check(player.IsPlaying && output.Active.Any(note => note.Pitch == 60), "negative-offset-clamps-to-start");
        player.Stop();
        var region = sequence.Until(TimeSpan.FromSeconds(1));
        check(region.Duration == TimeSpan.FromSeconds(1) && region.Events.All(item => item.At < TimeSpan.FromSeconds(1))
            && !region.Events.Any(item => (item.Message & 0xF0) == 0x90 && ((item.Message >> 8) & 127) == 67),
            "region-excludes-first-attack-of-next-measure");
        check(region.Events.SequenceEqual(sequence.Events.Where(item => item.At < TimeSpan.FromSeconds(1)))
            && region.LeadingSilenceTrimmed == sequence.LeadingSilenceTrimmed,
            "region-preserves-controller-history-and-original-timing");
        check(ReferenceEquals(sequence.Until(sequence.Duration + TimeSpan.FromSeconds(5)), sequence), "region-end-after-source-keeps-existing-duration");
        player.Play(region, region.Duration);
        check(!player.IsPlaying && output.Active.Length == 0, "region-end-remains-silent");
        var pedal = MidiAuditionSequence.Create(Pedal(), new(0, 1, 0));
        player.Play(pedal, TimeSpan.FromSeconds(.75));
        check(player.IsPlaying && output.Active.Any(note => note.Pitch == 60), "offset-play-restores-sustained-tail");
        player.Stop();
    }
}
