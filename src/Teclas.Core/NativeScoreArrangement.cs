using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Teclas.Core;

public enum ArrangementLevel { Original, Intermediate, Beginner }
public sealed record ArrangementOptions(ArrangementLevel Level, int Staff, int Voice = 1);
public sealed record ArrangementChange(int Staff, int Measure, int Voice, double? Quarter,
    string Kind, string Before, string After, string Reason);
public sealed record ArrangementPlan(string SourcePath, string SourceSha256, NativeScoreMetadata Metadata,
    ArrangementOptions Options, IReadOnlyList<ArrangementChange> Changes, IReadOnlyList<string> Limitations, bool CanPrepare);

/// <summary>Conservative adaptation of one native staff and voice; no source is rewritten.</summary>
public static class NativeScoreArrangement
{
    public static ArrangementPlan CreatePlan(string path, ArrangementOptions options, CancellationToken token = default) => Build(path, options, token).Plan;

    public static void WriteDraft(ArrangementPlan plan, string destination, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(destination) || !Path.GetExtension(destination).Equals(".mscz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Escolha um novo arquivo MSCZ com caminho completo.", nameof(destination));
        if (File.Exists(destination) || Path.GetFullPath(destination).Equals(Path.GetFullPath(plan.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("O arranjo deve ser salvo em uma nova cópia; a partitura existente será preservada.");
        CheckSource(plan, token);
        var state = Build(plan.SourcePath, plan.Options, token);
        if (PlanStamp(plan) != PlanStamp(state.Plan)) throw new InvalidOperationException("O plano mudou. Analise novamente o arranjo.");
        if (!state.Plan.CanPrepare) throw new InvalidOperationException("Este perfil não propôs alterações musicais seguras para a seleção.");
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".teclas-arranjo-" + Guid.NewGuid().ToString("N") + ".mscz");
        try
        {
            state.Archive.Save(temporary);
            CheckSource(plan, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string PlanStamp(ArrangementPlan plan) => JsonSerializer.Serialize(plan);
    internal static void CheckSource(ArrangementPlan plan, CancellationToken token)
    {
        if (!NativeMusicEditor.HashFile(plan.SourcePath, token).Equals(plan.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A partitura mudou desde a análise. Abra a versão atual e analise novamente.");
    }

    // Reimport may recalculate ordinary accidental glyphs dependent on removed or octave-shifted
    // notes. Only their exact diatonic positions later in that bar qualify; all pitches stay strict.
    internal static string NormalizedFingerprint(string path, ArrangementPlan plan, CancellationToken token)
    {
        CheckSource(plan, token);
        var expected = Build(plan.SourcePath, plan.Options, token);
        if (PlanStamp(expected.Plan) != PlanStamp(plan)) throw new InvalidOperationException("O plano do arranjo mudou.");
        var actual = NativeScoreArchive.Read(path);
        foreach (var scope in expected.Scopes)
        {
            token.ThrowIfCancellationRequested();
            var em = expected.Archive.Score.Elements("Staff").ElementAt(scope.Staff - 1).Elements("Measure").ElementAt(scope.Measure - 1);
            var am = actual.Score.Elements("Staff").ElementAtOrDefault(scope.Staff - 1)?.Elements("Measure").ElementAtOrDefault(scope.Measure - 1);
            if (am is null) throw new InvalidDataException("A normalização alterou a estrutura de compassos.");
            var evs = em.Elements("voice").ToArray(); var avs = am.Elements("voice").ToArray();
            if (evs.Length != avs.Length) throw new InvalidDataException("A normalização alterou as vozes.");
            for (int v = 0; v < evs.Length; v++)
            {
                // A second voice may carry tuplets or cursor moves even when the adapted
                // voice is simple. Unknown time positions must never broaden this exception.
                if (evs[v].Descendants().Any(e => e.Name.LocalName is "Tuplet" or "tuplet" or "endTuplet" or "location" or "timeStretch" or "Tremolo" or "TremoloTwoChord") ||
                    evs[v].Elements().Any(e => !IsEvent(e) && !TimedAnnotations.Contains(e.Name.LocalName))) continue;
                double quarter = 0;
                var ee = evs[v].Elements().Where(IsEvent).ToArray(); var ae = avs[v].Elements().Where(IsEvent).ToArray();
                if (ee.Length != ae.Length) throw new InvalidDataException("A normalização alterou os ataques.");
                for (int e = 0; e < ee.Length; e++)
                {
                    var en = ee[e].Elements("Note").ToArray(); var an = ae[e].Elements("Note").ToArray();
                    if (en.Length != an.Length || ee[e].Name != ae[e].Name) throw new InvalidDataException("A normalização alterou as notas.");
                    for (int n = 0; n < en.Length; n++)
                    {
                        if (en[n].Element("pitch")?.Value != an[n].Element("pitch")?.Value || en[n].Element("tpc")?.Value != an[n].Element("tpc")?.Value)
                            throw new InvalidDataException("A normalização alterou uma altura ou grafia.");
                        if (!scope.Slots.Contains(Slot(en[n])) || quarter + 1e-8 < scope.Quarter || en[n].Elements("Accidental").Any(a => !Automatic(a))) continue;
                        foreach (var a in an[n].Elements("Accidental").Where(a => Automatic(a) && Matches(a, Int(en[n].Element("tpc")))).ToArray()) a.Remove();
                    }
                    if (!IsGrace(ee[e]))
                    {
                        if (Duration(ee[e]) is not { } duration) break;
                        quarter += duration;
                    }
                }
            }
        }
        return actual.MusicalFingerprint();
    }

    private static State Build(string path, ArrangementOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Level) || options.Staff < 1 || options.Voice is < 1 or > 4)
            throw new ArgumentException("Selecione um perfil, uma pauta e uma voz válidos.", nameof(options));
        var model = NativeMusicEditor.Open(path, token);
        var archive = NativeScoreArchive.Read(model.SourcePath);
        var staves = archive.Score.Elements("Staff").ToArray();
        if (options.Staff > staves.Length) throw new ArgumentException("A pauta selecionada não existe.", nameof(options));
        var measures = staves[options.Staff - 1].Elements("Measure").ToArray();
        if (!measures.Any(m => m.Elements("voice").Count() >= options.Voice)) throw new ArgumentException("A voz selecionada não existe nesta pauta.", nameof(options));
        var changes = new List<ArrangementChange>();
        var limits = new List<string> {
            "Os perfis aplicam regras de redução; não avaliam a habilidade de quem toca. Confira a prévia antes de salvar.",
            "Somente a pauta e a voz escolhidas são adaptadas. Cifras, diagramas, textos, outras vozes e o cabeçalho são preservados." };
        var scopes = new List<Scope>();
        if (options.Level == ArrangementLevel.Original)
        {
            limits.Add("Original é a referência de comparação; nenhuma nova cópia é necessária.");
            return Finish();
        }
        // Keyboard diagrams use explicit spacing. Removing time events can compress an
        // entire system and collide diagrams in neighbouring, otherwise untouched bars.
        bool preserveTemporalEvents = archive.Score.Descendants("Image").Any();
        if (preserveTemporalEvents)
            limits.Add("Esta partitura contém diagramas ou imagens: os ataques rítmicos e os ornamentos foram preservados para manter o espaçamento. A redução de notas simultâneas e o ajuste uniforme de registro continuam disponíveis quando seguros; confira a prévia.");
        var protectedBars = ProtectedSpans(staves[options.Staff - 1], measures, options.Voice);
        var targets = new List<Target>();
        int omittedLimits = 0;
        void Limit(int number, string message)
        {
            string value = $"Compasso {number} · pauta {options.Staff} · voz {options.Voice}: {message}";
            if (limits.Contains(value, StringComparer.Ordinal)) return;
            if (limits.Count < 120) limits.Add(value); else omittedLimits++;
        }
        for (int mi = 0; mi < measures.Length; mi++)
        {
            token.ThrowIfCancellationRequested();
            var voice = measures[mi].Elements("voice").ElementAtOrDefault(options.Voice - 1);
            if (voice is null) continue;
            var views = model.Measures.First(m => m.Staff == options.Staff && m.Number == mi + 1).Events.Where(e => e.Voice == options.Voice).ToArray();
            var events = voice.Elements().Where(IsEvent).ToArray();
            if (events.Length != views.Length) throw new InvalidDataException("A estrutura mudou durante a análise.");
            bool special = voice.Descendants().Any(e => e.Name.LocalName is "Tuplet" or "tuplet" or "endTuplet" or "location" or "Tremolo" or "TremoloTwoChord" or "timeStretch") ||
                views.Any(e => !e.CanEditDuration && !e.Restriction.Contains("ornamento", StringComparison.Ordinal) &&
                    !e.Restriction.Contains("pausa de compasso", StringComparison.OrdinalIgnoreCase) && !e.Restriction.Contains("ligações", StringComparison.Ordinal));
            bool linkedPhrase = protectedBars.Contains(mi + 1);
            if (special) Limit(mi + 1, "Ritmo ou instrumento especial: este trecho permanece integralmente preservado.");
            if (linkedPhrase) Limit(mi + 1, "Frase com ligação ou deslocamento de registro: este trecho permanece preservado.");
            for (int ei = 0; ei < events.Length; ei++)
            {
                var node = events[ei]; var view = views[ei];
                bool safe = !special && !linkedPhrase && node.Name == "Chord" && view.Notes.Count > 0 &&
                    view.Notes.All(n => n.CanEdit && NativeMusicEditor.IsValidPitch(n.Pitch, n.Tpc)) && SimpleChord(node);
                var target = new Target(node, view, safe, voice); targets.Add(target);
                if (!safe)
                {
                    if (node.Name == "Chord" && !special && !linkedPhrase)
                        Limit(mi + 1, view.Notes.FirstOrDefault(n => !n.CanEdit)?.Restriction is { Length: > 0 } reason ? reason :
                            "Articulações, letras ou propriedades especiais deste acorde foram preservadas.");
                    continue;
                }
                if (IsGrace(node))
                {
                    if (preserveTemporalEvents) continue;
                    Add(view, "Ornamento", Notes(node), "Ornamento removido", "Retira um ornamento simples sem deslocar os ataques principais.");
                    Affect(view, node.Elements("Note")); node.Remove(); target.Removed = true;
                    continue;
                }
                var original = node.Elements("Note").ToArray();
                var ordered = original.OrderByDescending(n => Int(n.Element("pitch"))).ToArray();
                var kept = options.Level == ArrangementLevel.Beginner ? ordered.Take(1).ToArray() :
                    ordered.Where(n => Int(ordered[0].Element("pitch")) - Int(n.Element("pitch")) <= 12).DistinctBy(n => Int(n.Element("pitch")) % 12).Take(3).ToArray();
                if (kept.Length < original.Length)
                {
                    string before = Notes(node); var removed = original.Except(kept).ToArray();
                    Affect(view, removed);
                    foreach (var note in removed) note.Remove();
                    Add(view, "Polifonia", before, Notes(node), options.Level == ArrangementLevel.Beginner ?
                        "Mantém a nota mais aguda de cada ataque para uma linha única." :
                        "Mantém até três notas superiores, sem duplicações de oitava e com abertura máxima de uma oitava.");
                }
            }
        }
        // Shift the entire surviving line uniformly, never only neighbours of a protected phrase.
        var sounding = targets.Where(t => !t.Removed && t.Node.Name == "Chord" && !IsGrace(t.Node)).ToArray();
        var pitches = sounding.SelectMany(t => t.Node.Elements("Note")).Select(n => Int(n.Element("pitch"))).ToArray();
        int lower = options.Level == ArrangementLevel.Beginner ? 60 : 48, upper = 84;
        if (pitches.Length > 0 && (pitches.Min() < lower || pitches.Max() > upper))
        {
            var offsets = Enumerable.Range(-10, 21).Select(o => o * 12).Where(o => pitches.Min() + o >= lower && pitches.Max() + o <= upper).OrderBy(Math.Abs).ToArray();
            if (offsets.Length > 0 && sounding.All(t => t.Safe) && !targets.Any(t => !t.Removed && IsGrace(t.Node)))
            {
                int offset = offsets[0];
                foreach (var target in sounding)
                {
                    string before = Notes(target.Node); Affect(target.View, target.Node.Elements("Note"));
                    foreach (var note in target.Node.Elements("Note"))
                    {
                        note.SetElementValue("pitch", Int(note.Element("pitch")) + offset);
                        foreach (var accidental in note.Elements("Accidental").Where(Automatic).ToArray()) accidental.Remove();
                    }
                    Affect(target.View, target.Node.Elements("Note"));
                    Add(target.View, "Registro", before, Notes(target.Node), $"Desloca toda a linha escolhida em {offset / 12:+0;-0} oitava(s), preservando intervalos e grafia.");
                }
            }
            else limits.Add(offsets.Length == 0 ? "O registro total excede a faixa proposta. As oitavas foram preservadas para não quebrar a linha melódica." :
                "Há trechos protegidos na linha escolhida. O registro foi preservado para evitar mudanças parciais de oitava.");
        }
        // Every intervening native element is an anchor: no silent movement of symbols or text.
        double minimum = options.Level == ArrangementLevel.Beginner ? 1 : .5;
        foreach (var group in targets.Where(t => !preserveTemporalEvents && t.Safe && !t.Removed && !IsGrace(t.Node) && t.View.CanEditDuration).GroupBy(t => t.Voice))
        {
            var eligible = group.ToDictionary(t => t.Node);
            foreach (var first in group)
            {
                if (first.Removed || Duration(first.Node) is not { } length || length >= minimum) continue;
                while (length < minimum && first.Node.ElementsAfterSelf().FirstOrDefault() is { } next &&
                    eligible.TryGetValue(next, out var following) && !following.Removed && Duration(next) is { } nextLength &&
                    nextLength < minimum && SameAttacks(first.Node, next) && length + nextLength <= minimum && FindDuration(length + nextLength) is { } duration)
                {
                    string before = $"{Figure(first.Node)} + {Figure(next)} · {Notes(first.Node)}";
                    SetDuration(first.Node, duration.Type, duration.Dots); next.Remove(); following.Removed = true; length += nextLength;
                    Add(first.View, "Ritmo", before, $"{Figure(first.Node)} · {Notes(first.Node)}",
                        "Une ataques curtos consecutivos da mesma nota sem mover os próximos eventos ou anotações.");
                }
            }
        }
        if (omittedLimits > 0) limits.Add($"Mais {omittedLimits} avisos de trechos protegidos foram omitidos desta lista.");
        if (changes.Count == 0) limits.Add("Nenhuma alteração segura é necessária para este perfil e esta seleção; não será criada uma cópia redundante.");
        else
        {
            SetMeta("teclasArrangementLevel", options.Level.ToString()); SetMeta("teclasArrangementStaff", options.Staff.ToString(CultureInfo.InvariantCulture));
            SetMeta("teclasArrangementVoice", options.Voice.ToString(CultureInfo.InvariantCulture));
            SetMeta("teclasArrangementSummary", $"{(options.Level == ArrangementLevel.Beginner ? "Iniciante" : "Intermediário")} · pauta {options.Staff} · voz {options.Voice}");
        }
        return Finish();

        void SetMeta(string name, string value)
        {
            foreach (var previous in archive.Score.Elements("metaTag").Where(e => (string?)e.Attribute("name") == name).ToArray()) previous.Remove();
            archive.Score.AddFirst(new XElement("metaTag", new XAttribute("name", name), value));
        }
        void Add(MusicEvent e, string kind, string before, string after, string reason) => changes.Add(new(options.Staff, e.Measure, options.Voice, e.Quarter, kind, before, after, reason));
        void Affect(MusicEvent e, IEnumerable<XElement> notes)
        {
            if (e.Quarter is not { } at) return;
            var slots = notes.Select(Slot).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (slots.Count > 0) scopes.Add(new(options.Staff, e.Measure, at, slots));
        }
        State Finish()
        {
            token.ThrowIfCancellationRequested();
            if (NativeMusicEditor.HashFile(model.SourcePath, token) != model.SourceSha256) throw new IOException("A partitura mudou durante a análise.");
            return new(archive, new(model.SourcePath, model.SourceSha256, model.Metadata, options, changes.ToArray(), limits.ToArray(), changes.Count > 0), scopes);
        }
    }
    private static HashSet<int> ProtectedSpans(XElement staff, XElement[] measures, int voice)
    {
        var result = new HashSet<int>();
        for (int m = 0; m < measures.Length; m++)
        {
            var chosen = measures[m].Elements("voice").ElementAtOrDefault(voice - 1);
            if (chosen is null) continue;
            foreach (var span in chosen.Descendants().Where(e => e.Name.LocalName is "Spanner" or "Slur" or "Tie" or "endSpanner" or "endTie" or "Ottava" or "Glissando"))
            {
                result.Add(m + 1);
                if (span.Name != "Spanner")
                {
                    if (span.Attribute("id") is not null && !span.Ancestors("Spanner").Any())
                        result.UnionWith(Enumerable.Range(1, measures.Length));
                    continue;
                }
                string type = (string?)span.Attribute("type") ?? "";
                if (type is not ("Tie" or "Slur" or "Ottava" or "Glissando")) continue;
                var locations = span.Elements().Where(e => e.Name.LocalName is "next" or "prev").Elements("location").ToArray();
                foreach (var location in locations)
                {
                    int delta = Int(location.Element("measures"));
                    if (Math.Abs((long)delta) > measures.Length) { result.UnionWith(Enumerable.Range(1, measures.Length)); continue; }
                    int end = Math.Clamp(m + delta + 1, 1, measures.Length);
                    result.UnionWith(Enumerable.Range(Math.Min(m + 1, end), Math.Abs(end - (m + 1)) + 1));
                }
                if (locations.Length == 0 && span.Attribute("id") is not null) result.UnionWith(Enumerable.Range(1, measures.Length));
            }
        }
        if (staff.Elements().Any(e => e.Name.LocalName is "Spanner" or "Slur" or "Ottava")) result.UnionWith(Enumerable.Range(1, measures.Length));
        return result;
    }
    private static bool SimpleChord(XElement chord) => !chord.HasAttributes && chord.Elements().All(e =>
        e.Name.LocalName is "eid" or "durationType" or "dots" or "duration" or "Note" or "stemDirection" or "offset" or "pos" or "color" || GraceNames.Contains(e.Name.LocalName));
    private static bool SameAttacks(XElement first, XElement second)
    {
        static XElement Signature(XElement chord)
        {
            var copy = new XElement(chord);
            foreach (var e in copy.Descendants().Where(e => e.Name.LocalName is "eid" or "durationType" or "dots" or "duration" || e.Name == "Accidental" && Automatic(e)).ToArray()) e.Remove();
            return copy;
        }
        return XNode.DeepEquals(Signature(first), Signature(second));
    }
    private static readonly HashSet<string> GraceNames = ["acciaccatura", "appoggiatura", "grace4", "grace16", "grace32", "grace8after", "grace16after", "grace32after"];
    private static readonly HashSet<string> TimedAnnotations = ["eid", "Clef", "KeySig", "TimeSig", "Tempo", "Harmony", "Image", "BarLine", "Segment",
        "StaffText", "SystemText", "RehearsalMark", "Dynamic", "FretDiagram", "FiguredBass", "Symbol", "Breath"];
    private static bool IsGrace(XElement e) => e.Elements().Any(child => GraceNames.Contains(child.Name.LocalName));
    private static bool IsEvent(XElement e) => e.Name.LocalName is "Chord" or "Rest";
    private static int Int(XElement? e) => int.TryParse(e?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    private static double? Duration(XElement e)
    {
        int dots = Int(e.Element("dots")); if (dots is < 0 or > 3) return null;
        double? basis = e.Element("durationType")?.Value switch
        { "whole" => 4, "half" => 2, "quarter" => 1, "eighth" => .5, "16th" => .25, "32nd" => .125, "64th" => .0625, "128th" => .03125, _ => null };
        return basis * (2 - Math.Pow(.5, dots));
    }
    private static (string Type, int Dots)? FindDuration(double value)
    {
        foreach (string type in new[] { "whole", "half", "quarter", "eighth", "16th", "32nd", "64th", "128th" })
            for (int dots = 0; dots <= 2; dots++)
                if (Math.Abs((Duration(new XElement("Chord", new XElement("durationType", type), new XElement("dots", dots))) ?? 0) - value) < 1e-8) return (type, dots);
        return null;
    }
    private static void SetDuration(XElement e, string type, int dots)
    {
        e.SetElementValue("durationType", type); e.Element("dots")?.Remove();
        if (dots > 0) e.Element("durationType")!.AddBeforeSelf(new XElement("dots", dots));
        if (e.Element("duration") is not null)
        {
            int n = (int)Math.Round(Duration(e)!.Value * 256), d = 1024;
            while (n % 2 == 0 && d > 1) { n /= 2; d /= 2; }
            e.SetElementValue("duration", $"{n}/{d}");
        }
    }
    private static string Notes(XElement e) => string.Join(" · ", e.Elements("Note").Select(n => NativeMusicEditor.NoteName(Int(n.Element("pitch")), Int(n.Element("tpc")))));
    private static string Figure(XElement e)
    {
        string type = e.Element("durationType")?.Value switch { "whole" => "semibreve", "half" => "mínima", "quarter" => "semínima",
            "eighth" => "colcheia", "16th" => "semicolcheia", "32nd" => "fusa", "64th" => "semifusa", _ => e.Element("durationType")?.Value ?? "figura" };
        return type + (Int(e.Element("dots")) > 0 ? $" + {Int(e.Element("dots"))} ponto(s)" : "");
    }
    private static string Slot(XElement note)
    {
        int p = Int(note.Element("pitch")), t = Int(note.Element("tpc"));
        if (!NativeMusicEditor.IsValidPitch(p, t)) return "";
        var s = Spellings.First(s => (t - s.Tpc) % 7 == 0 && Math.Abs(t - s.Tpc) <= 14);
        return s.Name + ((p - s.PitchClass - (t - s.Tpc) / 7) / 12).ToString(CultureInfo.InvariantCulture);
    }
    private static bool Automatic(XElement a) => !a.HasAttributes && (a.Element("role")?.Value is null or "0" or "auto") &&
        a.Element("bracket") is null && a.Element("bracketType") is null &&
        a.Elements().All(e => e.Name.LocalName is "subtype" or "eid" or "role" or "offset" or "pos" or "color" or "visible" or "small") &&
        a.Element("subtype")?.Value is "accidentalFlat" or "accidentalNatural" or "accidentalSharp" or "accidentalDoubleSharp" or "accidentalDoubleFlat" or "flat" or "natural" or "sharp" or "flat2" or "sharp2";
    private static bool Matches(XElement a, int tpc)
    {
        var s = Spellings.FirstOrDefault(s => (tpc - s.Tpc) % 7 == 0 && Math.Abs(tpc - s.Tpc) <= 14);
        int? d = a.Element("subtype")?.Value switch { "accidentalFlat" or "flat" => -1, "accidentalNatural" or "natural" => 0,
            "accidentalSharp" or "sharp" => 1, "accidentalDoubleSharp" or "sharp2" => 2, "accidentalDoubleFlat" or "flat2" => -2, _ => null };
        return s.Name is not null && d == (tpc - s.Tpc) / 7;
    }
    private static readonly (string Name, int Tpc, int PitchClass)[] Spellings = [("C",14,0),("D",16,2),("E",18,4),("F",13,5),("G",15,7),("A",17,9),("B",19,11)];
    private sealed class Target(XElement node, MusicEvent view, bool safe, XElement voice)
    { internal XElement Node { get; } = node; internal MusicEvent View { get; } = view; internal bool Safe { get; } = safe; internal XElement Voice { get; } = voice; internal bool Removed { get; set; } }
    private sealed record Scope(int Staff, int Measure, double Quarter, HashSet<string> Slots);
    private sealed record State(NativeScoreArchive Archive, ArrangementPlan Plan, IReadOnlyList<Scope> Scopes);
}
