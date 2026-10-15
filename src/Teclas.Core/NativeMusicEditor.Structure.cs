using System.Xml.Linq;

namespace Teclas.Core;

public sealed record MusicPitch(int Pitch, int Tpc);
/// <summary>A new attack or rest. An empty pitch list represents a rest.</summary>
public sealed record MusicEventPart(string DurationType, int Dots, IReadOnlyList<MusicPitch> Notes);
public sealed record MusicEventReplacement(string EventId, IReadOnlyList<MusicEventPart> Parts);
public sealed record MusicVoiceCopy(int SourceStaff, int SourceMeasure, int SourceVoice, int TargetStaff, int TargetMeasure, int TargetVoice);

public static partial class NativeMusicEditor
{
    public static IReadOnlyList<MusicEventPart> SplitRest(MusicEvent item, int pitch, int tpc, string duration, int dots)
    {
        if (item.Kind != "Rest" || !item.CanRestructure || item.DurationQuarter is not { } available)
            throw new InvalidOperationException("Selecione uma pausa simples na aba Ritmo para inserir uma nota.");
        if (!IsValidPitch(pitch, tpc) || Duration(duration, dots) is not { } requested || requested > available + 1e-9)
            throw new InvalidOperationException("A nota precisa caber na pausa selecionada e ter uma altura válida.");
        var result = new List<MusicEventPart> { new(duration, dots, [new(pitch, tpc)]) };
        double remaining = available - requested;
        foreach (string type in new[] { "long", "breve", "whole", "half", "quarter", "eighth", "16th", "32nd", "64th", "128th", "256th", "512th", "1024th" })
            while (remaining + 1e-9 >= Duration(type, 0))
            { result.Add(new(type, 0, [])); remaining -= Duration(type, 0)!.Value; }
        if (remaining > 1e-9) throw new InvalidOperationException("A pausa restante exige uma figura especial; ajuste este trecho no MuseScore.");
        return result;
    }

    private static string StructureRestriction(XElement item)
    {
        if (item.Name == "Chord" && item.Elements("Note").Any(n => NoteRestriction(item, n).Length > 0))
            return "Este ataque contém ligações ou notas especiais; sua estrutura permanece protegida.";
        if (IsGrace(item) || item.Elements().Any(e => !StructuralElements.Contains(e.Name.LocalName)))
            return "Este evento tem letra, ligação, articulação ou outra propriedade que precisa ser preservada no MuseScore.";
        return "";
    }
    private static readonly HashSet<string> StructuralElements = new(StringComparer.Ordinal)
    { "eid", "durationType", "duration", "dots", "Note", "StemDirection", "BeamMode", "offset", "pos", "color", "visible", "small" };

