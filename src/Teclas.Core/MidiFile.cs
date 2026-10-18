using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
namespace Teclas.Core;

public record MidiNote(int Track,int Channel,int Program,int Pitch,int Velocity,long Start,long End)
{
    public int StartOrder { get; init; }
    public int EndOrder { get; init; }
}
public record SourceKey(int Track,int Channel,int Program);
public record MidiSource(SourceKey Key,string Name,int Count,int Minimum,int Maximum,int DistinctPitches,double SimultaneousFraction,string Hint);
public record MidiTempoChange(long Tick,int MicrosecondsPerQuarter,int Track=0,int Order=0);
public record MidiChannelEvent(long Tick,int Track,int Order,int Channel,int Kind,int Data1,int Data2);
public record MidiTextEvent(long Tick,int Track,int Type,string Text);
public record MidiTimeSignature(long Tick,int Numerator,int Denominator,int Track=0,int Order=0);
public record MidiDocument(string Path,string Sha256,int Format,int Ppq,IReadOnlyList<MidiNote> Notes,IReadOnlyList<MidiSource> Sources,IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<MidiTempoChange> TempoChanges { get; init; } = [];
    public IReadOnlyList<MidiChannelEvent> ChannelEvents { get; init; } = [];
    // Original channel messages, including note-offs and zero-velocity note-ons.
    // Null denotes a programmatically built document without an original event stream.
    // Notes and ChannelEvents retain their existing analysis/source-isolation contracts.
    public IReadOnlyList<MidiChannelEvent>? PlaybackEvents { get; init; }
    public IReadOnlyList<MidiTextEvent> TextEvents { get; init; } = [];
    public IReadOnlyList<MidiTimeSignature> TimeSignatures { get; init; } = [];
    public long EndTick { get; init; }
    public bool HasSystemExclusive { get; init; }
}

