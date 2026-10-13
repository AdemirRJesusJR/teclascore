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
