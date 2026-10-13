using System.Globalization;
using System.Text;

namespace Teclas.Core;

/// <summary>A listening priority, not a probability that a source is the melody.</summary>
public record MelodySourceSuggestion(SourceKey Key,int Rank,double Score,string Label,string Reason,long SampleStartTick,long SampleEndTick);

/// <summary>Ranks sources without changing notes or selecting a melody for the user.</summary>
public static class MelodySourceAdvisor
{
    private sealed record Attack(long Tick,int Pitch,int Count);
    private sealed record Features(MidiSource Source,MidiNote[] Notes,Attack[] Attacks,double MedianPitch,
        double SpanQuarters,double SoundingQuarters,double Overlap,double Chords,double Density,
        int DistinctPitches,int Range,double Steps,double Repeated,double Regularity,double Periodicity,
        double OnQuarter,double MedianDuration);

    public static IReadOnlyList<MelodySourceSuggestion> Analyze(MidiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if(document.Ppq<=0)throw new ArgumentException("A resolução musical do MIDI precisa ser positiva.",nameof(document));
        var groups=document.Notes.Where(n=>n.Start>=0 && n.End>n.Start && n.Velocity>0 && n.Pitch is >=0 and <=127)
            .GroupBy(n=>new SourceKey(n.Track,n.Channel,n.Program)).ToDictionary(g=>g.Key,g=>g.OrderBy(n=>n.Start).ThenBy(n=>n.Pitch).ToArray());
        var features=document.Sources.Select(source=>Describe(source,groups.GetValueOrDefault(source.Key,[]),document.Ppq)).ToArray();
        var pitched=features.Where(f=>f.Source.Key.Channel!=10 && f.Notes.Length>=8).ToArray();
        double registerCenter=pitched.Length>0?Median(pitched.Select(f=>f.MedianPitch)):60;
        var allNotes=features.SelectMany(f=>f.Notes).ToArray();
        double songQuarters=allNotes.Length>0?Math.Max(1,((double)allNotes.Max(n=>n.End)-allNotes.Min(n=>n.Start))/document.Ppq):1;
        var candidates=new List<MelodySourceSuggestion>();
        foreach(var f in features)
        {
            var sample=ChooseSample(f,document.Ppq);
            double score=0;
            string label,reason;
            if(f.Source.Key.Channel==10)
            {
                label="Percussão";
                reason="Canal de percussão; não é uma sugestão de melodia com altura definida.";
            }
            else if(f.Notes.Length==0)
            {
                label="Sem notas válidas";
                reason="Não há notas com duração válida para avaliar ou ouvir nesta fonte.";
            }
            else
            {
                bool few=f.Attacks.Length<12 || f.SpanQuarters<4;
                bool fixedPitch=f.DistinctPitches<3;
                bool bass=f.MedianPitch<55 && f.MedianPitch<=registerCenter-8 && f.Overlap<.3 && f.Chords<.3
                    && (f.OnQuarter>.55 || f.Periodicity>.7 || f.Source.Key.Program is >=32 and <=39);
                bool arpeggio=f.Periodicity>.85 && f.Regularity>.78 && f.Steps<.55;
                bool pad=f.MedianDuration>2 && f.Density<.55;
                bool polyphonic=f.Chords>.38 || f.Overlap>.45;
                double contour=f.Steps*8;
                double variation=Math.Min(f.DistinctPitches/8d,1)*8;
                double coverage=Math.Sqrt(Math.Clamp(f.SoundingQuarters/songQuarters,0,1))*5;
                score=31+(1-f.Overlap)*17+(1-f.Chords)*10+variation+contour+coverage;
                if(f.Range is >=5 and <=24)score+=4;
                if(f.Range>36)score-=8;
                if(f.Regularity<.8 && f.Attacks.Length>=12)score+=4;
                if(f.Density>6)score-=15;
                else if(f.Density>3.5)score-=5;
                if(f.Density<.3)score-=8;
                if(f.Repeated>.65)score-=12;
                if(bass)score-=26;
                if(arpeggio)score-=25;
                if(pad)score-=17;
                if(polyphonic)score-=10;
                // Names and programs provide weak corroboration only. A bass instrument
                // playing a high, varied solo must not become a bass solely by its patch.
                string name=Normalize(f.Source.Name);
                if(new[]{"melodia","melody","vocal","solo","lead"}.Any(name.Contains))score+=2;
                if(new[]{"acompanh","chord","arpeggio","arpejo"}.Any(name.Contains))score-=2;
                score=Math.Clamp(score,1,92);
                if(fixedPitch)score=Math.Min(score,f.DistinctPitches==1?18:36);
                if(few)score=Math.Min(score,f.Attacks.Length<8 || f.SpanQuarters<2?24:44);
                if(few)
                {
                    label="Poucos dados";
                    reason="Poucos ataques ou trecho curto; ainda há pouca evidência para priorizar esta fonte.";
                }
                else if(fixedPitch)
                {
                    label="Guia possível";
                    reason="A altura varia muito pouco; pode ser uma nota-guia ou um padrão repetido. Confira ouvindo.";
                }
                else if(bass)
                {
                    label="Baixo provável";
                    reason="Linha grave em relação às outras fontes, com ataques ou padrões compatíveis com baixo.";
                }
                else if(polyphonic || pad || arpeggio)
                {
                    label="Acompanhamento provável";
                    reason=polyphonic
                        ?f.Chords>.38?"Muitos ataques formam acordes, inclusive notas iniciadas com pequenos atrasos entre si.":"Há sobreposição prolongada de notas; a fonte pode conter várias vozes ou acompanhamento."
                        :arpeggio?"Padrão regular e repetido com saltos de altura; pode ser um arpejo de acompanhamento."
                        :"Notas longas e ataques esparsos sugerem uma camada de sustentação. Confira ouvindo.";
                }
                else
                {
                    label=score>=58?"Boa candidata":"Conferir";
                    reason=f.Overlap<.2 && f.Chords<.2
                        ?"Predomina uma nota por vez, com variação de alturas. É uma prioridade de escuta, não uma seleção automática."
                        :"Há variação de alturas e alguns trechos sobrepostos. Ouça para distinguir melodia e acompanhamento.";
                }
                if(label=="Boa candidata" && pitched.Count(other=>other!=f && SimilarLine(f,other))>0)
                    reason="Há outra fonte com linha semelhante; compare o timbre e o trecho antes de escolher a melodia.";
            }
            candidates.Add(new(f.Source.Key,0,Math.Round(score,1),label,reason,sample.Start,sample.End));
        }
        return candidates.OrderByDescending(c=>c.Score).ThenBy(c=>c.Key.Track).ThenBy(c=>c.Key.Channel).ThenBy(c=>c.Key.Program)
            .Select((c,index)=>c with{Rank=index+1}).ToArray();
    }

