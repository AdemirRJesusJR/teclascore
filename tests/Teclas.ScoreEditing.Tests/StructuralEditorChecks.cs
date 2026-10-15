using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Teclas.Core;

internal static class StructuralEditorChecks
{
    internal static async Task Run(Action<bool, string> check, string work, string? museScore)
    {
        string dir = Path.Combine(work, "structural-editor"); Directory.CreateDirectory(dir); int sequence = 0;
        const string meter = "<TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>";
        static string N(int pitch=60,int tpc=14) => $"<Note><pitch>{pitch}</pitch><tpc>{tpc}</tpc></Note>";
        static string C(string length="quarter", string? notes=null) => $"<Chord><durationType>{length}</durationType>{notes??N()}</Chord>";
        static string R(string length="quarter") => $"<Rest><durationType>{length}</durationType></Rest>";
        string Write(string bars)
        {
            string file=Path.Combine(dir,$"source-{++sequence}.mscz"); using var zip=ZipFile.Open(file,ZipArchiveMode.Create);
            using var writer=new StreamWriter(zip.CreateEntry("score.mscx").Open(),new UTF8Encoding(false));
            writer.Write($"<museScore version='4.70'><programVersion>4.7.5</programVersion><Score><Part><Staff id='1'><StaffType group='pitched'><name>stdNormal</name></StaffType></Staff><Instrument id='piano'><trackName>Piano</trackName></Instrument></Part><Staff id='1'>{bars}</Staff></Score></museScore>"); return file;
        }
        static string Bar(string first,string? second=null) => $"<Measure><voice>{first}</voice>{(second is null?"":$"<voice>{second}</voice>")}</Measure>";
        NativeMusicModel Model(string content) => NativeMusicEditor.Open(Write(Bar(meter+content)));
        static NativeMusicEdits Replace(MusicEvent e, params MusicEventPart[] parts)=>new([],[],[]){EventReplacements=[new(e.Id,parts)]};
        static bool Valid(NativeMusicModel m,NativeMusicEdits e)=>NativeMusicEditor.Validate(m,e).Count==0;
        NativeMusicModel Draft(NativeMusicModel m,NativeMusicEdits e)
        {string path=Path.Combine(dir,$"draft-{++sequence}.mscz");NativeMusicEditor.WriteDraft(m,e,path);return NativeMusicEditor.Open(path);}
        var original=Model(R("whole"));var rest=original.Measures[0].Events[0];byte[] bytes=File.ReadAllBytes(original.SourcePath);
        var parts=NativeMusicEditor.SplitRest(rest,61,21,"eighth",0);var edit=Replace(rest,parts.ToArray());var result=Draft(original,edit);
        check(Valid(original,edit)&&result.Measures[0].Events.Count==4,"Insert subdivides a whole rest into eighth note and exact remainder rests");
        check(result.Measures[0].Events[0].Notes.Single().Pitch==61&&result.Measures[0].Events.Sum(e=>e.DurationQuarter)==4,"Inserted pitch and total duration are preserved");
        check(bytes.SequenceEqual(File.ReadAllBytes(original.SourcePath)),"Structure editing preserves original bytes");
        check(!Valid(original,Replace(rest,new MusicEventPart("quarter",0,[new(60,14)]))),"Unbalanced insertion is rejected");
        check(!Valid(original,Replace(rest,new MusicEventPart("whole",0,[new(61,14)]))),"Inserted spelling must match pitch");
        check(!Valid(original,Replace(rest,new MusicEventPart("whole",0,[new(60,14),new(60,14)]))),"Duplicate pitches in new attacks are rejected");
        check(!Valid(original,new([],[],[]){EventReplacements=[new("stale",parts)]}),"Stale structural references are rejected");
        var fullBar=Model(R("measure"));var full=fullBar.Measures[0].Events[0];
        check(full.CanRestructure&&Valid(fullBar,Replace(full,NativeMusicEditor.SplitRest(full,64,18,"quarter",0).ToArray())),"Full-measure rests can be safely split using meter duration");
        var chord=Model(C("whole",N()+N(64,18)));var eventChord=chord.Measures[0].Events[0];
        var deleted=Draft(chord,Replace(eventChord,new MusicEventPart("whole",0,[new(64,18)])));
        check(deleted.Metadata.NoteCount==1&&deleted.Measures[0].Events[0].Notes[0].Pitch==64,"Deleting one note preserves remaining chord pitches");
        var pause=Draft(chord,Replace(eventChord,new MusicEventPart("whole",0,[])));
        check(pause.Measures[0].Events[0].Kind=="Rest"&&pause.Measures[0].Events[0].DurationQuarter==4,"Deleting final note creates same-duration rest");
        check(!Valid(chord,Replace(eventChord,new MusicEventPart("whole",0,[])) with {Notes=[new(eventChord.Notes[0].Id,62,16)]}),"Conflicting pitch and structural edits are rejected");
        foreach(string extra in new[]{"<Tuplet>1</Tuplet>","<Lyrics><text>la</text></Lyrics>","<Spanner type='Slur'/>","<Articulation><subtype>staccato</subtype></Articulation>"})
        {
            var special=Model(C("whole").Replace("</Chord>",extra+"</Chord>"));
            check(!Valid(special,Replace(special.Measures[0].Events[0],new MusicEventPart("whole",0,[]))),"Protected musical property blocks destructive replacement: "+extra);
        }
        var copied=NativeMusicEditor.Open(Write(Bar(meter+C("half")+C("half",N(67,15)),R("whole"))+Bar("<Harmony><root>14</root><name>m</name></Harmony>"+R("whole"),C("whole",N(48,14)))));
        var copy=new NativeMusicEdits([],[],[]){VoiceCopies=[new(1,1,1,1,2,1)]};var afterCopy=Draft(copied,copy);
        check(afterCopy.Measures[1].Events.Where(e=>e.Voice==1).SelectMany(e=>e.Notes).Select(n=>n.Pitch).SequenceEqual(new[]{60,67}),"Copy fills destination voice with source attacks");
        check(afterCopy.Measures[1].Harmonies.Count==1&&afterCopy.Measures[1].Events.Single(e=>e.Voice==2).Notes[0].Pitch==48,"Copy preserves destination harmony and other voice");
        check(!Valid(copied,copy with{VoiceCopies=[new(1,1,1,1,1,1)]}),"Copy cannot replace its own origin");
        check(!Valid(copied,copy with{VoiceCopies=[new(1,1,1,1,2,1),new(1,2,1,1,1,1)]}),"Circular and chained copy operations are rejected");
        var mismatch=NativeMusicEditor.Open(Write(Bar(meter+C("whole"))+Bar("<TimeSig><sigN>3</sigN><sigD>4</sigD></TimeSig>"+R("half")+R())));
        check(!Valid(mismatch,copy),"Copy rejects unequal measure duration");
        var partial=NativeMusicEditor.Open(Write(Bar(meter+C())+Bar(R("whole"))));
        check(!Valid(partial,copy),"Copy rejects incomplete voices");
        var anchored=NativeMusicEditor.Open(Write(Bar(meter+C("whole"))+Bar(R()+"<Harmony><root>14</root></Harmony>"+R()+R()+R())));
        check(!Valid(anchored,copy),"Copy refuses to move a destination harmony inside a new note");
        var conflict=Replace(copied.Measures[0].Events[0],new MusicEventPart("half",0,[])) with {VoiceCopies=copy.VoiceCopies};
        check(!Valid(copied,conflict),"Source alterations must be completed before copying its voice");
        if(museScore is not null)
        {
            string raw=Write(Bar(meter+R("whole"))+Bar(C("whole")));string canonical=Path.Combine(dir,"canonical.mscz");
            var exporter=new ScoreExporter(museScore,Path.Combine(dir,"cache"));await exporter.ExportNativeAsync(raw,canonical,CancellationToken.None);
            var model=NativeMusicEditor.Open(canonical);var target=model.Measures[0].Events[0];
            var nativeEdits=Replace(target,NativeMusicEditor.SplitRest(target,61,21,"eighth",0).ToArray());
            var service=new NativeMusicEditingService(museScore,Path.Combine(dir,"cache"));
            var prepared=await service.PrepareAsync(model,nativeEdits,null,CancellationToken.None);
            check(prepared.PreviewPages.Count>0&&prepared.Metadata.NoteCount==2,"MuseScore reopens and renders inserted note with accidental and split rests");
            var published=await service.PublishAsync(model,prepared,Path.Combine(dir,"published"),null,CancellationToken.None);
            check(File.Exists(published.Path)&&NativeMusicEditor.Open(published.Path).Measures[0].Events.Sum(e=>e.DurationQuarter)==4,"Validated structural edit publishes as new version");
            var reopened=NativeMusicEditor.Open(published.Path);var newlyInserted=reopened.Measures[0].Events[0];
            var remove=Replace(newlyInserted,new MusicEventPart("eighth",0,[]));
            var deletedNative=await service.PrepareAsync(reopened,remove,null,CancellationToken.None);
            check(deletedNative.Metadata.NoteCount==1,"Deleting inserted note reopens through real MuseScore");
            var copyPrepared=await service.PrepareAsync(reopened,new([],[],[]){VoiceCopies=[new(1,1,1,1,2,1)]},null,CancellationToken.None);
            check(copyPrepared.Metadata.NoteCount==2&&NativeMusicEditor.Open(copyPrepared.Path).Measures[1].Events[0].Notes[0].Pitch==61,"Copied voice reopens through real MuseScore");
        }
    }
}
