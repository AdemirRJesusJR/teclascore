using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Teclas.Core;

string work = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "teclas-arrangement-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(work);
var passed = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); passed.Add(message); }
bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); return false; } catch (T) { return true; } }
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
const string meter = "<TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>";
const string part = "<Part><Staff id='1'><StaffType group='pitched'/></Staff><Instrument id='piano'/></Part>";
string Note(int p = 60, int t = 14, string extra = "") => $"<Note><pitch>{p}</pitch><tpc>{t}</tpc>{extra}</Note>";
string Chord(string type = "quarter", string? notes = null, string extra = "") => $"<Chord><durationType>{type}</durationType>{extra}{notes ?? Note()}</Chord>";
string Rest(string type = "quarter") => $"<Rest><durationType>{type}</durationType></Rest>";
string Voice(string body) => "<voice>" + body + "</voice>";
string Measure(string body, string attrs = "") => $"<Measure {attrs}>" + body + "</Measure>";
string Staff(string body, int id = 1) => $"<Staff id='{id}'>" + body + "</Staff>";
string Poly(string type = "quarter", string extra = "") => Chord(type, Note(48) + Note(60) + Note(64, 18) + Note(67, 15) + Note(72), extra);
int sequence = 0;
string Write(string staff, string definition = part, string extra = "", Dictionary<string, byte[]>? entries = null)
{
    string path = Path.Combine(work, $"fixture-{++sequence}.mscz");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using (var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false)))
        writer.Write($"<museScore version='4.70'><Score><metaTag name='workTitle'>Estudo</metaTag><metaTag name='composer'>Autora</metaTag><metaTag name='artist'>Artista</metaTag>{definition}{staff}{extra}</Score></museScore>");
    using (var stream = zip.CreateEntry("preserve.bin").Open()) stream.Write([0, 1, 255, 3]);
    if (entries is not null) foreach (var entry in entries) { using var stream = zip.CreateEntry(entry.Key).Open(); stream.Write(entry.Value); }
    return path;
}
XDocument Xml(string path)
{
    using var zip = ZipFile.OpenRead(path);
    var entry = zip.Entries.First(e => e.FullName.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase));
    using var stream = entry.Open(); return XDocument.Load(stream);
}
void ChangeXml(string path, Action<XDocument> action)
{
    var doc = Xml(path); action(doc);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
    var entry = zip.Entries.First(e => e.FullName.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase)); string name = entry.FullName;
    entry.Delete(); using var output = zip.CreateEntry(name).Open(); doc.Save(output);
}
ArrangementPlan Plan(string voice, ArrangementLevel level = ArrangementLevel.Beginner, string attrs = "", string definition = part) =>
    NativeScoreArrangement.CreatePlan(Write(Staff(Measure(Voice(voice), attrs)), definition), new(level, 1));
string Draft(ArrangementPlan p) { string output = Path.Combine(work, $"draft-{++sequence}.mscz"); NativeScoreArrangement.WriteDraft(p, output); return output; }
var basePath = Write(Staff(Measure(Voice(meter + Poly("whole")))));
string originalHash = Hash(basePath);
var original = NativeScoreArrangement.CreatePlan(basePath, new(ArrangementLevel.Original, 1));
Check(!original.CanPrepare && original.Changes.Count == 0, "Original is a read-only comparison without a redundant copy");
Check(Throws<InvalidOperationException>(() => Draft(original)), "Original cannot publish a no-op draft");
var beginner = NativeScoreArrangement.CreatePlan(basePath, new(ArrangementLevel.Beginner, 1));
Check(beginner.CanPrepare && beginner.Changes.Single().Kind == "Polifonia" && beginner.Changes.Single().Measure == 1, "Beginner proposes an explicit located polyphony reduction");
string beginnerDraft = Draft(beginner);
var bxml = Xml(beginnerDraft);
Check(bxml.Descendants("Note").Single().Element("pitch")?.Value == "72", "Beginner retains the upper original note");
Check(Hash(basePath) == originalHash && NativeScoreDocument.Inspect(basePath).ArrangementSummary == "", "Planning and drafting preserve the original bytes and metadata");
Check(NativeScoreDocument.Inspect(beginnerDraft).ArrangementSummary == "Iniciante · pauta 1 · voz 1" &&
    NativeScoreDocument.Inspect(beginnerDraft).Title == "Estudo" && NativeScoreDocument.Inspect(beginnerDraft).Artist == "Artista", "A changed copy carries its level while preserving visible credits");
