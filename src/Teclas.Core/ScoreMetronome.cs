using System.Buffers.Binary;

namespace Teclas.Core;

/// <summary>Seconds are actual rendered-audio seconds; the player's logical position is these seconds multiplied by the study speed.</summary>
public sealed record MetronomeBeat(double Seconds, bool Accent, int BeatInBar, int BeatsInBar);
/// <summary>Clicks before the music, ending at StartSeconds (a beat of the rendered audio).</summary>
public sealed record MetronomeCountIn(int Beats, double IntervalSeconds, double StartSeconds, IReadOnlyList<bool> Accents);

/// <summary>Builds beats from MuseScore's playback MIDI and mixes short synthetic clicks; the score and its audio cache stay unchanged.</summary>
public static class ScoreMetronome
{
    public const int MaximumBeats = 200_000;
    public const int CountInSampleRate = 44100;
    private const double ClickSeconds = .035;

    public static IReadOnlyList<MetronomeBeat> BuildBeats(MidiDocument midi)
    {
        ArgumentNullException.ThrowIfNull(midi);
        if (midi.Ppq <= 0 || midi.EndTick <= 0) return [];
        var signatures = midi.TimeSignatures.GroupBy(s => s.Tick).Select(g => g.Last()).Where(s => s.Tick < midi.EndTick).OrderBy(s => s.Tick).ToList();
        if (signatures.Count == 0 || signatures[0].Tick > 0) signatures.Insert(0, new(0, 4, 4));
        var tempo = new TempoMap(midi.Ppq, midi.TempoChanges);
        var beats = new List<MetronomeBeat>();
        for (int i = 0; i < signatures.Count; i++)
        {
            var signature = signatures[i];
            long start = signature.Tick, end = i + 1 < signatures.Count ? signatures[i + 1].Tick : midi.EndTick;
            var meter = Meter.From(signature, midi.Ppq);
            // MuseScore exports a pickup or shortened bar as its own one-bar signature (1/4 before 4/4).
            // Count it with the following meter's pulse, ending on that meter's last beats.
            if (i + 1 < signatures.Count && Math.Abs(end - start - meter.BarTicks) < .5)
            {
                var next = Meter.From(signatures[i + 1], midi.Ppq);
                double pulses = meter.BarTicks / next.BeatTicks;
                if (next.BarTicks > meter.BarTicks + .5 && Math.Abs(pulses - Math.Round(pulses)) < 1e-6 && pulses >= 1)
                {
                    int count = (int)Math.Round(pulses);
                    for (int beat = 0; beat < count; beat++)
                    {
                        int number = next.BeatsPerBar - count + beat + 1;
                        Add(start + beat * next.BeatTicks, i > 0 && beat == 0, i == 0 ? number : beat + 1, i == 0 ? next.BeatsPerBar : count);
                    }
                    continue;
                }
            }
            for (double tick = start; tick < end - .5; tick += meter.BeatTicks)
            {
                double inBar = (tick - start) % meter.BarTicks;
                int number = (int)Math.Round(inBar / meter.BeatTicks) % meter.BeatsPerBar + 1;
                Add(tick, number == 1, number, meter.BeatsPerBar);
            }
        }
        return beats;

        void Add(double tick, bool accent, int number, int perBar)
        {
            if (beats.Count >= MaximumBeats) throw new InvalidDataException("A partitura é longa demais para preparar o metrônomo.");
            beats.Add(new(tempo.Seconds(tick), accent, number, perBar));
        }
    }

    /// <summary>Snaps a start position to the beat at or before it and counts in up to that beat; null without beats.</summary>
    public static MetronomeCountIn? CountInAt(IReadOnlyList<MetronomeBeat> beats, double seconds)
    {
        if (beats.Count == 0 || !double.IsFinite(seconds)) return null;
        int index = 0;
        for (int low = 0, high = beats.Count - 1; low <= high;)
        {
            int middle = low + (high - low) / 2;
            if (beats[middle].Seconds <= seconds + .02) { index = middle; low = middle + 1; }
            else high = middle - 1;
        }
        if (seconds > beats[^1].Seconds + Interval(beats.Count - 1) * 2) return null;
        var beat = beats[index];
        double interval = Interval(index);
        if (!double.IsFinite(interval) || interval < .1 || interval > 4) return null;
        int perBar = Math.Clamp(beat.BeatsInBar, 1, 12);
        int count = beat.BeatInBar == 1 ? perBar : beat.BeatInBar - 1;
        while (count < 2) count += perBar;
        count = Math.Min(count, 12);
        var accents = new bool[count];
        for (int k = 0; k < count; k++)
        {
            int number = ((beat.BeatInBar - 1 - (count - k)) % perBar + perBar) % perBar + 1;
            accents[k] = number == 1;
        }
        return new(count, interval, Math.Min(beat.Seconds, seconds), accents);

        double Interval(int at) => at + 1 < beats.Count ? beats[at + 1].Seconds - beats[at].Seconds
            : at > 0 ? beats[at].Seconds - beats[at - 1].Seconds : .5;
    }

