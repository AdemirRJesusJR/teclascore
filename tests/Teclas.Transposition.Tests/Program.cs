using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Teclas.Core;

string work=Path.GetFullPath(args.Length>0?args[0]:Path.Combine(Path.GetTempPath(),"teclas-transposition-"+Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(work);var checks=new List<string>();int sequence=0;
void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);}
bool Throws<T>(Action action)where T:Exception{try{action();return false;}catch(T){return true;}}
async Task<bool> ThrowsAsync<T>(Func<Task> action)where T:Exception{try{await action();return false;}catch(T){return true;}}
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
const string part="<Part><Staff id='1'><StaffType group='pitched'/></Staff><Instrument id='piano'/></Part>";
const string meter="<TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>";
string Note(int pitch=60,int tpc=14,string extra="")=>$"<Chord><durationType>whole</durationType><Note><pitch>{pitch}</pitch><tpc>{tpc}</tpc>{extra}</Note></Chord>";
string Write(string body,string definition=part,string extra="",Dictionary<string,byte[]>? assets=null)
{
    string path=Path.Combine(work,$"fixture-{++sequence}.mscz");using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
    using(var writer=new StreamWriter(zip.CreateEntry("score.mscx").Open(),new UTF8Encoding(false)))
        writer.Write($"<museScore version='4.70'><Score><metaTag name='workTitle'>Estudo</metaTag><metaTag name='composer'>Compositora</metaTag>{definition}<Staff id='1'><Measure><voice>{meter}{body}</voice></Measure></Staff>{extra}</Score></museScore>");
    using(var output=zip.CreateEntry("preserve.bin").Open())output.Write([0,1,255,3]);
    if(assets is not null)foreach(var entry in assets){using var output=zip.CreateEntry(entry.Key).Open();output.Write(entry.Value);}
    using(var output=zip.CreateEntry("META-INF/container.xml").Open())
        new XDocument(new XElement("container",new XElement("rootfiles",new XElement("rootfile",new XAttribute("full-path","score.mscx")),
            assets?.Keys.Select(name=>new XElement("rootfile",new XAttribute("full-path",name)))))).Save(output);
    return path;
}
XDocument Xml(string path){using var zip=ZipFile.OpenRead(path);using var stream=zip.Entries.First(e=>e.FullName.EndsWith(".mscx",StringComparison.OrdinalIgnoreCase)).Open();return XDocument.Load(stream);}
void Change(string path,Action<XDocument> action)
{
    var doc=Xml(path);action(doc);using var zip=ZipFile.Open(path,ZipArchiveMode.Update);var e=zip.Entries.First(e=>e.FullName.EndsWith(".mscx",StringComparison.OrdinalIgnoreCase));string name=e.FullName;e.Delete();using var output=zip.CreateEntry(name).Open();doc.Save(output);
}
string Draft(TranspositionPlan plan){string path=Path.Combine(work,$"draft-{++sequence}.mscz");NativeScoreTransposition.WriteDraft(plan,path);return path;}
var source=Write("<Harmony><harmonyInfo><name>maj7</name><root>14</root><bass>18</bass></harmonyInfo></Harmony>"+Note());string sourceHash=Hash(source);
for(int delta=-12;delta<=12;delta++)
{
    var plan=NativeScoreTransposition.CreatePlan(source,new(delta));
    if(delta==0){Check(!plan.CanPrepare && Throws<InvalidOperationException>(()=>Draft(plan)),"Zero does not produce a redundant copy");continue;}
    Check(plan.CanPrepare,$"All ordinary intervals accepted: {delta}");var xml=Xml(Draft(plan));var note=xml.Descendants("Note").Single();
    Check((int)note.Element("pitch")! ==60+delta && NativeMusicEditor.IsValidPitch((int)note.Element("pitch")!,(int)note.Element("tpc")!),$"Pitch and TPC agree: {delta}");
    int root=(int)xml.Descendants("harmonyInfo").Single().Element("root")!,bass=(int)xml.Descendants("bass").Single();
    Check(((root*7-2)%12+12)%12==(delta%12+12)%12 && ((bass*7-2)%12+12)%12==((4+delta)%12+12)%12,$"Root and slash bass transpose: {delta}");
    Check(xml.Descendants("durationType").Single().Value=="whole" && xml.Descendants("name").Single().Value=="maj7",$"Rhythm and chord quality survive: {delta}");
    if(Math.Abs(delta)==12)Check(!xml.Descendants("KeySig").Any() && root==14 && bass==18 && (int)note.Element("tpc")! ==14,"Octave preserves enharmonics and harmony");
}
Check(Hash(source)==sourceHash,"Every draft preserves source bytes");
var up=NativeScoreTransposition.CreatePlan(source,new(2));var upXml=Xml(Draft(up));
Check(up.KeySignatures==1 && upXml.Descendants("KeySig").Single().Element("concertKey")?.Value=="2","Implicit C major receives transposed D major signature");
Check(Throws<IOException>(()=>NativeScoreTransposition.WriteDraft(up,source)),"Cannot overwrite original");
Check(Throws<IOException>(()=>Draft(up with{SourceSha256=new string('0',64)})),"Stale hash refused");
Check(Throws<InvalidOperationException>(()=>Draft(up with{Changes=[]})),"Forged public plan refused");
Check(Throws<ArgumentOutOfRangeException>(()=>NativeScoreTransposition.CreatePlan(source,new(13))),"Out of range interval refused");
Check(Throws<OperationCanceledException>(()=>NativeScoreTransposition.CreatePlan(source,new(1),new CancellationToken(true))),"Cancellation before analysis leaves no output");
foreach(var (body,definition,extra,label) in new[]{
    (Note(127,15),part,"","MIDI overflow"),
    (Note(extra:"<tuning>50</tuning>"),part,"","Microtonal note"),
    (Note(extra:"<Accidental><subtype>sharp</subtype><role>user</role></Accidental>"),part,"","Manual accidental"),
    (Note(),part,"<Excerpt><name>linked</name></Excerpt>","Linked part"),
    (Note(),part,"<Score><Staff id='2'><Measure><voice>"+Note()+"</voice></Measure></Staff></Score>","Nested score"),
    (Note(),part.Replace("<Staff id='1'","<Staff linkedTo='2' id='1'"),"","Linked attribute"),
    (Note()+"<InstrumentChange/>",part,"","Instrument change"),
    (Note(),part.Replace("pitched","tablature"),"","Tablature"),
    ("<Harmony><name>C7</name></Harmony>"+Note(),part,"","Unstructured textual harmony"),
    ("<KeySig><custom>1</custom></KeySig>"+Note(),part,"","Custom key signature")})
    Check(!NativeScoreTransposition.CreatePlan(Write(body,definition,extra),new(2)).CanPrepare,label+" fails entire-score validation");
