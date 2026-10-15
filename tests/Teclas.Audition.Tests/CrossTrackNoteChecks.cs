using Teclas.Core;
using static MidiFixtures;

internal static class CrossTrackNoteChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // Both tracks address channel 1. The note releases belong to track 2 while
        // note attacks belong to track 1; source analysis intentionally stays track-based.
        var document = MidiReader.Read(File(1,
            [new(0, 0xc0, 24), new(0, 0x90, 60, 91), new(240, 0x90, 64, 83), new(960, 0xff, 1, 1, 0x78)],
            [new(480, 0x80, 60, 37), new(720, 0x90, 64, 0), new(960, 0xb0, 7, 80)]));
        var original = document.PlaybackEvents!;
        check(original.Count == 6 && original.Count(item => item.Kind is 8 or 9) == 4,
            "reader-retains-original-note-messages-across-shared-channel-tracks");
        check(document.ChannelEvents.Count == 2 && document.ChannelEvents.All(item => item.Kind >= 10)
            && document.Notes.Count == 2 && document.Notes.All(note => note.Track == 0 && note.End == 960),
            "raw-playback-events-do-not-change-track-analysis-or-controller-contracts");
        var full = MidiAuditionSequence.CreateFull(document);
        uint release = MidiAuditionSequence.Message(8, 1, 60, 37);
        uint zeroVelocityRelease = MidiAuditionSequence.Message(9, 1, 64, 0);
        check(full.Events.Any(item => item.At == TimeSpan.FromSeconds(.5) && item.Message == release),
            "full-midi-keeps-cross-track-note-off-at-original-time-and-release-velocity");
        check(full.Events.Any(item => item.At == TimeSpan.FromSeconds(.75) && item.Message == zeroVelocityRelease),
            "full-midi-keeps-zero-velocity-note-on-release-without-rewriting-it");
        check(!full.Events.Any(item => item.At == TimeSpan.FromSeconds(1) && (item.Message & 0xf0) == 0x80),
            "full-midi-does-not-invent-delayed-note-offs-from-analysis-notes");
        int initialCount = full.Events.Count - original.Count - 3 * full.Channels.Count;
        check(full.Events.Skip(initialCount).Take(original.Count).Select(item => item.Message)
            .SequenceEqual(original.Select(item => MidiAuditionSequence.Message(item.Kind, item.Channel, item.Data1, item.Data2))),
            "full-midi-retains-every-original-channel-message-once-in-merge-order");
        // Stretch only the fake transport's timeline so host load cannot cross the
        // next release while asserting seek/resume. The original times were checked above.
        var playback = MidiAuditionSequence.CreateFull(document with { Ppq = 4 });
        using (var output = new RecordingMidiOutput())
        using (var player = new MidiAuditionPlayer(() => output))
        {
            player.Play(playback, TimeSpan.FromSeconds(75));
            check(!output.Active.Contains((0, 60)) && output.Active.Contains((0, 64)),
                "full-seek-observes-note-off-from-another-track");
            player.Pause(); player.Resume();
            check(!output.Active.Contains((0, 60)) && output.Active.Contains((0, 64)),
                "full-resume-does-not-revive-a-cross-track-released-note");
            player.Seek(TimeSpan.FromSeconds(105));
            check(output.Active.Length == 0, "full-seek-observes-zero-velocity-cross-track-release");
        }
        var synthetic = new MidiDocument("synthetic.mid", "synthetic", 0, 480,
            [new MidiNote(0, 2, 40, 67, 85, 0, 480)], [], [])
        { ChannelEvents = [new MidiChannelEvent(0, 0, 0, 2, 12, 40, 0)], EndTick = 480 };
        var fallback = MidiAuditionSequence.CreateFull(synthetic);
        check(synthetic.PlaybackEvents is null && fallback.Events.Any(item => item.At == TimeSpan.FromSeconds(.5)
            && item.Message == MidiAuditionSequence.Message(8, 2, 67)), "synthetic-midi-without-original-events-retains-notes-fallback");
        check(document.PlaybackEvents!.SequenceEqual(original) && document.Notes.All(note => note.End == 960),
            "full-playback-never-mutates-original-events-or-analysis-notes");
    }
}
