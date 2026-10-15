using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Teclas.Core;

public sealed record TranspositionOptions(int Semitones, bool RemoveUnrecognizedDiagrams = false);
public sealed record TranspositionChange(int Staff, int Measure, string Kind, string Before, string After);
public sealed record TranspositionPlan(string SourcePath, string SourceSha256, NativeScoreMetadata Metadata,
    TranspositionOptions Options, IReadOnlyList<TranspositionChange> Changes, IReadOnlyList<string> Limitations, bool CanPrepare)
{
    public int Notes { get; init; }
    public int Harmonies { get; init; }
    public int KeySignatures { get; init; }
    public int UpdatedDiagrams { get; init; }
    public int RemovedDiagrams { get; init; }
    public int UnrecognizedDiagrams { get; init; }
}

/// <summary>Transposes a complete native score; unsupported pitch-bearing structures fail closed.</summary>
public static class NativeScoreTransposition
{
    public static TranspositionPlan CreatePlan(string path, TranspositionOptions options, CancellationToken token = default) => Build(path, options, token).Plan;

    public static void WriteDraft(TranspositionPlan plan, string destination, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan); token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(destination) || !Path.GetExtension(destination).Equals(".mscz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Escolha um novo arquivo MSCZ com caminho completo.", nameof(destination));
        if (File.Exists(destination) || Path.GetFullPath(destination).Equals(Path.GetFullPath(plan.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A transposição precisa de uma nova cópia; a partitura existente será preservada.");
        CheckSource(plan, token);
        var state = Build(plan.SourcePath, plan.Options, token);
        if (PlanStamp(plan) != PlanStamp(state.Plan)) throw new InvalidOperationException("A análise da transposição mudou. Prepare novamente a prévia.");
        if (!plan.CanPrepare) throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Limitations));
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".teclas-transposicao-" + Guid.NewGuid().ToString("N") + ".mscz");
        try
        {
            state.Archive.Save(temporary); CheckSource(plan, token); token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string PlanStamp(TranspositionPlan plan) => JsonSerializer.Serialize(plan);
    internal static void CheckSource(TranspositionPlan plan, CancellationToken token)
    {
        if (!NativeMusicEditor.HashFile(plan.SourcePath, token).Equals(plan.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A partitura mudou desde a análise. Reabra a versão atual para transpor.");
    }

    // All pitches are transposed together. Only ordinary automatic accidental glyphs may be
    // recalculated on reopening. Pitch, TPC/TPC2, keys, ties and every other element stay strict.
    internal static string NormalizedFingerprint(string path, CancellationToken token)
    {
        var archive = NativeScoreArchive.Read(path);
        bool concertPitch = archive.ReadStyle().Element("concertPitch")?.Value == "1";
        foreach (var note in archive.Score.Elements("Staff").Descendants("Note"))
        {
            token.ThrowIfCancellationRequested();
            int tpc = Integer(concertPitch ? note.Element("tpc") : note.Element("tpc2") ?? note.Element("tpc"), int.MinValue);
            foreach (var accidental in note.Elements("Accidental").Where(IsAutomaticAccidental).ToArray())
            {
                string name = TpcName(tpc);
                int alteration = name.Count(c => c == '#') - name.Count(c => c == 'b');
                int glyph = accidental.Element("subtype")!.Value switch
                { "natural" or "accidentalNatural" => 0, "sharp" or "accidentalSharp" => 1, "flat" or "accidentalFlat" => -1,
                    "sharp2" or "double-sharp" or "accidentalDoubleSharp" => 2, _ => -2 };
                if (name == "?" || alteration != glyph) throw new InvalidDataException("O acidente exibido não corresponde à grafia da nota transposta.");
                accidental.Remove();
            }
        }
        // MuseScore omits a redundant actualKey equal to concertKey for nontransposing staves.
        foreach (var signature in archive.Score.Elements("Staff").Descendants("KeySig"))
            if (signature.Element("actualKey") is { } actual && actual.Value == signature.Element("concertKey")?.Value) actual.Remove();
        return archive.MusicalFingerprint();
    }

    private static State Build(string path, TranspositionOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Semitones is < -12 or > 12) throw new ArgumentOutOfRangeException(nameof(options), "Escolha de −12 a +12 semitons.");
        token.ThrowIfCancellationRequested(); path = Path.GetFullPath(path);
        string hash = NativeMusicEditor.HashFile(path, token);
        var archive = NativeScoreArchive.Read(path); var metadata = archive.Metadata;
        var changes = new List<TranspositionChange>(); var errors = new List<string>();
        var limits = new List<string> { "O intervalo é relativo à versão aberta. Todas as pautas e vozes são transpostas juntas; ritmo, pausas, ligações e créditos permanecem iguais." };
        int notes = 0, harmonies = 0, keys = 0, updated = 0, removed = 0, unknown = 0;
        if (options.Semitones == 0) { limits.Add("Zero semitons mantém a versão atual; não é necessário salvar uma cópia."); return Finish(); }
        var score = archive.Score;
        bool concertPitch = archive.ReadStyle().Element("concertPitch")?.Value == "1";
        if (archive.Entries.Keys.Count(name => name.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase)) != 1 ||
            score.DescendantsAndSelf().Any(e => e.Name.LocalName is "Excerpt" || e != score && e.Name == "Score" ||
                e.Name.LocalName.StartsWith("linked", StringComparison.OrdinalIgnoreCase) ||
                e.Attributes().Any(a => a.Name.LocalName.StartsWith("linked", StringComparison.OrdinalIgnoreCase))))
            errors.Add("Partes vinculadas ou excertos devem ser transpostos no MuseScore para manter suas relações.");
        if (score.Descendants().Any(e => e.Name.LocalName is "Note" or "Harmony" or "KeySig" &&
                !e.Ancestors("Measure").Any(m => m.Parent?.Name == "Staff" && m.Parent.Parent == score)) ||
            score.Descendants("Note").Any(n => n.Parent?.Name != "Chord"))
            errors.Add("Há conteúdo musical fora da estrutura convencional de compassos. A partitura inteira deve ser transposta no MuseScore.");
        string[] unsupported = ["InstrumentChange", "StaffTypeChange", "FiguredBass", "HarpPedalDiagram", "Bend", "GuitarBend", "TremoloBar", "NoteDot", "NoteHead"];
        if (score.Descendants().Any(e => unsupported.Contains(e.Name.LocalName)))
            errors.Add("Há mudança de instrumento/pauta, baixo cifrado, afinação ou outra estrutura de altura especial. Faça a transposição no MuseScore.");
        if (score.Descendants("Accidental").Any(a => a.Parent?.Name != "Note" || !IsAutomaticAccidental(a)))
            errors.Add("Acidentes manuais, de ornamentos ou microtonais precisam ser conferidos no MuseScore antes da transposição.");
        if (score.Descendants("Note").Any(n => n.Elements().Any(e => !NoteElements.Contains(e.Name.LocalName)) ||
            n.Element("tuning") is { } tuning && tuning.Value != "0" || n.Elements("pitch").Count() != 1 ||
            n.Elements("tpc").Count() != 1 || n.Elements("tpc2").Skip(1).Any()))
            errors.Add("Há notas com afinação, execução personalizada ou propriedades de altura não suportadas. A partitura inteira foi preservada.");

        var staves = score.Elements("Staff").ToArray();
        var definitions = score.Elements("Part").SelectMany(p => p.Elements("Staff").Select(s => (Part: p, Staff: s))).ToArray();
        if (staves.Length == 0 || definitions.Length != staves.Length || staves.Select(s => (string?)s.Attribute("id")).Distinct().Count() != staves.Length)
            errors.Add("Não foi possível associar todas as pautas aos instrumentos com segurança.");
        var contexts = new List<StaffContext>();
        for (int index = 0; index < staves.Length && index < definitions.Length; index++)
        {
            var staff = staves[index]; string id = (string?)staff.Attribute("id") ?? "";
            // MuseScore omits Part/Staff ids for its sequential staff order. Only accept this
            // native shape when the actual musical ids prove the same sequence.
            var matches = definitions.Where(d => (string?)d.Staff.Attribute("id") == id).ToArray();
            var definition = matches.Length == 1 ? matches[0] : definitions[index];
            if (matches.Length > 1 || matches.Length == 0 &&
                (definition.Staff.Attribute("id") is not null || id != (index + 1).ToString(CultureInfo.InvariantCulture)))
            { errors.Add($"Pauta {index + 1}: associação de instrumento ambígua."); continue; }
            var instruments = definition.Part.Elements("Instrument").ToArray();
            if (instruments.Length != 1) { errors.Add($"Pauta {index + 1}: instrumento não identificado de forma única."); continue; }
            var instrument = instruments[0];
            if (definition.Staff.Descendants("StaffType").Any(t => (string?)t.Attribute("group") is not (null or "pitched")) ||
                instrument.Descendants().Any(e => e.Name.LocalName is "Drum" or "useDrumset" or "StringData"))
            { errors.Add($"Pauta {index + 1}: tablatura, percussão ou cordas com afinação exigem transposição no MuseScore."); continue; }
            int chromatic = Integer(instrument.Element("transposeChromatic"), 0), diatonic = Integer(instrument.Element("transposeDiatonic"), 0);
            if (chromatic is < -36 or > 36 || diatonic is < -21 or > 21)
            { errors.Add($"Pauta {index + 1}: intervalo do instrumento não reconhecido."); continue; }
            int instrumentFifths = 7 * chromatic - 12 * diatonic;
            if (instrumentFifths is < -14 or > 14) { errors.Add($"Pauta {index + 1}: grafia do instrumento transpositor não suportada."); continue; }
            contexts.Add(new(staff, index + 1, chromatic, instrumentFifths));
        }
        var signatureValues = new List<int>();
        foreach (var context in contexts)
        {
            var signatures = context.Staff.Descendants("KeySig").ToArray();
            if (!context.Staff.Elements("Measure").FirstOrDefault()?.Descendants("KeySig").Any(IsAtMeasureStart) ?? true)
            { signatureValues.Add(0); signatureValues.Add(-context.InstrumentFifths); }
            foreach (var signature in signatures)
            {
                if (signature.Elements().Any(e => !KeyElements.Contains(e.Name.LocalName)) || signature.Elements("custom").Any() ||
                    signature.Element("mode") is { } mode && mode.Value is not ("major" or "minor" or "none") ||
                    signature.Elements("concertKey").Skip(1).Any() || signature.Elements("actualKey").Skip(1).Any() ||
                    signature.Element("concertKey") is not null && signature.Element("accidental") is not null)
                { errors.Add($"Pauta {context.Number}: armadura personalizada ou atonal não suportada."); continue; }
                int displayedFifths = concertPitch ? 0 : context.InstrumentFifths;
                int concert = ReadConcertKey(signature), actual = Integer(signature.Element("actualKey"), concert - displayedFifths);
                if (concert is < -7 or > 7 || actual is < -7 or > 7 || actual != concert - displayedFifths)
                    errors.Add($"Pauta {context.Number}: armadura real/escrita inconsistente com o instrumento.");
                signatureValues.Add(concert); signatureValues.Add(concert - context.InstrumentFifths);
            }
        }
        int semitones = options.Semitones;
        int[] candidates = semitones % 12 == 0 ? [0] : Enumerable.Range(-11, 23).Where(delta => Mod(7 * delta, 12) == Mod(semitones, 12))
            .OrderBy(delta => signatureValues.Count(key => key + delta is < -7 or > 7))
            .ThenBy(delta => signatureValues.Sum(key => Math.Abs(key + delta))).ThenBy(Math.Abs).ToArray();
        int fifths = candidates[0];
        if (signatureValues.Any(value => value + fifths is < -7 or > 7))
            errors.Add("O intervalo produziria armaduras com mais de sete acidentes. Escolha outro intervalo ou a grafia no MuseScore.");

        // Images immediately following Harmony can be proven TeclaScore keyboard assets.
        // Unknown images and fret diagrams inside music are optional removals. Header images
        // remain unchanged and are explicitly called out as outside musical transposition.
        var recognizedImages = new HashSet<XElement>();
        var chordInfos = new Dictionary<XElement, NativeHarmonyInfo>();
        foreach (var harmony in score.Elements("Staff").Descendants("Harmony"))
        {
            var info = NativeHarmonyEditing.Inspect(archive, harmony); chordInfos[harmony] = info;
            if (info.CanEdit && info.HasDiagram && harmony.ElementsAfterSelf().FirstOrDefault() is { } image) recognizedImages.Add(image);
        }
        var external = score.Elements("Staff").Elements("Measure").Descendants()
            .Where(e => e.Name == "FretDiagram" || e.Name == "Image" && !recognizedImages.Contains(e)).ToArray();
        unknown = external.Length;
        if (score.Elements("Staff").Elements().Where(e => e.Name != "Measure").Descendants("Image").Any())
            limits.Add("Imagens de cabeçalho e molduras serão preservadas. Textos livres e imagens de cabeçalho não são transpostos; confira referências de tonalidade escritas neles.");
        if (unknown > 0 && !options.RemoveUnrecognizedDiagrams)
            errors.Add($"Há {unknown} imagem(ns)/diagrama(s) não reconhecido(s) dentro dos compassos. Para continuar, escolha removê-los somente da cópia transposta; imagens de cabeçalho permanecem.");
        if (unknown > 0 && options.RemoveUnrecognizedDiagrams)
        {
            foreach (var element in external)
            {
                var staff = element.Ancestors("Staff").First(); var measure = element.Ancestors("Measure").First();
                var nested = element.Descendants("Harmony").ToArray();
                if (nested.Length > 1 || nested.Any(h => h.Parent != element) || element.Parent?.Name != "voice")
                { errors.Add("Uma imagem/diagrama possui estrutura musical interna não reconhecida e não pode ser removida automaticamente."); continue; }
                foreach (var harmony in nested) { harmony.Remove(); element.AddBeforeSelf(harmony); }
                changes.Add(new(Array.IndexOf(staves, staff) + 1, staff.Elements("Measure").TakeWhile(m => m != measure).Count() + 1,
                    "Diagrama externo", element.Name == "FretDiagram" ? "Diagrama de cordas" : "Imagem no compasso", "Removido da cópia por sua escolha"));
                element.Remove(); removed++;
            }
            limits.Add($"A cópia excluirá {removed} imagem(ns)/diagrama(s) não reconhecido(s) dos compassos. O original e as imagens do cabeçalho permanecem intactos.");
        }
        foreach (var context in contexts)
        {
            int measureNumber = 0;
            foreach (var measure in context.Staff.Elements("Measure"))
            {
                token.ThrowIfCancellationRequested(); measureNumber++;
                int changedNotes = 0;
                foreach (var note in measure.Descendants("Note"))
                {
                    int pitch = Integer(note.Element("pitch"), -1), tpc = Integer(note.Element("tpc"), int.MinValue);
                    int written = Integer(note.Element("tpc2"), tpc);
                    if (!NativeMusicEditor.IsValidPitch(pitch, tpc) || !NativeMusicEditor.IsValidPitch(pitch - context.Chromatic, written) ||
                        written != tpc - context.InstrumentFifths || !NativeMusicEditor.IsValidPitch(pitch + semitones, tpc + fifths) ||
                        !NativeMusicEditor.IsValidPitch(pitch + semitones - context.Chromatic, written + fifths))
                    { errors.Add($"Pauta {context.Number}, compasso {measureNumber}: nota fora da extensão ou grafia real/escrita incompatível."); continue; }
                    note.SetElementValue("pitch", pitch + semitones); note.SetElementValue("tpc", tpc + fifths);
                    if (note.Element("tpc2") is not null) note.SetElementValue("tpc2", written + fifths);
                    foreach (var accidental in note.Elements("Accidental").Where(IsAutomaticAccidental).ToArray()) accidental.Remove();
                    notes++; changedNotes++;
                }
                if (changedNotes > 0) changes.Add(new(context.Number, measureNumber, "Notas", $"{changedNotes} nota(s)", $"{semitones:+0;-0} {(Math.Abs(semitones) == 1 ? "semitom" : "semitons")}; ritmos preservados"));
                var signatures = measure.Descendants("KeySig").ToList();
                if (measureNumber == 1 && !signatures.Any(IsAtMeasureStart) && fifths != 0)
                {
                    var firstVoice = measure.Elements("voice").FirstOrDefault();
                    if (firstVoice is null) { errors.Add($"Pauta {context.Number}: primeiro compasso sem voz para registrar a armadura."); continue; }
                    var signature = new XElement("KeySig", new XElement("concertKey", 0));
                    if (!concertPitch && context.InstrumentFifths != 0) signature.Add(new XElement("actualKey", -context.InstrumentFifths));
                    var anchor = firstVoice.Elements().FirstOrDefault(e => e.Name != "Clef" && e.Name != "eid");
                    if (anchor is null) firstVoice.Add(signature); else anchor.AddBeforeSelf(signature);
                    signatures.Insert(0, signature);
                }
                foreach (var signature in signatures)
                {
                    int concert = ReadConcertKey(signature), actual = Integer(signature.Element("actualKey"), concert - (concertPitch ? 0 : context.InstrumentFifths));
                    if (fifths == 0) continue;
                    if (signature.Element("accidental") is { } legacy) legacy.Value = (concert + fifths).ToString(CultureInfo.InvariantCulture);
                    else signature.SetElementValue("concertKey", concert + fifths);
                    if (signature.Element("actualKey") is not null || !concertPitch && context.InstrumentFifths != 0) signature.SetElementValue("actualKey", actual + fifths);
                    string keyLabel = context.InstrumentFifths == 0 ? "Armadura" : "Armadura (concerto)";
                    changes.Add(new(context.Number, measureNumber, keyLabel, KeyName(concert), KeyName(concert + fifths))); keys++;
                }
                foreach (var harmony in measure.Descendants("Harmony").ToArray())
                {
                    var info = harmony.Element("harmonyInfo") ?? harmony;
                    if (harmony.Elements("harmonyInfo").Skip(1).Any() || info.Elements("root").Count() != 1 ||
                        info.Elements().Any(e => !HarmonyFields.Contains(e.Name.LocalName) &&
                            !(ReferenceEquals(info,harmony) && HarmonyPresentation.Contains(e.Name.LocalName))) ||
                        !ReferenceEquals(info,harmony) && harmony.Elements().Any(e => e.Name != "harmonyInfo" && !HarmonyPresentation.Contains(e.Name.LocalName)) ||
                        info.Elements("base").Concat(info.Elements("bass")).Skip(1).Any() ||
                        harmony.Descendants().Any(e => e.Name.LocalName is "function" or "text" or "rootCase" or "baseCase") ||
                        harmony.Element("harmonyType") is { } type && type.Value != "0")
                    { errors.Add($"Pauta {context.Number}, compasso {measureNumber}: cifra textual/funcional não suportada para transposição automática."); continue; }
                    int root = Integer(info.Element("root"), int.MinValue);
                    if (!ValidTpc(root) || !ValidTpc(root + fifths))
                    { errors.Add($"Pauta {context.Number}, compasso {measureNumber}: fundamental da cifra fora da grafia suportada."); continue; }
                    foreach (var bass in info.Elements().Where(e => e.Name.LocalName is "base" or "bass"))
                    {
                        int value = Integer(bass, int.MinValue);
                        if (!ValidTpc(value) || !ValidTpc(value + fifths)) errors.Add($"Pauta {context.Number}, compasso {measureNumber}: baixo alternativo da cifra não reconhecido.");
                        else bass.Value = (value + fifths).ToString(CultureInfo.InvariantCulture);
                    }
                    if (fifths != 0 && chordInfos.TryGetValue(harmony, out var known) && known.CanEdit && known.HasDiagram)
                    { NativeHarmonyEditing.Apply(archive, harmony, TpcName(root + fifths), known.Quality, known.DiagramInversion); updated++; }
                    else info.SetElementValue("root", root + fifths);
                    harmonies++;
                    if (fifths != 0) changes.Add(new(context.Number, measureNumber, "Cifra", TpcName(root) + (string?)info.Element("name"), TpcName(root + fifths) + (string?)info.Element("name")));
                }
            }
        }
        if (notes == 0) errors.Add("Não há notas com altura convencional para transpor nesta partitura.");
        limits.Add("Cifras absolutas e baixos alternativos acompanham as notas. Diagramas TeclaScore reconhecidos mantêm a inversão; uma transposição de oitava conserva a posição dos acordes nos desenhos.");
        limits.Add("A nova tonalidade pode alterar o espaço dos símbolos. Confira a distribuição das páginas na prévia antes de salvar.");
        if (errors.Count == 0)
        {
            try { _ = archive.MusicalFingerprint(); }
            catch (InvalidDataException error) { errors.Add(error.Message); }
        }
        return Finish();

        State Finish()
        {
            token.ThrowIfCancellationRequested();
            if (NativeMusicEditor.HashFile(path, token) != hash) throw new IOException("A partitura mudou durante a análise. Tente novamente.");
            limits.AddRange(errors.Distinct(StringComparer.Ordinal));
            return new(archive, new(path, hash, metadata, options, changes.ToArray(), limits.ToArray(), options.Semitones != 0 && errors.Count == 0 && notes > 0)
                { Notes = notes, Harmonies = harmonies, KeySignatures = keys, UpdatedDiagrams = updated, RemovedDiagrams = removed, UnrecognizedDiagrams = unknown });
        }
    }

    private static bool IsAtMeasureStart(XElement key) => key.Parent?.Name == "voice" && !key.ElementsBeforeSelf().Any(e => e.Name.LocalName is "Chord" or "Rest" or "location" or "tick");
    private static int ReadConcertKey(XElement signature) => Integer(signature.Element("concertKey") ?? signature.Element("accidental"), 0);
    private static int Integer(XElement? element, int fallback) => element is null ? fallback : int.TryParse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : int.MinValue;
    private static int Mod(int value, int divisor) => (value % divisor + divisor) % divisor;
    private static bool ValidTpc(int tpc) => tpc is >= -1 and <= 33;
    private static string TpcName(int tpc)
    {
        (string Name, int Tpc)[] naturals = [("C",14),("D",16),("E",18),("F",13),("G",15),("A",17),("B",19)];
        foreach (var natural in naturals)
            if ((tpc - natural.Tpc) % 7 == 0 && Math.Abs(tpc - natural.Tpc) <= 14)
            { int alteration = (tpc - natural.Tpc) / 7; return natural.Name + (alteration < 0 ? new string('b', -alteration) : new string('#', alteration)); }
        return "?";
    }
    private static string KeyName(int value) => value == 0 ? "Sem acidentes" : $"{Math.Abs(value)} {(value > 0 ? "sustenido(s)" : "bemol(is)")}";
    private static bool IsAutomaticAccidental(XElement accidental) => !accidental.HasAttributes &&
        accidental.Elements().All(e => e.Name.LocalName is "eid" or "subtype" or "role") &&
        accidental.Elements("role").All(e => e.Value is "0" or "auto") &&
        (string?)accidental.Element("subtype") is "natural" or "sharp" or "flat" or "sharp2" or "flat2" or "double-sharp" or "double-flat"
            or "accidentalNatural" or "accidentalSharp" or "accidentalFlat" or "accidentalDoubleSharp" or "accidentalDoubleFlat";
    private static readonly HashSet<string> NoteElements = ["eid","pitch","tpc","tpc2","Accidental","tuning","small","visible","color","offset","pos","play","velocity","veloType","velocityType","Spanner","Tie","endSpanner","Fingering","Symbol"];
    private static readonly HashSet<string> KeyElements = ["eid","concertKey","actualKey","accidental","mode","isHeader","showCourtesy","visible","color","offset","pos","forInstrumentChange"];
    private static readonly HashSet<string> HarmonyFields = ["root","name","bass","base","degree"];
    private static readonly HashSet<string> HarmonyPresentation = ["eid","harmonyType","offset","pos","autoplace","minDistance","color","placement","visible","z","fontFace","fontSize","fontStyle","fontSpatiumDependent"];
    private sealed record StaffContext(XElement Staff, int Number, int Chromatic, int InstrumentFifths);
    private sealed record State(NativeScoreArchive Archive, TranspositionPlan Plan);
}