var transposer=part.Replace("<Instrument id='piano'/>","<Instrument id='clarinet'><transposeChromatic>-2</transposeChromatic><transposeDiatonic>-1</transposeDiatonic></Instrument>");
var clarinet=Write("<KeySig><concertKey>0</concertKey><actualKey>2</actualKey></KeySig><Harmony><harmonyInfo><root>16</root></harmonyInfo></Harmony>"+Note(extra:"<tpc2>16</tpc2>"),transposer);
var clarinetPlan=NativeScoreTransposition.CreatePlan(clarinet,new(2));Check(clarinetPlan.CanPrepare,"Fixed Bb transposer accepted");
var cx=Xml(Draft(clarinetPlan));Check(cx.Descendants("pitch").Single().Value=="62" && cx.Descendants("tpc").Single().Value=="16" && cx.Descendants("tpc2").Single().Value=="18" && cx.Descendants("actualKey").Single().Value=="4","Sounding and written pitch/key stay distinct");
var ambiguousStaff=Write(Note(),part.Replace("id='1'","id='9'"));Check(!NativeScoreTransposition.CreatePlan(ambiguousStaff,new(2)).CanPrepare,"Mismatching part staff identity refused");
var tied=Write(Note(extra:"<Spanner type='Tie'><Tie/><next><location><measures>1</measures></location></next></Spanner>"));
Check(Xml(Draft(NativeScoreTransposition.CreatePlan(tied,new(2)))).Descendants("Spanner").Single().ToString()==Xml(tied).Descendants("Spanner").Single().ToString(),"Tie data survives exactly");
var modulation=Write("<KeySig><concertKey>-1</concertKey><actualKey>-1</actualKey><mode>minor</mode></KeySig>"+Note());
Change(modulation,d=>d.Root!.Element("Score")!.Elements("Staff").Single().Add(XElement.Parse("<Measure><voice><KeySig><concertKey>2</concertKey><actualKey>2</actualKey><mode>minor</mode></KeySig>"+Note(62,16)+"</voice></Measure>")));
var modulationXml=Xml(Draft(NativeScoreTransposition.CreatePlan(modulation,new(2))));
Check(modulationXml.Descendants("concertKey").Select(e=>(int)e).SequenceEqual(new[]{1,4})&&modulationXml.Descendants("mode").All(e=>e.Value=="minor"),"Local key changes and minor mode preserved through transposition");
var multiple=Write("<Tempo><tempo>1.5</tempo><text>Allegro</text></Tempo><Tuplet id='1'><normalNotes>2</normalNotes><actualNotes>3</actualNotes><baseNote>eighth</baseNote></Tuplet>"+Note(extra:"<Spanner type='Tie'><Tie/><next><location><measures>1</measures></location></next></Spanner>"));
Change(multiple,d=>{
    var score=d.Root!.Element("Score")!;var first=score.Elements("Staff").Single();
    first.Elements("Measure").Single().Add(XElement.Parse("<voice><Rest><durationType>half</durationType></Rest><Chord><durationType>half</durationType><Note><pitch>64</pitch><tpc>18</tpc></Note></Chord></voice>"));
    score.Element("Part")!.AddAfterSelf(XElement.Parse(part.Replace("id='1'","id='2'")));
    score.Add(XElement.Parse("<Staff id='2'><Measure><voice><Clef><concertClefType>F</concertClefType><transposingClefType>F</transposingClefType></Clef>"+meter+Note(48)+"</voice></Measure></Staff>"));
});
var multiplePlan=NativeScoreTransposition.CreatePlan(multiple,new(-1));Check(multiplePlan.CanPrepare&&multiplePlan.Notes==3,"All staves and voices participate in one transposition plan");
var multipleBefore=Xml(multiple);var multipleAfter=Xml(Draft(multiplePlan));
foreach(var doc in new[]{multipleBefore,multipleAfter})foreach(var e in doc.Descendants().Where(e=>e.Name.LocalName is "pitch" or "tpc" or "tpc2" or "KeySig").ToArray())e.Remove();
Check(XNode.DeepEquals(multipleBefore,multipleAfter),"Voices, rests, tempo, clefs, tuplets, ties and metadata are byte-equivalent XML apart from approved pitches/keys");
var unnumbered=Write(Note(),part.Replace(" id='1'",""));Check(NativeScoreTransposition.CreatePlan(unnumbered,new(2)).CanPrepare,"MuseScore sequential unnumbered part staff is accepted only with matching musical id");
var headerImage=Write(Note(),assets:new(){["Pictures/header.svg"]=Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>")});
Change(headerImage,d=>d.Root!.Element("Score")!.Elements("Staff").Single().AddFirst(XElement.Parse("<VBox><Image><path>header.svg</path></Image></VBox>")));
var headerPlan=NativeScoreTransposition.CreatePlan(headerImage,new(2,true));Check(headerPlan.CanPrepare&&headerPlan.RemovedDiagrams==0&&Xml(Draft(headerPlan)).Descendants("VBox").Descendants("Image").Any(),"Header logo is preserved even when removing unknown musical images is allowed");
var normalized=typeof(NativeScoreTransposition).GetMethod("NormalizedFingerprint",BindingFlags.Static|BindingFlags.NonPublic)!;
string Fingerprint(string path)=>(string)normalized.Invoke(null,[path,CancellationToken.None])!;
var normalizedDraft=Draft(up);var badGlyph=Path.Combine(work,"wrong-accidental.mscz");File.Copy(normalizedDraft,badGlyph);
Change(badGlyph,d=>d.Descendants("Note").Single().AddFirst(new XElement("Accidental",new XElement("subtype","accidentalSharp"))));
Check(Throws<TargetInvocationException>(()=>Fingerprint(badGlyph)),"Normalizer refuses an automatic glyph inconsistent with transposed spelling");
var badTie=Path.Combine(work,"lost-tie.mscz");File.Copy(Draft(NativeScoreTransposition.CreatePlan(tied,new(2))),badTie);
string tieFingerprint=Fingerprint(badTie);Change(badTie,d=>d.Descendants("Spanner").Single().Remove());
Check(Fingerprint(badTie)!=tieFingerprint,"Normalizer never ignores changed ties");
var outside=Write(Note());Change(outside,d=>d.Root!.Element("Score")!.Elements("Staff").Single().Add(XElement.Parse(Note())));
Check(!NativeScoreTransposition.CreatePlan(outside,new(2)).CanPrepare,"Pitch-bearing structures outside measures cannot cause partial transposition");
var safeName=typeof(NativeScoreTranspositionService).GetMethod("SafeName",BindingFlags.NonPublic|BindingFlags.Static)!;
string unicodeName=(string)safeName.Invoke(null,[new string('A',79)+"😀.mscz"])!;
Check(unicodeName.Length==79&&new UTF8Encoding(false,true).GetBytes(unicodeName).Length==79,"Output name truncation never splits a Unicode surrogate pair");
byte[] unknownSvg=Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'><rect width='20' height='20'/></svg>");
string image="<Image><path>unknown.svg</path><size w='14' h='4'/></Image>";
var external=Write("<Harmony><harmonyInfo><root>14</root></harmonyInfo></Harmony>"+image+Note(),assets:new(){["Pictures/unknown.svg"]=unknownSvg});
var externalPlan=NativeScoreTransposition.CreatePlan(external,new(2));Check(!externalPlan.CanPrepare&&externalPlan.UnrecognizedDiagrams==1,"Unknown musical image requires explicit decision");
var removePlan=NativeScoreTransposition.CreatePlan(external,new(2,true));Check(removePlan.CanPrepare&&removePlan.RemovedDiagrams==1&&!Xml(Draft(removePlan)).Descendants("Image").Any(),"Opt in removes unknown image from copied score only");
Check(Xml(external).Descendants("Image").Count()==1,"Unknown image remains in original");
var guitar=Write("<FretDiagram><Harmony><harmonyInfo><root>14</root></harmonyInfo></Harmony><string no='0'><dot fret='3'/></string></FretDiagram>"+Note());
var guitarPlan=NativeScoreTransposition.CreatePlan(guitar,new(2,true));var gx=Xml(Draft(guitarPlan));
Check(guitarPlan.CanPrepare&&!gx.Descendants("FretDiagram").Any()&&gx.Descendants("Harmony").Single().Descendants("root").Single().Value=="16","Removing fret diagram preserves and transposes its chord symbol");
var keyboardType=typeof(NativeScoreDocument).Assembly.GetType("Teclas.Core.NativeHarmonyEditing")!;
var svg=(XElement)keyboardType.GetMethod("KeyboardSvg",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[0,"",2])!;
byte[] keyboardBytes=Encoding.UTF8.GetBytes(svg.ToString());
string keyboardName=(string)keyboardType.GetMethod("Md4",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[keyboardBytes])!+".svg";
var keyboard=Write("<Harmony><harmonyInfo><root>14</root></harmonyInfo></Harmony><Image><path>"+keyboardName+"</path><offset x='4.5' y='-6.7'/><size w='14' h='4.170213'/></Image>"+Note(),assets:new(){["Pictures/"+keyboardName]=keyboardBytes});
var keyboardPlan=NativeScoreTransposition.CreatePlan(keyboard,new(2));Check(keyboardPlan.CanPrepare&&keyboardPlan.UpdatedDiagrams==1,"Recognized TeclaScore keyboard regenerates automatically");
var keyboardDraft=Draft(keyboardPlan);Check(Xml(keyboardDraft).Descendants("Image").Single().Element("size")!.ToString()==Xml(keyboard).Descendants("Image").Single().Element("size")!.ToString(),"Keyboard size is preserved");
using(var zip=ZipFile.OpenRead(keyboardDraft))
{
    var path=Xml(keyboardDraft).Descendants("Image").Single().Element("path")!.Value;
    using var data=zip.GetEntry("Pictures/"+path)!.Open();var actual=XDocument.Load(data);
    Check(actual.Root!.Element(XName.Get("desc","http://www.w3.org/2000/svg"))!.Value.Contains(string.Join(',',KeyboardDiagramVoicing.Pitches(2,"",2)),StringComparison.Ordinal),"Keyboard retains second inversion with transposed pitch classes");
    using var old=zip.GetEntry("Pictures/"+keyboardName)!.Open();using var bytes=new MemoryStream();old.CopyTo(bytes);
    Check(bytes.ToArray().SequenceEqual(Encoding.UTF8.GetBytes(svg.ToString())),"Original shared keyboard asset remains byte-identical");
}
var octaveKeyboard=NativeScoreTransposition.CreatePlan(keyboard,new(12));Check(octaveKeyboard.UpdatedDiagrams==0&&Xml(Draft(octaveKeyboard)).Descendants("Image").Single().Element("path")!.Value==keyboardName,"Octave leaves diagram voicing unchanged");

if(args.Length>1)
{
    string museScore=Path.GetFullPath(args[1]);var exporter=new ScoreExporter(museScore,Path.Combine(work,"cache"));
    string original=Path.Combine(work,"native-source.mscz");
    // Normalize the synthetic archive through the real installed MuseScore first.
    await exporter.ExportNativeAsync(clarinet,original,CancellationToken.None);
    string originalBytes=Hash(original);var plan=NativeScoreTransposition.CreatePlan(original,new(2));
    Check(plan.CanPrepare,"MuseScore-normalized clarinet fixture accepted");
    var service=new NativeScoreTranspositionService(museScore,Path.Combine(work,"cache"));
    var prepared=await service.PrepareAsync(plan,null,CancellationToken.None);
    Check(prepared.PreviewPages.Count>0&&prepared.PreviewPages.All(File.Exists),"Native prepare renders all pages");
    Check(Hash(original)==originalBytes,"Native preparation preserves source bytes");
    Check(await ThrowsAsync<InvalidOperationException>(()=>new NativeScoreTranspositionService(museScore,Path.Combine(work,"other-service")).PublishAsync(plan,prepared,Path.Combine(work,"foreign-preview"),null,CancellationToken.None)),"Another service instance cannot publish an unregistered preview");
    var reopened=Xml(prepared.Path);Check(reopened.Descendants("Note").Single().Element("pitch")?.Value=="62"&&reopened.Descendants("tpc2").Single().Value=="18","Real MuseScore reopens sounding D and written E");
    string musicxml=Path.Combine(work,"reopened.musicxml");await Export(prepared.Path,musicxml,museScore);
    var mx=XDocument.Load(musicxml);Check(mx.Descendants("pitch").Single().Element("step")?.Value=="E"&&mx.Descendants("fifths").Single().Value=="4"&&mx.Descendants("transpose").Single().Element("chromatic")?.Value=="-2","MusicXML independently confirms written E in E major and fixed Bb instrument");
    string midi=Path.Combine(work,"transposed.mid");await Export(prepared.Path,midi,museScore);
    Check(MidiReader.Read(midi).Notes.Any(n=>n.Pitch==62),"Exported MIDI contains sounding D rather than written E for the Bb instrument");
    Check(await ThrowsAsync<InvalidOperationException>(()=>service.PublishAsync(plan with{Options=new(3)},prepared,Path.Combine(work,"wrong-plan"),null,CancellationToken.None)),"Prepared output bound to exact requested interval");
    byte[] saved=File.ReadAllBytes(prepared.Path);File.AppendAllText(prepared.Path,"tampered");
    Check(await ThrowsAsync<IOException>(()=>service.PublishAsync(plan,prepared,Path.Combine(work,"tampered"),null,CancellationToken.None)),"Changed prepared archive cannot publish");File.WriteAllBytes(prepared.Path,saved);
    byte[] page=File.ReadAllBytes(prepared.PreviewPages[0]);File.AppendAllText(prepared.PreviewPages[0],"tampered");
    Check(await ThrowsAsync<IOException>(()=>service.PublishAsync(plan,prepared,Path.Combine(work,"tampered-page"),null,CancellationToken.None)),"Changed preview cannot publish");File.WriteAllBytes(prepared.PreviewPages[0],page);
    using(var cts=new CancellationTokenSource())
    {
        var progress=new ImmediateProgress(message=>{if(message.StartsWith("Salvando",StringComparison.Ordinal))cts.Cancel();});
        Check(await ThrowsAsync<OperationCanceledException>(()=>service.PublishAsync(plan,prepared,Path.Combine(work,"cancelled-publish"),progress,cts.Token))&&!Directory.EnumerateFileSystemEntries(Path.Combine(work,"cancelled-publish")).Any(),"Cancellation during publication leaves no partial final version");
    }
    var published=await service.PublishAsync(plan,prepared,Path.Combine(work,"published"),null,CancellationToken.None);
    Check(File.Exists(published.Path)&&published.PreviewPages.All(File.Exists)&&Hash(original)==originalBytes,"Atomic separate publication leaves original intact");
    Check(await ThrowsAsync<InvalidOperationException>(()=>service.PublishAsync(plan,prepared,Path.Combine(work,"published-twice"),null,CancellationToken.None)),"Prepared version cannot publish twice");
    string concertInput=Path.Combine(work,"concert-input.mscz");File.Copy(original,concertInput);
    using(var zip=ZipFile.Open(concertInput,ZipArchiveMode.Update))
    {
        var entry=zip.GetEntry("score_style.mss")!;XDocument style;using(var input=entry.Open())style=XDocument.Load(input);
        style.Root!.Element("Style")!.SetElementValue("concertPitch",1);entry.Delete();using var output=zip.CreateEntry("score_style.mss").Open();style.Save(output);
    }
    Change(concertInput,d=>{d.Descendants("Harmony").Single().Element("harmonyInfo")!.SetElementValue("root",14);d.Descendants("KeySig").Single().Element("actualKey")?.Remove();});
    string concertSource=Path.Combine(work,"concert-source.mscz");await exporter.ExportNativeAsync(concertInput,concertSource,CancellationToken.None);
    var concertPlan=NativeScoreTransposition.CreatePlan(concertSource,new(-2));Check(concertPlan.CanPrepare,"Concert-pitch mode transposing instrument is accepted");
    var concertPrepared=await service.PrepareAsync(concertPlan,null,CancellationToken.None);
    var concertXml=Xml(concertPrepared.Path);Check(concertXml.Descendants("pitch").Single().Value=="58"&&concertXml.Descendants("tpc").Single().Value=="12"&&concertXml.Descendants("tpc2").Single().Value=="14","Concert-pitch mode preserves sounding Bb and written C spellings");
    string concertMidi=Path.Combine(work,"concert.mid");await Export(concertPrepared.Path,concertMidi,museScore);
    Check(MidiReader.Read(concertMidi).Notes.Any(n=>n.Pitch==58),"Concert-pitch-mode MIDI sounds Bb after a downward whole step");
    var octavePlan=NativeScoreTransposition.CreatePlan(original,new(12));var octavePrepared=await service.PrepareAsync(octavePlan,null,CancellationToken.None);
    Check(Xml(octavePrepared.Path).Descendants("pitch").Single().Value=="72"&&Xml(octavePrepared.Path).Descendants("tpc2").Single().Value=="16","Real octave transposition preserves transposing instrument spelling");
    using(var cts=new CancellationTokenSource())
    {
        string cancelledCache=Path.Combine(work,"cancelled-prepare");var cancelledService=new NativeScoreTranspositionService(museScore,cancelledCache);
        var progress=new ImmediateProgress(message=>{if(message.StartsWith("Preparando",StringComparison.Ordinal))cts.Cancel();});
        Check(await ThrowsAsync<OperationCanceledException>(()=>cancelledService.PrepareAsync(plan,progress,cts.Token))&&!Directory.EnumerateFiles(cancelledCache,"*",SearchOption.AllDirectories).Any(),"Cancellation before renderer removes draft and partial preparation");
    }
    string nativeKeyboard=Path.Combine(work,"native-keyboard.mscz");await exporter.ExportNativeAsync(keyboard,nativeKeyboard,CancellationToken.None);
    var keyboardNativePlan=NativeScoreTransposition.CreatePlan(nativeKeyboard,new(-3));
    if(!keyboardNativePlan.CanPrepare)Console.Error.WriteLine(JsonSerializer.Serialize(keyboardNativePlan));
    Check(keyboardNativePlan.CanPrepare,"Real normalized keyboard remains recognizable");
    string keyboardCheckDraft=Draft(keyboardNativePlan);
    File.WriteAllText(Path.Combine(work,"keyboard-draft.mscx"),Xml(keyboardCheckDraft).ToString());
    string keyboardCheckRender=Path.Combine(work,"keyboard-render.mscz");
    await exporter.ExportNativeAsync(keyboardCheckDraft,keyboardCheckRender,CancellationToken.None);
    File.WriteAllText(Path.Combine(work,"keyboard-render.mscx"),Xml(keyboardCheckRender).ToString());
    var keyboardPrepared=await service.PrepareAsync(keyboardNativePlan,null,CancellationToken.None);
    Check(keyboardPrepared.PreviewPages.Count>0&&keyboardPrepared.Metadata.ImageCount==1,"Real keyboard transposition renders and preserves diagram inventory");
    // The structural unit fixture intentionally has a standalone tuplet/tie marker. Use
    // its complete, ordinary two-voice form for the real renderer rather than a dangling tie.
    string multipleInput=Path.Combine(work,"multiple-input.mscz");File.Copy(multiple,multipleInput);
    Change(multipleInput,d=>d.Descendants().Where(e=>e.Name.LocalName is "Tuplet" or "Spanner").ToList().ForEach(e=>e.Remove()));
    string nativeMultiple=Path.Combine(work,"native-multiple.mscz");await exporter.ExportNativeAsync(multipleInput,nativeMultiple,CancellationToken.None);
    var nativeMultiplePlan=NativeScoreTransposition.CreatePlan(nativeMultiple,new(2));
    if(!nativeMultiplePlan.CanPrepare)throw new InvalidOperationException(string.Join("\n",nativeMultiplePlan.Limitations));
    var multiplePrepared=await service.PrepareAsync(nativeMultiplePlan,null,CancellationToken.None);
    Check(multiplePrepared.Metadata.StaffCount==2&&multiplePrepared.Metadata.NoteCount==3&&multiplePrepared.PreviewPages.Count>0,"Real multipauta transposition preserves complete note and staff inventory");
    var stalePrepared=await service.PrepareAsync(plan,null,CancellationToken.None);File.AppendAllText(original,"source changed");
    Check(await ThrowsAsync<IOException>(()=>service.PublishAsync(plan,stalePrepared,Path.Combine(work,"stale"),null,CancellationToken.None)),"Source changes after preview block stale publication");
}
File.WriteAllText(Path.Combine(work,"report.json"),JsonSerializer.Serialize(new{status="passed",passed=checks.Count,checks},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{checks.Count} transposition checks passed.");

static async Task Export(string source,string target,string museScore)
{
    var start=new ProcessStartInfo(museScore){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true,RedirectStandardOutput=true};
    start.ArgumentList.Add("-o");start.ArgumentList.Add(target);start.ArgumentList.Add(source);
    using var p=Process.Start(start)!;var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
    try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));}catch{try{p.Kill(true);}catch{}throw;}
    if(p.ExitCode!=0||!File.Exists(target))throw new InvalidOperationException(await stdout+await stderr);
}
sealed class ImmediateProgress(Action<string> report):IProgress<string>{public void Report(string value)=>report(value);}