public static class MidiReader
{
    private record Event(long Tick,int Track,int Order,int Kind,int Channel,int A,int B);
    public static MidiDocument Read(string path) => Read(File.ReadAllBytes(path),System.IO.Path.GetFullPath(path));
    public static MidiDocument Read(byte[] original,string path="memory.mid")
    {
        if(original.Length>64*1024*1024) throw new InvalidDataException("O MIDI ultrapassa o limite de 64 MB.");
        byte[] bytes=original;
        if(original.Length>=12 && Encoding.ASCII.GetString(original,0,4)=="RIFF" && Encoding.ASCII.GetString(original,8,4)=="RMID")
        {
            int ri=12;bool found=false;
            while(ri+8<=original.Length)
            {
                int len=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(ri+4,4)));
                if(len<0 || len>original.Length-ri-8)throw new InvalidDataException("Contêiner RMID truncado.");
                if(Encoding.ASCII.GetString(original,ri,4)=="data"){bytes=original.AsSpan(ri+8,len).ToArray();found=true;break;}
                ri=checked(ri+8+len+(len%2));
            }
            if(!found)throw new InvalidDataException("O RMID não contém um bloco MIDI.");
        }
        int pos=0;
        void Need(int count){if(count<0 || pos>bytes.Length-count)throw new InvalidDataException("Arquivo MIDI truncado.");}
        int U16(){Need(2);int n=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos,2));pos+=2;return n;}
        int U32(){Need(4);int n=checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos,4)));pos+=4;return n;}
        string Tag(){Need(4);var s=Encoding.ASCII.GetString(bytes,pos,4);pos+=4;return s;}
        if(Tag()!="MThd")throw new InvalidDataException("O arquivo não tem um cabeçalho MIDI válido.");
        int header=U32();if(header<6)throw new InvalidDataException("Cabeçalho MIDI inválido.");
        Need(header);int format=U16(), declared=U16(), ppq=U16();pos+=header-6;
        if(format>1)throw new InvalidDataException("MIDI tipo 2 precisa ser dividido em músicas independentes.");
        if(ppq==0 || (ppq&0x8000)!=0)throw new InvalidDataException("Este MIDI usa tempo SMPTE ou uma resolução inválida; é necessário MIDI com tempo musical PPQ.");
        var events=new List<Event>();var tempos=new List<MidiTempoChange>();var signatures=new List<MidiTimeSignature>();var textEvents=new List<MidiTextEvent>();int lyricTexts=0,otherTexts=0;bool hasSysEx=false;var names=new Dictionary<int,string>();var warnings=new List<string>();int track=0;long last=0;
        while(pos+8<=bytes.Length)
        {
            if(track>=declared && Encoding.ASCII.GetString(bytes,pos,4)!="MTrk")break;
            string tag=Tag();int length=U32();Need(length);int end=pos+length;
            if(tag!="MTrk"){pos=end;continue;}
            int running=0,order=0;long tick=0;
            int Byte(){if(pos>=end)throw new InvalidDataException($"Pista {track+1} truncada.");return bytes[pos++];}
            int Vlq(){int result=0;for(int n=0;n<4;n++){int v=Byte();result=(result<<7)|(v&127);if((v&128)==0)return result;}throw new InvalidDataException("Duração MIDI inválida (VLQ).");}
            while(pos<end)
            {
                tick=checked(tick+Vlq());int status=Byte();order++;
                if(status<128){if(running==0)throw new InvalidDataException("Running status MIDI ausente.");pos--;status=running;}
                if(status==255)
                {
                    int type=Byte(), len=Vlq();if(len>end-pos)throw new InvalidDataException("Metaevento MIDI truncado.");
                    if(type is 1 or 2 or 3 or 5)
                    {
                        string value=DecodeText(bytes.AsSpan(pos,len));
                        if(type==3)names[track]=value.Trim('\0','\r','\n');
                        // Lyrics (type 5) and short karaoke texts (type 1 syllables) can be hundreds; other texts stay bounded.
                        if(value.Length>0 && (type==5 || type==1 && value.Length<=64 ? lyricTexts++<20000 : otherTexts++<256))textEvents.Add(new(tick,track,type,value));
                    }
                    if(type==0x51)
                    {
                        if(len==3 && (bytes[pos]|bytes[pos+1]|bytes[pos+2])!=0)
                            tempos.Add(new(tick,(bytes[pos]<<16)|(bytes[pos+1]<<8)|bytes[pos+2],track,order));
                        else warnings.Add($"Tempo inválido na pista {track+1}; foi mantido o andamento anterior.");
                    }
                    if(type==0x58 && len>=2)
                    {
                        if(bytes[pos] is >0 and <=64 && bytes[pos+1]<=6)signatures.Add(new(tick,bytes[pos],1<<bytes[pos+1],track,order));
                        else warnings.Add($"Fórmula de compasso inválida na pista {track+1}; foi mantida a anterior.");
                    }
                    pos+=len;continue;
                }
                if(status is 240 or 247){hasSysEx=true;int len=Vlq();if(len>end-pos)throw new InvalidDataException("SysEx truncado.");pos+=len;running=0;continue;}
                if(status is <128 or >239)throw new InvalidDataException($"Status MIDI não suportado: {status:X2}.");
                running=status;int kind=status>>4,channel=(status&15)+1,a=Byte(),b=(kind is 12 or 13)?0:Byte();
                if(a>=128 || b>=128)throw new InvalidDataException("Dados de nota MIDI inválidos.");
                events.Add(new(tick,track,order,kind,channel,a,b));
            }
            last=Math.Max(last,tick);track++;
        }
        if(pos<bytes.Length)warnings.Add($"{bytes.Length-pos} bytes extras após as pistas foram ignorados.");
        if(track!=declared)warnings.Add($"Cabeçalho informa {declared} pistas, mas foram encontradas {track}.");
        var programs=new int[17];var active=new Dictionary<(int,int,int),Queue<MidiNote>>();var notes=new List<MidiNote>();
        foreach(var e in events.OrderBy(e=>e.Tick).ThenBy(e=>e.Track).ThenBy(e=>e.Order))
        {
            if(e.Kind==12){programs[e.Channel]=e.A;continue;}
            var key=(e.Track,e.Channel,e.A);
            if(e.Kind==9 && e.B>0)
            {
                if(!active.TryGetValue(key,out var q))active[key]=q=new();
                q.Enqueue(new(e.Track,e.Channel,programs[e.Channel],e.A,e.B,e.Tick,e.Tick){StartOrder=e.Order});
            }
            else if(e.Kind==8 || (e.Kind==9 && e.B==0))
            {
                if(active.TryGetValue(key,out var q) && q.Count>0)notes.Add(q.Dequeue() with{End=e.Tick,EndOrder=e.Order});
            }
        }
        int dangling=0;
        foreach(var q in active.Values)while(q.Count>0){notes.Add(q.Dequeue() with{End=last,EndOrder=int.MaxValue});dangling++;}
        if(dangling>0)warnings.Add($"{dangling} notas sem término explícito; revisar a duração.");
        var sources=notes.GroupBy(n=>new SourceKey(n.Track,n.Channel,n.Program)).OrderBy(g=>g.Key.Track).ThenBy(g=>g.Key.Channel).ThenBy(g=>g.Key.Program).Select(g=>{
            var pitches=g.Select(n=>n.Pitch).ToArray();int distinct=pitches.Distinct().Count();
            double simultaneous=(double)g.GroupBy(n=>n.Start).Where(s=>s.Count()>1).Sum(s=>s.Count())/g.Count();
            string hint=g.Key.Channel==10?"Percussão":g.Key.Program is >=32 and <=39?"Baixo":distinct<3?"Nota-guia / altura quase fixa":simultaneous>.5?"Acompanhamento em acordes":"Revisar como possível melodia";
            return new MidiSource(g.Key,names.GetValueOrDefault(g.Key.Track,$"Pista {g.Key.Track+1}"),g.Count(),pitches.Min(),pitches.Max(),distinct,simultaneous,hint);
        }).ToList();
        if(notes.Count==0)throw new InvalidDataException("O MIDI não contém notas.");
        var playbackEvents=events.OrderBy(e=>e.Tick).ThenBy(e=>e.Track).ThenBy(e=>e.Order)
            .Select(e=>new MidiChannelEvent(e.Tick,e.Track,e.Order,e.Channel,e.Kind,e.A,e.B)).ToArray();
        return new(path,Convert.ToHexStringLower(SHA256.HashData(original)),format,ppq,notes,sources,warnings)
        {
            TempoChanges=tempos.OrderBy(t=>t.Tick).ThenBy(t=>t.Track).ThenBy(t=>t.Order).ToArray(),
            TextEvents=textEvents.ToArray(),
            TimeSignatures=signatures.OrderBy(t=>t.Tick).ThenBy(t=>t.Track).ThenBy(t=>t.Order).ToArray(),
            ChannelEvents=playbackEvents.Where(e=>e.Kind is >=10 and <=14).ToArray(),
            PlaybackEvents=playbackEvents,
            EndTick=last,HasSystemExclusive=hasSysEx
        };
    }
    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        var characters=new char[2048];
        try
        {
            new UTF8Encoding(false,true).GetDecoder().Convert(bytes,characters,true,out _,out int used,out _);
            return new string(characters,0,used).Trim('\0');
        }
        catch(DecoderFallbackException)
        {
            // Older karaoke files commonly use Windows-1252. Keep Latin-1 letters and
            // decode the printable 0x80–0x9f characters without a code-page dependency.
            const string windows1252="€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008dŽ\u008f\u0090‘’“”•–—˜™š›œ\u009džŸ";
            int count=Math.Min(bytes.Length,characters.Length);
            for(int i=0;i<count;i++)characters[i]=bytes[i] is >=0x80 and <=0x9f?windows1252[bytes[i]-0x80]:(char)bytes[i];
            return new string(characters,0,count).Trim('\0');
        }
    }
    public static string NoteName(int pitch)=>new[]{"C","C#","D","Eb","E","F","F#","G","Ab","A","Bb","B"}[pitch%12]+(pitch/12-1);
}