    private static bool ValidateStructure(State state, NativeMusicEdits edits, List<string> errors)
    {
        if (edits.EventReplacements.Count + edits.VoiceCopies.Count > 1000) { errors.Add("Aplique no máximo 1.000 alterações estruturais por vez."); return false; }
        if (edits.EventReplacements.Where(r => r?.Parts is not null).Sum(r => r.Parts.Sum(p => (long)(p?.Notes?.Count ?? 0) + 1)) > 10000)
        { errors.Add("Aplique no máximo 10.000 notas e pausas estruturais por vez."); return false; }
        bool changed = false;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var touchedVoices = new HashSet<(int, int, int)>();
        foreach (var replacement in edits.EventReplacements)
        {
            if (replacement is null || !used.Add(replacement.EventId) || !state.Events.TryGetValue(replacement.EventId, out var target))
            { errors.Add("Uma substituição aponta para um evento repetido ou fora da versão aberta."); continue; }
            touchedVoices.Add((target.View.Staff, target.View.Measure, target.View.Voice));
            if (!target.View.CanRestructure) errors.Add(target.View.StructureRestriction);
            if (edits.Durations.Any(d => d.Id == replacement.EventId) || edits.Notes.Any(n => target.View.Notes.Any(t => t.Id == n.Id)))
                errors.Add("Uma nota ou duração possui duas alterações conflitantes. Desfaça a alteração anterior deste evento.");
            if (replacement.Parts is null || replacement.Parts.Count is < 1 or > 128)
            { errors.Add("Cada evento precisa de uma sequência de 1 a 128 notas ou pausas."); continue; }
            double sum = 0;
            foreach (var part in replacement.Parts)
            {
                if (part is null || Duration(part.DurationType, part.Dots) is not { } length || part.Notes is null || part.Notes.Count > 32 ||
                    part.Notes.Any(n => n is null || !IsValidPitch(n.Pitch, n.Tpc)) || part.Notes.Select(n => n.Pitch).Distinct().Count() != part.Notes.Count)
                { errors.Add("Escolha figuras válidas e notas distintas com grafia compatível."); continue; }
                sum += length;
            }
            if (!Same(sum, target.Length)) errors.Add($"Compasso {target.View.Measure}, voz {target.View.Voice}: a substituição precisa conservar exatamente a duração do evento.");
            if (replacement.Parts[0] is { Notes: not null } first && first.Notes.All(n => n is not null))
                changed |= replacement.Parts.Count != 1 || first.DurationType != target.View.DurationType || first.Dots != target.View.Dots ||
                    !first.Notes.Select(p => (p.Pitch, p.Tpc)).SequenceEqual(target.View.Notes.Select(n => (n.Pitch, n.Tpc)));
        }
        var destinations = new HashSet<(int, int, int)>();
        foreach (var copy in edits.VoiceCopies)
        {
            if (copy is null) { errors.Add("Uma cópia de compasso está incompleta."); continue; }
            var from = (copy.SourceStaff, copy.SourceMeasure, copy.SourceVoice); var to = (copy.TargetStaff, copy.TargetMeasure, copy.TargetVoice);
            var source = state.Voices.FirstOrDefault(v => (v.Staff, v.Measure, v.Voice) == from);
            var target = state.Voices.FirstOrDefault(v => (v.Staff, v.Measure, v.Voice) == to);
            if (from == to || !destinations.Add(to) || source is null || target is null)
            { errors.Add("Escolha uma voz e um compasso de destino existentes e diferentes da origem, sem destinos repetidos."); continue; }
            if (source.Events.Count == 0 || target.Events.Count == 0 || source.Events.Any(e => !e.View.CanRestructure) || target.Events.Any(e => !e.View.CanRestructure) ||
                source.Node.Elements().Any(IsUnsafeCopyAnnotation) || target.Node.Elements().Any(IsUnsafeCopyAnnotation))
            { errors.Add("Copiar exige vozes simples, sem ligações, quiálteras, letras ou articulações especiais."); continue; }
            var sourceBar = MeasureLength(state, source); var targetBar = MeasureLength(state, target);
            if (!Same(sourceBar, source.Length) || !Same(targetBar, target.Length) || !Same(sourceBar, targetBar))
            { errors.Add("Os compassos de origem e destino precisam estar completos e ter a mesma duração."); continue; }
            bool edited = state.Events.Values.Where(e => from == (e.View.Staff, e.View.Measure, e.View.Voice) || to == (e.View.Staff, e.View.Measure, e.View.Voice))
                .Any(e => touchedVoices.Contains((e.View.Staff, e.View.Measure, e.View.Voice)) || edits.Durations.Any(d => d.Id == e.View.Id) || edits.Notes.Any(n => e.View.Notes.Any(note => note.Id == n.Id)));
            if (edited) errors.Add("Conclua ou desfaça as mudanças de notas e ritmos nas vozes de origem e destino antes de copiar o compasso.");
            var boundaries = source.Events.Select(e => e.View.Quarter).Append(source.Length).ToArray();
            if (target.Anchors.Any(a => a.EventId is null && !boundaries.Any(q => Same(q, a.Quarter))))
                errors.Add("Uma cifra, diagrama ou marcação do destino ficaria no meio de uma nota copiada. Escolha outro destino.");
            changed = true;
        }
        foreach (var copy in edits.VoiceCopies.Where(c => c is not null))
            if (destinations.Contains((copy.SourceStaff, copy.SourceMeasure, copy.SourceVoice)))
                errors.Add("Não encadeie cópias na mesma preparação. A origem deve ser um compasso que não está sendo substituído.");
        return changed;
    }
    private static bool IsUnsafeCopyAnnotation(XElement e) => e.Name.LocalName is not
        ("Chord" or "Rest" or "eid" or "Clef" or "KeySig" or "TimeSig" or "Tempo" or "Harmony" or "Image" or "Segment" or "StaffText" or "SystemText" or "RehearsalMark" or "Dynamic" or "BarLine");
    private static double? MeasureLength(State state, VoiceTarget voice)
    {
        double? length = null;
        foreach (var bar in state.Archive.Score.Elements("Staff").ElementAt(voice.Staff - 1).Elements("Measure").Take(voice.Measure))
        {
            var time = bar.Elements("voice").Elements("TimeSig").FirstOrDefault();
            if (time is not null) length = TimeLength(time);
            if (ReferenceEquals(bar, voice.Node.Parent)) return bar.Attribute("len") is { } len ? Fraction(len.Value) * 4 : bar.Element("irregular")?.Value is "1" or "true" ? null : length;
        }
        return null;
    }
    private static XElement PartElement(MusicEventPart part, EventTarget? original = null)
    {
        var element = new XElement(part.Notes.Count == 0 ? "Rest" : "Chord");
        if (part.Dots > 0) element.Add(new XElement("dots", part.Dots));
        element.Add(new XElement("durationType", part.DurationType));
        foreach (var pitch in part.Notes)
        {
            var old = original?.Node.Elements("Note").FirstOrDefault(n => ParseInt(n.Element("pitch"), -1) == pitch.Pitch && ParseInt(n.Element("tpc"), -99) == pitch.Tpc);
            var note = old is null ? new XElement("Note", new XElement("pitch", pitch.Pitch), new XElement("tpc", pitch.Tpc)) : new XElement(old);
            note.Elements("eid").Remove(); note.Elements("Accidental").Where(IsAutomaticAccidental).Remove(); element.Add(note);
        }
        return element;
    }
    private static void ApplyStructure(State state, NativeMusicEdits edits)
    {
        foreach (var replacement in edits.EventReplacements)
        {
            var target = state.Events[replacement.EventId];
            target.Node.ReplaceWith(replacement.Parts.Select(p => PartElement(p, target)).ToArray());
        }
        foreach (var copy in edits.VoiceCopies)
        {
            var source = state.Voices.Single(v => v.Staff == copy.SourceStaff && v.Measure == copy.SourceMeasure && v.Voice == copy.SourceVoice);
            var target = state.Voices.Single(v => v.Staff == copy.TargetStaff && v.Measure == copy.TargetMeasure && v.Voice == copy.TargetVoice);
            var annotations = new List<(double At, XElement Node)>();
            double at = 0;
            foreach (var child in target.Node.Elements())
            {
                if (child.Name.LocalName is "Chord" or "Rest") at += target.Events.Single(e => ReferenceEquals(e.Node, child)).Length!.Value;
                else annotations.Add((at, new XElement(child)));
            }
            target.Node.RemoveNodes(); at = 0;
            foreach (var item in source.Events)
            {
                foreach (var annotation in annotations.Where(a => Same(a.At, at))) target.Node.Add(annotation.Node);
                var node = new XElement(item.Node); node.Descendants("eid").Remove();
                node.Descendants("Accidental").Where(IsAutomaticAccidental).Remove();
                target.Node.Add(node); at += item.Length!.Value;
            }
            foreach (var annotation in annotations.Where(a => Same(a.At, at))) target.Node.Add(annotation.Node);
        }
    }
    internal static string FingerprintForEdits(string path, NativeMusicEdits edits, NativeMusicModel source, CancellationToken token)
    {
        if (edits.EventReplacements.Count + edits.VoiceCopies.Count == 0)
            return FingerprintIgnoringEditedAutomaticAccidentals(path, edits.Notes, source, token);
        var original = ReadExpected(source, token); var result = NativeScoreArchive.Read(path);
        var affected = edits.EventReplacements.Select(r => original.Events[r.EventId].View).Select(e => (e.Staff, e.Measure))
            .Concat(edits.VoiceCopies.Select(c => (c.TargetStaff, c.TargetMeasure)))
            .Concat(edits.Notes.Select(n => original.Notes[n.Id].Address).Select(a => (a.Staff, a.Measure))).Distinct();
        foreach (var (staff, bar) in affected)
        {
            token.ThrowIfCancellationRequested();
            var measure = result.Score.Elements("Staff").ElementAt(staff - 1).Elements("Measure").ElementAt(bar - 1);
            foreach (var note in measure.Descendants("Note"))
                note.Elements("Accidental").Where(a => IsAutomaticAccidental(a) && AccidentalMatchesPitch(a, ParseInt(note.Element("tpc"), int.MinValue))).Remove();
        }
        return result.MusicalFingerprint();
    }
}