var intermediate = NativeScoreArrangement.CreatePlan(basePath, new(ArrangementLevel.Intermediate, 1));
var inotes = Xml(Draft(intermediate)).Descendants("Note").Select(n => (int)n.Element("pitch")!).ToArray();
Check(inotes.SequenceEqual(new[] { 64, 67, 72 }) && inotes.Max() - inotes.Min() <= 12, "Intermediate retains at most three upper pitch classes within one octave");
using (var zip = ZipFile.OpenRead(beginnerDraft))
{
    using var source = zip.GetEntry("preserve.bin")!.Open(); using var buffer = new MemoryStream(); source.CopyTo(buffer);
    Check(buffer.ToArray().SequenceEqual(new byte[] { 0, 1, 255, 3 }), "Unknown archive assets survive byte for byte");
}
var expected = Xml(basePath);
foreach (var note in expected.Descendants("Note").Where(n => (int)n.Element("pitch")! != 72).ToArray()) note.Remove();
foreach (var tag in bxml.Descendants("metaTag").Where(t => ((string?)t.Attribute("name"))?.StartsWith("teclasArrangement", StringComparison.Ordinal) == true).ToArray()) tag.Remove();
Check(XNode.DeepEquals(expected, bxml), "Beginner draft is exactly the declared patch apart from explicit arrangement metadata");
Check(Throws<IOException>(() => NativeScoreArrangement.WriteDraft(beginner, basePath)) && Throws<IOException>(() => NativeScoreArrangement.WriteDraft(beginner, beginnerDraft)), "Source and existing destinations cannot be overwritten");
Check(Throws<IOException>(() => Draft(beginner with { SourceSha256 = new string('0',64) })), "Stale source hashes reject adaptation");
Check(Throws<InvalidOperationException>(() => Draft(beginner with { Changes = [] })), "A modified public plan cannot authorize a hidden patch");
Check(Throws<ArgumentException>(() => NativeScoreArrangement.CreatePlan(basePath, new((ArrangementLevel)99,1))) &&
    Throws<ArgumentException>(() => NativeScoreArrangement.CreatePlan(basePath,new(ArrangementLevel.Beginner,3))) &&
    Throws<ArgumentException>(() => NativeScoreArrangement.CreatePlan(basePath,new(ArrangementLevel.Beginner,1,2))), "Invalid profile, staff and voice fail explicitly");
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    Check(Throws<OperationCanceledException>(() => NativeScoreArrangement.CreatePlan(basePath,new(ArrangementLevel.Beginner,1),cts.Token)) &&
        Throws<OperationCanceledException>(() => NativeScoreArrangement.WriteDraft(beginner,Path.Combine(work,"cancelled.mscz"),cts.Token)) &&
        !File.Exists(Path.Combine(work,"cancelled.mscz")), "Pre-cancellation leaves no output");
}
var simple = Plan(meter+Chord("whole",Note(67,15)));
Check(!simple.CanPrepare && simple.Limitations.Any(l => l.Contains("redundante",StringComparison.Ordinal)), "An already suitable line is reported honestly as unchanged");
var repeated = Plan(meter + Chord("eighth") + Chord("eighth") + Chord("half",Note(62,16)) + Rest());
var repeatedModel = NativeMusicEditor.Open(Draft(repeated));
Check(repeated.Changes.Single().Kind == "Ritmo" && repeatedModel.Measures[0].Events.Count == 3 &&
    repeatedModel.Measures[0].Events.Select(e=>e.Quarter).SequenceEqual(new double?[]{0,1,3}), "Repeated short notes consolidate without shifting subsequent event positions");
