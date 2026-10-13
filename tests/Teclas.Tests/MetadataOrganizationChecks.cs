using System.Buffers.Binary;
using System.Text;
using Teclas.Core;

internal static class MetadataOrganizationChecks
{
    public static void Run(Action<bool,string> check)
    {
        ScoreMetadataSuggestion Suggest(string name,params MidiTextEvent[] events)=>ScoreMetadataOrganizer.Suggest(name,events);
        var example=Suggest("Roberto Carlos - Luz divina (karaoplay.com).mid");
        check(example.Title=="Luz divina" && example.Artist=="Roberto Carlos" && example.Composer=="","Filename separates performer without inventing authorship");
        check(example.NeedsReview && example.Explanation.Contains("arquivo",StringComparison.OrdinalIgnoreCase),"Inferred filename convention explains its origin");
        var spaced=Suggest("Roberto_Carlos_-_Luz__divina_[www.karaoplay.com.br]_(Karaoke).kar");
        check(spaced.Title==example.Title && spaced.Artist==example.Artist,"Underscores, domain and technical suffix are removed");
        foreach(string dash in new[]{"–","—"})
        {
            var value=Suggest($"João Bosco {dash} Papel machê.mid");
            check(value.Title=="Papel machê" && value.Artist=="João Bosco","Delimited Unicode dash separates title and artist");
        }
        foreach(string version in new[]{"(Ao vivo)","(Acústico)","(Instrumental)","2","1984","(Remix)"})
            check(Suggest($"Artista - Canção {version}.mid").Title==$"Canção {version}","Musical edition and legitimate number retained: "+version);
        check(Suggest("A-ha - Take On Me.mid").Artist=="A-ha","Artist's internal hyphen retained");
        check(Suggest("Bem-me-quer.mid").Title=="Bem-me-quer" && Suggest("Bem-me-quer.mid").Artist=="","Undelimited title hyphens are retained");
        var ambiguous=Suggest("Artista - Canção - Ao vivo.mid");
        check(ambiguous.Title=="Artista - Canção - Ao vivo" && ambiguous.Artist=="" && ambiguous.NeedsReview,"Multiple separators are conservative and reviewable");
        var part=Suggest("Parte 1 - Introdução.mid");
        check(part.Artist=="" && part.Title=="Parte 1 - Introdução","Numbered section is not an artist");
        var live=Suggest("Garçom - Ao vivo.mid");
        check(live.Artist=="" && live.Title=="Garçom - Ao vivo" && live.NeedsReview,"Musical version after dash is not mistaken for a title with artist");
        var web=Suggest("Canção (www.karaoplay.com) [MIDI].mid");
        check(web.Title=="Canção","Website and empty brackets removed from filename");
        var embeddedWeb=Suggest("Canção.mid",new MidiTextEvent(0,0,1,"Title: Canção (https://karaoplay.com/download?id=4) [MIDI]"));
        check(embeddedWeb.Title=="Canção","Entire URL and empty brackets removed from declared title");
        check(Suggest("Mr. Brightside.mid").Title=="Mr. Brightside","Title punctuation is not mistaken for a domain");
        check(Suggest("Hotel California (Eagles).mid").Title=="Hotel California (Eagles)" && Suggest("Hotel California (Eagles).mid").Artist=="","Unlabelled parentheses do not invent an artist");
        check(Suggest("karaoplay.com.mid").Title=="Sem título","Empty cleaned title has editable fallback");
        foreach(var value in new[]{spaced,web,ambiguous,Suggest("Canção (Ao vivo) 2.mid")})
            check(Suggest(value.Title+".mid").Title==value.Title,"Cleaned display title is stable: "+value.Title);
        var declared=Suggest("Errado - Nome antigo.mid",new MidiTextEvent(0,0,1,"Título: Luz divina\nArtista: Roberto Carlos\nCompositor: Nome declarado"));
        check(declared.Title=="Luz divina" && declared.Artist=="Roberto Carlos" && declared.Composer=="Nome declarado" && !declared.NeedsReview,"Explicit MIDI fields take precedence");
        var author=Suggest("Canção.mid",new MidiTextEvent(0,0,1,"Author: Ana Souza"));
        check(author.Composer=="Ana Souza" && author.Artist=="","Explicit author accepted independently of artist");
        var conflict=Suggest("Canção.mid",new MidiTextEvent(0,0,1,"Title: Primeira\nArtist: Grupo\nTitle: Segunda"));
        check(conflict.Title=="Primeira" && conflict.NeedsReview,"Conflicting declarations retain first and request review");
        var copyright=Suggest("Canção.mid",new MidiTextEvent(0,0,2,"Composer: Site MIDI"),new MidiTextEvent(0,0,1,"Copyright (c) karaoplay.com"),new MidiTextEvent(0,0,1,"Sequenced by Programador"));
        check(copyright.Composer=="" && copyright.Artist=="" && copyright.Title=="Canção","Copyright and sequencing credits do not become authorship");
        var lyrics=Suggest("Canção.mid",new MidiTextEvent(0,0,5,"Title: Letra da música"),new MidiTextEvent(10,1,1,"Você passou por mim\nE eu sorri"));
        check(lyrics.Title=="Canção" && lyrics.Artist=="","Lyrics and unlabelled text do not become metadata");
        check(Suggest("Canção.mid",new MidiTextEvent(480,0,1,"Title: Cantando"),new MidiTextEvent(960,0,1,"Composer: Nome")).Composer=="" && Suggest("Canção.mid",new MidiTextEvent(480,0,1,"Title: Cantando")).Title=="Canção","Later text annotations never become header metadata");
        foreach(string trackName in new[]{"Acoustic Grand Piano","Brass Section","Track 0","Lead 1","String Ensemble","Church Organ","Arranjo por Fulano"})
            check(Suggest("Canção.mid",new MidiTextEvent(0,0,3,trackName)).Title=="Canção","Track instrument or generic label ignored: "+trackName);
        check(Suggest("track01.mid",new MidiTextEvent(0,0,3,"Minha sequência")).Title=="track01","Arbitrary sequence name does not replace generic filename");
        var namedSequence=Suggest("track01.mid",new MidiTextEvent(0,0,3,"Roberto Carlos - Luz divina"));
        check(namedSequence.Title=="Luz divina" && namedSequence.Artist=="Roberto Carlos","Structured sequence header recognized");
        var karaoke=Suggest("track01.kar",new MidiTextEvent(0,0,1,"@TLuz divina"),new MidiTextEvent(0,0,1,"@TRoberto Carlos"),new MidiTextEvent(0,0,1,"@TCopyright karaoplay.com"));
        check(karaoke.Title=="Luz divina" && karaoke.Artist=="Roberto Carlos" && karaoke.Composer=="" && karaoke.NeedsReview,"Karaoke headers recognized as suggestions, not composer evidence");
        var ambiguousKaraoke=Suggest("track01.kar",new MidiTextEvent(0,0,1,"@TCanção"),new MidiTextEvent(0,0,1,"@TPrimeiro nome"),new MidiTextEvent(0,0,1,"@TSegundo nome"));
        check(ambiguousKaraoke.Title=="Canção" && ambiguousKaraoke.Artist=="" && ambiguousKaraoke.NeedsReview,"Ambiguous karaoke credits do not invent artist");
        var karaokeFilename=Suggest("Roberto Carlos - Luz divina.mid",new MidiTextEvent(0,0,1,"@TLuz divina"),new MidiTextEvent(0,0,1,"@TNome sem função declarada"));
        check(karaokeFilename.Artist=="Roberto Carlos","Congruent filename performer retained over ambiguous karaoke credit");
        var longFields=Suggest("song.mid",new MidiTextEvent(0,0,1,"Title: "+new string('A',1000)),new MidiTextEvent(0,0,1,"Artist: "+new string('B',1000)),new MidiTextEvent(0,0,1,"Composer: "+new string('C',1000)));
        check(longFields.Title.Length<=160 && longFields.Artist.Length<=120 && longFields.Composer.Length<=120 && longFields.NeedsReview,"Suggestions fit editable score metadata limits and flag truncation");

        var utf8=MidiReader.Read(Midi((1,Encoding.UTF8.GetBytes("Título: Coração")),(1,Encoding.UTF8.GetBytes("Artista: João"))));
        check(utf8.TextEvents.Count==2 && Suggest("song.mid",utf8.TextEvents.ToArray()).Title=="Coração","UTF-8 metadata retained and parsed");
        var legacy=MidiReader.Read(Midi((1,Encoding.Latin1.GetBytes("Título: Coração")),(1,new byte[]{65,114,116,105,115,116,97,58,32,0x93,74,111,0xe3,111,0x94})));
        check(Suggest("song.mid",legacy.TextEvents.ToArray()).Title=="Coração" && legacy.TextEvents[1].Text=="Artista: “João”","Latin-1 and Windows-1252 decoded without replacement characters");
        check(MidiReader.Read(Midi((1,Encoding.Latin1.GetBytes("Title: Café")))).TextEvents.Single().Text=="Title: Café","Legacy accented final byte is decoded instead of silently omitted");
        var limit=MidiReader.Read(Midi(Enumerable.Range(0,300).Select(index=>(1,Encoding.ASCII.GetBytes(new string('A',2100)+index))).ToArray()));
        check(limit.TextEvents.Count==256 && limit.TextEvents.All(item=>item.Text.Length==2048),"MIDI text collection and lengths are bounded");
        var unicodeLimit=MidiReader.Read(Midi((1,Encoding.UTF8.GetBytes(new string('á',2200)))));
        check(unicodeLimit.TextEvents.Single().Text==new string('á',2048),"Length cap counts Unicode characters rather than UTF-8 bytes");
        var noTexts=MidiReader.Read(Midi());
        bool SameMusic(MidiDocument candidate)=>candidate.Notes.Select(note=>(note.Track,note.Channel,note.Program,note.Pitch,note.Velocity,note.Start,note.End)).SequenceEqual(noTexts.Notes.Select(note=>(note.Track,note.Channel,note.Program,note.Pitch,note.Velocity,note.Start,note.End)));
        check(SameMusic(utf8) && SameMusic(legacy) && SameMusic(limit),"Extracting metadata preserves all MIDI notes");
        check(noTexts.TextEvents.Count==0,"MIDI without metadata remains supported");
    }

    private static byte[] Midi(params (int Type,byte[] Bytes)[] metadata)
    {
        var track=new List<byte>();
        foreach(var value in metadata)
        {
            track.AddRange(new byte[]{0,0xff,(byte)value.Type});
            var length=new List<byte>{(byte)(value.Bytes.Length&127)};
            for(int remaining=value.Bytes.Length>>7;remaining>0;remaining>>=7)length.Insert(0,(byte)((remaining&127)|128));
            track.AddRange(length);track.AddRange(value.Bytes);
        }
        track.AddRange(new byte[]{0,0x90,60,90,0x81,0x70,0x80,60,0,0,0xff,0x2f,0});
        var bytes=new List<byte>(Encoding.ASCII.GetBytes("MThd"));
        bytes.AddRange(new byte[]{0,0,0,6,0,0,0,1,1,224});
        bytes.AddRange(Encoding.ASCII.GetBytes("MTrk"));
        var trackLength=new byte[4];BinaryPrimitives.WriteInt32BigEndian(trackLength,track.Count);
        bytes.AddRange(trackLength);bytes.AddRange(track);
        return bytes.ToArray();
    }
}
