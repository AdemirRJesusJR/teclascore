using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Teclas.Core;

internal static class MusicEditorChecks
{
    internal static void Run(Action<bool, string> check, string work)
    {
        string directory = Path.Combine(work, "music-editor");
        Directory.CreateDirectory(directory);
        int sequence = 0;
        const string meter = "<TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>";
        const string harmony = "<Harmony><harmonyInfo><root>14</root></harmonyInfo></Harmony>";
        const string part = "<Part><Staff id='1'><StaffType group='pitched'/></Staff><Instrument id='piano'/></Part>";
        static string Note(int pitch = 60, int tpc = 14, string extras = "") => $"<Note><pitch>{pitch}</pitch><tpc>{tpc}</tpc>{extras}</Note>";
        static string Chord(string duration = "quarter", string? notes = null, string extras = "") => $"<Chord><durationType>{duration}</durationType>{extras}{notes ?? Note()}</Chord>";
        static string Rest(string duration = "quarter", string extras = "") => $"<Rest><durationType>{duration}</durationType>{extras}</Rest>";
        static string Voice(string content) => "<voice>" + content + "</voice>";
        static string Measure(string content, string attrs = "") => $"<Measure {attrs}>{content}</Measure>";
        static string Staff(string bars) => "<Staff id='1'>" + bars + "</Staff>";
        string Four(string? before = null) => meter + before + Chord() + Chord(notes: Note(62, 16)) + Rest() + Chord(notes: Note(64, 18));
        string Write(string staff, string definition = part, string extraScore = "", Dictionary<string, byte[]>? entries = null)
        {
            string path = Path.Combine(directory, $"fixture-{++sequence}.mscz");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false)))
                writer.Write($"<museScore version='4.70'><Score><metaTag name='workTitle'>Estudo</metaTag>{definition}{staff}{extraScore}</Score></museScore>");
            using (var stream = zip.CreateEntry("preserve.bin").Open()) stream.Write([1, 2, 3, 4, 255]);
            if (entries is not null) foreach (var entry in entries)
            { using var stream = zip.CreateEntry(entry.Key).Open(); stream.Write(entry.Value); }
            return path;
        }
        NativeMusicModel Model(string voice, string attrs = "", string definition = part) => NativeMusicEditor.Open(Write(Staff(Measure(Voice(voice), attrs)), definition));
        static MusicEvent First(NativeMusicModel model) => model.Measures[0].Events[0];
        static MusicNote N(NativeMusicModel model) => First(model).Notes[0];
        static NativeMusicEdits Pitch(NativeMusicModel model, int pitch = 61, int tpc = 21) => new([new(N(model).Id, pitch, tpc)], [], []);
        static bool Valid(NativeMusicModel model, NativeMusicEdits edits) => NativeMusicEditor.Validate(model, edits).Count == 0;
        static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
        string Draft(NativeMusicModel model, NativeMusicEdits edits)
        { string destination = Path.Combine(directory, $"draft-{++sequence}.mscz"); NativeMusicEditor.WriteDraft(model, edits, destination); return destination; }
        static XDocument Xml(string path)
        { using var zip = ZipFile.OpenRead(path); using var stream = zip.GetEntry("score.mscx")!.Open(); return XDocument.Load(stream); }
        static NativeMusicEdits Rhythm(NativeMusicModel model) => new([], [new(model.Measures[0].Events[0].Id, "quarter", 1), new(model.Measures[0].Events[1].Id, "eighth", 0)], []);

        var model = Model(Four());
        byte[] source = File.ReadAllBytes(model.SourcePath);
        check(model.Metadata.Title == "Estudo" && model.Measures.Count == 1 && model.Measures[0].Events.Count == 4 && N(model).Name == "C4",
            "Music editor reads native events, notes and metadata without changing the score");
        check(model.Measures[0].Events.Select(e => e.Quarter).SequenceEqual(new double?[] { 0, 1, 2, 3 }) && First(model).Voice == 1 && First(model).Staff == 1,
            "Music editor addresses staff, measure, voice and quarter positions explicitly");
        check(NativeMusicEditor.Open(model.SourcePath).Measures[0].Events.Select(e => e.Id).SequenceEqual(model.Measures[0].Events.Select(e => e.Id)) &&
            model.SourceSha256 == Convert.ToHexString(SHA256.HashData(source)), "Music editor IDs and source hashes are stable for the immutable snapshot");
        check(model.Measures[0].Events.All(e => e.CanEditDuration) && model.Measures[0].Events.SelectMany(e => e.Notes).All(n => n.CanEdit),
            "Ordinary piano notes and unconnected rhythm events are editable");
        check(NativeMusicEditor.NoteName(60, 26) == "B#3" && NativeMusicEditor.NoteName(59, 7) == "Cb4" && NativeMusicEditor.NoteName(62, 28) == "C##4",
            "Pitch names preserve enharmonic spelling and octave boundaries");
        check(NativeMusicEditor.IsValidPitch(61, 21) && !NativeMusicEditor.IsValidPitch(61, 14) && !NativeMusicEditor.IsValidPitch(128, 16) && !NativeMusicEditor.IsValidPitch(-1, 19),
            "Pitch changes require a valid MIDI range and a compatible tonal pitch class");
        check(Valid(model, Pitch(model)), "A single unconnected pitch change validates");
        string draft = Draft(model, Pitch(model));
        var patched = NativeMusicEditor.Open(draft);
        check(N(patched).Pitch == 61 && N(patched).Tpc == 21 && patched.Metadata.NoteCount == model.Metadata.NoteCount &&
            patched.Measures[0].Events[1].Notes[0].Pitch == 62, "Draft changes only the selected pitch and spelling");
        using (var zip = ZipFile.OpenRead(draft))
        { using var stream = zip.GetEntry("preserve.bin")!.Open(); using var memory = new MemoryStream(); stream.CopyTo(memory); check(memory.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4, 255 }), "Draft retains unknown ZIP assets byte for byte"); }
        check(File.ReadAllBytes(model.SourcePath).SequenceEqual(source), "Native editing never rewrites the source score");
        var expectedXml = Xml(model.SourcePath); expectedXml.Descendants("Note").First().SetElementValue("pitch", 61); expectedXml.Descendants("Note").First().SetElementValue("tpc", 21);
        check(XNode.DeepEquals(expectedXml, Xml(draft)), "A pitch draft is exactly the requested native XML patch");
        check(!Valid(model, new([], [], [])) && !Valid(model, Pitch(model, 60, 14)), "Empty and no-op edits do not create redundant versions");
        check(!Valid(model, new([new("invalid", 61, 21)], [], [])) && !Valid(model, new([new(N(model).Id, 61, 21), new(N(model).Id, 62, 16)], [], [])),
            "Unknown and duplicate note references are rejected");
        check(!Valid(model, Pitch(model, 61, 14)), "Invalid note spelling is rejected before writing");
        check(Throws<IOException>(() => NativeMusicEditor.WriteDraft(model, Pitch(model), model.SourcePath)) && Throws<IOException>(() => NativeMusicEditor.WriteDraft(model, Pitch(model), draft)),
            "Draft cannot overwrite the source or any existing score");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel(); string canceled = Path.Combine(directory, "cancelled.mscz");
            check(Throws<OperationCanceledException>(() => NativeMusicEditor.WriteDraft(model, Pitch(model), canceled, cts.Token)) && !File.Exists(canceled), "Cancelled native edits create no output");
        }
        var stale = model with { SourceSha256 = new string('0', 64) };
        check(!Valid(stale, Pitch(stale)) && Throws<IOException>(() => Draft(stale, Pitch(stale))), "Source hash changes prevent stale edits at validation and writing");
        check(!Directory.EnumerateFiles(directory, ".teclas-draft-*").Any(), "Draft completion and rejection leave no temporary patch files");

        check(Valid(model, Rhythm(model)), "Compensated durations within a voice and bar validate as one batch");
        var rhythm = NativeMusicEditor.Open(Draft(model, Rhythm(model)));
        check(rhythm.Measures[0].Events.Select(e => e.Quarter).SequenceEqual(new double?[] { 0, 1.5, 2, 3 }) && rhythm.Measures[0].Events[0].Dots == 1,
            "Rhythm batches update duration and dots while preserving later event positions");
        check(!Valid(model, new([], [new(First(model).Id, "half", 0)], [])), "A duration batch cannot change a voice total or shift untouched events");
        check(!Valid(model, new([], [new(First(model).Id, "quarter", 4)], [])) && !Valid(model, new([], [new(First(model).Id, "unknown", 0)], [])),
            "Unknown figures and unsupported dots cannot be written");
        var annotation = Model(meter + Chord() + harmony + Chord() + Rest() + Chord());
        check(!Valid(annotation, Rhythm(annotation)), "Compensated durations cannot silently move an intervening chord symbol");
        var dynamic = Model(meter + Chord() + "<Dynamic><subtype>mf</subtype></Dynamic>" + Chord() + Rest() + Chord());
        check(!Valid(dynamic, Rhythm(dynamic)), "Compensated durations cannot silently move an intervening dynamic");
        var after = Model(Four() + harmony);
        check(Valid(after, Rhythm(after)), "A batch may preserve a following annotation at its original position");
        var voicePath = Write(Staff(Measure(Voice(Four()) + Voice(meter + Rest("whole")))));
        var twoVoices = NativeMusicEditor.Open(voicePath);
        check(twoVoices.Measures[0].Events[4].Voice == 2 && twoVoices.Measures[0].Events[4].Quarter == 0 && Valid(twoVoices, Rhythm(twoVoices)),
            "Independent voice positions and totals are not conflated");
        check(!Valid(twoVoices, new([], [new(twoVoices.Measures[0].Events[0].Id, "half", 0), new(twoVoices.Measures[0].Events[4].Id, "half", 1)], [])),
            "One voice cannot compensate duration changes in another voice");
        var pickup = Model(meter + Chord("eighth") + Chord("eighth"), "len='1/4'");
        check(pickup.Measures[0].Events.All(e => e.CanEditDuration) && Valid(pickup, new([], [new(pickup.Measures[0].Events[0].Id, "eighth", 1), new(pickup.Measures[0].Events[1].Id, "16th", 0)], [])),
            "Explicit pickup lengths allow safe compensated rhythm edits");
        foreach (var (name, content) in new[]
        {
            ("tuplets", meter + "<Tuplet id='1'><normalNotes>2</normalNotes><actualNotes>3</actualNotes></Tuplet>" + Chord("quarter", extras: "<Tuplet>1</Tuplet>")),
            ("location", meter + Chord() + "<location><fractions>-1/4</fractions></location>" + Chord()),
            ("unknown timing", meter + "<FutureTiming><value>1</value></FutureTiming>" + Chord()),
            ("grace notes", meter + Chord("eighth", extras: "<acciaccatura/>") + Chord("whole")),
            ("overflow", meter + Chord("whole") + Chord()),
            ("malformed dots", meter + Chord(extras: "<dots>NaN</dots>")),
            ("missing meter", Chord()),
            ("explicit inconsistent duration", meter + Chord(extras: "<duration>1/8</duration>"))
        })
        { var special = Model(content); check(special.Measures[0].Events.All(e => !e.CanEditDuration), "Unsupported rhythm is protected explicitly: " + name); }
        var fullRest = Model(meter + Rest("measure", "<duration>1/1</duration>"));
        check(!First(fullRest).CanEditDuration, "Full-measure rests retain their native relationship to the meter");

        foreach (var (name, noteExtras, chordExtras) in new[]
        {
            ("tie", "<Spanner type='Tie'><Tie/></Spanner>", ""),
            ("microtonal tuning", "<tuning>50</tuning>", ""),
            ("microtonal accidental", "<Accidental><subtype>accidentalQuarterToneSharpStein</subtype></Accidental>", ""),
            ("manual accidental", "<Accidental><subtype>accidentalSharp</subtype><role>1</role></Accidental>", ""),
            ("unknown accidental role", "<Accidental><subtype>accidentalSharp</subtype><role>9</role></Accidental>", ""),
            ("tablature note", "<string>2</string><fret>5</fret>", ""),
            ("transposed spelling", "<tpc2>16</tpc2>", ""),
            ("unknown note property", "<FuturePitch>7</FuturePitch>", ""),
            ("cross-staff note", "", "<staffMove>1</staffMove>"),
            ("chord-level connection", "", "<Spanner type='Slur'><Slur/></Spanner>")
        })
        { var special = Model(meter + Chord("whole", Note(extras: noteExtras), chordExtras)); check(!N(special).CanEdit && !Valid(special, Pitch(special)), "Protected pitch cannot be edited: " + name); }
        var tie = Model(meter + Chord(notes: Note(extras: "<Spanner type='Tie'><Tie/></Spanner>")) + Chord() + Rest() + Chord());
        check(!First(tie).CanEditDuration && tie.Measures[0].Events[1].Notes[0].CanEdit, "A tied target is protected without disabling independent unconnected pitches");
        var percussion = Model(Four(), definition: part.Replace("pitched", "percussion"));
        var transposing = Model(Four(), definition: part.Replace("<Instrument id='piano'/>", "<Instrument><transposeChromatic>2</transposeChromatic></Instrument>"));
        check(!N(percussion).CanEdit && !N(transposing).CanEdit && !First(transposing).CanEditDuration, "Percussion and transposing staff relationships remain protected");
        var linked = NativeMusicEditor.Open(Write(Staff(Measure(Voice(Four()))), extraScore: "<Excerpt><name>Piano</name></Excerpt>"));
        var linkedArchive = NativeMusicEditor.Open(Write(Staff(Measure(Voice(Four()))), entries: new() { ["Parts/piano.mscx"] = Encoding.UTF8.GetBytes("<museScore version='4.70'><Score/></museScore>") }));
        check(!N(linked).CanEdit && !N(linkedArchive).CanEdit && linked.Limitations.Any(text => text.Contains("vinculad", StringComparison.Ordinal)),
            "Linked parts block editing globally until synchronization can be guaranteed");
        var normalAccidental = Model(meter + Chord("whole", Note(61, 21, "<Accidental><subtype>accidentalSharp</subtype></Accidental>")));
        check(N(normalAccidental).CanEdit && !Xml(Draft(normalAccidental, Pitch(normalAccidental, 62, 16))).Descendants("Accidental").Any(),
            "Only an edited note's ordinary automatic accidental may be recalculated");
        var unchangedAccidental = Model(meter + Chord(notes: Note(61, 21, "<Accidental><subtype>accidentalSharp</subtype></Accidental>")) + Chord() + Rest() + Chord());
        var unchangedEdits = new NativeMusicEdits([new(N(unchangedAccidental).Id, 61, 21), new(unchangedAccidental.Measures[0].Events[1].Notes[0].Id, 62, 16)], [], []);
        check(Xml(Draft(unchangedAccidental, unchangedEdits)).Descendants("Accidental").Count() == 1, "A no-op pitch command cannot erase an unedited accidental");
        var custom = Model(Four() + "<FutureAnnotation id='123'>unchanged</FutureAnnotation>");
        string customDraft = Draft(custom, Pitch(custom));
        check(Xml(customDraft).Descendants("FutureAnnotation").Single().ToString() == Xml(custom.SourcePath).Descendants("FutureAnnotation").Single().ToString(),
            "Unknown musical XML is preserved while safe independent pitch edits remain possible");
        var harmonyModel = Model(Four(harmony)); var h = harmonyModel.Measures[0].Harmonies.Single();
        check(h.CanEdit && h.Root == "C" && h.Quarter == 0 && !h.HasDiagram, "Native harmony occurrences expose editable context without inventing diagrams");
        string harmonyDraft = Draft(harmonyModel, new([], [], [new(h.Id, "D", "m")]));
        check(NativeMusicEditor.Open(harmonyDraft).Measures[0].Harmonies.Single().Root == "D" && NativeMusicEditor.Open(harmonyDraft).Measures[0].Harmonies.Single().Quality == "m" &&
            Xml(harmonyDraft).Descendants("Note").Select(n => n.ToString()).SequenceEqual(Xml(harmonyModel.SourcePath).Descendants("Note").Select(n => n.ToString())),
            "Harmony patches integrate the native helper while preserving every note");
        check(!Valid(harmonyModel, new([], [], [new(h.Id, "H", "m")])) && !Valid(harmonyModel, new([], [], [new(h.Id, "C", "", 1)])),
            "Invalid harmony roots and no-op inversion changes without diagrams are rejected");
        string Mutate(string path, Action<XDocument> mutate)
        {
            string destination = Path.Combine(directory, $"mutated-{++sequence}.mscz");
            using var input = ZipFile.OpenRead(path);
            using var output = ZipFile.Open(destination, ZipArchiveMode.Create);
            foreach (var entry in input.Entries)
            {
                using var reader = entry.Open(); using var writer = output.CreateEntry(entry.FullName).Open();
                if (entry.FullName == "score.mscx") { var document = XDocument.Load(reader); mutate(document); document.Save(writer); }
                else reader.CopyTo(writer);
            }
            return destination;
        }
        var accidentalSource = NativeMusicEditor.Open(Write(Staff(Measure(Voice(meter + Chord() + Chord() + Chord(notes: Note(62, 16)) + Chord(notes: Note(48, 14)))) +
            Measure(Voice(Chord("whole"))))));
        var accidentalEdit = Pitch(accidentalSource);
        string accidentalDraft = Draft(accidentalSource, accidentalEdit);
        var normalize = typeof(NativeMusicEditor).GetMethod("FingerprintIgnoringEditedAutomaticAccidentals", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        string Fingerprint(string path) => (string)normalize.Invoke(null, [path, accidentalEdit.Notes, accidentalSource, CancellationToken.None])!;
        string expected = Fingerprint(accidentalDraft);
        static XElement Glyph(string subtype = "accidentalNatural", string role = "") => new("Accidental", new XElement("subtype", subtype), role.Length > 0 ? new XElement("role", role) : null);
        string Dependent(Action<XElement> change) => Mutate(accidentalDraft, xml => change(xml.Descendants("Note").ElementAt(1)));
        check(Fingerprint(Dependent(note => note.AddFirst(Glyph()))) == expected,
            "Accidental comparison permits a matching automatic natural on the dependent later note");
        check(Fingerprint(Dependent(note => note.AddFirst(Glyph("accidentalSharp")))) != expected,
            "Accidental comparison rejects a glyph inconsistent with the unchanged pitch spelling");
        check(Fingerprint(Dependent(note => note.AddFirst(Glyph(role: "1")))) != expected,
            "Accidental comparison never ignores a manual accidental on a dependent note");
        check(Fingerprint(Dependent(note => note.AddFirst(Glyph("accidentalQuarterToneSharpStein")))) != expected,
            "Accidental comparison never ignores a microtonal or special glyph");
        check(Fingerprint(Mutate(accidentalDraft, xml => xml.Descendants("Note").ElementAt(2).AddFirst(Glyph()))) != expected,
            "Accidental comparison remains strict for another diatonic line in the same bar");
        check(Fingerprint(Mutate(accidentalDraft, xml => xml.Descendants("Note").ElementAt(3).AddFirst(Glyph()))) != expected,
            "Accidental comparison remains strict for the same letter in another octave");
        check(Fingerprint(Mutate(accidentalDraft, xml => xml.Descendants("Note").ElementAt(4).AddFirst(Glyph()))) != expected,
            "Accidental comparison remains strict after the current barline");
        check(Throws<System.Reflection.TargetInvocationException>(() => Fingerprint(Dependent(note => note.SetElementValue("pitch", 62)))),
            "Accidental normalization cannot hide a changed pitch on an unedited dependent note");
        check(Fingerprint(Dependent(note => note.AddFirst(new XElement("Spanner", new XAttribute("type", "Tie"), new XElement("Tie"))))) != expected,
            "Accidental normalization retains every tie and other non-accidental property");
        check(File.ReadAllBytes(model.SourcePath).SequenceEqual(source) && !Directory.EnumerateFiles(directory, ".teclas-draft-*").Any(),
            "All editor checks preserve their sources and leave no pending draft files");
    }
    internal static async Task RunNativeAsync(Action<bool, string> check, string work, string nativeSource, string executable)
    {
        string directory = Path.Combine(work, "music-editor-native");
        Directory.CreateDirectory(directory);
        string musicXml = Path.Combine(directory, "editable.musicxml");
        File.WriteAllText(musicXml, """
        <?xml version="1.0" encoding="utf-8"?>
        <score-partwise version="4.0"><work><work-title>Editor musical</work-title></work>
        <part-list><score-part id="P1"><part-name>Piano</part-name><score-instrument id="I1"><instrument-name>Piano</instrument-name></score-instrument><midi-instrument id="I1"><midi-channel>1</midi-channel><midi-program>1</midi-program></midi-instrument></score-part></part-list>
        <part id="P1"><measure number="1"><attributes><divisions>4</divisions><key><fifths>0</fifths></key><time><beats>4</beats><beat-type>4</beat-type></time><clef><sign>G</sign><line>2</line></clef></attributes>
        <note><pitch><step>C</step><octave>4</octave></pitch><duration>4</duration><type>quarter</type></note>
        <note><pitch><step>C</step><octave>4</octave></pitch><duration>4</duration><type>quarter</type></note>
        <note><pitch><step>E</step><octave>4</octave></pitch><duration>4</duration><type>quarter</type></note>
        <note><pitch><step>F</step><octave>4</octave></pitch><duration>4</duration><type>quarter</type></note>
        </measure><measure number="2"><note><pitch><step>G</step><octave>4</octave></pitch><duration>16</duration><type>whole</type></note></measure></part></score-partwise>
        """);
        string cache = Path.Combine(directory, "cache"), output = Path.Combine(directory, "published");
        string native = Path.Combine(directory, "editable.mscz");
        await new ScoreExporter(executable, cache).ExportNativeAsync(musicXml, native, CancellationToken.None);
        var model = NativeMusicEditor.Open(native);
        byte[] original = File.ReadAllBytes(native);
        var events = model.Measures[0].Events;
        var edits = new NativeMusicEdits([new(events[0].Notes[0].Id, 61, 21)], [new(events[0].Id, "quarter", 1), new(events[1].Id, "eighth", 0)], []);
        check(NativeMusicEditor.Validate(model, edits).Count == 0, "Real MuseScore notes allow a chromatic pitch and compensated rhythm batch");
        var service = new NativeMusicEditingService(executable, cache);
        var prepared = await service.PrepareAsync(model, edits, new SynchronousProgress(Console.WriteLine), CancellationToken.None);
        var preparedModel = NativeMusicEditor.Open(prepared.Path);
        check(preparedModel.Measures[0].Events[0].Notes[0].Name == "C#4" && preparedModel.Measures[0].Events[0].Dots == 1 &&
            preparedModel.Measures[0].Events[1].Quarter == 1.5 && preparedModel.Measures[0].Events[1].Notes[0].Name == "C4" && preparedModel.Measures[0].Events[2].Quarter == 2,
            "MuseScore reopens C-sharp then C-natural and dotted rhythm while preserving the next unedited onset");
        check(prepared.PreviewPages.Count > 0 && prepared.PreviewPages.All(File.Exists) && !Directory.Exists(output),
            "Preparation renders all pages privately without publishing a score");
        check(NativeScoreDocument.MusicalFingerprint(prepared.Path) == prepared.MusicalFingerprint && File.ReadAllBytes(native).SequenceEqual(original),
            "Prepared fingerprint matches the rendered score while the source remains byte-identical");
        static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
        { try { await action(); return false; } catch (T) { return true; } }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            check(await ThrowsAsync<OperationCanceledException>(async () => await service.PublishAsync(model, prepared, output, null, cancelled.Token)) && !Directory.Exists(output),
                "Cancellation before publication creates no output directory");
        }
        check(await ThrowsAsync<InvalidOperationException>(async () => await service.PublishAsync(model, prepared with { MusicalFingerprint = "forged" }, output, null, CancellationToken.None)),
            "A forged prepared score cannot bypass the preparation registry");
        using (var cancelled = new CancellationTokenSource())
        {
            check(await ThrowsAsync<OperationCanceledException>(async () => await service.PublishAsync(model, prepared, output,
                new SynchronousProgress(message => { if (message.StartsWith("Salvando", StringComparison.Ordinal)) cancelled.Cancel(); }), cancelled.Token)) &&
                (!Directory.Exists(output) || !Directory.EnumerateFileSystemEntries(output).Any()),
                "Cancellation during publication removes only pending copies and keeps the approved preview reusable");
        }
        byte[] previewBytes = File.ReadAllBytes(prepared.Path);
        File.AppendAllText(prepared.Path, "tamper");
        check(await ThrowsAsync<IOException>(async () => await service.PublishAsync(model, prepared, output, null, CancellationToken.None)),
            "Prepared MSCZ mutation is rejected even when the ZIP can still be opened");
        File.WriteAllBytes(prepared.Path, previewBytes);
        byte[] pageBytes = File.ReadAllBytes(prepared.PreviewPages[0]);
        File.AppendAllText(prepared.PreviewPages[0], "tamper");
        check(await ThrowsAsync<IOException>(async () => await service.PublishAsync(model, prepared, output, null, CancellationToken.None)),
            "Prepared page mutation is rejected before publication");
        File.WriteAllBytes(prepared.PreviewPages[0], pageBytes);
        File.AppendAllText(native, "external change");
        check(await ThrowsAsync<IOException>(async () => await service.PublishAsync(model, prepared, output, null, CancellationToken.None)),
            "An external source change invalidates an already approved preview");
        File.WriteAllBytes(native, original);
        var published = await new NativeMusicEditingService(executable, cache).PublishAsync(model, prepared, output, null, CancellationToken.None);
        check(File.Exists(published.Path) && published.Path != native && published.Path != prepared.Path && published.PreviewPages.All(File.Exists) &&
            File.ReadAllBytes(published.Path).SequenceEqual(previewBytes), "A different service instance publishes the exact approved native score and all its pages as a new version");
        check(await ThrowsAsync<InvalidOperationException>(async () => await service.PublishAsync(model, prepared, output, null, CancellationToken.None)) &&
            Directory.GetDirectories(output).Length == 1, "The same approved preview cannot publish duplicate versions");
        check(File.ReadAllBytes(native).SequenceEqual(original) && !Directory.EnumerateDirectories(output, ".teclas-edicao-*").Any(),
            "Successful and interrupted publication preserve the original and leave no pending directory");

        byte[] realBytes = File.ReadAllBytes(nativeSource);
        var real = NativeMusicEditor.Open(nativeSource);
        var realHarmony = real.Measures.SelectMany(m => m.Harmonies).First(h => h.CanEdit && h.HasDiagram);
        string newRoot = realHarmony.Root == "C" ? "D" : "C";
        var realEdits = new NativeMusicEdits([], [], [new(realHarmony.Id, newRoot, "m", 1)]);
        var realPrepared = await service.PrepareAsync(real, realEdits, new SynchronousProgress(Console.WriteLine), CancellationToken.None);
        var realAfter = NativeMusicEditor.Open(realPrepared.Path);
        var harmonyAfter = realAfter.Measures.First(m => m.Staff == realHarmony.Staff && m.Number == realHarmony.Measure).Harmonies
            .First(h => h.Voice == realHarmony.Voice && h.Quarter == realHarmony.Quarter);
        check(harmonyAfter.Root == newRoot && harmonyAfter.Quality == "m" && harmonyAfter.DiagramInversion == 1 && harmonyAfter.HasDiagram,
            "A real generated score reopens the edited chord occurrence and its matching custom inversion diagram");
        check(realAfter.Metadata.NoteCount == real.Metadata.NoteCount && realAfter.Metadata.HarmonyCount == real.Metadata.HarmonyCount &&
            realAfter.Metadata.ImageCount == real.Metadata.ImageCount && File.ReadAllBytes(nativeSource).SequenceEqual(realBytes),
            "Editing a real chord and diagram preserves the complete musical inventory and the user's original bytes");
        var realPublished = await service.PublishAsync(real, realPrepared, output, null, CancellationToken.None);
        check(realPublished.PreviewPages.Count == realPrepared.PreviewPages.Count && File.Exists(realPublished.Path),
            "A reviewed real score publishes all pages and its new native file successfully");
        File.WriteAllText(Path.Combine(directory, "results.json"), System.Text.Json.JsonSerializer.Serialize(new { published, realPublished }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