    private static Features Describe(MidiSource source,MidiNote[] notes,int ppq)
    {
        if(notes.Length==0)return new(source,notes,[],60,0,0,0,0,0,0,0,0,0,0,0,0,0);
        var attacks=new List<Attack>();
        // Anchor to the first onset: staggered sequences must not form a single group
        // through a chain of individually small gaps.
        double onsetTolerance=Math.Max(1,ppq*.045);
        for(int index=0;index<notes.Length;)
        {
            long tick=notes[index].Start;
            int end=index+1,pitch=notes[index].Pitch;
            while(end<notes.Length && notes[end].Start-tick<=onsetTolerance){pitch=Math.Max(pitch,notes[end].Pitch);end++;}
            attacks.Add(new(tick,pitch,end-index));
            index=end;
        }
        var sounding=SoundingIntervals(notes,ppq);
        double span=((double)notes.Max(n=>n.End)-notes[0].Start)/ppq;
        var intervals=attacks.Zip(attacks.Skip(1),(a,b)=>b.Pitch-a.Pitch).ToArray();
        var gaps=attacks.Zip(attacks.Skip(1),(a,b)=>(b.Tick-a.Tick)/(double)ppq).ToArray();
        var varying=intervals.Where(i=>i!=0).ToArray();
        // Resolution of 1/24 quarter tolerates humanized attacks while separating
        // common binary/triplet durations. This does not quantize the MIDI itself.
        var gapGroups=gaps.GroupBy(g=>Math.Round(g*24)).ToArray();
        double regularity=gaps.Length>0?gapGroups.Max(g=>g.Count())/(double)gaps.Length:0;
        double periodicity=0;
        for(int lag=2;lag<=16 && lag*3<=attacks.Count;lag++)
        {
            int matches=0,total=attacks.Count-lag;
            for(int index=lag;index<attacks.Count;index++)
                if(attacks[index].Pitch==attacks[index-lag].Pitch)matches++;
            periodicity=Math.Max(periodicity,matches/(double)total);
        }
        return new(source,notes,attacks.ToArray(),Median(notes.Select(n=>(double)n.Pitch)),span,sounding.Occupied/ppq,
            sounding.Occupied>0?sounding.Multiple/sounding.Occupied:0,
            attacks.Count(a=>a.Count>1)/(double)attacks.Count,attacks.Count/Math.Max(span,.25),
            notes.Select(n=>n.Pitch).Distinct().Count(),notes.Max(n=>n.Pitch)-notes.Min(n=>n.Pitch),
            varying.Length>0?varying.Count(i=>Math.Abs(i)<=2)/(double)varying.Length:0,
            intervals.Length>0?intervals.Count(i=>i==0)/(double)intervals.Length:0,regularity,periodicity,
            attacks.Count(a=>Math.Abs(a.Tick/(double)ppq-Math.Round(a.Tick/(double)ppq))<.07)/(double)attacks.Count,
            Median(notes.Select(n=>(n.End-n.Start)/(double)ppq)));
    }

