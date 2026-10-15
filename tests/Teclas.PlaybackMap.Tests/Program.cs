using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Teclas.Core;

string work=Path.GetFullPath(args.Length>0?args[0]:Path.Combine(Path.GetTempPath(),"teclas-map-"+Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(work);
string museScore=args.Length>1?args[1]:@"C:\Program Files\MuseScore 4\bin\MuseScore4.exe";
var checks=new List<string>();
void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);checks.Add(message);}
async Task<bool> Throws<T>(Func<Task> action)where T:Exception{try{await action();return false;}catch(T){return true;}}
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
const string meter="<TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>";
string Note(int pitch,int tpc)=>$"<Note><pitch>{pitch}</pitch><tpc>{tpc}</tpc></Note>";
string Chord(string type,int pitch,int tpc)=>$"<Chord><durationType>{type}</durationType>{Note(pitch,tpc)}</Chord>";
string Voice(string text)=>"<voice>"+text+"</voice>";
string Measure(string text,string extra="")=>"<Measure>"+extra+Voice(text)+"</Measure>";
string Write(string measures,string? otherStaff=null)
{
    string path=Path.Combine(work,"fixture-"+Guid.NewGuid().ToString("N")+".mscz");
    using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
    using var writer=new StreamWriter(zip.CreateEntry("score.mscx").Open(),new UTF8Encoding(false));
    writer.Write("<museScore version='4.70'><Score><metaTag name='workTitle'>Mapa de estudo</metaTag><Part><Staff id='1'><StaffType group='pitched'/></Staff>"+
        (otherStaff is null?"":"<Staff id='2'><StaffType group='pitched'/></Staff>")+
        "<Instrument id='piano'><Channel><program value='0'/><synti>Fluid</synti></Channel></Instrument></Part><Staff id='1'>"+measures+"</Staff>"+
        (otherStaff is null?"":"<Staff id='2'>"+otherStaff+"</Staff>")+"</Score></museScore>");
    return path;
}
string repeated=Write(Measure(meter+"<Tempo><tempo>1</tempo><text>60</text></Tempo>"+Chord("whole",60,14),"<startRepeat/>")+
    Measure("<Tempo><tempo>2</tempo><text>120</text></Tempo>"+Chord("whole",62,16),"<endRepeat>2</endRepeat>")+
    Measure("<TimeSig><sigN>3</sigN><sigD>4</sigD></TimeSig>"+Chord("half",64,18)+Chord("quarter",65,13))+
    "<Measure len='1/4'>"+Voice(Chord("quarter",67,15))+"</Measure>");
string initialHash=Hash(repeated);
var service=new ScorePlaybackMapService(museScore,Path.Combine(work,"cache"));
var mapCache=new ScorePlaybackMapCache();int nativeMapPreparations=0;
Task<ScorePlaybackMap> PrepareCached(CancellationToken token)=>mapCache.GetOrPrepareAsync(repeated,museScore,1,async request=>
{nativeMapPreparations++;return await service.PrepareAsync(repeated,1,null,request);},null,token);
var cacheWatch=Stopwatch.StartNew();var map=await PrepareCached(CancellationToken.None);cacheWatch.Stop();
double coldMapMilliseconds=cacheWatch.Elapsed.TotalMilliseconds;var cachedMapMilliseconds=new List<double>();
for(int request=0;request<3;request++)
{
    cacheWatch.Restart();var cached=await PrepareCached(CancellationToken.None);cacheWatch.Stop();cachedMapMilliseconds.Add(cacheWatch.Elapsed.TotalMilliseconds);
    Check(ReferenceEquals(map,cached),"Native map cache reuses verified real pages and the exact occurrence timeline");
}
Check(nativeMapPreparations==1&&mapCache.CacheHits==3,"Four repeated native requests invoke only one MuseScore map export");
Check(map.CanSynchronize,"Native MuseScore exports enable verified measure synchronization");
Check(map.Measures.Count==4&&map.MeasureTimeline.Select(t=>t.Measure).SequenceEqual(new[]{1,2,1,2,3,4}),"Repeated measures are represented by actual ordered playback occurrences");
Check(map.MeasureTimeline.Select(t=>t.StartSeconds).Zip(new[]{0d,4,6,10,12,13.5}).All(p=>Math.Abs(p.First-p.Second)<.012),
    "Tempo changes, return repeats, three-four and pickup durations retain native event times");
Check(Math.Abs(map.DurationSeconds-14)<.03,"Exported MIDI tempo map sets the final musical boundary");
Check(map.At(4.5)?.Measure==2&&map.At(6.1)?.Measure==1&&map.At(map.DurationSeconds) is null,"Position lookup follows repeats and clears beyond musical completion");
Check(map.FindOccurrence(1,null)?.StartSeconds==0&&Math.Abs(map.FindOccurrence(1,null,5)!.StartSeconds-6)<.01&&
    Math.Abs(map.FindOccurrence(1,null,6.1)!.StartSeconds-6)<.01,"Click seeking chooses first, next or current repeat occurrence explicitly");