var fourRepeated = Plan(meter + string.Concat(Enumerable.Repeat(Chord("16th"),4)) + Chord("half",Note(62,16)) + Rest());
Check(NativeMusicEditor.Open(Draft(fourRepeated)).Measures[0].Events[0].DurationType == "quarter", "Four equal sixteenths consolidate to one quarter");
var intermediateRhythm = Plan(meter + Chord("eighth") + Chord("eighth") + Chord("half",Note(62,16)) + Rest(),ArrangementLevel.Intermediate);
Check(!intermediateRhythm.CanPrepare, "Intermediate preserves eighth-note attacks instead of applying the beginner threshold");
var differentPitches = Plan(meter+Chord("eighth")+Chord("eighth",Note(62,16))+Rest("half")+Rest());
Check(!differentPitches.CanPrepare, "Rhythm adaptation never replaces a melodic pitch change by a sustained note");
var differentVelocity = Plan(meter+Chord("eighth",Note(extra:"<velocity>80</velocity>"))+Chord("eighth",Note(extra:"<velocity>95</velocity>"))+Rest("half")+Rest());
Check(!differentVelocity.CanPrepare, "Distinct attack dynamics block consolidation");
foreach (string annotation in new[] {
    "<Harmony><harmonyInfo><root>14</root></harmonyInfo></Harmony>", "<StaffText><text>Piano</text></StaffText>",
    "<Dynamic><subtype>f</subtype></Dynamic>", "<Image><path>foreign.png</path></Image>", "<Segment><leadingSpace>1</leadingSpace></Segment>" })
{
    var anchored = Plan(meter+Chord("eighth")+annotation+Chord("eighth")+Rest("half")+Rest());
    Check(!anchored.CanPrepare, "Intervening annotation remains at its onset: "+XElement.Parse(annotation).Name);
}
var register = Plan(meter+Chord("half",Note(48))+Chord("half",Note(55,15)));
var registerModel = NativeMusicEditor.Open(Draft(register));
Check(register.Changes.All(c=>c.Kind=="Registro") && registerModel.Measures[0].Events.SelectMany(e=>e.Notes).Select(n=>n.Pitch).SequenceEqual(new[]{60,67}), "Register adaptation uses one uniform octave shift for the whole line");
var tooWide = Plan(meter+Chord("half",Note(36))+Chord("half",Note(96)));
Check(!tooWide.CanPrepare && tooWide.Limitations.Any(l=>l.Contains("excede a faixa",StringComparison.Ordinal)), "A line wider than the target register is not folded by isolated notes");
var grace = Plan(meter+Chord("eighth",Note(62,16),"<acciaccatura/>")+Chord("whole"));
Check(grace.Changes.Single().Kind=="Ornamento" && NativeMusicEditor.Open(Draft(grace)).Measures[0].Events.Single().Quarter==0, "An isolated simple grace is removed without moving its principal attack");
var graceSlur = Plan(meter+Chord("eighth",Note(62,16),"<acciaccatura/><Spanner type='Slur'><Slur/></Spanner>")+Chord("whole"));
Check(!graceSlur.CanPrepare, "Connected grace ornaments are preserved");
foreach (var (label, voice) in new[] {
    ("tuplets",meter+"<Tuplet id='1'><normalNotes>2</normalNotes><actualNotes>3</actualNotes></Tuplet>"+Poly(extra:"<Tuplet>1</Tuplet>")),
    ("location",meter+Poly()+"<location><fractions>-1/4</fractions></location>"+Poly()),
    ("unknown timing",meter+"<FutureTiming>1</FutureTiming>"+Poly("whole")),
    ("overflow",meter+Poly("whole")+Poly()),
    ("unknown chord",meter+Poly("whole","<FutureChord>1</FutureChord>")),
    ("articulation",meter+Poly("whole","<Articulation><subtype>ornamentTrill</subtype></Articulation>")),
    ("lyrics",meter+Poly("whole","<Lyrics><text>La</text></Lyrics>")),
    ("manual note",meter+Chord("whole",Note(60,14,"<Accidental><subtype>accidentalNatural</subtype><role>1</role></Accidental>")+Note(72))),
    ("microtone",meter+Chord("whole",Note(60,14,"<tuning>50</tuning>")+Note(72))) })
{
    var p=Plan(voice); Check(!p.CanPrepare && p.Limitations.Any(l=>l.Contains("Compasso 1",StringComparison.Ordinal)), "Protected structure is reported and untouched: "+label);
}
var specialFirst = Write(Staff(Measure(Voice(meter+Poly("whole","<Lyrics><text>La</text></Lyrics>")))+Measure(Voice(Poly("whole")))));
var partial = NativeScoreArrangement.CreatePlan(specialFirst,new(ArrangementLevel.Beginner,1));
var partialXml=Xml(Draft(partial));
Check(partial.Changes.All(c=>c.Measure==2) && XNode.DeepEquals(Xml(specialFirst).Descendants("Measure").First(),partialXml.Descendants("Measure").First()), "One protected measure does not prevent adapting later safe measures");
string nextSpan="<Spanner type='Slur'><Slur/><next><location><measures>2</measures><fractions>0/1</fractions></location></next></Spanner>";
string prevSpan="<Spanner type='Slur'><prev><location><measures>-2</measures><fractions>0/1</fractions></location></prev></Spanner>";
var spanPath=Write(Staff(Measure(Voice(meter+Poly("whole",nextSpan)))+Measure(Voice(Poly("whole")))+Measure(Voice(Poly("whole",prevSpan)))+Measure(Voice(Poly("whole")))));
var spanPlan=NativeScoreArrangement.CreatePlan(spanPath,new(ArrangementLevel.Beginner,1));
Check(spanPlan.Changes.All(c=>c.Measure==4) && spanPlan.Limitations.Count(l=>l.Contains("Frase com ligação",StringComparison.Ordinal))==3, "A multi-measure phrase protects its interior as well as both endpoints");
string legacySpan=Write(Staff(Measure(Voice(meter+Poly("whole","<Slur id='4'/>")))+Measure(Voice(Poly("whole")))));
Check(!NativeScoreArrangement.CreatePlan(legacySpan,new(ArrangementLevel.Beginner,1)).CanPrepare,"An unresolved legacy span ID cannot expose intermediate bars to adaptation");
var tiedRegister=Plan(meter+Chord("half",Note(48,14,"<Spanner type='Tie'><Tie/></Spanner>"))+Chord("half",Note(55,15)));
Check(!tiedRegister.CanPrepare && tiedRegister.Limitations.Any(l=>l.Contains("registro foi preservado",StringComparison.Ordinal)), "Register never shifts only the untied fragment of a protected line");
var voicePath=Write(Staff(Measure(Voice(meter+Poly("whole"))+Voice(Chord("whole",Note(55,15)))))+Staff(Measure(Voice(meter+Chord("whole",Note(43,15)))),2),
    part.Replace("<Instrument", "<Staff id='2'><StaffType group='pitched'/></Staff><Instrument"));
