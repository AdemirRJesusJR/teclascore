using System.Security.Cryptography;
using Teclas.Core;

internal static class MetronomeChecks
{
    /// <summary>Synthetic MIDI shaped like MuseScore's export: pickup as 1/4, repeat re-emitting 4/4, then 6/8.</summary>
    internal static object Run(string work, Action<bool, string> check)
    {
        var midi = MidiReader.Read(SyntheticMidi(480, 500000,
            [(0, 1, 2), (480, 4, 2), (4320, 4, 2), (8160, 6, 3)], endTick: 9600));
        check(midi.TimeSignatures.Select(s => (s.Tick, s.Numerator, s.Denominator)).SequenceEqual([(0L, 1, 4), (480L, 4, 4), (4320L, 4, 4), (8160L, 6, 8)]), "MIDI time signatures");
        var beats = ScoreMetronome.BuildBeats(midi);
        check(beats.Count == 1 + 8 + 8 + 2, "Beat count across pickup, repeat and compound meter");
        check(!beats[0].Accent && beats[0].BeatInBar == 4 && beats[0].BeatsInBar == 4, "Pickup is the last beat of the following bar, without accent");
        check(beats[1].Accent && Math.Abs(beats[1].Seconds - .5) < 1e-9, "First full bar starts after the pickup");
        check(beats.Where(b => b.Accent).Select(b => Math.Round(b.Seconds, 6)).SequenceEqual([.5, 2.5, 4.5, 6.5, 8.5]), "Downbeats on every bar line, including the repeat");
        check(Math.Abs(beats[^1].Seconds - 9.25) < 1e-9 && beats[^2].Accent && beats[^1].BeatsInBar == 2, "6/8 pulses in dotted quarters");

        var start = ScoreMetronome.CountInAt(beats, 0)!;
        check(start.Beats == 3 && start.StartSeconds == 0 && start.Accents.SequenceEqual([true, false, false]), "Pickup count-in: one, two, three, then the pickup");
        check(Math.Abs(start.IntervalSeconds - .5) < 1e-9, "Count-in follows the beat interval");
        var downbeat = ScoreMetronome.CountInAt(beats, 2.5)!;
        check(downbeat.Beats == 4 && downbeat.Accents[0], "Count-in from a downbeat uses a full bar");
        var midBeat = ScoreMetronome.CountInAt(beats, 3.2)!;
        check(Math.Abs(midBeat.StartSeconds - 3.0) < 1e-9 && midBeat.Beats == 5 && midBeat.Accents.SequenceEqual([true, false, false, false, true]), "Start between beats snaps to beat 2 and counts a bar plus one");
        check(ScoreMetronome.CountInAt([], 1) is null, "No beats, no count-in");

        var wave = ScoreMetronome.CreateCountInWave(start, .05);
        check(wave.Length == 44 + (int)Math.Ceiling((1.5 - .05) * ScoreMetronome.CountInSampleRate) * 2, "Count-in length is the beats minus the lead");

        var tempo = MidiReader.Read(SyntheticMidi(480, 1000000, [(0, 2, 2)], endTick: 1920, tempoChangeTick: 960, secondTempo: 500000));
        var tempoBeats = ScoreMetronome.BuildBeats(tempo);
        check(tempoBeats.Select(b => Math.Round(b.Seconds, 6)).SequenceEqual([0, 1, 2, 2.5]), "Tempo changes move the beats");