    /// <summary>PCM16 mono WAV for the count-in. Lead shortens the tail so the caller can start the music player slightly early.</summary>
    public static byte[] CreateCountInWave(MetronomeCountIn countIn, double leadSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(countIn);
        if (countIn.Beats is < 1 or > 12 || !double.IsFinite(countIn.IntervalSeconds) || countIn.IntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(countIn));
        double lastClickEnd = (countIn.Beats - 1) * countIn.IntervalSeconds + ClickSeconds;
        double length = Math.Max(lastClickEnd, countIn.Beats * countIn.IntervalSeconds - Math.Max(0, leadSeconds));
        int frames = (int)Math.Ceiling(length * CountInSampleRate);
        var samples = new float[frames];
        for (int k = 0; k < countIn.Beats; k++)
        {
            var click = Click(CountInSampleRate, k < countIn.Accents.Count && countIn.Accents[k]);
            int offset = (int)Math.Round(k * countIn.IntervalSeconds * CountInSampleRate);
            for (int i = 0; i < click.Length && offset + i < frames; i++) samples[offset + i] += click[i];
        }
        var bytes = new byte[44 + frames * 2];
        WriteHeader(bytes, 1, CountInSampleRate, (uint)(frames * 2));
        for (int i = 0; i < frames; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + i * 2), (short)Math.Clamp(Math.Round(samples[i] * 32767), short.MinValue, short.MaxValue));
        return bytes;
    }

    /// <summary>Writes a new PCM16 WAV with clicks added to every channel; the source audio is not changed.</summary>
    public static WaveInfo MixClicks(string source, string destination, IReadOnlyList<MetronomeBeat> beats, CancellationToken token)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O metrônomo precisa de um arquivo de áudio separado.");
        var info = ScoreExporter.ReadWaveInfo(source);
        if (info.Format != 1 || info.BitsPerSample != 16) throw new InvalidDataException("O metrônomo espera o áudio PCM de 16 bits preparado pelo TeclaScore.");
        var accent = Click((int)info.SampleRate, true); var normal = Click((int)info.SampleRate, false);
        long totalFrames = info.DataLength / (info.Channels * 2L);
        var starts = beats.Select(b => (Frame: (long)Math.Round(b.Seconds * info.SampleRate), b.Accent)).Where(b => b.Frame >= 0 && b.Frame < totalFrames).ToArray();
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var header = new byte[44];
        WriteHeader(header, info.Channels, info.SampleRate, info.DataLength);
        output.Write(header);
        input.Position = info.DataOffset;
        const int chunkFrames = 65536;
        int frameBytes = info.Channels * 2;
        var buffer = new byte[chunkFrames * frameBytes];
        int next = 0;
        for (long frame = 0; frame < totalFrames;)
        {
            token.ThrowIfCancellationRequested();
            int frames = (int)Math.Min(chunkFrames, totalFrames - frame);
            input.ReadExactly(buffer, 0, frames * frameBytes);
            while (next < starts.Length && starts[next].Frame + normal.Length <= frame) next++;
            for (int b = next; b < starts.Length && starts[b].Frame < frame + frames; b++)
            {
                var click = starts[b].Accent ? accent : normal;
                long from = Math.Max(frame, starts[b].Frame), to = Math.Min(frame + frames, starts[b].Frame + click.Length);
                for (long f = from; f < to; f++)
                {
                    float value = click[f - starts[b].Frame];
                    for (int c = 0; c < info.Channels; c++)
                    {
                        var span = buffer.AsSpan((int)(f - frame) * frameBytes + c * 2, 2);
                        int mixed = BinaryPrimitives.ReadInt16LittleEndian(span) + (int)Math.Round(value * 32767);
                        BinaryPrimitives.WriteInt16LittleEndian(span, (short)Math.Clamp(mixed, short.MinValue, short.MaxValue));
                    }
                }
            }
            output.Write(buffer, 0, frames * frameBytes);
            frame += frames;
        }
        output.Flush(true);
        return new(1, info.Channels, info.SampleRate, 16, 44, info.DataLength);
    }

    internal static float[] Click(int sampleRate, bool accent)
    {
        int length = (int)(ClickSeconds * sampleRate);
        double frequency = accent ? 1760 : 1320, amplitude = accent ? .5 : .32;
        var click = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)sampleRate;
            double envelope = Math.Min(1, t / .001) * Math.Exp(-t / .008);
            click[i] = (float)(amplitude * envelope * Math.Sin(2 * Math.PI * frequency * t));
        }
        return click;
    }

    private static void WriteHeader(Span<byte> header, ushort channels, uint sampleRate, uint dataLength)
    {
        "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked(dataLength + 36));
        "WAVEfmt "u8.CopyTo(header[8..]); BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1); BinaryPrimitives.WriteUInt16LittleEndian(header[22..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], sampleRate); BinaryPrimitives.WriteUInt32LittleEndian(header[28..], sampleRate * channels * 2u);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(channels * 2)); BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]); BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataLength);
    }

    private readonly record struct Meter(double BeatTicks, double BarTicks, int BeatsPerBar)
    {
        public static Meter From(MidiTimeSignature signature, int ppq)
        {
            double unit = ppq * 4d / signature.Denominator, bar = unit * signature.Numerator;
            // Compound meters (6/8, 9/8, 12/8…) pulse in dotted beats.
            bool compound = signature.Denominator >= 8 && signature.Numerator > 3 && signature.Numerator % 3 == 0;
            return compound ? new(unit * 3, bar, signature.Numerator / 3) : new(unit, bar, signature.Numerator);
        }
    }

    private sealed class TempoMap
    {
        private readonly List<(long Tick, double Seconds, int Tempo)> segments = [(0, 0, 500000)];
        private readonly int ppq;
        internal TempoMap(int ppq, IReadOnlyList<MidiTempoChange> changes)
        {
            this.ppq = ppq;
            foreach (var change in changes.OrderBy(t => t.Tick).ThenBy(t => t.Track).ThenBy(t => t.Order))
            {
                var previous = segments[^1];
                segments.Add((change.Tick, previous.Seconds + (change.Tick - previous.Tick) * previous.Tempo / (1000000d * ppq), change.MicrosecondsPerQuarter));
            }
        }
        internal double Seconds(double tick)
        {
            var segment = segments.Last(s => s.Tick <= tick);
            return segment.Seconds + (tick - segment.Tick) * segment.Tempo / (1000000d * ppq);
        }
    }
}
