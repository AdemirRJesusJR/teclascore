using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Teclas.Core;

/// <summary>Lyrics editor: correct, move, join, split and paste syllables, saved as verse 1 lyrics without touching the music.</summary>
internal static class LyricsEditorChecks
{
    internal static async Task Run(Action<bool, string> check, string work, string? museScore)
    {
        string dir = Path.Combine(work, "lyrics-editor"); Directory.CreateDirectory(dir); int sequence = 0;
        const string meter = "<TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>";
        static string L(string text, string syllabic = "single") =>
            $"<Lyrics>{(syllabic == "single" ? "" : $"<syllabic>{syllabic}</syllabic>")}<text>{text}</text></Lyrics>";
        static string C(int pitch, string length = "quarter", string lyric = "") =>
            $"<Chord><durationType>{length}</durationType>{lyric}<Note><pitch>{pitch}</pitch><tpc>{(pitch % 12) switch { 2 => 16, 4 => 18, 5 => 13, 7 => 15, 9 => 17, _ => 14 }}</tpc></Note></Chord>";
        static string R(string length = "quarter") => $"<Rest><durationType>{length}</durationType></Rest>";
        string Write(string bars, string? lower = null)
        {
            string file = Path.Combine(dir, $"source-{++sequence}.mscz"); using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false));
            string staffs = $"<Staff id='1'>{bars}</Staff>" + (lower is null ? "" : $"<Staff id='2'>{lower}</Staff>");
            string parts = "<Staff id='1'><StaffType group='pitched'><name>stdNormal</name></StaffType></Staff>" + (lower is null ? "" : "<Staff id='2'><StaffType group='pitched'><name>stdNormal</name></StaffType></Staff>");
            writer.Write($"<museScore version='4.70'><programVersion>4.7.5</programVersion><Score><Part>{parts}<Instrument id='piano'><trackName>Piano</trackName></Instrument></Part>{staffs}</Score></museScore>");
            return file;
        }
        static string Bar(string content) => $"<Measure><voice>{content}</voice></Measure>";
        NativeMusicModel Draft(NativeMusicModel model, NativeMusicEdits edits)
        { string path = Path.Combine(dir, $"draft-{++sequence}.mscz"); NativeMusicEditor.WriteDraft(model, edits, path); return NativeMusicEditor.Open(path); }
        static XDocument Xml(string path) { using var zip = ZipFile.OpenRead(path); using var stream = zip.GetEntry("score.mscx")!.Open(); return XDocument.Load(stream); }

        // "Ca-sa a-zul" over C C E F | G(half) rest A, with a lower staff that must never receive syllables.
        string source = Write(
            Bar(meter + C(60, lyric: L("Ca", "begin")) + C(60, lyric: L("sa", "end")) + C(64, lyric: L("a", "begin")) + C(65, lyric: L("zul", "end"))) +
            Bar(C(67, "half") + R() + C(69)),
            Bar(meter + C(48, "whole")) + Bar(C(43, "whole")));
        byte[] originalBytes = File.ReadAllBytes(source);
        var model = NativeMusicEditor.Open(source);
        var editor = LyricsEditorDocument.FromModel(model);
        check(editor.Slots.Count == 6 && editor.Slots.All(s => model.Measures.Single(m => m.Staff == 1 && m.Number == s.Measure).Events.Any(e => e.Id == s.EventId)),
            "The lyrics editor lists the melody notes of the top staff only");
        check(editor.Slots.Select(s => s.Text).SequenceEqual(new[] { "Ca", "sa", "a", "zul", "", "" }) && editor.Slots[0].Continues && !editor.Slots[1].Continues,
            "Existing syllables and hyphens are read from the score");
        check(editor.Slots[5].AfterRest && !editor.Slots[4].AfterRest && editor.Verses().Count == 2 && editor.Verses()[0].Text == "Casa azul",
            "Verses break after a rest and show whole words");
        check(!editor.HasChanges, "An untouched editor has nothing to save");

        editor.SetText(1, "za");
        var corrected = Draft(model, editor.ToEdits());
        check(corrected.Measures[0].Events[1].Lyric == "za" && corrected.Measures[0].Events[0].Lyric == "Ca" && editor.ToEdits().Lyrics.Count == 1,
            "Correcting one syllable changes only that note");
        check(corrected.Measures.Where(m => m.Staff == 1).SelectMany(m => m.Events).Select(e => (e.Quarter, e.DurationQuarter, e.Notes.Select(n => n.Pitch).FirstOrDefault()))
            .SequenceEqual(model.Measures.Where(m => m.Staff == 1).SelectMany(m => m.Events).Select(e => (e.Quarter, e.DurationQuarter, e.Notes.Select(n => n.Pitch).FirstOrDefault()))),
            "Lyric edits keep every pitch, onset and duration");
        check(File.ReadAllBytes(source).SequenceEqual(originalBytes), "Lyric edits never change the opened score");

        // Move "zul" to the next note (G); then move it back.
        editor = LyricsEditorDocument.FromModel(model);
        editor.Move(3, 1);
        check(editor.Slots[3].Text == "" && editor.Slots[4].Text == "zul" && editor.Slots[2].Continues, "A syllable moves to the next free note and keeps its word");
        var moved = Draft(model, editor.ToEdits());
        check(moved.Measures[0].Events[3].Lyric == "" && moved.Measures[1].Events[0].Lyric == "zul" && !moved.Measures[1].Events[0].LyricContinues
            && moved.Measures[0].Events[2].LyricContinues, "The moved syllable is saved on the new note with its hyphen");
        var movedXml = Xml(Path.Combine(dir, $"draft-{sequence}.mscz"));
        var gChord = movedXml.Descendants("Chord").Single(c => (int?)c.Element("Note")?.Element("pitch") == 67);
        check(gChord.Element("Lyrics")?.ElementsAfterSelf("Note").Any() == true && (string?)gChord.Element("Lyrics")?.Element("syllabic") == "end",
            "New lyrics are written before the note, as MuseScore does");
        editor.Move(4, -1);
        check(!editor.HasChanges, "Moving back restores the original lyric");
        // Moving into a taken note pushes the next syllables along.
        editor.Move(0, 1);
        check(editor.Slots.Select(s => s.Text).SequenceEqual(new[] { "", "Ca", "sa", "a", "zul", "" }), "Moving into a taken note shifts the following syllables to the first free note");
        try { var full = LyricsEditorDocument.FromModel(model); full.Paste("um dois três quatro cin co"); full.Move(0, 1); check(false, "Move without a free note accepted"); }
        catch (InvalidOperationException) { check(true, "A move with no free note in that direction is refused"); }

        // Join and split syllables.
        editor = LyricsEditorDocument.FromModel(model);
        editor.JoinWithNext(0);
        check(editor.Slots[0].Text == "Casa" && !editor.Slots[0].Continues && editor.Slots[1].Text == "", "Joining two syllables of a word writes the word on one note");
        editor.Split(0, 2);
        check(editor.Slots[0].Text == "Ca" && editor.Slots[0].Continues && editor.Slots[1].Text == "sa" && !editor.HasChanges, "Splitting a syllable gives the next note the rest");
        editor.ToggleHyphen(1);
        var hyphen = editor.ToEdits().Lyrics;
        check(hyphen.Count == 2 && hyphen[0].Syllabic == "middle" && hyphen[1].Syllabic == "middle", "Linking two words updates the hyphens on both syllables");
        editor.ToggleHyphen(1);
        editor.SetWord(2, "anil");
        check(editor.Slots[2].Text == "a" && editor.Slots[3].Text == "nil", "Correcting a word divides it again over its notes");

        // Paste a whole lyric on a score without lyrics.
        string plain = Write(Bar(meter + C(60) + C(60) + C(64) + C(65)) + Bar(C(67, "half") + R() + C(69)) + Bar(C(65) + C(64) + C(62) + C(60)));
        var plainModel = NativeMusicEditor.Open(plain);
        var paste = LyricsEditorDocument.FromModel(plainModel);
        int left = paste.Paste("Que saudade\nde você");
        check(left == 0 && paste.Slots.Select(s => s.Text).Take(8).SequenceEqual(new[] { "Que", "sau", "da", "de", "", "de", "vo", "cê" }),
            "Pasting divides Portuguese syllables and starts the next verse after the rest");
        var pasted = Draft(plainModel, paste.ToEdits());
        var syllables = KaraokeLyrics.FromScore(pasted);
        check(syllables.Count == 7 && syllables[1].Text == "sau" && syllables[1].Continues && !syllables[0].Continues && !syllables[6].Continues,
            "The pasted lyric reads back from the saved score with its hyphens");
        check(pasted.Metadata.NoteCount == plainModel.Metadata.NoteCount, "Pasting a lyric keeps the notes");
        check(LyricsEditorDocument.FromModel(plainModel).Paste("um dois três quatro cinco seis sete oito nove dez onze doze") > 0, "Syllables without notes are reported, never added as notes");

        // Protections: rests, unknown ids, mixed edits and the duration lock of notes with lyrics.
        var restId = plainModel.Measures[1].Events.Single(e => e.Kind == "Rest").Id;
        check(NativeMusicEditor.Validate(plainModel, new([], [], []) { Lyrics = [new(restId, "la", "single")] }).Count > 0, "Lyrics cannot be put on a rest");
        check(NativeMusicEditor.Validate(plainModel, new([], [], []) { Lyrics = [new("stale", "la", "single")] }).Count > 0, "Stale lyric targets are refused");
        var first = plainModel.Measures[0].Events[0];
        check(NativeMusicEditor.Validate(plainModel, new([], [new(first.Id, "half", 0)], []) { Lyrics = [new(first.Id, "la", "single")] }).Count > 0,
            "A lyric and a duration change of the same note are saved separately");
        check(NativeMusicEditor.Validate(plainModel, new([], [], []) { Lyrics = [new(first.Id, "la\nla", "single")] }).Count > 0, "Line breaks inside a syllable are refused");
        check(NativeMusicEditor.Validate(model, new([], [new(model.Measures[0].Events[0].Id, "half", 0)], [])).Count > 0, "Notes with lyrics keep their duration protected");

        if (museScore is not null)
        {
            string canonical = Path.Combine(dir, "canonical.mscz");
            await new ScoreExporter(museScore, Path.Combine(dir, "cache")).ExportNativeAsync(plain, canonical, CancellationToken.None);
            var nativeModel = NativeMusicEditor.Open(canonical);
            var nativeEditor = LyricsEditorDocument.FromModel(nativeModel);
            nativeEditor.Paste("Que saudade\nde você");
            var service = new NativeMusicEditingService(museScore, Path.Combine(dir, "cache"));
            var prepared = await service.PrepareAsync(nativeModel, nativeEditor.ToEdits(), null, CancellationToken.None);
            check(prepared.PreviewPages.Count > 0 && LyricsEditorDocument.FromModel(NativeMusicEditor.Open(prepared.Path)).Slots.Count(s => s.Text.Length > 0) == 7,
                "MuseScore reopens and renders the pasted lyric");
            var published = await service.PublishAsync(nativeModel, prepared, Path.Combine(dir, "published"), null, CancellationToken.None);
            var again = LyricsEditorDocument.FromModel(NativeMusicEditor.Open(published.Path));
            again.Move(again.Slots.ToList().FindIndex(s => s.Text == "cê"), 1);
            var reprepared = await service.PrepareAsync(NativeMusicEditor.Open(published.Path), again.ToEdits(), null, CancellationToken.None);
            check(NativeMusicEditor.Open(reprepared.Path).Metadata.NoteCount == nativeModel.Metadata.NoteCount, "A moved syllable reopens through real MuseScore");
        }
    }
}