    private static (double Occupied,double Multiple) SoundingIntervals(MidiNote[] notes,int ppq)
    {
        var events=new List<(double Tick,int Delta)>(notes.Length*2);
        foreach(var note in notes)
        {
            // Short legato release overlaps are not the same evidence as sustained
            // polyphony. Limit the ignored tail both musically and proportionally.
            double tail=Math.Min(ppq*.12,(note.End-note.Start)*.15);
            events.Add((note.Start,1));
            events.Add((note.End-tail,-1));
        }
        events.Sort((a,b)=>a.Tick.CompareTo(b.Tick));
        int active=0;
        double occupied=0,multiple=0,previous=events[0].Tick;
        foreach(var change in events)
        {
            double elapsed=change.Tick-previous;
            if(active>0)occupied+=elapsed;
            if(active>1)multiple+=elapsed;
            active+=change.Delta;
            previous=change.Tick;
        }
        return(occupied,multiple);
    }

    private static (long Start,long End) ChooseSample(Features f,int ppq)
    {
        if(f.Notes.Length==0)return(0,0);
        long last=f.Notes.Max(n=>n.End),length=(long)ppq*12,minimumLength=(long)ppq*8;
        if(last-f.Attacks[0].Tick<=length)return(f.Attacks[0].Tick,last);
        long bestStart=f.Attacks[0].Tick,bestEnd=bestStart+length;
        double bestValue=double.NegativeInfinity;
        // Bound the search for long files. All starts are original source attacks;
        // twelve quarters is a listening window, not an assumption of 4/4 bars.
        int stride=Math.Max(1,(int)Math.Ceiling(f.Attacks.Length/64d));
        for(int index=0;index<f.Attacks.Length;index+=stride)
        {
            long start=f.Attacks[index].Tick;
            if(last-start<minimumLength)break;
            long end=last-start>length?start+length:last;
            var pitches=new HashSet<int>();
            int count=0,chords=0;
            for(int cursor=index;cursor<f.Attacks.Length && f.Attacks[cursor].Tick<end;cursor++)
            {
                var attack=f.Attacks[cursor];
                pitches.Add(attack.Pitch);count++;
                if(attack.Count>1)chords++;
            }
            double value=Math.Min(pitches.Count,9)*.6+Math.Min(count,24)*.1
                -chords/(double)Math.Max(count,1)*2+(end-start)/(double)length;
            if(value>bestValue){bestValue=value;bestStart=start;bestEnd=end;}
        }
        return(bestStart,bestEnd);
    }

    private static bool SimilarLine(Features left,Features right)
    {
        if(left.Attacks.Length<12 || right.Attacks.Length!=left.Attacks.Length || left.Overlap>.3 || right.Overlap>.3)return false;
        int count=0;
        for(int index=0;index<left.Attacks.Length;index++)
            if(left.Attacks[index].Tick==right.Attacks[index].Tick && left.Attacks[index].Pitch==right.Attacks[index].Pitch)count++;
        return count>=left.Attacks.Length*.9;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted=values.Order().ToArray();
        return sorted.Length%2==0?(sorted[sorted.Length/2-1]+sorted[sorted.Length/2])/2:sorted[sorted.Length/2];
    }

    private static string Normalize(string value)=>new string(value.Normalize(NormalizationForm.FormD)
        .Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
}
