using System.Text.Json;
using Teclas.Core;
using static MidiFixtures;

var checks = new Dictionary<string, bool>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks[name] = true; Console.WriteLine("PASS " + name);
}
static bool On(uint message) => (message & 0xF0) == 0x90 && ((message >> 16) & 127) > 0;
static bool Off(uint message) => (message & 0xF0) == 0x80 || (message & 0xF0) == 0x90 && ((message >> 16) & 127) == 0;
static int A(uint message) => (int)((message >> 8) & 127);
static int B(uint message) => (int)((message >> 16) & 127);
static int Channel(uint message) => (int)(message & 15) + 1;
static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.000001;
static async Task Wait(Func<bool> predicate, string reason, int timeout = 2500)
{
    var start = System.Diagnostics.Stopwatch.StartNew();
    while (!predicate() && start.ElapsedMilliseconds < timeout) await Task.Delay(5);
    if (!predicate()) throw new TimeoutException(reason);
}

try
{
    var zero = FormatZero();
    Check(zero.Format == 0 && zero.Sources.Count == 4, "format-0-keeps-channel-and-program-sources");
    var first = MidiAuditionSequence.Create(zero, new(0, 1, 0));
    var firstOns = first.Events.Where(item => On(item.Message)).ToArray();
    Check(firstOns.Length == 1 && A(firstOns[0].Message) == 60 && B(firstOns[0].Message) == 101
        && first.Events.All(item => Channel(item.Message) == 1), "source-key-isolates-channel-program-and-velocity");
    var later = MidiAuditionSequence.Create(zero, new(0, 1, 24));
    Check(later.Events.Where(item => On(item.Message)).Select(item => A(item.Message)).SequenceEqual([67])
        && later.Events.Any(item => (item.Message & 0xF0) == 0xC0 && A(item.Message) == 24)
        && !later.Events.Any(item => (item.Message & 0xF0) == 0xC0 && A(item.Message) == 0), "later-program-does-not-replay-earlier-instrument");
    var drums = MidiAuditionSequence.Create(zero, new(0, 10, 0));
    Check(drums.Events.Where(item => On(item.Message)).All(item => Channel(item.Message) == 10
        && A(item.Message) == 36 && B(item.Message) == 112), "percussion-keeps-channel-10-and-original-velocity");
    var tracks = MidiReader.Read(File(1,
        [new(0, 0x90, 60, 70), new(480, 0x80, 60, 0)],
        [new(0, 0x90, 67, 85), new(480, 0x80, 67, 0)]));
    Check(MidiAuditionSequence.Create(tracks, new(1, 1, 0)).Events.Where(item => On(item.Message))
        .Select(item => A(item.Message)).SequenceEqual([67]), "source-key-isolates-track-on-shared-channel");
    Check(Near(first.Events.First(item => Off(item.Message)).At.TotalSeconds, 0.5), "default-tempo-is-120-bpm");
    var tempo = MidiReader.Read(File(1,
        [new(480, 0xFF, 0x51, 3, 0x0F, 0x42, 0x40)],
        [new(0, 0x90, 60, 80), new(960, 0x80, 60, 0)]));
    Check(tempo.TempoChanges.Count == 1 && tempo.TempoChanges[0].MicrosecondsPerQuarter == 1_000_000,
        "reader-preserves-global-tempo-map");
    Check(Near(MidiAuditionSequence.Create(tempo, new(1, 1, 0)).Events.First(item => Off(item.Message)).At.TotalSeconds, 1.5),
        "tempo-change-integrates-elapsed-time-across-tracks");
    var late = MidiReader.Read(File(1,
        [new(480, 0xFF, 0x51, 3, 0x0F, 0x42, 0x40)],
        [new(960, 0x90, 64, 70), new(1440, 0x80, 64, 0)]));
    var trimmed = MidiAuditionSequence.Create(late, new(1, 1, 0));
    var complete = MidiAuditionSequence.Create(late, new(1, 1, 0), false);
    Check(Near(trimmed.LeadingSilenceTrimmed.TotalSeconds, 1.5) && trimmed.Events.First(item => On(item.Message)).At == TimeSpan.Zero
        && Near(trimmed.Events.First(item => Off(item.Message)).At.TotalSeconds, 1), "late-start-trim-preserves-tempo-adjusted-note-duration");
    Check(Near(complete.Events.First(item => On(item.Message)).At.TotalSeconds, 1.5)
        && Near(complete.Events.First(item => Off(item.Message)).At.TotalSeconds, 2.5), "optional-untrimmed-audition-retains-original-start");

    var controlled = MidiReader.Read(File(1,
        [new(0, 0xB0, 7, 45), new(0, 0xB0, 10, 20), new(0, 0xB0, 11, 80),
         new(0, 0xE0, 0, 96), new(0, 0xD0, 30), new(0, 0xB1, 7, 9)],
        [new(960, 0x90, 60, 90), new(1440, 0x80, 60, 0)]));
    var controls = MidiAuditionSequence.Create(controlled, new(1, 1, 0));
    var beforeNote = controls.Events.TakeWhile(item => !On(item.Message)).Select(item => item.Message).ToArray();
    Check(controlled.ChannelEvents.Any(item => item.Kind == 11 && item.Channel == 1 && item.Data1 == 7 && item.Data2 == 45)
        && B(beforeNote.Last(message => (message & 0xF0) == 0xB0 && A(message) == 7)) == 45
        && B(beforeNote.Last(message => (message & 0xF0) == 0xB0 && A(message) == 10)) == 20
        && B(beforeNote.Last(message => (message & 0xF0) == 0xB0 && A(message) == 11)) == 80,
        "pre-note-global-volume-pan-expression-restored");
    Check(beforeNote.Any(message => (message & 0xF0) == 0xE0 && B(message) == 96)
        && beforeNote.Any(message => (message & 0xF0) == 0xD0 && A(message) == 30)
        && controls.Events.All(item => Channel(item.Message) == 1), "bend-pressure-and-channel-isolation-preserved");
    var pedal = MidiAuditionSequence.Create(Pedal(), new(0, 1, 0));
    Check(pedal.Events.Any(item => (item.Message & 0xF0) == 0xB0 && A(item.Message) == 64 && B(item.Message) == 0
        && Near(item.At.TotalSeconds, 1)), "sustain-release-after-last-note-off-is-retained");

    var outputs = new List<RecordingMidiOutput>();
    using (var player = new MidiAuditionPlayer(() => { var output = new RecordingMidiOutput(); lock (outputs) outputs.Add(output); return output; }))
    {
        RecordingMidiOutput Current() { lock (outputs) return outputs[^1]; }
        var held = MidiAuditionSequence.Create(Held(), new(0, 1, 0));
        player.Play(held);
        await Wait(() => { lock (outputs) return outputs.Count > 0 && outputs[^1].Active.Any(); }, "The player did not send the first selected note.");
        await Task.Delay(40);
        Check(player.IsPlaying && !player.IsPaused && Current().Active.Any(note => note.Pitch == 60), "player-sends-selected-notes-and-advances");
        int beforePauseReset = Current().ResetCount;
        player.Pause(); double pausedAt = player.Position.TotalSeconds;
        await Task.Delay(80);
        Check(player.IsPaused && !player.IsPlaying && Current().Active.Length == 0 && Current().ResetCount > beforePauseReset
            && Math.Abs(player.Position.TotalSeconds - pausedAt) < 0.02, "pause-silences-notes-and-holds-position");
        var silence = Current().Messages;
        Check(new[] { 64, 66, 120, 123 }.All(control => silence.Any(message => (message & 0xF0) == 0xB0 && A(message) == control && B(message) == 0)),
            "pause-releases-sustain-sostenuto-and-all-notes");
        player.Resume();
        await Wait(() => player.IsPlaying && Current().Active.Any(note => note.Pitch == 60), "Resume did not restore a note crossing the paused position.");
        Check(!player.IsPaused && Current().Active.Any(note => note.Pitch == 60), "resume-restores-active-note-without-restarting");
        int beforeSeek = Current().Messages.Length;
        player.Seek(TimeSpan.FromSeconds(4));
        await Wait(() => player.Position.TotalSeconds >= 4 && Current().Active.Any(), "Seek did not restore the active note.");
        Check(player.Position.TotalSeconds < 4.2 && Current().Active.All(note => note.Pitch == 60)
            && !Current().Messages.Skip(beforeSeek).Any(message => On(message) && A(message) == 62), "seek-restores-only-notes-active-at-target");
        player.Stop();
        Check(!player.IsPlaying && !player.IsPaused && player.Position == TimeSpan.Zero && outputs.All(output => output.Active.Length == 0),
            "stop-rewinds-and-silences-every-open-transport");
        player.Play(held);
        await Wait(() => Current().Active.Any(), "Second play did not start.");
        var switched = MidiAuditionSequence.Create(zero, new(0, 2, 40));
        player.Play(switched);
        await Wait(() => Current().Active.Any(note => note.Channel == 1 && note.Pitch == 72), "Switch did not start the new source.");
        Check(outputs.SelectMany(output => output.Active).All(note => note.Channel == 1 && note.Pitch == 72),
            "switching-source-releases-previous-channel-notes");
        player.Stop();
        player.Play(pedal); player.Pause(); player.Seek(TimeSpan.FromSeconds(0.75)); player.Resume();
        await Wait(() => Current().Active.Any(note => note.Pitch == 60), "Seeking into sustained tail did not restore the sustained note.");
        Check(Current().Active.Any(note => note.Pitch == 60), "seek-restores-pedal-sustained-note-after-note-off");
        player.Stop();
        player.Play(first);
        await Wait(() => !player.IsPlaying && player.Position == player.Duration, "Natural sequence end did not stop the transport.");
        Check(!player.IsPaused && Current().Active.Length == 0, "natural-end-silences-notes-and-retains-completed-position");
    }
    Check(outputs.All(output => output.IsDisposed && output.Active.Length == 0), "dispose-closes-transports-and-silences-notes");

    var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var broken = new MidiAuditionPlayer(() => new RecordingMidiOutput { FailOnNoteOn = true }))
    {
        broken.PlaybackFailed += (_, error) => failure.TrySetResult(error.Message);
        try { broken.Play(first); } catch (InvalidOperationException) { /* Synchronous device failures also raise PlaybackFailed. */ }
        string message = await failure.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!string.IsNullOrWhiteSpace(message) && !broken.IsPlaying, "transport-failure-reports-error-and-stops-playback");
    }
    await SpeedChecks.Run(Check);
    OffsetChecks.Run(Check);
    MixChecks.Run(Check);
    await FullPlaybackChecks.Run(Check);
    CrossTrackNoteChecks.Run(Check);
    var cacheTimings = await PreparationCacheChecks.Run(Check);
    Console.WriteLine($"Audition QA passed: {checks.Count} checks; synthetic MIDIs and fake transports only.");
    if (args.Length > 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        System.IO.File.WriteAllText(args[0], JsonSerializer.Serialize(new { status = "passed", checks, cacheTimings }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    if (args.Length > 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        System.IO.File.WriteAllText(args[0], JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
    }
    Environment.ExitCode = 1;
}