var multi=NativeScoreArrangement.CreatePlan(voicePath,new(ArrangementLevel.Beginner,1,1));
var multiDraft=Xml(Draft(multi)); var multiSource=Xml(voicePath);
Check(XNode.DeepEquals(multiSource.Root!.Element("Score")!.Elements("Staff").Last(),multiDraft.Root!.Element("Score")!.Elements("Staff").Last()) &&
    XNode.DeepEquals(multiSource.Descendants("voice").ElementAt(1),multiDraft.Descendants("voice").ElementAt(1)), "Non-target staff and voice remain exactly native-identical");
var secondVoice=NativeScoreArrangement.CreatePlan(voicePath,new(ArrangementLevel.Beginner,1,2));
Check(secondVoice.Changes.All(c=>c.Voice==2) && secondVoice.Changes.Single().Kind=="Registro", "The caller can explicitly target a second voice");
Check(!Plan(meter+Poly("whole"),definition:part.Replace("pitched","percussion")).CanPrepare &&
    !Plan(meter+Poly("whole"),definition:part.Replace("<Instrument id='piano'/>","<Instrument><transposeChromatic>2</transposeChromatic></Instrument>")).CanPrepare, "Percussion and transposing parts remain protected");
string linked=Write(Staff(Measure(Voice(meter+Poly("whole")))),extra:"<Excerpt><name>Part</name></Excerpt>");
Check(!NativeScoreArrangement.CreatePlan(linked,new(ArrangementLevel.Beginner,1)).CanPrepare,"Linked parts are not changed without synchronized editing");
var pickup=Plan(meter+Chord("16th")+Chord("16th"),attrs:"len='1/8'");
Check(NativeMusicEditor.Open(Draft(pickup)).Measures[0].Events.Single().DurationType=="eighth","Explicit pickup length is preserved during safe local consolidation");
string longMeta=Write(Staff(Measure(Voice(meter+Chord("whole")))),extra:"<metaTag name='teclasArrangementSummary'>"+new string('x',400)+"</metaTag>");
Check(NativeScoreDocument.Inspect(longMeta).ArrangementSummary.Length==200,"Untrusted imported arrangement summary is bounded for display");
Check(!Directory.EnumerateFiles(work,".teclas-arranjo-*").Any(),"Draft operations do not leave temporary patch files");
var summary = new { passed = passed.Count, checks=passed };
string embeddedImage="<Image><path>fixture.svg</path></Image>";
string diagramVoice=meter+Chord("eighth")+Chord("eighth")+Chord("half",Note(62,16))+Rest()+embeddedImage;
var diagramRhythm=Plan(diagramVoice);
Check(!diagramRhythm.CanPrepare && diagramRhythm.Limitations.Any(l=>l.Contains("espaçamento",StringComparison.Ordinal)),
    "Images anywhere in the score protect rhythmic attacks to avoid compressing neighbouring diagrams");
