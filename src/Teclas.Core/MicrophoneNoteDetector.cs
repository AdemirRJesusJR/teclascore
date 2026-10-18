namespace Teclas.Core;

/// <summary>
/// Finds which key was struck on an acoustic piano or keyboard from microphone samples, one note at a time (melody
/// practice). Pitch is measured with the YIN method on 2048-sample windows every 1024 samples; a note is reported once,
/// when it starts: after silence, on a clear attack (the sound gets much louder; the pitch is read one window later,
/// when the window holds only the new note), or when a held sound settles on a different key (legato). Octave mistakes of the method are harmless when practice accepts any octave.
/// </summary>
public sealed class MicrophoneNoteDetector
{
    public const int Window = 2048, Hop = 1024;
    private readonly int sampleRate;
    private readonly float[] ring = new float[Window], frame = new float[Window];
    private int filled, sinceAnalysis, write;
    private double previousLevel, noiseFloor = .002;
    private int? sounding, candidate;
    private int candidateFrames;
    private bool attackPending;
    private int refractory;
    public double Level { get; private set; }

    public MicrophoneNoteDetector(int sampleRate = 44100)
    {
        if (sampleRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.sampleRate = sampleRate;
    }

    /// <summary>Adds samples (−1..1); returns the MIDI keys that started in them, usually none or one.</summary>
    public IReadOnlyList<int> Feed(ReadOnlySpan<float> samples)
    {
        var started = new List<int>();
        foreach (float sample in samples)
        {
            ring[write] = sample; write = (write + 1) % Window;
            if (filled < Window) filled++;
            if (++sinceAnalysis < Hop || filled < Window) continue;
            sinceAnalysis = 0;
            // Oldest sample first.
            Array.Copy(ring, write, frame, 0, Window - write); Array.Copy(ring, 0, frame, Window - write, write);
            if (Analyze() is { } pitch) started.Add(pitch);
        }
        return started;
    }

    private int? Analyze()
    {
        double sum = 0; foreach (float s in frame) sum += s * s;
        double level = Math.Sqrt(sum / Window); Level = level;
        double gate = Math.Max(.01, noiseFloor * 4);
        if (level < gate)
        {
            noiseFloor = noiseFloor * .95 + level * .05;
            sounding = null; candidate = null; candidateFrames = 0; attackPending = false; previousLevel = level;
            return null;
        }
        bool attack = level > previousLevel * 1.8 || previousLevel < gate;
        previousLevel = level;
        // The window that catches the start of a note still holds the end of the previous one, so the pitch is read in
        // the next window; and the following windows of the same strike do not count as new attacks.
        if (refractory > 0) { refractory--; attack = false; }
        if (attack && !attackPending) { attackPending = true; return null; }
        var (frequency, clarity) = Yin.Detect(frame, sampleRate);
        if (frequency <= 0 || clarity < .8) return null;
        int pitch = (int)Math.Round(69 + 12 * Math.Log2(frequency / 440));
        if (pitch is < 21 or > 108) return null;
        if (attackPending)
        {
            // A new strike: report at once, even on the same key.
            attackPending = false; sounding = pitch; candidate = null; candidateFrames = 0; refractory = 2;
            return pitch;
        }
        if (pitch == sounding) { candidate = null; candidateFrames = 0; return null; }
        // Legato: a different key held for two windows in a row.
        if (pitch == candidate) candidateFrames++; else { candidate = pitch; candidateFrames = 1; }
        if (candidateFrames < 2) return null;
        sounding = pitch; candidate = null; candidateFrames = 0;
        return pitch;
    }
}

/// <summary>The YIN fundamental-frequency estimator (de Cheveigné and Kawahara, 2002).</summary>
public static class Yin
{
    /// <summary>Returns the fundamental in Hz (0 when none) and clarity 0..1 (1 − aperiodicity).</summary>
    public static (double Frequency, double Clarity) Detect(ReadOnlySpan<float> frame, int sampleRate, double minimum = 50, double maximum = 2000, double threshold = .15)
    {
        int half = frame.Length / 2;
        int tauMin = Math.Max(2, (int)(sampleRate / maximum)), tauMax = Math.Min(half - 1, (int)(sampleRate / minimum));
        if (tauMax <= tauMin + 2) return (0, 0);
        var difference = new double[tauMax + 2];
        for (int tau = 1; tau <= tauMax + 1; tau++)
        {
            double total = 0;
            for (int i = 0; i < half; i++) { double delta = frame[i] - frame[i + tau]; total += delta * delta; }
            difference[tau] = total;
        }
        var normalized = new double[tauMax + 2]; normalized[0] = 1; double running = 0;
        for (int tau = 1; tau <= tauMax + 1; tau++) { running += difference[tau]; normalized[tau] = running <= 0 ? 1 : difference[tau] * tau / running; }
        int best = -1;
        for (int tau = tauMin; tau <= tauMax; tau++)
            if (normalized[tau] < threshold)
            {
                while (tau + 1 <= tauMax && normalized[tau + 1] < normalized[tau]) tau++;
                best = tau; break;
            }
        if (best < 0)
        {
            best = tauMin; for (int tau = tauMin; tau <= tauMax; tau++) if (normalized[tau] < normalized[best]) best = tau;
            if (normalized[best] > .35) return (0, 1 - Math.Min(1, normalized[best]));
        }
        double shift = 0;
        if (best > 1 && best < tauMax + 1)
        {
            double a = normalized[best - 1], b = normalized[best], c = normalized[best + 1], denominator = a - 2 * b + c;
            if (Math.Abs(denominator) > 1e-12) shift = Math.Clamp(.5 * (a - c) / denominator, -1, 1);
        }
        return (sampleRate / (best + shift), 1 - Math.Min(1, normalized[best]));
    }
}
