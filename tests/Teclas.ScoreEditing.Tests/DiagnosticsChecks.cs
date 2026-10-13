using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Teclas.Core;

internal static class DiagnosticsChecks
{
    internal static void Run(Action<bool, string> check, string work)
    {
        string directory = Path.Combine(work, "diagnostics");
        Directory.CreateDirectory(directory);
        int sequence = 0;
        const string meter = "<TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>";
        const string harmony = "<Harmony><harmonyInfo><root>14</root></harmonyInfo></Harmony>";
        static string Note(string pitch, string extras = "") => $"<Note><pitch>{pitch}</pitch><tpc>14</tpc>{extras}</Note>";
        static string Chord(string duration, string notes, string extras = "") => $"<Chord><durationType>{duration}</durationType>{extras}{notes}</Chord>";
        static string Voice(string content) => $"<voice>{content}</voice>";
        static string Measure(string voices, string attributes = "", string extras = "") => $"<Measure {attributes}>{extras}{voices}</Measure>";
        static string Staff(string bars, int id = 1) => $"<Staff id='{id}'>{bars}</Staff>";
        string Whole() => Chord("whole", Note("60"));
        string BasicVoice() => Voice(meter + Whole());
        string BasicStaff() => Staff(Measure(BasicVoice()));
        string Write(string content, string title = "Estudo", Dictionary<string, byte[]>? entries = null, string? style = null)
        {
            string path = Path.Combine(directory, $"fixture-{++sequence}.mscz");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false)))
                writer.Write($"<museScore version='4.70'><Score><metaTag name='workTitle'>{title}</metaTag>{content}</Score></museScore>");
            if (style is not null)
                using (var writer = new StreamWriter(zip.CreateEntry("score_style.mss").Open(), new UTF8Encoding(false)))
                    writer.Write($"<museScore version='4.70'><Style>{style}</Style></museScore>");
            if (entries is not null)
                foreach (var entry in entries)
                {
                    using var stream = zip.CreateEntry(entry.Key).Open();
                    stream.Write(entry.Value);
                }
            return path;
        }
        ScoreDiagnosticReport Analyze(string content) => NativeScoreDiagnostics.Analyze(Write(content));
        static bool Has(ScoreDiagnosticReport report, string code) => report.Issues.Any(issue => issue.Code == code);
        static bool HasNoRhythmIssue(ScoreDiagnosticReport report) => !Has(report, "VOICE_TOO_LONG");

        string clean = Write(BasicStaff());
        byte[] original = File.ReadAllBytes(clean);
        DateTime lastWrite = File.GetLastWriteTimeUtc(clean);
        var cleanReport = NativeScoreDiagnostics.Analyze(clean);
        check(cleanReport.Issues.Count == 0 && cleanReport.Metadata.NoteCount == 1 && cleanReport.Metadata.Title == "Estudo",
            "Diagnostics reads a valid score without inventing musical or metadata problems");
        check(cleanReport.SourceSha256 == Convert.ToHexString(SHA256.HashData(original)) &&
            File.ReadAllBytes(clean).SequenceEqual(original) && File.GetLastWriteTimeUtc(clean) == lastWrite,
            "Diagnostics preserves source bytes and last-write time and returns its exact SHA-256");
        check(cleanReport.Limitations.Any(text => text.Contains("sobreposições", StringComparison.Ordinal)) &&
            cleanReport.Limitations.Any(text => text.Contains("numeração impressa", StringComparison.Ordinal)),
            "Diagnostics explains visual limits and ordinal measure numbering even for a clean result");
        var untitled = NativeScoreDiagnostics.Analyze(Write(BasicStaff(), title: "  "));
        check(Has(untitled, "TITLE_MISSING") && untitled.Issues.All(issue => !issue.Message.Contains("compositor", StringComparison.Ordinal)),
            "An empty title is an optional information point; empty composer and subtitle are not errors");
        var noStaff = Analyze("");
        check(noStaff.Issues.Any(issue => issue.Code == "STAFF_MISSING" && issue.Severity == ScoreDiagnosticSeverity.Error),
            "A score without staves receives a structural error");
        var rests = Analyze(Staff(Measure(Voice(meter + "<Rest><durationType>measure</durationType><duration>1/1</duration></Rest>"))));
        check(rests.Issues.Single().Code == "STAFF_WITHOUT_NOTES" && rests.Issues.Single().Severity == ScoreDiagnosticSeverity.Information,
            "A rest-only staff is informational rather than a corrupt or incomplete bar");

        var invalid = Analyze(Staff(Measure(BasicVoice())) + Staff(Measure(Voice(meter + Chord("whole", Note("128") + Note("-1") + "<Note/>"))), 7));
        check(invalid.Issues.Count(issue => issue.Code == "NOTE_PITCH_INVALID") == 3 && invalid.Issues.All(issue =>
                issue.Staff == 2 && issue.Measure == 1 && issue.Voice == 1 && issue.Location == "Pauta 2, compasso 1, voz 1"),
            "Invalid and missing pitches identify the ordinal staff, measure and voice independently from native IDs");
        var malformedPitch = Analyze(Staff(Measure(Voice(meter + Chord("whole", Note("NaN"))))));
        check(Has(malformedPitch, "NOTE_PITCH_INVALID"), "Non-numeric pitches are reported without crashing the diagnostic");
        var duplicate = Analyze(Staff(Measure(Voice(meter + Chord("whole", Note("60") + Note("60"))))));
        check(duplicate.Issues.Single().Code == "CHORD_NOTE_DUPLICATED" && duplicate.Issues.Single().Severity == ScoreDiagnosticSeverity.Review,
            "An exact repeated notehead in one chord requests musical review rather than automatic deletion");
        var microtones = Analyze(Staff(Measure(Voice(meter + Chord("whole", Note("60") + Note("60", "<tuning>50</tuning>"))))));
        check(!Has(microtones, "CHORD_NOTE_DUPLICATED"), "Microtonal differences are not collapsed into duplicate notes");
        var enharmonic = Analyze(Staff(Measure(Voice(meter + Chord("whole", Note("60") + "<Note><pitch>60</pitch><tpc>26</tpc></Note>")))));
        check(!Has(enharmonic, "CHORD_NOTE_DUPLICATED"), "Enharmonic noteheads are preserved for the musician to interpret");
        var polyphony = Analyze(Staff(Measure(BasicVoice() + Voice(Whole()))));
        check(polyphony.Issues.Count == 0, "Parallel voices are not added together and unisons across voices are not called duplicates");
        var chromatic = Analyze(Staff(Measure(Voice(meter + Chord("whole", Note("61"))))));
        check(chromatic.Issues.Count == 0, "Chromatic pitches are not declared musically wrong");

        var tooLong = Analyze(Staff(Measure(Voice(meter + Whole() + Chord("quarter", Note("64"))))));
        check(tooLong.Issues.Single().Code == "VOICE_TOO_LONG" && tooLong.Issues.Single().Message.Contains("5 semínimas", StringComparison.Ordinal),
            "An interpretable five-quarter voice in 4/4 receives a localized duration review");
        var shortVoice = Analyze(Staff(Measure(Voice(meter + Chord("quarter", Note("60"))))));
        check(HasNoRhythmIssue(shortVoice), "Partial voices are not falsely diagnosed as missing musical content");
        var pickup = Analyze(Staff(Measure(Voice(meter + Chord("quarter", Note("60"))), "len='1/4'", "<irregular>1</irregular>")));
        check(HasNoRhythmIssue(pickup) && pickup.Limitations.Count == 2, "An explicit pickup length overrides the nominal meter safely");
        var longPickup = Analyze(Staff(Measure(Voice(meter + Chord("half", Note("60"))), "len='1/4'")));
        check(Has(longPickup, "VOICE_TOO_LONG"), "The actual explicit pickup duration is used when detecting excess duration");
        var irregular = Analyze(Staff(Measure(Voice(meter + Whole() + Whole()), extras: "<irregular>1</irregular>")));
        check(HasNoRhythmIssue(irregular) && irregular.Limitations.Any(text => text.Contains("irregulares", StringComparison.Ordinal)),
            "Irregular bars without a declared length are skipped with an explicit limitation");
        var missingMeter = Analyze(Staff(Measure(Voice(Whole() + Whole()))));
        check(HasNoRhythmIssue(missingMeter) && missingMeter.Limitations.Any(text => text.Contains("sem fórmula", StringComparison.Ordinal)),
            "The diagnostic never guesses 4/4 when the native file has no known meter");
        var dotted = Analyze(Staff(Measure(Voice(meter + Chord("half", Note("60"), "<dots>1</dots>") + Chord("quarter", Note("62"))))));
        check(HasNoRhythmIssue(dotted), "Dotted durations are counted exactly once");
        var grace = Analyze(Staff(Measure(Voice(meter + Chord("eighth", Note("62"), "<acciaccatura/>") + Whole()))));
        check(HasNoRhythmIssue(grace), "Grace notes do not consume the notated duration of the bar");
        var afterGrace = Analyze(Staff(Measure(Voice(meter + Whole() + Chord("16th", Note("62"), "<grace16after/>")))));
        check(HasNoRhythmIssue(afterGrace), "After-grace notes do not falsely overflow a complete bar");
        var tied = Analyze(Staff(Measure(Voice(meter + Chord("whole", Note("60", "<Spanner type='Tie'><next><location><measures>1</measures></location></next></Spanner>")))) +
            Measure(Voice(Chord("whole", Note("60", "<Spanner type='Tie'><prev><location><measures>-1</measures></location></prev></Spanner>"))))));
        check(tied.Issues.Count == 0 && tied.Limitations.Count == 2, "Tie endpoint locations do not disable otherwise valid rhythmic analysis");
        var changedMeter = Analyze(Staff(Measure(BasicVoice()) + Measure(Voice("<TimeSig><sigN>3</sigN><sigD>4</sigD></TimeSig>" +
            Chord("half", Note("60"), "<dots>1</dots>"))) + Measure(Voice(Whole()))));
        check(changedMeter.Issues.Single().Code == "VOICE_TOO_LONG" && changedMeter.Issues.Single().Measure == 3,
            "A meter change persists to subsequent bars without changing prior bars");
        var polymeter = Analyze(Staff(Measure(Voice(meter + "<TimeSig><sigN>3</sigN><sigD>4</sigD><stretch>3/4</stretch></TimeSig>" + Whole() + Whole()))));
        check(HasNoRhythmIssue(polymeter) && polymeter.Limitations.Any(text => text.Contains("locais", StringComparison.Ordinal)),
            "Local stretched time signatures are excluded rather than misread as ordinary meter");
        var tuplets = Analyze(Staff(Measure(Voice(meter + "<Tuplet id='1'><normalNotes>2</normalNotes><actualNotes>3</actualNotes></Tuplet>" +
            Whole() + Whole() + "<endTuplet/>"))));
        check(HasNoRhythmIssue(tuplets) && tuplets.Limitations.Any(text => text.Contains("quiálteras", StringComparison.Ordinal)),
            "Tuplet timing remains an explicit limitation instead of a false overflow");
        var cursor = Analyze(Staff(Measure(Voice(meter + Whole() + "<location><fractions>-1/1</fractions></location>" + Whole()))));
        check(HasNoRhythmIssue(cursor) && cursor.Limitations.Any(text => text.Contains("deslocamentos", StringComparison.Ordinal)),
            "Explicit voice cursor movement prevents unsafe duration sums");
        var unknownDuration = Analyze(Staff(Measure(Voice(meter + Chord("unknown", Note("60")) + Whole() + Whole()))));
        check(HasNoRhythmIssue(unknownDuration) && unknownDuration.Limitations.Any(text => text.Contains("durações não reconhecidas", StringComparison.Ordinal)),
            "Unknown duration types do not generate false rhythmic findings");
        var unknownStructure = Analyze(Staff(Measure(Voice(meter + "<FutureRhythmFeature/>" + Whole() + Whole()))));
        check(HasNoRhythmIssue(unknownStructure) && unknownStructure.Limitations.Any(text => text.Contains("estruturas adicionais", StringComparison.Ordinal)),
            "Future unsupported voice structures fail closed for rhythmic diagnosis");

        var repeatedHarmony = Analyze(Staff(Measure(Voice(meter + harmony + harmony.Replace("</Harmony>", "<eid>different</eid><offset x='4' y='2'/></Harmony>") + Whole()))));
        check(repeatedHarmony.Issues.Single().Code == "HARMONY_DUPLICATED", "Identical harmony at the same voice position is detected even with different visual offsets");
        var harmonyOnBeats = Analyze(Staff(Measure(Voice(meter + harmony + Chord("half", Note("60")) + harmony + Chord("half", Note("60"))))));
        check(!Has(harmonyOnBeats, "HARMONY_DUPLICATED"), "Repeated harmony on distinct beats is not treated as a duplicate");
        var harmonyOnVoices = Analyze(Staff(Measure(Voice(meter + harmony + Whole()) + Voice(harmony + Whole()))));
        check(!Has(harmonyOnVoices, "HARMONY_DUPLICATED"), "The same harmony in separate voices is retained without guessing musical intent");
        var differentHarmony = Analyze(Staff(Measure(Voice(meter + harmony + harmony.Replace("<root>14</root>", "<root>17</root>") + Whole()))));
        check(!Has(differentHarmony, "HARMONY_DUPLICATED"), "Different harmonies at one position are not incorrectly merged");

        string imageContent = Staff(Measure(Voice(meter + "<Image><path>keyboard.svg</path></Image>" + Whole())));
        var missingImage = Analyze(imageContent);
        check(missingImage.Issues.Single().Code == "IMAGE_MISSING" && missingImage.Issues.Single().Measure == 1,
            "An absent embedded diagram is reported at its musical location without trying external paths");
        var existingImage = NativeScoreDiagnostics.Analyze(Write(imageContent, entries: new() { ["Pictures/keyboard.svg"] = Encoding.UTF8.GetBytes("<svg/>") }));
        check(!Has(existingImage, "IMAGE_MISSING"), "An image under the native Pictures prefix resolves correctly");
        var rootedImage = NativeScoreDiagnostics.Analyze(Write(imageContent.Replace("keyboard.svg", "Pictures/keyboard.svg"), entries: new() { ["Pictures/keyboard.svg"] = Encoding.UTF8.GetBytes("<svg/>") }));
        check(!Has(rootedImage, "IMAGE_MISSING"), "An already-prefixed embedded image path resolves without a double-prefix error");
        var invalidStyle = NativeScoreDiagnostics.Analyze(Write(BasicStaff(), style: "<pageWidth>NaN</pageWidth><spatium>0</spatium>"));
        check(invalidStyle.Issues.Count(issue => issue.Code == "PAGE_STYLE_INVALID") == 2, "Nonfinite and zero page dimensions are detected before a layout improvement");
        var oversizedStyle = NativeScoreDiagnostics.Analyze(Write(BasicStaff(), style: "<pageWidth>8</pageWidth><pagePrintableWidth>9</pagePrintableWidth><pageHeight>10</pageHeight><pageOddTopMargin>5</pageOddTopMargin><pageOddBottomMargin>5</pageOddBottomMargin>"));
        check(Has(oversizedStyle, "PAGE_WIDTH_OVERFLOW") && Has(oversizedStyle, "PAGE_MARGINS_OVERFLOW"), "Impossible printable width and vertical margins produce objective layout review points");

        var many = Analyze(Staff(Measure(Voice(meter + Chord("whole", string.Concat(Enumerable.Repeat(Note("999"), 250)))))));
        check(many.Issues.Count == 201 && many.Issues.Last().Code == "MORE_ISSUES" && many.Issues.Last().Message.Contains("50 ocorrências", StringComparison.Ordinal),
            "Large reports keep at most 200 detailed points and disclose the exact omitted count");
        var nestedPart = NativeScoreDiagnostics.Analyze(Write(BasicStaff(), entries: new() { ["parts/piano.mscx"] = Encoding.UTF8.GetBytes("<museScore/>") }));
        check(nestedPart.Limitations.Any(text => text.Contains("Partes individuais", StringComparison.Ordinal)), "Embedded individual parts are not silently represented as fully diagnosed");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { NativeScoreDiagnostics.Analyze(clean, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && File.ReadAllBytes(clean).SequenceEqual(original), "Cancellation is honored before opening or altering a source");
        bool rejected = false;
        string wrongVersion = Write(BasicStaff());
        using (var zip = ZipFile.Open(wrongVersion, ZipArchiveMode.Update))
        {
            zip.GetEntry("score.mscx")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open());
            writer.Write("<museScore version='3.02'><Score/></museScore>");
        }
        try { NativeScoreDiagnostics.Analyze(wrongVersion); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "Diagnostics reuses the existing native format safety gate for unsupported MuseScore versions");
    }
}
