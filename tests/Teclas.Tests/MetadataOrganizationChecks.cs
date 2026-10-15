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
        foreach(var pair in new[]{("A gente fez amor","Gusttavo Lima"),("Agora eu pego mesmo","Os Baraes da Pisadinha"),("50 Reais","Naiara Azevedo"),
            ("Eu preciso de você","Ana Souza"),("Deixa o som ligado","Forro Boys"),("Minha canção","Grupo Horizonte")})
        {
            foreach(string filename in new[]{$"{pair.Item1} - {pair.Item2}.mid",$"{pair.Item2} - {pair.Item1}.mid"})
            {
                var value=Suggest(filename);
                check(value.Title==pair.Item1 && value.Artist==pair.Item2 && value.Composer=="","Both filename orders use linguistic evidence: "+filename);
                check(value.NeedsReview,"Filename inference always remains reviewable: "+filename);
            }
        }
        var lower=Suggest("a gente fez amor - gusttavo lima.mid");
        check(lower.Title=="a gente fez amor" && lower.Artist=="gusttavo lima","Lowercase names retain original casing and use phrase evidence");
        var upper=Suggest("50 REAIS - NAIARA AZEVEDO.mid");
        check(upper.Title=="50 REAIS" && upper.Artist=="NAIARA AZEVEDO","Uppercase numeric title is not mistaken for performer");
        foreach(string filename in new[]{"Lua Azul - Mar Aberto.mid","Time - Eagles.mid","Alpha Beta - Gamma Delta.mid","eu te amo - voce me ama.mid"})
        {
            var value=Suggest(filename);
            check(value.Title==Path.GetFileNameWithoutExtension(filename) && value.Artist=="" && value.NeedsReview && value.Explanation.Contains("ambígua"),"Ambiguous orders preserve the complete title instead of inventing an artist: "+filename);
        }
        var numericEdition=Suggest("50 Reais (Ao vivo) - Naiara Azevedo [www.karaoplay.com].mid");
        check(numericEdition.Title=="50 Reais (Ao vivo)" && numericEdition.Artist=="Naiara Azevedo","Numeric title, live version and performer retained while website removed");
        var titleEvidence=Suggest("Lua Azul - Mar Aberto.mid",new MidiTextEvent(0,0,1,"Title: Lua Azul"));
        check(titleEvidence.Title=="Lua Azul" && titleEvidence.Artist=="Mar Aberto","Declared title resolves reversed filename order");
        var artistEvidence=Suggest("Lua Azul - Mar Aberto.mid",new MidiTextEvent(0,0,1,"Artist: Lua Azul"));
        check(artistEvidence.Title=="Mar Aberto" && artistEvidence.Artist=="Lua Azul","Declared artist resolves conventional filename order");
        var numericHeader=Suggest("50 Reais - Naiara Azevedo.mid",new MidiTextEvent(0,0,3,"50 Reais - Naiara Azevedo"));
        check(numericHeader.Title=="50 Reais" && numericHeader.Artist=="Naiara Azevedo","Repeated sequence header does not reverse title-first name");
        var reverseDeclared=Suggest("Gusttavo Lima - A gente fez amor.mid",new MidiTextEvent(0,0,1,"Title: Outro título\nArtist: Outra pessoa\nComposer: Autoria declarada"));
        check(reverseDeclared.Title=="Outro título" && reverseDeclared.Artist=="Outra pessoa" && reverseDeclared.Composer=="Autoria declarada" && !reverseDeclared.NeedsReview,"Explicit metadata wins over every filename heuristic");
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
        var differentKaraoke=Suggest("Roberto Carlos - Luz divina.mid",new MidiTextEvent(0,0,1,"@TOutra canção"));
        check(differentKaraoke.Title=="Outra canção" && differentKaraoke.Artist=="" && differentKaraoke.NeedsReview,"An unrelated embedded title does not inherit filename performer without corroboration");
        var phraseCredit=Suggest("track01.kar",new MidiTextEvent(0,0,1,"@TMinha canção"),new MidiTextEvent(0,0,1,"@TEu te amo"));
        check(phraseCredit.Artist=="" && phraseCredit.NeedsReview,"A lyrical phrase in a karaoke credit is not assumed to be a performer");
        var reversedKaraoke=Suggest("track01.kar",new MidiTextEvent(0,0,1,"@T50 Reais - Naiara Azevedo"));
        check(reversedKaraoke.Title=="50 Reais" && reversedKaraoke.Artist=="Naiara Azevedo","Title-first karaoke header follows the same direction rules");
        var longFields=Suggest("song.mid",new MidiTextEvent(0,0,1,"Title: "+new string('A',1000)),new MidiTextEvent(0,0,1,"Artist: "+new string('B',1000)),new MidiTextEvent(0,0,1,"Composer: "+new string('C',1000)));
        check(longFields.Title.Length<=160 && longFields.Artist.Length<=120 && longFields.Composer.Length<=120 && longFields.NeedsReview,"Suggestions fit editable score metadata limits and flag truncation");
        string boundaryText="Title: "+new string('x',2040)+char.ConvertFromUtf32(0x1f3b5);
        var boundarySuggestion=Suggest("Canção - Grupo Exemplo.mid",new MidiTextEvent(0,0,1,boundaryText));
        check(boundarySuggestion.Title.Length<=160 && boundarySuggestion.NeedsReview,"Raw metadata cap never splits a supplementary Unicode character before normalization");
        check(Suggest(new string('x',2047)+char.ConvertFromUtf32(0x1f3b5)+".mid").Title.Length<=160,"Filename cleanup cap also preserves Unicode boundaries");
        var malformedUnicode=Suggest("Canção - Grupo Exemplo.mid",new MidiTextEvent(0,0,1,"Title: Canção\ud800\nArtist: Grupo Exemplo"));
        check(malformedUnicode.Title=="Canção\ufffd" && malformedUnicode.Artist=="Grupo Exemplo","Unpaired UTF-16 input is repaired without a normalization exception");

        var utf8=MidiReader.Read(Midi((1,Encoding.UTF8.GetBytes("Título: Coração")),(1,Encoding.UTF8.GetBytes("Artista: João"))));
        check(utf8.TextEvents.Count==2 && Suggest("song.mid",utf8.TextEvents.ToArray()).Title=="Coração","UTF-8 metadata retained and parsed");
        var legacy=MidiReader.Read(Midi((1,Encoding.Latin1.GetBytes("Título: Coração")),(1,new byte[]{65,114,116,105,115,116,97,58,32,0x93,74,111,0xe3,111,0x94})));
        check(Suggest("song.mid",legacy.TextEvents.ToArray()).Title=="Coração" && legacy.TextEvents[1].Text=="Artista: “João”","Latin-1 and Windows-1252 decoded without replacement characters");
        check(MidiReader.Read(Midi((1,Encoding.Latin1.GetBytes("Title: Café")))).TextEvents.Single().Text=="Title: Café","Legacy accented final byte is decoded instead of silently omitted");
        var limit=MidiReader.Read(Midi(Enumerable.Range(0,300).Select(index=>(1,Encoding.ASCII.GetBytes(new string('A',2100)+index))).ToArray()));
        check(limit.TextEvents.Count==256 && limit.TextEvents.All(item=>item.Text.Length==2048),"MIDI text collection and lengths are bounded");
        var unicodeLimit=MidiReader.Read(Midi((1,Encoding.UTF8.GetBytes(new string('á',2200)))));
        check(unicodeLimit.TextEvents.Single().Text==new string('á',2048),"Length cap counts Unicode characters rather than UTF-8 bytes");
        var decodedBoundary=MidiReader.Read(Midi((1,Encoding.UTF8.GetBytes(boundaryText))));
        check(decodedBoundary.TextEvents.Single().Text.Length==2047 && Suggest("Canção - Grupo Exemplo.mid",decodedBoundary.TextEvents.ToArray()).Title==boundarySuggestion.Title,
            "Valid UTF-8 decoding already preserves surrogate pairs and agrees with the raw metadata guard");
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
