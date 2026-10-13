using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Teclas.Core;

int checks = 0;
var passed = new List<string>();
void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); passed.Add(message); }
string work = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "teclas-native-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(work);
const string xml = """
<museScore version="4.70"><programVersion>4.7.5</programVersion><Score>
 <metaTag name="workTitle">Peça &amp; teste</metaTag><metaTag name="subtitle">Estudo</metaTag><metaTag name="composer">Autor</metaTag>
 <Part id="1"><Staff><StaffType group="pitched"><name>stdNormal</name></StaffType></Staff><Instrument id="piano"><trackName>Piano</trackName></Instrument></Part>
 <Staff id="1"><VBox><height>10</height><Text><style>title</style><text>Peça &amp; teste</text></Text></VBox>
  <Measure><LayoutBreak><subtype>line</subtype></LayoutBreak><voice><KeySig><concertKey>0</concertKey></KeySig><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>
  <Tempo><tempo>2</tempo><text>Allegro</text></Tempo><Harmony><harmonyInfo><root>14</root></harmonyInfo></Harmony>
  <Chord><durationType>whole</durationType><Note><pitch>60</pitch><tpc>14</tpc></Note></Chord>
  <UnknownMusicalFeature><value>preserve me</value></UnknownMusicalFeature></voice></Measure>
 </Staff></Score></museScore>
""";
string WriteFixture(string name, string content, Dictionary<string, byte[]>? additional = null)
{
    string path = Path.Combine(work, name + ".mscz");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using (var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false))) writer.Write(content);
    using (var writer = new StreamWriter(zip.CreateEntry("score_style.mss").Open())) writer.Write("<museScore version=\"4.70\"><Style><pageWidth>8.26771654</pageWidth><pageHeight>11.69291339</pageHeight><spatium>1.75</spatium></Style></museScore>");
    if (additional is not null) foreach (var entry in additional)
    { using var stream = zip.CreateEntry(entry.Key).Open(); stream.Write(entry.Value); }
    return path;
}
string fixture = WriteFixture("fixture", xml);
string fingerprint = NativeScoreDocument.MusicalFingerprint(fixture);
var metadata = NativeScoreDocument.Inspect(fixture);
Check(metadata.Title == "Peça & teste" && metadata.Subtitle == "Estudo" && metadata.Composer == "Autor", "Read native Unicode and XML-escaped metadata");
Check(metadata.NoteCount == 1 && metadata.MeasureCount == 1 && metadata.StaffCount == 1 && metadata.HarmonyCount == 1, "Read native musical inventory");
Check(metadata.PageWidthMm == 210 && metadata.PageHeightMm == 297, "Convert native page inches to millimetres");
string cosmetic = WriteFixture("cosmetic", xml.Replace("<height>10</height>", "<height>20</height>")
    .Replace("<subtype>line</subtype>", "<subtype>page</subtype>").Replace("<pitch>60</pitch>", "<offset x=\"2\" y=\"3\"/><eid>generated</eid><pitch>60</pitch>"));
Check(NativeScoreDocument.MusicalFingerprint(cosmetic) == fingerprint, "Presentation and generated IDs do not change the musical fingerprint");
foreach (var (name, before, after) in new[]
{
    ("pitch", "<pitch>60</pitch>", "<pitch>61</pitch>"), ("duration", "<durationType>whole</durationType>", "<durationType>half</durationType>"),
    ("key", "<concertKey>0</concertKey>", "<concertKey>1</concertKey>"), ("harmony", "<root>14</root>", "<root>15</root>"),
    ("tempo", "<tempo>2</tempo>", "<tempo>3</tempo>"), ("unknown feature", "preserve me", "changed"),
    ("section break", "<subtype>line</subtype>", "<subtype>section</subtype>")
})
    Check(NativeScoreDocument.MusicalFingerprint(WriteFixture(name, xml.Replace(before, after))) != fingerprint, "Fingerprint detects " + name);
