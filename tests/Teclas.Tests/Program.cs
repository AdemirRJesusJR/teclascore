using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Teclas.Core;
int passed=0;
void Check(bool ok,string message){if(!ok)throw new Exception(message);passed++;}
MetadataOrganizationChecks.Run(Check);
ScoreFileNameChecks.Run(Check);
MelodySourceAdvisorChecks.Run(Check);
byte[] Midi(byte[] track)
{
    var b=new List<byte>(Encoding.ASCII.GetBytes("MThd"));b.AddRange(new byte[]{0,0,0,6,0,0,0,1,1,224});b.AddRange(Encoding.ASCII.GetBytes("MTrk"));b.AddRange(new byte[]{0,0,(byte)(track.Length>>8),(byte)track.Length});b.AddRange(track);return b.ToArray();
}
var input=Midi(new byte[]{0,0xC0,21,0,0x90,60,90,0x81,0x70,60,0,0,0xFF,0x2F,0});
var doc=MidiReader.Read(input);
Check(doc.Ppq==480 && doc.Notes.Count==1,"PPQ and running-status pairing");
Check(doc.Notes[0].End==240 && doc.Notes[0].Program==21,"Program and zero-velocity note-off");
try{MidiReader.Read(input[..^2]);throw new Exception("Truncated file accepted");}catch(InvalidDataException){passed++;}
try{MidiReader.Read(new byte[]{0,1,2});throw new Exception("Invalid header accepted");}catch(InvalidDataException){passed++;}
var overlap=MidiReader.Read(Midi(new byte[]{0,0x90,60,80,1,60,90,1,0x80,60,0,1,60,0,0,0xFF,0x2F,0}));
Check(overlap.Notes.Count==2 && overlap.Notes[0].Start==0 && overlap.Notes[0].End==2 && overlap.Notes[1].End==3,"Overlapping notes are paired FIFO");
if(args.Length>0 && args[0]!="--no-inventory")
{
    using var inventory=JsonDocument.Parse(File.ReadAllText(args[0]));
    foreach(var file in inventory.RootElement.GetProperty("files").EnumerateArray())
    {
        var path=file.GetProperty("path").GetString()!;MidiDocument midi;
        try{midi=MidiReader.Read(path);}catch(Exception ex){throw new Exception("Source failed: "+path,ex);}
        Check(midi.Notes.Count==file.GetProperty("note_count").GetInt32(),"Note count: "+path);
        Check(midi.Sha256==file.GetProperty("sha256").GetString(),"Source hash: "+path);
        Check(midi.Ppq==file.GetProperty("ppq").GetInt32(),"PPQ: "+path);
        foreach(var group in file.GetProperty("note_groups").EnumerateArray())
        {
            var source=midi.Sources.Single(s=>s.Key==new SourceKey(group.GetProperty("track").GetInt32(),group.GetProperty("channel").GetInt32(),group.GetProperty("program").GetInt32()));
            Check(source.Count==group.GetProperty("notes").GetInt32(),"Group count: "+path);
        }
    }
    foreach(var bad in inventory.RootElement.GetProperty("failed_files").EnumerateArray())
    {
        try{MidiReader.Read(bad.GetProperty("path").GetString()!);throw new Exception("Damaged source accepted");}catch(InvalidDataException){passed++;}
    }
}
if(args.Length>1 && args[1]=="engine")
{
    var python=args[2];var engine=args[3];var midi=args[4];var work=args[5];Directory.CreateDirectory(work);
    var client=new EngineClient(python,engine);
    var progress=new Progress<string>(Console.WriteLine);
    var request=new JsonObject{["mode"]="analyze",["workspace"]=work,["inputMidi"]=midi,["useReviewedProfile"]=true,["melodySources"]=new JsonArray()};
    var analysis=await client.RunAsync(request,work,progress,CancellationToken.None);
    Check(analysis["reviewedProfile"]!.GetValue<bool>(),"Reviewed profile recognized through C# protocol");
    var chords=analysis["chords"]!.DeepClone();
    var generate=new JsonObject{["mode"]="generate",["workspace"]=work,["inputMidi"]=midi,["analysisPath"]=analysis["analysisPath"]!.GetValue<string>(),["musescorePath"]=args[6],["outputDirectory"]=Path.Combine(work,"output"),["chords"]=chords,["keyFifths"]=analysis["keyFifths"]!.GetValue<int>(),["keyMinor"]=analysis["keyMinor"]!.GetValue<bool>()};
    var result=await client.RunAsync(generate,work,progress,CancellationToken.None);
    Check(File.Exists(result["scorePath"]!.GetValue<string>()),"Actual MSCZ output");
    Check(File.Exists(result["previewPath"]!.GetValue<string>()),"Actual score preview");
    Console.WriteLine(result.ToJsonString());
}
Console.WriteLine($"PASS: {passed} checks.");
