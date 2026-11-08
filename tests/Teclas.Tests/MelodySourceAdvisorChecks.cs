using Teclas.Core;

internal static class MelodySourceAdvisorChecks
{
    public static void Run(Action<bool,string> check)
    {
        const int ppq=480;
        var melody=Line(0,1,0,64);
        var bass=Enumerable.Range(0,48).Select(i=>Note(1,2,33,new[]{36,36,43,41}[i%4],i*ppq,ppq*.8)).ToArray();
        var pad=Enumerable.Range(0,12).SelectMany(i=>new[]{60,64,67}.Select(pitch=>Note(2,3,89,pitch,i*8L*ppq,9*ppq))).ToArray();
        var arpeggio=Enumerable.Range(0,96).Select(i=>Note(3,4,24,new[]{60,64,67,64}[i%4],i*ppq/2,ppq*.42)).ToArray();
        var percussion=Enumerable.Range(0,96).Select(i=>Note(4,10,0,new[]{36,38,42}[i%3],i*ppq/2,ppq*.1)).ToArray();
        var guide=Enumerable.Range(0,48).Select(i=>Note(5,5,0,72,i*ppq,ppq*.8)).ToArray();
        var shortSolo=new[]{Note(6,6,80,76,14*ppq,ppq*.5)};
        var document=Document(melody,bass,pad,arpeggio,percussion,guide,shortSolo);
        var before=document.Notes.ToArray();
        var suggestions=MelodySourceAdvisor.Analyze(document);
        MelodySourceSuggestion Track(int track)=>suggestions.Single(s=>s.Key.Track==track);
        check(suggestions.Count==document.Sources.Count && suggestions.Select(s=>s.Key).Distinct().Count()==document.Sources.Count,"Melody advisor preserves all selectable sources");
        check(suggestions.First().Key.Track==0 && Track(0).Label=="Boa candidata","Varied single-line melody outranks bass, pads, arpeggio, percussion and guide");
        check(Track(1).Label=="Baixo provável" && Track(1).Score<Track(0).Score,"Low register and structure identify probable bass");
        check(Track(2).Label=="Acompanhamento provável" && Track(3).Label=="Acompanhamento provável","Sustained chords and repeated arpeggio are accompanied by explainable labels");
        check(Track(4).Label=="Percussão" && Track(4).Score==0,"Percussion is never promoted as pitched melody");
        check(Track(5).Label=="Guia possível" && Track(5).Score<Track(0).Score,"Fixed-pitch guide is not favored as a monophonic melody");
        check(Track(6).Label=="Poucos dados" && Track(6).Score<Track(0).Score,"One high solo note is insufficient evidence");
        check(suggestions.All(s=>s.Score>=0 && s.Score<100 && !string.IsNullOrWhiteSpace(s.Reason)),"Scores express bounded heuristic priority with human explanations, never certainty");
        check(suggestions.Select(s=>s.Rank).SequenceEqual(Enumerable.Range(1,suggestions.Count)),"Ranks are contiguous and begin at one");
        check(document.Notes.SequenceEqual(before),"Advising does not alter notes or source selection");
        check(suggestions.SequenceEqual(MelodySourceAdvisor.Analyze(document)),"Repeated advising is deterministic");

        // Exact tick equality is insufficient for humanized chord attacks.
        var staggeredChords=Enumerable.Range(0,24).SelectMany(i=>new[]{60,64,67}
            .Select((pitch,index)=>Note(7,7,0,pitch,i*2L*ppq+index*8,ppq*1.7))).ToArray();
        var staggered=MelodySourceAdvisor.Analyze(Document(melody,staggeredChords));
        var staggeredSuggestion=staggered.Single(s=>s.Key.Track==7);
        check(staggeredSuggestion.Label=="Acompanhamento provável" && staggeredSuggestion.Reason.Contains("atrasos"),"Near-simultaneous chord attacks are recognized despite different ticks");
        check(staggered.First().Key.Track==0,"Humanized chords do not outrank the melody");

        // These onsets never form a chord group; sustained overlap still reveals polyphony.
        var overlapping=Enumerable.Range(0,80).Select(i=>Note(8,8,48,60+i%7,i*ppq/2,ppq*3)).ToArray();
        var overlapSuggestion=MelodySourceAdvisor.Analyze(Document(melody,overlapping)).Single(s=>s.Key.Track==8);
        check(overlapSuggestion.Label=="Acompanhamento provável" && overlapSuggestion.Reason.Contains("sobreposição"),"Sustained polyphony is detected from durations as well as attacks");

        var legato=melody.Select((note,index)=>note with{End=note.End+50}).ToArray();
        var legatoSuggestion=MelodySourceAdvisor.Analyze(Document(legato)).Single();
        check(legatoSuggestion.Label=="Boa candidata","Small expressive legato overlaps do not become sustained polyphony");
        check(Math.Abs(legatoSuggestion.Score-MelodySourceAdvisor.Analyze(Document(melody)).Single().Score)<6,"Legato preserves comparable listening priority");

        var highBassPatch=melody.Select(n=>n with{Program=33}).ToArray();
        check(MelodySourceAdvisor.Analyze(Document(highBassPatch)).Single().Label=="Boa candidata","Bass program alone never forces a high melodic line into bass");
        var misleadingName=document with{Sources=document.Sources.Select(s=>s.Key.Track==1?s with{Name="Lead vocal solo melodia"}:s).ToArray()};
        var named=MelodySourceAdvisor.Analyze(misleadingName);
        check(named.First().Key.Track==0 && named.Single(s=>s.Key.Track==1).Label=="Baixo provável","Suggestive source name cannot override strong musical structure");

        var duplicate=melody.Select(n=>n with{Track=9,Channel=9,Program=4}).ToArray();
        var duplicates=MelodySourceAdvisor.Analyze(Document(melody,duplicate));
        check(duplicates.All(s=>s.Label=="Boa candidata" && s.Reason.Contains("semelhante") && s.Score<100),"Duplicated melody candidates invite listening comparison without false certainty");
        check(duplicates[0].Score==duplicates[1].Score && duplicates[0].Key.Track==0,"Equivalent musical evidence has equal score and a stable tie order");

        foreach(var suggestion in suggestions)
        {
            var notes=document.Notes.Where(n=>new SourceKey(n.Track,n.Channel,n.Program)==suggestion.Key).ToArray();
            check(notes.Any(n=>n.Start==suggestion.SampleStartTick) && suggestion.SampleEndTick>suggestion.SampleStartTick,"Audition excerpt starts at a real source attack and has a positive duration");
            check(suggestion.SampleStartTick>=notes.Min(n=>n.Start) && suggestion.SampleEndTick<=notes.Max(n=>n.End),"Audition excerpt stays within the source's original ticks");
            long span=notes.Max(n=>n.End)-notes.Min(n=>n.Start);
            if(span>=8*ppq)check(suggestion.SampleEndTick-suggestion.SampleStartTick is >=8*ppq and <=16*ppq,"Long sources supply eight to sixteen quarters without assuming a time signature");
        }
        var delayed=melody.Select(n=>n with{Start=n.Start+120L*ppq,End=n.End+120L*ppq}).ToArray();
        var delayedSample=MelodySourceAdvisor.Analyze(Document(delayed)).Single();
        check(delayedSample.SampleStartTick>=120L*ppq,"Audition skips long initial silence and retains absolute original ticks");
        var lateMelody=Enumerable.Range(0,20).Select(i=>Note(0,1,0,64,i*ppq,ppq*.8))
            .Concat(melody.Select(n=>n with{Start=n.Start+24L*ppq,End=n.End+24L*ppq})).ToArray();
        check(MelodySourceAdvisor.Analyze(Document(lateMelody)).Single().SampleStartTick>=15L*ppq,"Representative audition prefers an active varied passage over a fixed-pitch introduction");

        var scaled=Document(melody) with{Ppq=960,Notes=melody.Select(n=>n with{Start=n.Start*2,End=n.End*2}).ToArray()};
        var originalSuggestion=MelodySourceAdvisor.Analyze(Document(melody)).Single();
        var scaledSuggestion=MelodySourceAdvisor.Analyze(scaled).Single();
        check(originalSuggestion.Score==scaledSuggestion.Score && originalSuggestion.Label==scaledSuggestion.Label,"Musical ranking is invariant to PPQ resolution");
        check(scaledSuggestion.SampleStartTick==originalSuggestion.SampleStartTick*2 && scaledSuggestion.SampleEndTick==originalSuggestion.SampleEndTick*2,"Audition excerpt scales in original ticks with PPQ");
        var empty=new MidiDocument("memory.mid","",1,ppq,[],[],[]);
        check(MelodySourceAdvisor.Analyze(empty).Count==0,"Empty document has no invented sources");
        var emptySource=empty with{Sources=[new(new(0,1,0),"Empty",0,0,0,0,0,"")]};
        var emptySuggestion=MelodySourceAdvisor.Analyze(emptySource).Single();
        check(emptySuggestion.Label=="Sem notas válidas" && emptySuggestion.SampleStartTick==0 && emptySuggestion.SampleEndTick==0,"Empty source remains visible without a fabricated audition");
        var invalidNotes=Document([Note(0,1,0,60,0,0),new(0,1,0,61,0,0,480)]);
        check(MelodySourceAdvisor.Analyze(invalidNotes).Single().Label=="Sem notas válidas","Non-sounding or zero-duration notes cannot produce a recommendation");
        LyricsRanking(check);
    }