string withImage = xml.Replace("<Harmony>", "<Image><path>keyboard.svg</path><size w=\"14\" h=\"4\"/></Image><Harmony>");
string imageA = WriteFixture("image-a", withImage, new() { ["Pictures/keyboard.svg"] = Encoding.UTF8.GetBytes("<svg>notes-a</svg>") });
string imageB = WriteFixture("image-b", withImage, new() { ["Pictures/keyboard.svg"] = Encoding.UTF8.GetBytes("<svg>notes-b</svg>") });
Check(NativeScoreDocument.MusicalFingerprint(imageA) != NativeScoreDocument.MusicalFingerprint(imageB), "Fingerprint detects an embedded diagram content change");
try { NativeScoreDocument.MusicalFingerprint(WriteFixture("missing-image", withImage)); throw new Exception("External image accepted"); }
catch (InvalidDataException) { checks++; passed.Add("Missing external image rejected"); }
try { NativeScoreDocument.Inspect(WriteFixture("old-version", xml.Replace("version=\"4.70\"", "version=\"3.02\""))); throw new Exception("Unvalidated old version accepted"); }
catch (InvalidDataException) { checks++; passed.Add("MuseScore 3 input receives migration guidance"); }
var unavailable = new ScoreEditingService("missing-musescore.exe", Path.Combine(work, "cache"));
try { await unavailable.ImproveAsync(fixture, work, new() { MarginMm = double.NaN }, null, CancellationToken.None); throw new Exception("Invalid layout accepted"); }
catch (ArgumentOutOfRangeException) { checks++; passed.Add("Invalid layout rejected before native export"); }
try { await unavailable.ImproveAsync(fixture, work, new() { Title = new string('x', 161) }, null, CancellationToken.None); throw new Exception("Oversized title accepted"); }
catch (InvalidOperationException) { checks++; passed.Add("Oversized metadata rejected without truncating user input"); }
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await unavailable.ImproveAsync(fixture, work, new(), null, cancelled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { checks++; passed.Add("Cancellation respected before creating output"); }
}

