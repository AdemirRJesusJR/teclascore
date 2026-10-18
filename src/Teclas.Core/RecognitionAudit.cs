namespace Teclas.Core;

/// <summary>A measure worth checking after optical recognition, with the reason in words.</summary>
public sealed record SuspectMeasure(int Number, string Reason);

/// <summary>
/// After a printed score is read by optical recognition (Audiveris), points out the measures most likely misread:
/// a voice with fewer or more beats than the usual measure (the first and last measures may be partial), staves that
/// end at different beats in the same measure, and notes far outside a keyboard's usual range. It only reports; the
/// score is never changed.
/// </summary>
public static class RecognitionAudit
{
    public static IReadOnlyList<SuspectMeasure> Check(NativeMusicModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var ends = model.Measures
            .SelectMany(m => m.Events.Where(e => e.Quarter is not null && e.DurationQuarter is not null)
                .Select(e => (m.Number, m.Staff, e.Voice, End: e.Quarter!.Value + e.DurationQuarter!.Value)))
            .GroupBy(x => (x.Number, x.Staff, x.Voice))
            .ToDictionary(g => g.Key, g => Math.Round(g.Max(x => x.End), 3));
        var numbers = model.Measures.Select(m => m.Number).Distinct().Order().ToList();
        if (numbers.Count == 0) return [];
        // The usual measure: the most common full length of the first voice.
        double nominal = ends.Where(e => e.Key.Voice <= 1).GroupBy(e => e.Value).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).Select(g => g.Key).FirstOrDefault();
        var result = new List<SuspectMeasure>();
        foreach (int number in numbers)
        {
            var reasons = new List<string>();
            bool edge = number == numbers[0] || number == numbers[^1];
            var voices = ends.Where(e => e.Key.Number == number).ToList();
            if (nominal > 0 && !edge)
                foreach (var voice in voices.Where(v => v.Key.Voice <= 1))
                {
                    double length = voice.Value;
                    if (length < nominal - .01) reasons.Add($"{Beats(length)} de {Beats(nominal)} tempos na pauta {voice.Key.Staff} (falta algo ou uma figura foi lida errada)");
                    else if (length > nominal + .01) reasons.Add($"{Beats(length)} tempos na pauta {voice.Key.Staff}, mais que os {Beats(nominal)} do compasso");
                }
            var staffEnds = voices.Where(v => v.Key.Voice <= 1).Select(v => v.Value).Distinct().ToList();
            if (staffEnds.Count > 1 && reasons.Count == 0 && edge) reasons.Add("as pautas terminam em tempos diferentes");
            var notes = model.Measures.Where(m => m.Number == number).SelectMany(m => m.Events).SelectMany(e => e.Notes).ToList();
            if (notes.Any(n => n.Pitch < 28)) reasons.Add("nota grave demais para um teclado comum (pode ser clave lida errada)");
            if (notes.Any(n => n.Pitch > 100)) reasons.Add("nota aguda demais para um teclado comum (pode ser clave ou linha suplementar lida errada)");
            if (reasons.Count > 0) result.Add(new SuspectMeasure(number, string.Join("; ", reasons.Distinct())));
        }
        return result;
    }

    private static string Beats(double quarters) => quarters.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
}