    /// <summary>Karaoke MIDIs written with SimpleMidiWriter and read back: the lyrics decide the default melody.</summary>
    private static void LyricsRanking(Action<bool,string> check)
    {
        // The sung line: varied, one attack per syllable. A busier flute line scores better without lyrics.
        int[] steps=[0,2,4,5,7,5,4,2];
        var voice=Enumerable.Range(0,64).Select(i=>new WrittenNote(i+(i/8)*1.0,.9,64+steps[i%8])).ToArray();
        var syllables=voice.Select((n,i)=>new WrittenLyric(n.Start+.02,(i%8==0?"/":"")+"la")).ToArray();
        var flute=Enumerable.Range(0,150).Select(i=>new WrittenNote(i*.5,.45,76+new[]{0,3,5,7,10,7,5,3,12,10}[i%10])).ToArray();
        var guideNotes=voice.Select(n=>new WrittenNote(n.Start,.9,64)).ToArray();
        MidiDocument Read(params WrittenTrack[] tracks)=>MidiReader.Read(SimpleMidiWriter.Write(100,4,tracks));
        var plain=Read(new("Flauta",0,73,flute),new("Voz",1,0,voice));
        var withoutLyrics=MelodySourceAdvisor.Analyze(plain);
        check(withoutLyrics.All(s=>s.LyricsShare is null && !s.FollowsLyrics),"Without lyrics nothing claims to follow them");
        var karaoke=Read(new("Flauta",0,73,flute),new WrittenTrack("Voz",1,0,voice){Lyrics=syllables},new("Guia",2,0,guideNotes));
        var ranked=MelodySourceAdvisor.Analyze(karaoke);
        var top=ranked[0];
        check(top.Key.Channel==2 && top.FollowsLyrics && top.LyricsShare>=.9 && top.Label=="Boa candidata" && top.Reason.Contains("letra"),
            "The source that carries 90% or more of the syllables is the suggested melody: "+string.Join(" | ",ranked.Select(s=>$"{s.Key.Channel} {s.Score} {s.LyricsShare}")));
        check(ranked.Single(s=>s.Key.Channel==3).Label=="Guia possível" && !ranked.Single(s=>s.Key.Channel==3).FollowsLyrics,"A fixed-pitch guide on the lyrics is never the melody");
        var proposal=MelodyBatchAdvisor.Propose(ranked);
        check(proposal.Confident && proposal.Source==top.Key,"Batch proposal, autopilot and queue take the lyrics melody with confidence");

        // Only a guide follows the words: the sung voice is missing, so no instrument is chosen confidently.
        var guideOnly=MelodySourceAdvisor.Analyze(Read(new("Sax",3,65,voice.Select(n=>n with{Start=n.Start+.5,Length=.4}).ToArray()),new WrittenTrack("Guia",2,0,guideNotes){Lyrics=syllables}));
        check(guideOnly.All(s=>!s.FollowsLyrics) && guideOnly[0].Reason.Contains("nota-guia"),"Lyrics on a guide note explain that the voice may be missing: "+string.Join(" | ",guideOnly.Select(s=>$"{s.Key.Channel} {s.Label} {s.LyricsShare} {s.Reason}")));
        check(!MelodyBatchAdvisor.Propose(guideOnly).Confident,"No confident melody when only a guide follows the lyrics");

        // Two lines equally close to the words (same coverage, same sparseness) leave the choice to the user.
        var other=voice.Select((n,i)=>n with{Pitch=60+steps[(i+3)%8]}).ToArray();
        var rivals=MelodySourceAdvisor.Analyze(Read(new WrittenTrack("Voz",1,0,voice){Lyrics=syllables},new("Sax",3,65,other)));
        check(rivals.All(s=>!s.FollowsLyrics),"Two different lines on the lyrics are a doubt, not a lyrics choice");

        // An octave doubling 20 ticks late is the same line; the one on the lyrics is kept and the other marked as its double.
        var doubled=voice.Select(n=>n with{Start=n.Start+20d/480,Pitch=n.Pitch+12}).ToArray();
        var doubles=MelodySourceAdvisor.Analyze(Read(new WrittenTrack("Teclado",1,0,voice){Lyrics=syllables},new("Sintetizador",4,80,doubled)));
        var kept=doubles.Single(s=>s.Key.Channel==2);var doubling=doubles.Single(s=>s.Key.Channel==5);
        check(kept.FollowsLyrics && doubling.DoubleOf==kept.Key && doubling.Reason.Contains("Dobra"),"Octave doubling with a small delay is recognized; the lyrics line is kept: "+string.Join(" | ",doubles.Select(s=>$"{s.Key.Channel} {s.Score} {s.LyricsShare} {s.Reason}")));
        check(MelodyBatchAdvisor.Propose(doubles).Confident,"A double of the melody is not a rival in the batch proposal");
        // Without lyrics the voice timbre decides between two doubled lines.
        var choir=voice.Select(n=>n with{Start=Math.Max(0,n.Start-25d/480)}).ToArray();
        var timbre=MelodySourceAdvisor.Analyze(Read(new("Teclado",1,0,voice),new("Coro",5,52,choir)));
        check(timbre[0].Key.Channel==6 && timbre.Single(s=>s.Key.Channel==2).DoubleOf==timbre[0].Key,"Without lyrics, a unison double keeps the voice timbre: "+string.Join(" | ",timbre.Select(s=>$"{s.Key.Channel} {s.Score} {s.Reason}")));
        // Identical lines with nothing to tell them apart stay as equal candidates (no false certainty).
        var same=MelodySourceAdvisor.Analyze(Read(new("Teclado",1,0,voice),new("Piano",6,1,voice)));
        check(same.All(s=>s.DoubleOf is null && s.Reason.Contains("semelhante")),"Undecided doubles keep the comparison hint");

        // Coverage of a melody chosen from two tracks counts their attacks together.
        var flutes=karaoke.Sources.Where(s=>s.Key.Channel==1).Select(s=>s.Key).ToArray();
        var half=LyricsTrack.CoverageOf(karaoke,flutes);
        var union=LyricsTrack.CoverageOf(karaoke,karaoke.Sources.Where(s=>s.Key.Channel is 1 or 2).Select(s=>s.Key));
        check(union is not null && half is not null && union.Share>=half.Share && union.Share>=.9,"Lyrics coverage of several chosen tracks is their union");
    }

