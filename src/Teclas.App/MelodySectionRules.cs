using System.Text.Json.Nodes;
using Teclas.Core;

namespace Teclas.App;

public sealed record MelodyMeasure(int Number, double StartQuarter, double EndQuarter, int Numerator, int Denominator, bool Partial)
{
    public string Meter => $"{Numerator}/{Denominator}" + (Partial ? " · parcial" : "");
    public string OriginalPosition => $"{StartQuarter:0.###}–{EndQuarter:0.###}";
}
public sealed record MelodyMeasureMap(string SourceSha256, double OriginQuarter, IReadOnlyList<MelodyMeasure> Measures)
{
    public static MelodyMeasureMap Read(JsonObject value)
    {
        var measures = value["measures"]!.AsArray().Select(item => new MelodyMeasure(
            item!["number"]!.GetValue<int>(), item["startQuarter"]!.GetValue<double>(), item["endQuarter"]!.GetValue<double>(),
            item["numerator"]!.GetValue<int>(), item["denominator"]!.GetValue<int>(), item["partial"]!.GetValue<bool>())).ToArray();
        if (measures.Length is 0 or > 700 || measures.Where((measure, index) => measure.Number != index + 1 ||
            !double.IsFinite(measure.StartQuarter) || !double.IsFinite(measure.EndQuarter) || measure.StartQuarter < 0 ||
            measure.EndQuarter <= measure.StartQuarter || measure.Numerator < 1 || measure.Denominator < 1).Any() ||
            measures.Zip(measures.Skip(1)).Any(pair => Math.Abs(pair.First.EndQuarter - pair.Second.StartQuarter) > 1e-7))
            throw new InvalidOperationException("O mapa de compassos não pôde ser confirmado.");
        double origin = value["originQuarter"]!.GetValue<double>();
        if (!double.IsFinite(origin) || Math.Abs(origin - measures[0].StartQuarter) > 1e-7)
            throw new InvalidOperationException("O início do mapa de compassos é inválido.");
        return new(value["sourceSha256"]!.GetValue<string>(), origin, measures);
    }
}

public static class MelodySectionRules
{
    public static IReadOnlyList<SavedMelodySection> Validate(IEnumerable<SavedMelodySection> sections,
        MelodyMeasureMap map, IEnumerable<SourceKey> available)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var result = Settings.CloneMelodySections(sections).OrderBy(section => section.FirstMeasure).ThenBy(section => section.LastMeasure).ToArray();
        if (result.Length > 700) throw new ArgumentException("A música pode ter no máximo 700 trechos.");
        var sources = available.Where(key => key.Channel != 10).ToHashSet();
        int previous = 0;
        foreach (var section in result)
        {
            if (section.FirstMeasure < 1 || section.LastMeasure < section.FirstMeasure || section.LastMeasure > map.Measures.Count)
                throw new ArgumentException($"Informe um intervalo válido entre os compassos 1 e {map.Measures.Count}.");
            if (section.FirstMeasure <= previous) throw new ArgumentException("Os trechos não podem se sobrepor. Ajuste os compassos inicial e final.");
            if (section.Source is not { } source || source.Track < 0 || source.Channel is < 1 or > 16 || source.Program is < 0 or > 127 ||
                !sources.Contains(new(source.Track, source.Channel, source.Program)))
                throw new ArgumentException("Escolha uma fonte disponível neste MIDI. Pistas de percussão não podem ser usadas como melodia.");
            previous = section.LastMeasure;
        }
        return result;
    }

    public static JsonArray ToJson(IEnumerable<SavedMelodySection> sections) => new(sections.OrderBy(section => section.FirstMeasure)
        .ThenBy(section => section.LastMeasure).Select(section => (JsonNode)new JsonObject
        {
            ["firstMeasure"] = section.FirstMeasure, ["lastMeasure"] = section.LastMeasure,
            ["source"] = new JsonObject { ["track"] = section.Source.Track, ["channel"] = section.Source.Channel, ["program"] = section.Source.Program }
        }).ToArray());
}