var nativeOutputs = new List<object>();
if (args.Length >= 3)
{
    string source = Path.GetFullPath(args[1]); string executable = Path.GetFullPath(args[2]);
    byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(source));
    string originalFingerprint = NativeScoreDocument.MusicalFingerprint(source);
    var original = NativeScoreDocument.Inspect(source);
    var service = new ScoreEditingService(executable, Path.Combine(work, "cache"));
    var imported = await service.ImportAsync(source, Path.Combine(work, "imported"), null, CancellationToken.None);
    Check(imported.Path != source && imported.PreviewPages.Count > 0 && imported.MusicalFingerprint == originalFingerprint &&
        SHA256.HashData(File.ReadAllBytes(imported.Path)).SequenceEqual(sourceHash), "Native import saves a byte-identical independent snapshot and renders every page");
    var improved = await service.ImproveAsync(source, Path.Combine(work, "edited"), new()
    {
        Title = "Uma partitura com título personalizado, acentuação e símbolos < > & preservados",
        Subtitle = "Melodia e cifras para estudar no teclado", Composer = "Ademir — revisão de apresentação",
        MarginMm = 16, TitleFontSize = 20, StaffSpaceMm = 1.75
    }, new Progress<string>(Console.WriteLine), CancellationToken.None);
    Check(improved.MusicalFingerprint == originalFingerprint && NativeScoreDocument.MusicalFingerprint(improved.Path) == originalFingerprint, "Native save preserves the complete musical fingerprint");
    Check(improved.Path != source && File.Exists(improved.Path), "Native revision is published as a new MSCZ copy");
    Check(improved.Metadata.Title.Contains("< > &", StringComparison.Ordinal) && improved.Metadata.Composer == "Ademir — revisão de apresentação", "Edited title and composer survive native MuseScore save");
    Check(improved.Metadata.NoteCount == original.NoteCount && improved.Metadata.HarmonyCount == original.HarmonyCount && improved.Metadata.ImageCount == original.ImageCount, "Notes, chords and embedded diagrams remain present");
    Check(improved.PreviewPages.Count > 0 && improved.PreviewPages.All(File.Exists), "All pages of the saved native revision reopen and render");
    byte[] approvedHash = SHA256.HashData(File.ReadAllBytes(improved.Path));
    var second = await service.ImproveAsync(improved.Path, Path.Combine(work, "edited"), new()
    { Subtitle = "", Composer = "" }, null, CancellationToken.None);
    Check(second.Metadata.Title == improved.Metadata.Title && second.Metadata.Subtitle == "" && second.Metadata.Composer == "", "Null preserves title while explicitly empty fields remove subtitle and composer");
    Check(second.Path != improved.Path && SHA256.HashData(File.ReadAllBytes(improved.Path)).SequenceEqual(approvedHash), "A second revision preserves its approved predecessor");
    Check(second.MusicalFingerprint == originalFingerprint && second.PreviewPages.Count > 0, "A second layout pass preserves music and renders successfully");
    if (original.ImageCount > 0)
    {
        try { await service.ImproveAsync(source, work, new() { RedistributeSystems = true }, null, CancellationToken.None); throw new Exception("Unsafe image reflow accepted"); }
        catch (InvalidOperationException) { checks++; passed.Add("Automatic reflow is blocked for image scores to avoid cropped diagrams"); }
    }
    string withoutDiagrams = Path.Combine(work, "sem-diagramas.mscz");
    using (var input = ZipFile.OpenRead(source))
    using (var output = ZipFile.Open(withoutDiagrams, ZipArchiveMode.Create))
    {
        foreach (var entry in input.Entries)
        {
            using var entryInput = entry.Open(); using var entryOutput = output.CreateEntry(entry.FullName).Open();
            if (entry.FullName.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase))
            {
                var document = XDocument.Load(entryInput);
                foreach (var image in document.Descendants("Image").ToArray()) image.Remove();
                document.Save(entryOutput);
            }
            else entryInput.CopyTo(entryOutput);
        }
    }
    string plainFingerprint = NativeScoreDocument.MusicalFingerprint(withoutDiagrams);
    var reflowed = await service.ImproveAsync(withoutDiagrams, Path.Combine(work, "edited"), new() { RedistributeSystems = true }, null, CancellationToken.None);
    Check(reflowed.MusicalFingerprint == plainFingerprint && reflowed.PreviewPages.Count > 0, "Scores without images can redistribute systems without changing musical content");
    Check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(sourceHash), "User's original MSCZ remains byte-identical after all operations");
    nativeOutputs.Add(new { imported.Path, imported.PreviewPages, imported.Metadata });
    nativeOutputs.Add(new { improved.Path, improved.PreviewPages, improved.Metadata });
    nativeOutputs.Add(new { second.Path, second.PreviewPages, second.Metadata });
    nativeOutputs.Add(new { reflowed.Path, reflowed.PreviewPages, reflowed.Metadata });
    string grandStaffXml = Path.Combine(work, "duas-pautas.musicxml");
    File.WriteAllText(grandStaffXml, """
    <?xml version="1.0" encoding="utf-8"?>
    <score-partwise version="4.0"><work><work-title>Teste de preservação</work-title></work>
    <part-list><score-part id="P1"><part-name>Piano</part-name><score-instrument id="I1"><instrument-name>Piano</instrument-name></score-instrument><midi-instrument id="I1"><midi-channel>1</midi-channel><midi-program>1</midi-program></midi-instrument></score-part></part-list>
    <part id="P1"><measure number="1"><attributes><divisions>12</divisions><key><fifths>0</fifths></key><time><beats>4</beats><beat-type>4</beat-type></time><staves>2</staves><clef number="1"><sign>G</sign><line>2</line></clef><clef number="2"><sign>F</sign><line>4</line></clef></attributes>
    <direction placement="above"><direction-type><words>Expressivo</words></direction-type><staff>1</staff></direction><direction><direction-type><dynamics><p/></dynamics></direction-type><staff>1</staff></direction>
    <note><pitch><step>C</step><octave>4</octave></pitch><duration>24</duration><tie type="start"/><voice>1</voice><type>half</type><staff>1</staff><notations><tied type="start"/><slur type="start" number="1"/></notations></note>
    <note><pitch><step>C</step><octave>4</octave></pitch><duration>12</duration><tie type="stop"/><voice>1</voice><type>quarter</type><staff>1</staff><notations><tied type="stop"/></notations></note>
    <note><pitch><step>D</step><octave>4</octave></pitch><duration>12</duration><voice>1</voice><type>quarter</type><staff>1</staff><notations><slur type="stop" number="1"/><articulations><staccato/></articulations></notations><lyric><syllabic>single</syllabic><text>lá</text></lyric></note>
    <backup><duration>48</duration></backup><note><pitch><step>C</step><octave>3</octave></pitch><duration>48</duration><voice>2</voice><type>whole</type><staff>2</staff></note>
    </measure><measure number="2"><attributes><key><fifths>1</fifths></key></attributes><direction><direction-type><metronome><beat-unit>quarter</beat-unit><per-minute>72</per-minute></metronome></direction-type><staff>1</staff><sound tempo="72"/></direction>
    <note><pitch><step>F</step><alter>1</alter><octave>4</octave></pitch><duration>48</duration><voice>1</voice><type>whole</type><staff>1</staff></note>
    <backup><duration>48</duration></backup><note><pitch><step>G</step><octave>2</octave></pitch><duration>48</duration><voice>2</voice><type>whole</type><staff>2</staff></note><barline location="right"><repeat direction="backward"/></barline>
    </measure></part></score-partwise>
    """);
    string grandStaff = Path.Combine(work, "duas-pautas.mscz");
    await new ScoreExporter(executable, Path.Combine(work, "cache")).ExportNativeAsync(grandStaffXml, grandStaff, CancellationToken.None);
    string grandFingerprint = NativeScoreDocument.MusicalFingerprint(grandStaff);
    var grandResult = await service.ImproveAsync(grandStaff, Path.Combine(work, "edited"), new() { RedistributeSystems = true }, null, CancellationToken.None);
    Check(grandResult.Metadata.StaffCount == 2 && grandResult.Metadata.NoteCount == 6 && grandResult.MusicalFingerprint == grandFingerprint,
        "Grand staff, independent voices, ties, slur, lyric, articulation, dynamics, tempo, key change and repeat survive native revision");
    nativeOutputs.Add(new { grandResult.Path, grandResult.PreviewPages, grandResult.Metadata });
}
File.WriteAllText(Path.Combine(work, "report.json"), JsonSerializer.Serialize(new { checks, passed, nativeOutputs }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks} native score editing checks.");