    private static MidiNote Note(int track,int channel,int program,int pitch,long start,double duration)=>new(track,channel,program,pitch,80,start,start+(long)duration);

    private static MidiNote[] Line(int track,int channel,int program,int root)
    {
        int[] pitches=[0,2,4,2,5,4,2,0,7,5,4,2,4,7,9,7];
        double[] durations=[.5,.5,1,.5,1.5,.5,.5,1];
        var notes=new List<MidiNote>();long tick=0;
        for(int index=0;index<64;index++)
        {
            long duration=(long)(480*durations[index%durations.Length]);
            notes.Add(Note(track,channel,program,root+pitches[index%pitches.Length],tick,duration-20));
            tick+=duration;
            if(index%16==15)tick+=480;
        }
        return notes.ToArray();
    }

    private static MidiDocument Document(params MidiNote[][] parts)
    {
        var notes=parts.SelectMany(part=>part).ToArray();
        var sources=notes.GroupBy(n=>new SourceKey(n.Track,n.Channel,n.Program)).Select(group=>new MidiSource(group.Key,"Pista "+group.Key.Track,
            group.Count(),group.Min(n=>n.Pitch),group.Max(n=>n.Pitch),group.Select(n=>n.Pitch).Distinct().Count(),0,"")).ToArray();
        return new("memory.mid","",1,480,notes,sources,[]){EndTick=notes.Length>0?notes.Max(n=>n.End):0};
    }
}