var diagramGrace=Plan(meter+Chord("eighth",Note(62,16),"<acciaccatura/>")+Chord("whole")+embeddedImage);
Check(!diagramGrace.CanPrepare,"Images protect ornamental event spacing as well as rhythmic attacks");
var lowGrace=Plan(meter+Chord("eighth",Note(50,16),"<acciaccatura/>")+Chord("whole",Note(48))+embeddedImage);
Check(!lowGrace.CanPrepare,"A preserved grace prevents shifting only its principal note by an octave");
var diagramPoly=Plan(meter+Poly("whole")+embeddedImage);
Check(diagramPoly.CanPrepare&&diagramPoly.Changes.All(c=>c.Kind=="Polifonia")&&Xml(Draft(diagramPoly)).Descendants("Image").Single().Element("path")?.Value=="fixture.svg",
    "Diagram protection retains useful chord reduction and preserves every image reference");
var diagramOtherStaff=Write(Staff(Measure(Voice(meter+Chord("eighth")+Chord("eighth")+Rest("half")+Rest())))+
    Staff(Measure(Voice(meter+Rest("whole")+embeddedImage)),2),part.Replace("<Instrument","<Staff id='2'><StaffType group='pitched'/></Staff><Instrument"));
Check(!NativeScoreArrangement.CreatePlan(diagramOtherStaff,new(ArrangementLevel.Beginner,1)).CanPrepare,
    "An image in another staff protects shared system spacing");
string luz=Path.GetFullPath("qa/luz-v0.11.0/output/Roberto Carlos - Luz divina (karaoplay.com) - teclado.mscz");
if(File.Exists(luz))
{
    string luzHash=Hash(luz);var luzPlan=NativeScoreArrangement.CreatePlan(luz,new(ArrangementLevel.Beginner,1));
    Check(!luzPlan.CanPrepare&&luzPlan.Changes.Count==0&&Hash(luz)==luzHash,
        "Luz diagram regression: no rhythm-only copy can introduce collisions in neighbouring bars");
}
var accidentalPlan = Plan(meter + Chord("half", Note(61,21,"<Accidental><subtype>accidentalSharp</subtype></Accidental>")+Note(64,18))+
    Chord("half",Note(60,14,"<Accidental><subtype>accidentalNatural</subtype></Accidental>")));
string accidentalDraft=Draft(accidentalPlan), normalizedCopy=Path.Combine(work,"automatic-normalized.mscz");File.Copy(accidentalDraft,normalizedCopy);
ChangeXml(normalizedCopy, d=>d.Descendants("Accidental").ToList().ForEach(a=>a.Remove()));
var normalize=typeof(NativeScoreArrangement).GetMethod("NormalizedFingerprint",BindingFlags.NonPublic|BindingFlags.Static)!;
string Normalized(string p,ArrangementPlan pl) => (string)normalize.Invoke(null,[p,pl,CancellationToken.None])!;
Check(NativeScoreDocument.MusicalFingerprint(accidentalDraft)!=NativeScoreDocument.MusicalFingerprint(normalizedCopy)&&
    Normalized(accidentalDraft,accidentalPlan)==Normalized(normalizedCopy,accidentalPlan),"Only the automatic accidental dependent on a removed note may normalize");
ChangeXml(normalizedCopy,d=>d.Descendants("Note").First().Add(new XElement("Accidental",new XElement("subtype","accidentalNatural"))));
Check(Normalized(accidentalDraft,accidentalPlan)!=Normalized(normalizedCopy,accidentalPlan),"Accidental changes on unrelated diatonic positions remain in the fingerprint");
ChangeXml(normalizedCopy,d=>d.Descendants("Note").Last().SetElementValue("pitch",61));
Check(Throws<TargetInvocationException>(()=>Normalized(normalizedCopy,accidentalPlan)),"Accidental normalization cannot hide pitch changes");
string unknownVoicePath=Write(Staff(Measure(Voice(meter+Chord("whole",Note(61,21)+Note(64,18)))+
    Voice("<location><fractions>-1/4</fractions></location>"+Chord("whole",Note(60,14,"<Accidental><subtype>accidentalNatural</subtype></Accidental>"))))));