        string source = Path.Combine(work, "metronomo-silencio.wav"), mixed = Path.Combine(work, "metronomo-mix.wav");
        WriteSilence(source, 44100, 2, 3);
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var info = ScoreMetronome.MixClicks(source, mixed, [new(.5, true, 1, 2), new(1.5, false, 2, 2)], CancellationToken.None);
        check(sourceHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), "Source audio preserved");
        check(Math.Abs(info.Duration.TotalSeconds - 3) < 1e-6 && ScoreExporter.ReadWaveInfo(mixed).Channels == 2, "Mixed audio keeps format and duration");
        double firstClick = FirstLoudSecond(mixed);
        check(Math.Abs(firstClick - .5) < .003, "Click starts on the beat sample");
        try { ScoreMetronome.MixClicks(source, source, [], CancellationToken.None); check(false, "Overwrite accepted"); } catch (InvalidOperationException) { check(true, "Overwrite rejected"); }
        return new { beats = beats.Count, countIn = start.Beats, firstClick };
    }

    /// <summary>Native stage: MuseScore's MIDI drives the beats of the rendered study audio.</summary>
    internal static async Task<object> RunNative(string work, string musescore, string native, Action<bool, string> check)
    {
        var service = new ScoreStudyAudio(musescore, Path.Combine(work, "cache"));
        var results = new List<object>();
        foreach (var speed in new[] { 1d, .5 })
        {
            var beats = await service.PrepareBeatsAsync(native, speed, null, CancellationToken.None);
            // native score: 4/4, measure 1 at 120 bpm, measure 2 at 90 bpm, both repeated.
            check(beats.Count == 16 && beats.Count(b => b.Accent) == 4, "Native beats include the repeat");
            check(Math.Abs(beats[4].Seconds - 2 / speed) < .02 && Math.Abs(beats[5].Seconds - beats[4].Seconds - (60d / 90) / speed) < .02, "Native beats follow tempo and speed");
            var audio = await service.PrepareAsync(native, speed, null, CancellationToken.None);
            var withClicks = await service.PrepareMetronomeAudioAsync(audio, beats, null, CancellationToken.None);
            check(withClicks.Path != audio.Path && File.Exists(audio.Path), "Metronome audio is a separate cache file");
            check(Math.Abs(withClicks.Duration.TotalSeconds - audio.Duration.TotalSeconds) < 1e-6, "Metronome keeps the audio length");
            var again = await service.PrepareMetronomeAudioAsync(audio, beats, null, CancellationToken.None);
            check(again.Path == withClicks.Path, "Metronome audio is reused");
            results.Add(new { speed, beats = beats.Count, path = withClicks.Path });
        }
        // Copies for other editors and image sharing; an existing PNG is never replaced.
        var exporter = new ScoreExporter(musescore, Path.Combine(work, "cache"));
        string copies = Path.Combine(work, "copias"); Directory.CreateDirectory(copies);
        var xml = await exporter.ExportCopyAsync(native, Path.Combine(copies, "Estudo.musicxml"), null, CancellationToken.None);
        check(xml.Count == 1 && System.Xml.Linq.XDocument.Load(xml[0]).Root?.Name.LocalName == "score-partwise", "MusicXML copy is a complete score");
        File.WriteAllText(Path.Combine(copies, "Estudo.png"), "existing");
        var png = await exporter.ExportCopyAsync(native, Path.Combine(copies, "Estudo.png"), null, CancellationToken.None);
        check(png.Count >= 1 && File.ReadAllText(Path.Combine(copies, "Estudo.png")) == "existing" && png.All(path => File.ReadAllBytes(path).Take(4).SequenceEqual(new byte[] { 137, 80, 78, 71 })),
            "PNG pages are saved without replacing an existing file");
        results.Add(new { musicXml = xml[0], pngPages = png.Count });
        return results;
    }

    private static byte[] SyntheticMidi(int ppq, int tempo, (long Tick, int Numerator, int Power)[] signatures, long endTick, long tempoChangeTick = -1, int secondTempo = 0)
    {
        var events = new List<(long Tick, byte[] Data)> { (0, [0xff, 0x51, 3, (byte)(tempo >> 16), (byte)(tempo >> 8), (byte)tempo]) };
        if (tempoChangeTick >= 0) events.Add((tempoChangeTick, [0xff, 0x51, 3, (byte)(secondTempo >> 16), (byte)(secondTempo >> 8), (byte)secondTempo]));
        events.AddRange(signatures.Select(s => (s.Tick, new byte[] { 0xff, 0x58, 4, (byte)s.Numerator, (byte)s.Power, 24, 8 })));
        events.Add((0, [0x90, 60, 80])); events.Add((endTick, [0x80, 60, 0]));
        var track = new List<byte>(); long last = 0;
        foreach (var (tick, data) in events.OrderBy(e => e.Tick).ThenBy(e => e.Data[0] == 0x80 ? 1 : 0))
        {
            long delta = tick - last; last = tick;
            var vlq = new Stack<byte>(); vlq.Push((byte)(delta & 127));
            while ((delta >>= 7) > 0) vlq.Push((byte)(delta & 127 | 128));
            track.AddRange(vlq); track.AddRange(data);
        }
        track.AddRange([0, 0xff, 0x2f, 0]);
        var bytes = new List<byte>("MThd"u8.ToArray()) { 0, 0, 0, 6, 0, 0, 0, 1, (byte)(ppq >> 8), (byte)ppq };
        bytes.AddRange("MTrk"u8.ToArray()); bytes.AddRange([(byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count]);
        bytes.AddRange(track);
        return bytes.ToArray();
    }

    private static void WriteSilence(string path, int rate, short channels, int seconds)
    {
        int length = rate * channels * 2 * seconds;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(length + 36); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write(channels);
        writer.Write(rate); writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16); writer.Write("data"u8); writer.Write(length);
        writer.Write(new byte[length]);
    }

    private static double FirstLoudSecond(string path)
    {
        var info = ScoreExporter.ReadWaveInfo(path);
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = info.DataOffset;
        for (long frame = 0; frame < info.DataLength / (info.Channels * 2); frame++)
        {
            short left = reader.ReadInt16(); for (int c = 1; c < info.Channels; c++) reader.ReadInt16();
            if (Math.Abs((int)left) > 300) return frame / (double)info.SampleRate;
        }
        return double.NaN;
    }
}
