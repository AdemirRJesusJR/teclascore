namespace Teclas.Core;

/// <summary>One measure of the MIDI's own meter, in quarters from the start of the file.</summary>
public sealed record MeterMeasure(int Number, double StartQuarter, double EndQuarter, int Numerator, int Denominator)
{
    public double BeatQuarters => 4.0 / Denominator;
}

/// <summary>
/// Measures, tempo and duration read from the MIDI's time signatures and tempo map, so the editor can say
/// "compasso 44, tempo 2" instead of a position in quarters. Positions are counted from the start of the file
/// (a pickup is shown as the first measure).
/// </summary>
public sealed class SongMeter
{
    public IReadOnlyList<MeterMeasure> Measures { get; }
    public double Bpm { get; }
    public TimeSpan Duration { get; }
    public string MeterText { get; }

    private SongMeter(IReadOnlyList<MeterMeasure> measures, double bpm, TimeSpan duration, string meter)
    { Measures = measures; Bpm = bpm; Duration = duration; MeterText = meter; }

    public static SongMeter Of(MidiDocument document)
    {
        int ppq = Math.Max(1, document.Ppq);
        long end = Math.Max(document.EndTick, document.Notes.Count == 0 ? 0 : document.Notes.Max(n => n.End));
        double endQuarter = Math.Max(end / (double)ppq, 1e-6);
        var signatures = document.TimeSignatures.Where(s => s.Numerator > 0 && s.Denominator > 0)
            .OrderBy(s => s.Tick).ThenBy(s => s.Order).GroupBy(s => s.Tick).Select(g => g.Last()).ToList();
        if (signatures.Count == 0 || signatures[0].Tick > 0) signatures.Insert(0, new MidiTimeSignature(0, signatures.Count == 0 ? 4 : signatures[0].Numerator, signatures.Count == 0 ? 4 : signatures[0].Denominator));
        var measures = new List<MeterMeasure>();
        double position = 0;
        for (int i = 0; i < signatures.Count && measures.Count < 5000; i++)
        {
            var signature = signatures[i];
            double length = signature.Numerator * 4.0 / signature.Denominator;
            double until = i + 1 < signatures.Count ? signatures[i + 1].Tick / (double)ppq : endQuarter;
            while (position < until - 1e-6 && measures.Count < 5000)
            {
                double stop = Math.Min(position + length, i + 1 < signatures.Count ? until : position + length);
                measures.Add(new MeterMeasure(measures.Count + 1, position, stop, signature.Numerator, signature.Denominator));
                position = stop;
            }
        }
        if (measures.Count == 0) measures.Add(new MeterMeasure(1, 0, 4, 4, 4));

        var tempos = document.TempoChanges.Where(t => t.MicrosecondsPerQuarter > 0).OrderBy(t => t.Tick).ThenBy(t => t.Order).ToList();
        double bpm = tempos.Count == 0 ? 120 : 60_000_000.0 / tempos[0].MicrosecondsPerQuarter;
        double seconds = 0, last = 0, microseconds = tempos.Count > 0 && tempos[0].Tick == 0 ? tempos[0].MicrosecondsPerQuarter : 500_000;
        foreach (var tempo in tempos)
        {
            double at = Math.Min(tempo.Tick / (double)ppq, endQuarter);
            seconds += (at - last) * microseconds / 1_000_000.0; last = at; microseconds = tempo.MicrosecondsPerQuarter;
        }
        seconds += Math.Max(0, endQuarter - last) * microseconds / 1_000_000.0;
        var first = signatures[0];
        string meter = signatures.Select(s => (s.Numerator, s.Denominator)).Distinct().Count() > 1 ? $"{first.Numerator}/{first.Denominator} (muda)" : $"{first.Numerator}/{first.Denominator}";
        return new SongMeter(measures, bpm, TimeSpan.FromSeconds(seconds), meter);
    }

    /// <summary>The measure containing a position (the last measure for positions past the end).</summary>
    public MeterMeasure MeasureAt(double quarter)
    {
        int low = 0, high = Measures.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (Measures[middle].StartQuarter <= quarter + 1e-6) low = middle; else high = middle - 1;
        }
        return Measures[low];
    }

    /// <summary>Measure number and beat (1-based, may be fractional) of a position.</summary>
    public (int Measure, double Beat) Locate(double quarter)
    {
        var measure = MeasureAt(Math.Max(0, quarter));
        return (measure.Number, 1 + Math.Max(0, quarter - measure.StartQuarter) / measure.BeatQuarters);
    }

    /// <summary>"Compasso 44, tempo 2" or, off the beat, "Compasso 44, tempo 2½".</summary>
    public string Describe(double quarter, bool shortForm = false)
    {
        var (number, beat) = Locate(quarter);
        string beatText = BeatText(beat);
        return shortForm ? $"{number}.{beatText}" : $"Compasso {number}, tempo {beatText}";
    }

    public static string BeatText(double beat)
    {
        int whole = (int)Math.Floor(beat + 1e-6);
        double fraction = beat - whole;
        string part = fraction switch
        {
            < 0.08 => "",
            < 0.29 => "¼",
            < 0.42 => "⅓",
            < 0.58 => "½",
            < 0.71 => "⅔",
            < 0.92 => "¾",
            _ => ""
        };
        if (fraction >= 0.92) whole++;
        return whole + part;
    }
}