var unknownVoicePlan=NativeScoreArrangement.CreatePlan(unknownVoicePath,new(ArrangementLevel.Beginner,1));
string unknownVoiceDraft=Draft(unknownVoicePlan), unknownVoiceCopy=Path.Combine(work,"unknown-voice-normalized.mscz");File.Copy(unknownVoiceDraft,unknownVoiceCopy);
ChangeXml(unknownVoiceCopy,d=>d.Descendants("voice").Last().Descendants("Accidental").ToList().ForEach(a=>a.Remove()));
Check(Normalized(unknownVoiceDraft,unknownVoicePlan)!=Normalized(unknownVoiceCopy,unknownVoicePlan),
    "An unrecognized time cursor in another voice cannot broaden the accidental exception");
summary = new { passed = passed.Count, checks=passed };
File.WriteAllText(Path.Combine(work,"report.json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine($"{passed.Count} arrangement checks passed.");

if(args.Length > 1)
{
    string nativeSource = args[1], museScore = args.Length>2?args[2]:@"C:\Program Files\MuseScore 4\bin\MuseScore4.exe";
    string input=Path.Combine(work,"native-source.mscz");File.Copy(nativeSource,input);
    var plan=NativeScoreArrangement.CreatePlan(input,new(ArrangementLevel.Beginner,1));
    Check(plan.CanPrepare,"Native fixture provides actual safe changes");
    var service=new NativeScoreArrangementService(museScore,Path.Combine(work,"cache"));
    var prepared=await service.PrepareAsync(plan,null,CancellationToken.None);
    Check(prepared.PreviewPages.Count>0&&prepared.PreviewPages.All(File.Exists),"Native preparation renders every preview page");
    Check(Hash(input)==Hash(nativeSource),"Native preparation preserves original source bytes");
    Check(await ThrowsAsync<InvalidOperationException>(()=>service.PublishAsync(plan with { Options = new(ArrangementLevel.Intermediate,1)},prepared,Path.Combine(work,"badplan"),null,CancellationToken.None)),
        "Prepared preview is bound to its exact arrangement plan");
    byte[] scoreBytes=File.ReadAllBytes(prepared.Path);File.AppendAllText(prepared.Path,"tamper");
    Check(await ThrowsAsync<IOException>(()=>service.PublishAsync(plan,prepared,Path.Combine(work,"tampered"),null,CancellationToken.None)),"Prepared score tampering blocks publication");
    File.WriteAllBytes(prepared.Path,scoreBytes);
    byte[] pageBytes=File.ReadAllBytes(prepared.PreviewPages[0]);File.AppendAllText(prepared.PreviewPages[0],"tamper");
    Check(await ThrowsAsync<IOException>(()=>service.PublishAsync(plan,prepared,Path.Combine(work,"page-tampered"),null,CancellationToken.None)),"Prepared page tampering blocks publication");
    File.WriteAllBytes(prepared.PreviewPages[0],pageBytes);
    using(var cts=new CancellationTokenSource())
    {
        var progress=new SyncProgress(s=>{if(s.Contains("Salvando",StringComparison.Ordinal))cts.Cancel();});
        Check(await ThrowsAsync<OperationCanceledException>(()=>service.PublishAsync(plan,prepared,Path.Combine(work,"cancel-save"),progress,cts.Token)) &&
            !Directory.EnumerateFileSystemEntries(Path.Combine(work,"cancel-save")).Any(),"Cancellation during publication leaves no partial final directory");
    }
    var published=await service.PublishAsync(plan,prepared,Path.Combine(work,"saved"),null,CancellationToken.None);
    Check(File.Exists(published.Path)&&published.PreviewPages.All(File.Exists)&&published.Metadata.ArrangementSummary.StartsWith("Iniciante",StringComparison.Ordinal),
        "Approved arrangement publishes a separate identified copy with all pages");
    Check(await ThrowsAsync<InvalidOperationException>(()=>service.PublishAsync(plan,prepared,Path.Combine(work,"again"),null,CancellationToken.None)),"Same approved preview cannot be published twice");
    Check(Hash(input)==Hash(nativeSource)&&NativeMusicEditor.Open(published.Path).Metadata.NoteCount>0,"Published arrangement remains editable and leaves its source intact");
    var prepared2=await service.PrepareAsync(plan,null,CancellationToken.None);File.AppendAllText(input,"changed");
    Check(await ThrowsAsync<IOException>(()=>service.PublishAsync(plan,prepared2,Path.Combine(work,"stale"),null,CancellationToken.None)),"Source changes after preview block stale publication");
    File.WriteAllText(Path.Combine(work,"report.json"),JsonSerializer.Serialize(new{passed=passed.Count,checks=passed},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"{passed.Count} total arrangement checks passed including native publication.");
}
sealed class SyncProgress(Action<string> report):IProgress<string>{public void Report(string value)=>report(value);}
