using System.Globalization;
using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>Opening tempo and meter of a score: quarter-note BPM, beats in the first measure and the length of one beat.</summary>
public sealed record ScoreTempo(double Bpm, int Numerator, int Denominator)
{
    /// <summary>Compound meters (6/8, 9/8, 12/8) are counted in dotted quarters; the others in their written beat.</summary>
    public bool Compound => Denominator == 8 && Numerator % 3 == 0 && Numerator > 3;
    public int Beats => Compound ? Numerator / 3 : Numerator;
    public double BeatSeconds => 60.0 / Bpm * (Compound ? 1.5 : 4.0 / Denominator);
    public string Label => $"♩ = {Math.Round(Bpm)} · {Numerator}/{Denominator}";

    /// <summary>Reads the first tempo (MuseScore stores beats per second) and time signature; 120 and 4/4 when absent.</summary>
    public static ScoreTempo Read(string scorePath)
    {
        var score = NativeScoreArchive.Read(scorePath).Score;
        double perSecond = double.TryParse((string?)score.Descendants("Tempo").FirstOrDefault()?.Element("tempo"), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t > 0 ? t : 2;
        var signature = score.Descendants("TimeSig").FirstOrDefault();
        int numerator = int.TryParse((string?)signature?.Element("sigN"), out int n) && n is > 0 and <= 32 ? n : 4;
        int denominator = int.TryParse((string?)signature?.Element("sigD"), out int d) && d is 1 or 2 or 4 or 8 or 16 ? d : 4;
        return new(Math.Clamp(perSecond * 60, 20, 400), numerator, denominator);
    }
}

/// <summary>One measure of clicks before a song on stage: woodblock on the GM percussion channel, the first beat accented.</summary>
public static class StageCountIn
{
    public static async Task PlayAsync(IMidiOutputTransport output, ScoreTempo tempo, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(tempo);
        var beat = TimeSpan.FromSeconds(tempo.BeatSeconds);
        for (int i = 0; i < tempo.Beats; i++)
        {
            token.ThrowIfCancellationRequested();
            int note = i == 0 ? 76 : 77, velocity = i == 0 ? 120 : 90;
            output.Send(0x99u | (uint)note << 8 | (uint)velocity << 16);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(80, beat.TotalMilliseconds / 2)), token).ConfigureAwait(false);
            output.Send(0x89u | (uint)note << 8);
            await Task.Delay(beat - TimeSpan.FromMilliseconds(Math.Min(80, beat.TotalMilliseconds / 2)), token).ConfigureAwait(false);
        }
    }
}