Check(map.FindOccurrence(999,null) is null&&map.At(double.NaN) is null&&map.At(-1) is null,"Unknown measures and invalid positions never invent a cursor");
Check(map.Segments.Count==5&&map.Segments.All(s=>s.EventIds.Count==1),"Simple attacks map one-to-one to native event identities");
var model=NativeMusicEditor.Open(repeated);
Check(map.Segments.SelectMany(s=>s.EventIds).Order().SequenceEqual(model.Measures.SelectMany(m=>m.Events).Select(e=>e.Id).Order()),"Clicked attack identities belong to the exact immutable native model");
Check(map.PreviewPages.Count==1&&File.Exists(map.PreviewPages[0])&&map.Measures.All(m=>m.Bounds.Page==1&&m.Bounds.X>=0&&m.Bounds.X+m.Bounds.Width<=1.01),"Map coordinates describe the complete exported page");
Check(Hash(repeated)==initialHash,"Position and MIDI exports preserve the original archive byte for byte");
var slow=await service.PrepareAsync(repeated,.5,null,CancellationToken.None);
Check(slow.CanSynchronize&&slow.Speed==.5&&slow.MeasureTimeline.Select(t=>t.StartSeconds).Zip(map.MeasureTimeline.Select(t=>t.StartSeconds)).All(p=>Math.Abs(p.First-p.Second)<.01),
    "Study map positions use the player's audio-times-speed domain");
Check(map.MeasureTimeline.Count==slow.MeasureTimeline.Count&&map.Measures.SequenceEqual(slow.Measures),
    "Changing study speed preserves repeated occurrence structure and visual geometry");
using(var cts=new CancellationTokenSource()){cts.Cancel();Check(await Throws<OperationCanceledException>(()=>service.PrepareAsync(repeated,1,null,cts.Token)),"Cancelled map preparation never starts conversion");}
Check(await Throws<ArgumentOutOfRangeException>(()=>service.PrepareAsync(repeated,.33,null,CancellationToken.None)),"Unsupported study speed is rejected");
string multi=Write(Measure(meter+Chord("half",60,14)+Chord("half",62,16)),Measure(meter+Chord("quarter",48,14)+Chord("quarter",50,16)+Chord("half",52,18)));
var multiMap=await service.PrepareAsync(multi,1,null,CancellationToken.None);
Check(multiMap.CanSynchronize&&multiMap.Segments.Count==3&&multiMap.Segments.Count(s=>s.EventIds.Count==2)==2,
    "Simultaneous staves share a segment without selecting an arbitrary voice");
string special=Write(Measure(meter+"<Tuplet id='1'><normalNotes>2</normalNotes><actualNotes>3</actualNotes><baseNote>eighth</baseNote></Tuplet>"+
    "<Chord><Tuplet>1</Tuplet><durationType>eighth</durationType>"+Note(60,14)+"</Chord>"+
    "<Chord><Tuplet>1</Tuplet><durationType>eighth</durationType>"+Note(62,16)+"</Chord>"+
    "<Chord><Tuplet>1</Tuplet><durationType>eighth</durationType>"+Note(64,18)+"</Chord>"+
    "<Rest><dots>1</dots><durationType>half</durationType></Rest>"));
var specialMap=await service.PrepareAsync(special,1,null,CancellationToken.None);
Check(specialMap.CanSynchronize&&specialMap.Segments.All(s=>s.EventIds.Count==0)&&specialMap.Limitations.Any(l=>l.Contains("Quiálteras",StringComparison.Ordinal)),
    "Complex notation keeps real measure synchronization while explicitly withholding ambiguous editing targets");
string mapDirectory=Path.GetDirectoryName(map.PreviewPages[0])!;
string mpos=Path.Combine(mapDirectory,"compassos.mpos"),spos=Path.Combine(mapDirectory,"segmentos.spos"),midi=Path.Combine(mapDirectory,"partitura.mid");
string broken=Path.Combine(work,"bad.mpos");var xml=XDocument.Load(mpos);xml.Root!.Element("events")!.Elements("event").Last().SetAttributeValue("position","1");xml.Save(broken);
var bad=ScorePlaybackMapService.ReadExports(repeated,initialHash,1,broken,spos,midi,map.PreviewPages);
Check(!bad.CanSynchronize&&bad.Timeline.Count==0&&bad.Limitations.Count>0,"Out-of-order native event times disable synchronization instead of estimating progress");
Check(await Throws<IOException>(()=>Task.Run(()=>ScorePlaybackMapService.ReadExports(repeated,new string('0',64),1,mpos,spos,midi,map.PreviewPages))),
    "A changed source cannot reuse stale map exports");
var changed=XDocument.Load(spos);changed.Root!.Element("events")!.Elements("event").First().SetAttributeValue("elid","99999");string badSpos=Path.Combine(work,"bad.spos");changed.Save(badSpos);
var unresolved=ScorePlaybackMapService.ReadExports(repeated,initialHash,1,mpos,badSpos,midi,map.PreviewPages);
Check(!unresolved.CanSynchronize&&unresolved.Timeline.Count==0,"Unknown exported segment references cannot be treated as valid playback");
var cacheMeasurement=new{requests=4,nativeMapPreparations,cacheHits=mapCache.CacheHits,coldMapMilliseconds,cachedMapMilliseconds,
    note="Native synthetic-score map preparation only; not an audio-output latency measurement. No absolute timing threshold."};
var report=new{passed=checks.Count,checks,cacheMeasurement,map,slow};
File.WriteAllText(Path.Combine(work,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{checks.Count} playback-map checks passed.");
