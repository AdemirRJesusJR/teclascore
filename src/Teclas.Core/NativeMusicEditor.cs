using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Teclas.Core;

public sealed record MusicNote(string Id, int Pitch, int Tpc, string Name, bool CanEdit, string Restriction);
public sealed record MusicEvent(string Id, int Staff, int Measure, int Voice, double? Quarter, string Kind,
    string DurationType, int Dots, IReadOnlyList<MusicNote> Notes, bool CanEditDuration, string Restriction)
{
    public bool CanRestructure { get; init; }
    public string StructureRestriction { get; init; } = "";
    public double? DurationQuarter { get; init; }
    /// <summary>First lyric syllable on this event (verse 1) and whether a word continues after it.</summary>
    public string Lyric { get; init; } = "";
    public bool LyricContinues { get; init; }
}
public sealed record MusicHarmony(string Id, int Staff, int Measure, int Voice, double? Quarter, string Root,
    string Quality, bool CanEdit, string Restriction, bool HasDiagram, int? DiagramInversion);
public sealed record MusicMeasure(int Staff, int Number, string Label, IReadOnlyList<MusicEvent> Events, IReadOnlyList<MusicHarmony> Harmonies);
public sealed record NativeMusicModel(string SourcePath, string SourceSha256, NativeScoreMetadata Metadata,
    IReadOnlyList<MusicMeasure> Measures, IReadOnlyList<string> Limitations);
public sealed record MusicNoteChange(string Id, int Pitch, int Tpc);
public sealed record MusicDurationChange(string Id, string DurationType, int Dots);
public sealed record MusicHarmonyChange(string Id, string Root, string Quality, int? DiagramInversion = null);
public sealed record NativeMusicEdits(IReadOnlyList<MusicNoteChange> Notes, IReadOnlyList<MusicDurationChange> Durations,
    IReadOnlyList<MusicHarmonyChange> Harmonies)
{
    public IReadOnlyList<MusicEventReplacement> EventReplacements { get; init; } = [];
    public IReadOnlyList<MusicVoiceCopy> VoiceCopies { get; init; } = [];
}

/// <summary>Conservative patches over the native XML; unrelated music and archive entries are retained.</summary>
public static partial class NativeMusicEditor
{
    public static NativeMusicModel Open(string path, CancellationToken token = default) => Read(path, token).Model;

    public static IReadOnlyList<string> Validate(NativeMusicModel model, NativeMusicEdits edits)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(edits);
        try { return Validate(ReadExpected(model, CancellationToken.None), edits); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return [error.Message]; }
    }

    public static void WriteDraft(NativeMusicModel model, NativeMusicEdits edits, string destination, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(edits);
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(destination) || !Path.GetExtension(destination).Equals(".mscz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Escolha um novo arquivo MSCZ com caminho completo para a cópia de edição.", nameof(destination));
        if (File.Exists(destination) || string.Equals(Path.GetFullPath(model.SourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A edição precisa de um novo arquivo; a partitura existente será preservada.");
        var state = ReadExpected(model, token);
        var errors = Validate(state, edits);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        foreach (var change in edits.Notes)
        {
            token.ThrowIfCancellationRequested();
            var target = state.Notes[change.Id];
            if (change.Pitch == target.View.Pitch && change.Tpc == target.View.Tpc) continue;
            var note = target.Node;
            note.SetElementValue("pitch", change.Pitch);
            note.SetElementValue("tpc", change.Tpc);
            if (note.Element("tpc2") is not null) note.SetElementValue("tpc2", change.Tpc);
            // Only automatic accidentals of the edited note may be recalculated by MuseScore.
            foreach (var accidental in note.Elements("Accidental").Where(IsAutomaticAccidental).ToArray()) accidental.Remove();
        }
        foreach (var change in edits.Durations)
        {
            token.ThrowIfCancellationRequested();
            var target = state.Events[change.Id];
            if (change.DurationType == target.View.DurationType && change.Dots == target.View.Dots) continue;
            var item = target.Node;
            item.SetElementValue("durationType", change.DurationType);
            if (change.Dots == 0) item.Element("dots")?.Remove();
            else if (item.Element("dots") is { } dots) dots.Value = change.Dots.ToString(CultureInfo.InvariantCulture);
            else item.Element("durationType")!.AddBeforeSelf(new XElement("dots", change.Dots));
            if (item.Element("duration") is not null)
                item.SetElementValue("duration", DurationFraction(change.DurationType, change.Dots));
        }
        foreach (var change in edits.Harmonies)
        {
            token.ThrowIfCancellationRequested();
            NativeHarmonyEditing.Apply(state.Archive, state.Harmonies[change.Id].Node, change.Root, change.Quality, change.DiagramInversion);
        }
        ApplyStructure(state, edits);
        CheckSource(model, token);
        token.ThrowIfCancellationRequested();
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".teclas-draft-" + Guid.NewGuid().ToString("N") + ".mscz");
        try
        {
            state.Archive.Save(temporary);
            token.ThrowIfCancellationRequested();
            CheckSource(model, token);
            File.Move(temporary, destination, overwrite: false);
        }
        finally { TryDelete(temporary); }
    }

    public static bool IsValidPitch(int pitch, int tpc) => pitch is >= 0 and <= 127 &&
        Spellings.Any(spelling => tpc >= spelling.Tpc - 14 && tpc <= spelling.Tpc + 14 &&
            (tpc - spelling.Tpc) % 7 == 0 && Mod12(spelling.PitchClass + (tpc - spelling.Tpc) / 7) == pitch % 12);

    public static string NoteName(int pitch, int tpc)
    {
        if (!IsValidPitch(pitch, tpc)) return $"Altura {pitch}";
        var spelling = Spellings.First(s => (tpc - s.Tpc) % 7 == 0 && Math.Abs(tpc - s.Tpc) <= 14);
        int alteration = (tpc - spelling.Tpc) / 7;
        string accidental = alteration switch { -2 => "bb", -1 => "b", 1 => "#", 2 => "##", _ => "" };
        int octave = (pitch - spelling.PitchClass - alteration) / 12 - 1;
        return spelling.Name + accidental + octave.ToString(CultureInfo.InvariantCulture);
    }

    internal static string HashFile(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void CheckSource(NativeMusicModel model, CancellationToken token)
    {
        if (!string.Equals(HashFile(model.SourcePath, token), model.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A partitura foi alterada desde que o editor abriu. Reabra a versão atual antes de aplicar suas mudanças.");
    }

    internal static string FingerprintIgnoringEditedAutomaticAccidentals(string path, IReadOnlyList<MusicNoteChange> changes,
        NativeMusicModel source, CancellationToken token)
    {
        var original = ReadExpected(source, token);
        var result = NativeScoreArchive.Read(path);
        var affected = new Dictionary<string, NoteTarget>(StringComparer.Ordinal);
        var replacements = new Dictionary<string, MusicNoteChange>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!original.Notes.TryGetValue(change.Id, out var note)) throw new InvalidDataException("A referência de nota mudou durante a edição.");
            if (change.Pitch == note.View.Pitch && change.Tpc == note.View.Tpc) continue;
            replacements.Add(change.Id, change);
            affected.TryAdd(note.View.Id, note);
            // An altered note changes the accidental state of its diatonic staff position for
            // the remainder of this measure, including other voices. Both old and new slots matter.
            string? oldSlot = DiatonicPosition(note.View.Pitch, note.View.Tpc), newSlot = DiatonicPosition(change.Pitch, change.Tpc);
            if (note.Quarter is not { } changedAt) continue;
            foreach (var following in original.Notes.Values.Where(candidate => candidate.Address.Staff == note.Address.Staff &&
                candidate.Address.Measure == note.Address.Measure && candidate.Quarter is { } at && at >= changedAt &&
                (DiatonicPosition(candidate.View.Pitch, candidate.View.Tpc) is { } slot && (slot == oldSlot || slot == newSlot))))
            {
                if (following.Node.Elements("Accidental").Any(a => !IsAutomaticAccidental(a)) ||
                    following.Node.Element("tuning") is { } tuning && tuning.Value != "0") continue;
                affected.TryAdd(following.View.Id, following);
            }
        }
        foreach (var note in affected.Values)
        {
            token.ThrowIfCancellationRequested();
            int pitch = replacements.TryGetValue(note.View.Id, out var change) ? change.Pitch : note.View.Pitch;
            int tpc = change?.Tpc ?? note.View.Tpc;
            var address = note.Address;
            var target = result.Score.Elements("Staff").ElementAtOrDefault(address.Staff - 1)?.Elements("Measure").ElementAtOrDefault(address.Measure - 1)?
                .Elements("voice").ElementAtOrDefault(address.Voice - 1)?.Elements().ElementAtOrDefault(address.ElementIndex)?.Elements("Note").ElementAtOrDefault(address.NoteIndex);
            if (target is null || ParseInt(target.Element("pitch"), -1) != pitch || ParseInt(target.Element("tpc"), int.MinValue) != tpc)
                throw new InvalidDataException("O MuseScore modificou a organização da nota editada. A cópia não foi publicada.");
            foreach (var accidental in target.Elements("Accidental").Where(a => IsAutomaticAccidental(a) && AccidentalMatchesPitch(a, tpc)).ToArray()) accidental.Remove();
        }
        token.ThrowIfCancellationRequested();
        return result.MusicalFingerprint();
    }

    private static State ReadExpected(NativeMusicModel model, CancellationToken token)
    {
        var state = Read(model.SourcePath, token);
        if (!state.Model.SourceSha256.Equals(model.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A partitura foi alterada desde que o editor abriu. Reabra a versão atual antes de aplicar suas mudanças.");
        return state;
    }

    private static State Read(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        string hash = HashFile(path, token);
        var archive = NativeScoreArchive.Read(path);
        var notes = new Dictionary<string, NoteTarget>(StringComparer.Ordinal);
        var events = new Dictionary<string, EventTarget>(StringComparer.Ordinal);
        var harmonies = new Dictionary<string, HarmonyTarget>(StringComparer.Ordinal);
        var voices = new List<VoiceTarget>();
        var measures = new List<MusicMeasure>();
        var limits = new HashSet<string>(StringComparer.Ordinal)
        {
            "A edição conserva pautas e vozes separadas. Alterações só serão publicadas em uma nova cópia após a conferência no MuseScore.",
            "Posições são contadas em semínimas a partir do início de cada compasso. Compassos e vozes seguem a ordem do arquivo."
        };
        string global = GlobalRestriction(archive);
        if (global.Length > 0) limits.Add(global);
        var staves = archive.Score.Elements("Staff").ToArray();
        var partStaves = archive.Score.Elements("Part").SelectMany(part => part.Elements("Staff").Select(staff => (Part: part, Staff: staff))).ToArray();
        for (int si = 0; si < staves.Length; si++)
        {
            token.ThrowIfCancellationRequested();
            var staff = staves[si];
            var part = partStaves.FirstOrDefault(p => p.Staff.Attribute("id") is { } id && id.Value == (string?)staff.Attribute("id"));
            if (part.Part is null && si < partStaves.Length) part = partStaves[si];
            string staffRestriction = StaffRestriction(part.Part, part.Staff, staff);
            double? meter = null;
            int mi = 0;
            foreach (var measure in staff.Elements("Measure"))
            {
                token.ThrowIfCancellationRequested(); mi++;
                var measureEvents = new List<MusicEvent>();
                var measureHarmonies = new List<MusicHarmony>();
                var time = measure.Elements("voice").Elements("TimeSig").ToArray();
                if (time.Length > 0)
                {
                    var values = time.Select(TimeLength).ToArray();
                    meter = values.All(value => value is not null) && values.Distinct().Count() == 1 ? values[0] : null;
                }
                double? measureQuarter = measure.Attribute("len") is { } len ? Fraction(len.Value) * 4 :
                    measure.Element("irregular")?.Value is "1" or "true" ? null : meter;
                int vi = 0;
                foreach (var voice in measure.Elements("voice"))
                {
                    vi++;
                    string timing = TimingRestriction(voice, measureQuarter);
                    if (timing.Length > 0 && limits.Count < 20) limits.Add(timing);
                    double? quarter = 0;
                    var anchors = new List<Anchor>();
                    var eventTargets = new List<EventTarget>();
                    int elementIndex = -1;
                    foreach (var child in voice.Elements())
                    {
                        token.ThrowIfCancellationRequested(); elementIndex++;
                        string id = $"s{si + 1}.m{mi}.v{vi}.e{elementIndex}:{ElementHash(child)}";
                        if (child.Name.LocalName is "Chord" or "Rest")
                        {
                            var eventNotes = new List<MusicNote>();
                            int ni = 0;
                            foreach (var note in child.Elements("Note"))
                            {
                                string noteId = id + $".n{ni}:{ElementHash(note)}";
                                int pitch = ParseInt(note.Element("pitch"), -1), tpc = ParseInt(note.Element("tpc"), int.MinValue);
                                string restriction = First(global, staffRestriction, NoteRestriction(child, note));
                                var view = new MusicNote(noteId, pitch, tpc, NoteName(pitch, tpc), restriction.Length == 0, restriction);
                                eventNotes.Add(view);
                                notes.Add(noteId, new(note, view, new(si + 1, mi, vi, elementIndex, ni++), quarter));
                            }
                            string restrictionDuration = First(global, staffRestriction, timing, DurationRestriction(child));
                            string type = (string?)child.Element("durationType") ?? "";
                            int dots = ReadDots(child);
                            var item = new MusicEvent(id, si + 1, mi, vi, quarter, child.Name.LocalName, type, dots,
                                eventNotes.ToArray(), restrictionDuration.Length == 0, restrictionDuration)
                            { DurationQuarter = EventLength(child, measureQuarter),
                              Lyric = (string?)child.Elements("Lyrics").FirstOrDefault(l => (string?)l.Element("no") is null or "0")?.Element("text") ?? "",
                              LyricContinues = (string?)child.Elements("Lyrics").FirstOrDefault(l => (string?)l.Element("no") is null or "0")?.Element("syllabic") is "begin" or "middle",
                              StructureRestriction = First(global, staffRestriction, timing, StructureRestriction(child)),
                              CanRestructure = First(global, staffRestriction, timing, StructureRestriction(child)).Length == 0 };
                            var target = new EventTarget(child, item, EventLength(child, measureQuarter));
                            events.Add(id, target); eventTargets.Add(target); measureEvents.Add(item);
                            anchors.Add(new(quarter, id));
                            quarter = quarter is { } start && target.Length is { } duration ? start + duration : null;
                        }
                        else
                        {
                            if (child.Name == "Harmony")
                            {
                                var harmony = NativeHarmonyEditing.Inspect(archive, child);
                                string restriction = First(global, harmony.CanEdit ? "" : harmony.Restriction);
                                var view = new MusicHarmony(id, si + 1, mi, vi, quarter, harmony.Root, harmony.Quality,
                                    restriction.Length == 0, restriction, harmony.HasDiagram, harmony.DiagramInversion);
                                harmonies.Add(id, new(child, view, new(si + 1, mi, vi, elementIndex, -1))); measureHarmonies.Add(view);
                            }
                            if (child.Name != "eid") anchors.Add(new(quarter, null));
                            if (IsUnknownTiming(child)) quarter = null;
                        }
                    }
                    voices.Add(new(si + 1, mi, vi, voice, eventTargets.ToArray(), anchors.ToArray(), quarter));
                }
                measures.Add(new(si + 1, mi, $"Compasso {mi} · pauta {si + 1}", measureEvents.ToArray(), measureHarmonies.ToArray()));
            }
        }
        if (archive.Score.Elements("Staff").Descendants("Harmony").Count() != harmonies.Count)
            limits.Add("Algumas cifras estão dentro de estruturas especiais e permanecem disponíveis somente no MuseScore.");
        if (HashFile(path, token) != hash) throw new IOException("A partitura mudou durante a leitura. Abra-a novamente.");
        return new(archive, new(path, hash, archive.Metadata, measures.ToArray(), limits.ToArray()), notes, events, harmonies, voices);
    }

    private static IReadOnlyList<string> Validate(State state, NativeMusicEdits edits)
    {
        var errors = new List<string>();
        if (edits.Notes is null || edits.Durations is null || edits.Harmonies is null) return ["A lista de alterações está incompleta."];
        if (edits.EventReplacements is null || edits.VoiceCopies is null) return ["A lista de alterações estruturais está incompleta."];
        if (edits.Notes.Count + edits.Durations.Count + edits.Harmonies.Count + edits.EventReplacements.Count + edits.VoiceCopies.Count == 0) return ["Faça uma alteração antes de preparar a prévia."];
        if ((long)edits.Notes.Count + edits.Durations.Count + edits.Harmonies.Count > 10000) return ["Aplique no máximo 10.000 alterações por vez."];
        var used = new HashSet<string>(StringComparer.Ordinal);
        bool changed = false;
        foreach (var edit in edits.Notes)
        {
            if (!used.Add(edit.Id) || !state.Notes.TryGetValue(edit.Id, out var target)) { errors.Add("Uma referência de nota é repetida ou não pertence à versão aberta."); continue; }
            if (!target.View.CanEdit) errors.Add($"{target.View.Name}: {target.View.Restriction}");
            if (!IsValidPitch(edit.Pitch, edit.Tpc)) errors.Add("Escolha uma altura entre 0 e 127 e uma grafia compatível com a nota.");
            changed |= edit.Pitch != target.View.Pitch || edit.Tpc != target.View.Tpc;
        }
        var durations = new Dictionary<string, MusicDurationChange>(StringComparer.Ordinal);
        foreach (var edit in edits.Durations)
        {
            if (!durations.TryAdd(edit.Id, edit) || !state.Events.TryGetValue(edit.Id, out var target)) { errors.Add("Uma referência de duração é repetida ou não pertence à versão aberta."); continue; }
            if (!target.View.CanEditDuration) errors.Add($"Compasso {target.View.Measure}, voz {target.View.Voice}: {target.View.Restriction}");
            if (Duration(edit.DurationType, edit.Dots) is null) errors.Add("Escolha uma figura reconhecida e de zero a três pontos de aumento.");
            changed |= edit.DurationType != target.View.DurationType || edit.Dots != target.View.Dots;
        }
        foreach (var voice in state.Voices.Where(v => v.Events.Any(e => durations.ContainsKey(e.View.Id))))
        {
            double? position = 0;
            int anchorIndex = 0, eventIndex = 0;
            foreach (var child in voice.Node.Elements())
            {
                if (child.Name == "eid") continue;
                var anchor = voice.Anchors[anchorIndex++];
                bool edited = anchor.EventId is { } id && durations.ContainsKey(id);
                if (!edited && !Same(position, anchor.Quarter))
                    errors.Add($"Compasso {voice.Measure}, pauta {voice.Staff}, voz {voice.Voice}: a alteração deslocaria uma nota, cifra, imagem ou marcação não editada. Compense a duração antes desse ponto.");
                if (child.Name.LocalName is "Chord" or "Rest")
                {
                    var item = voice.Events[eventIndex++];
                    double? length = durations.TryGetValue(item.View.Id, out var replacement) ? Duration(replacement.DurationType, replacement.Dots) : item.Length;
                    position = position is { } start && length is { } value ? start + value : null;
                }
                else if (IsUnknownTiming(child)) position = null;
            }
            if (!Same(position, voice.Length))
                errors.Add($"Compasso {voice.Measure}, pauta {voice.Staff}, voz {voice.Voice}: conserve a soma das durações, ajustando também outra nota ou pausa desta voz.");
        }
        used.Clear();
        var harmonyValidation = edits.Harmonies.Count > 0 ? CloneArchive(state.Archive) : null;
        foreach (var edit in edits.Harmonies)
        {
            if (!used.Add(edit.Id) || !state.Harmonies.TryGetValue(edit.Id, out var target)) { errors.Add("Uma referência de cifra é repetida ou não pertence à versão aberta."); continue; }
            if (!target.View.CanEdit) { errors.Add(target.View.Restriction); continue; }
            try
            {
                var address = target.Address;
                var harmony = harmonyValidation!.Score.Elements("Staff").ElementAt(address.Staff - 1).Elements("Measure").ElementAt(address.Measure - 1)
                    .Elements("voice").ElementAt(address.Voice - 1).Elements().ElementAt(address.ElementIndex);
                NativeHarmonyEditing.Apply(harmonyValidation, harmony, edit.Root, edit.Quality, edit.DiagramInversion);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException) { errors.Add(error.Message); }
            changed |= edit.Root != target.View.Root || edit.Quality != target.View.Quality || target.View.HasDiagram && edit.DiagramInversion is { } inversion && inversion != target.View.DiagramInversion;
        }
        changed |= ValidateStructure(state, edits, errors);
        if (!changed) errors.Add("Nenhuma alteração musical foi solicitada.");
        return errors.Distinct(StringComparer.Ordinal).Take(30).ToArray();
    }

    private static string GlobalRestriction(NativeScoreArchive archive) =>
        archive.Entries.Keys.Any(name => name != archive.MainEntry && name.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase)) ||
        archive.Score.Descendants().Any(e => e.Name.LocalName is "Score" or "Excerpt" || e.Name.LocalName.StartsWith("linked", StringComparison.OrdinalIgnoreCase) || e.Attributes().Any(a => a.Name.LocalName.StartsWith("linked", StringComparison.OrdinalIgnoreCase)))
            ? "Esta partitura contém partes ou elementos vinculados. Edite-a no MuseScore para manter todas as versões sincronizadas." : "";

    private static NativeScoreArchive CloneArchive(NativeScoreArchive archive)
    {
        var clone = new NativeScoreArchive { MainEntry = archive.MainEntry, MainDocument = new XDocument(archive.MainDocument) };
        foreach (var entry in archive.Entries) clone.Entries.Add(entry.Key, entry.Value);
        return clone;
    }

    private static string StaffRestriction(XElement? part, XElement? definition, XElement staff)
    {
        if (definition?.Element("StaffType")?.Attribute("group")?.Value is { } group && group != "pitched" ||
            staff.Descendants("StaffType").Any(type => (string?)type.Attribute("group") is "percussion" or "tablature"))
            return "Esta pauta usa percussão ou tablatura; suas relações de altura precisam ser editadas no MuseScore.";
        if (part?.Descendants().Any(e => (e.Name.LocalName is "transposeChromatic" or "transposeDiatonic") && e.Value != "0") == true ||
            staff.Descendants("InstrumentChange").Any())
            return "Esta pauta usa transposição ou mudança de instrumento; a edição deve preservar a escrita transposta no MuseScore.";
        return "";
    }

    private static string NoteRestriction(XElement chord, XElement note)
    {
        if (note.Descendants("Spanner").Any() || chord.Elements().Any(e => e.Name.LocalName is "Spanner" or "Glissando") || note.Elements().Any(e => e.Name.LocalName is "Tie" or "endSpanner" or "endTie"))
            return "Esta nota pertence a uma ligação musical. Edite a cadeia completa no MuseScore para preservá-la.";
        if (note.Element("tuning") is { } tuning && (!double.TryParse(tuning.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value != 0) ||
            note.Elements().Any(e => e.Name.LocalName is "string" or "fret" or "head" or "headGroup"))
            return "Esta nota possui afinação, corda ou cabeça especial que requer edição no MuseScore.";
        if (chord.Element("staffMove") is { } move && move.Value != "0") return "Esta nota cruza pautas e deve ser editada no MuseScore.";
        if (note.Element("tpc2") is { } second && second.Value != note.Element("tpc")?.Value)
            return "Esta nota tem grafia transposta diferente da altura de concerto.";
        if (note.Elements("Accidental").Any(a => !IsAutomaticAccidental(a)))
            return "Esta nota tem um acidente manual ou especial; confira-o no MuseScore antes de mudar a altura.";
        if (note.Elements().Any(e => !SimpleNoteElements.Contains(e.Name.LocalName)))
            return "Esta nota contém uma propriedade musical ainda não reconhecida pelo editor.";
        return "";
    }

    private static string TimingRestriction(XElement voice, double? measureLength)
    {
        if (measureLength is null) return "A duração deste compasso não foi interpretada com segurança; altere o ritmo no MuseScore.";
        if (voice.Descendants().Any(e => e.Name.LocalName is "Tuplet" or "endTuplet" or "tuplet" or "Tremolo" or "TremoloTwoChord" or "timeStretch") ||
            voice.Elements().Any(IsUnknownTiming))
            return "Esta voz contém quiálteras, deslocamentos ou estruturas de tempo especiais; sua duração permanece protegida.";
        if (voice.Elements("Chord").Any(IsGrace)) return "Esta voz contém notas de ornamento; ajuste seu ritmo no MuseScore.";
        if (voice.Elements().Where(e => e.Name.LocalName is "Chord" or "Rest").Any(e => EventLength(e, measureLength) is null))
            return "Uma duração desta voz não foi reconhecida; ajuste seu ritmo no MuseScore.";
        if (voice.Elements().Where(e => e.Name.LocalName is "Chord" or "Rest").Sum(e => EventLength(e, measureLength) ?? 0) > measureLength + .000000001)
            return "Esta voz já excede a duração do compasso. Confira o ritmo no MuseScore antes de editar suas durações.";
        if (voice.Elements("TimeSig").Any(t => t.ElementsBeforeSelf().Any(e => e.Name.LocalName is "Chord" or "Rest")))
            return "Há uma mudança de fórmula dentro do compasso; ajuste seu ritmo no MuseScore.";
        return "";
    }

    private static string DurationRestriction(XElement item)
    {
        if ((string?)item.Element("durationType") == "measure") return "A pausa de compasso inteiro conserva a duração do compasso.";
        if (item.Descendants().Any(e => e.Name.LocalName is "Spanner" or "Tie" or "Slur" or "Lyrics" or "endSpanner" or "Beam"))
            return "Este evento possui ligações, letra ou agrupamentos explícitos. Sua duração permanece protegida.";
        return "";
    }

    private static double? EventLength(XElement item, double? measureLength)
    {
        if (IsGrace(item)) return 0;
        if ((string?)item.Element("durationType") == "measure" && item.Name == "Rest")
            return item.Element("duration") is { } full ? Fraction(full.Value) * 4 : measureLength;
        double? result = Duration((string?)item.Element("durationType") ?? "", ReadDots(item));
        if (item.Element("duration") is { } explicitDuration && !Same(result, Fraction(explicitDuration.Value) * 4)) return null;
        return result;
    }

    private static double? Duration(string type, int dots)
    {
        if (dots is < 0 or > 3) return null;
        double? basis = type switch
        {
            "long" or "longa" => 16, "breve" => 8, "whole" => 4, "half" => 2, "quarter" => 1, "eighth" => .5,
            "16th" => .25, "32nd" => .125, "64th" => .0625, "128th" => .03125, "256th" => .015625,
            "512th" => .0078125, "1024th" => .00390625, _ => null
        };
        return basis * (2 - Math.Pow(.5, dots));
    }

    private static string DurationFraction(string type, int dots)
    {
        long numerator = (long)Math.Round(Duration(type, dots)!.Value * 2048), denominator = 8192;
        while (numerator % 2 == 0 && denominator > 1) { numerator /= 2; denominator /= 2; }
        return $"{numerator}/{denominator}";
    }

    private static double? TimeLength(XElement time)
    {
        if (time.Element("stretch") is { } stretch && Fraction(stretch.Value) != 1) return null;
        int n = ParseInt(time.Element("sigN"), 0), d = ParseInt(time.Element("sigD"), 0);
        return n is > 0 and <= 1024 && d is > 0 and <= 1024 ? 4d * n / d : null;
    }
    private static double? Fraction(string text)
    {
        var parts = text.Split('/');
        return parts.Length == 2 && long.TryParse(parts[0], out long n) && long.TryParse(parts[1], out long d) &&
            n is > 0 and <= 1000000 && d is > 0 and <= 1000000 ? (double)n / d : null;
    }
    private static bool Same(double? first, double? second) => first is { } a && second is { } b && Math.Abs(a - b) < .000000001;
    private static bool IsGrace(XElement item) => item.Elements().Any(e => e.Name.LocalName is "acciaccatura" or "appoggiatura" || e.Name.LocalName.StartsWith("grace", StringComparison.Ordinal));
    private static bool IsUnknownTiming(XElement element) => element.Name.LocalName is not ("Chord" or "Rest") && !TimedAnnotations.Contains(element.Name.LocalName);
    private static bool IsAutomaticAccidental(XElement accidental) => !accidental.HasAttributes &&
        (accidental.Element("role")?.Value is null or "0" or "auto") &&
        (accidental.Element("subtype")?.Value is "accidentalFlat" or "accidentalNatural" or "accidentalSharp" or "accidentalDoubleSharp" or "accidentalDoubleFlat" or "flat" or "natural" or "sharp" or "flat2" or "sharp2") &&
        accidental.Element("bracket") is null && accidental.Element("bracketType") is null &&
        accidental.Elements().All(e => e.Name.LocalName is "subtype" or "eid" or "role" or "offset" or "pos" or "color" or "visible" or "small");
    private static bool AccidentalMatchesPitch(XElement accidental, int tpc)
    {
        var spelling = Spellings.FirstOrDefault(s => (tpc - s.Tpc) % 7 == 0 && Math.Abs(tpc - s.Tpc) <= 14);
        if (spelling.Name is null) return false;
        int? alteration = accidental.Element("subtype")?.Value switch
        {
            "accidentalFlat" or "flat" => -1, "accidentalNatural" or "natural" => 0, "accidentalSharp" or "sharp" => 1,
            "accidentalDoubleSharp" or "sharp2" => 2, "accidentalDoubleFlat" or "flat2" => -2, _ => null
        };
        return alteration == (tpc - spelling.Tpc) / 7;
    }
    private static string? DiatonicPosition(int pitch, int tpc)
    {
        if (!IsValidPitch(pitch, tpc)) return null;
        var spelling = Spellings.First(s => (tpc - s.Tpc) % 7 == 0 && Math.Abs(tpc - s.Tpc) <= 14);
        return spelling.Name + ((pitch - spelling.PitchClass - (tpc - spelling.Tpc) / 7) / 12).ToString(CultureInfo.InvariantCulture);
    }
    private static string ElementHash(XElement element) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting))));
    private static int ParseInt(XElement? element, int fallback) => int.TryParse(element?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    private static int ReadDots(XElement item) => item.Element("dots") is { } dots ? ParseInt(dots, -1) : 0;
    private static string First(params string[] values) => values.FirstOrDefault(value => value.Length > 0) ?? "";
    private static int Mod12(int value) => (value % 12 + 12) % 12;
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    private static readonly (string Name, int Tpc, int PitchClass)[] Spellings = [("C", 14, 0), ("D", 16, 2), ("E", 18, 4), ("F", 13, 5), ("G", 15, 7), ("A", 17, 9), ("B", 19, 11)];
    private static readonly HashSet<string> SimpleNoteElements = new(StringComparer.Ordinal)
    { "eid", "pitch", "tpc", "tpc2", "Accidental", "tuning", "small", "visible", "color", "offset", "pos", "play", "velocity", "veloType", "velocityType" };
    private static readonly HashSet<string> TimedAnnotations = new(StringComparer.Ordinal)
    { "eid", "Clef", "KeySig", "TimeSig", "Tempo", "Harmony", "Image", "BarLine", "Segment", "StaffText", "SystemText", "RehearsalMark", "Dynamic", "HairPin", "Hairpin", "TextLine", "Ottava", "Pedal", "Slur", "Tie", "Spanner", "endSpanner", "Fermata", "Breath", "Articulation", "Beam", "FretDiagram", "FiguredBass", "Symbol", "TremoloBar", "StaffTypeChange", "InstrumentChange" };
    private sealed record Address(int Staff, int Measure, int Voice, int ElementIndex, int NoteIndex);
    private sealed record NoteTarget(XElement Node, MusicNote View, Address Address, double? Quarter);
    private sealed record EventTarget(XElement Node, MusicEvent View, double? Length);
    private sealed record HarmonyTarget(XElement Node, MusicHarmony View, Address Address);
    private sealed record Anchor(double? Quarter, string? EventId);
    private sealed record VoiceTarget(int Staff, int Measure, int Voice, XElement Node, IReadOnlyList<EventTarget> Events, IReadOnlyList<Anchor> Anchors, double? Length);
    private sealed record State(NativeScoreArchive Archive, NativeMusicModel Model, Dictionary<string, NoteTarget> Notes,
        Dictionary<string, EventTarget> Events, Dictionary<string, HarmonyTarget> Harmonies, IReadOnlyList<VoiceTarget> Voices);
}
