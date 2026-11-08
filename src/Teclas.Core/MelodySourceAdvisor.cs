using System.Globalization;
using System.Text;

namespace Teclas.Core;

/// <summary>A listening priority, not a probability that a source is the melody.</summary>
public record MelodySourceSuggestion(SourceKey Key,int Rank,double Score,string Label,string Reason,long SampleStartTick,long SampleEndTick)
{
    /// <summary>Share of the MIDI's lyric syllables that fall on this source's attacks; null when the MIDI has no lyrics.</summary>
    public double? LyricsShare { get; init; }
    /// <summary>The source carries the lyrics (90% of the syllables or more, clearly ahead of the other lines): the default melody.</summary>
    public bool FollowsLyrics { get; init; }
    /// <summary>This source doubles another one in unison or octave; that one (the voice, or the one on the lyrics) is preferred.</summary>
    public SourceKey? DoubleOf { get; init; }
}

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
        // In a karaoke MIDI the words say where the sung line is: their coverage is the strongest evidence.
        var lyrics=LyricsTrack.Coverage(document).ToDictionary(c=>c.Source);
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
                if(lyrics.Count>0)score+=24*(lyrics.GetValueOrDefault(f.Source.Key)?.Share??0)-12;
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
                if(label=="Boa candidata" && pitched.Count(other=>other!=f && SimilarLine(f,other,document.Ppq))>0)
                    reason="Há outra fonte com linha semelhante; compare o timbre e o trecho antes de escolher a melodia.";
            }
            candidates.Add(new(f.Source.Key,0,Math.Round(score,1),label,reason,sample.Start,sample.End)
                {LyricsShare=lyrics.Count>0 && f.Source.Key.Channel!=10?Math.Round(lyrics.GetValueOrDefault(f.Source.Key)?.Share??0,4):null});
        }
        ResolveDoubles(candidates,features,lyrics,document.Ppq);
        MarkLyricsMelody(candidates,features,lyrics,document.Ppq);
        return candidates.OrderByDescending(c=>c.FollowsLyrics).ThenByDescending(c=>c.Score).ThenBy(c=>c.Key.Track).ThenBy(c=>c.Key.Channel).ThenBy(c=>c.Key.Program)
            .Select((c,index)=>c with{Rank=index+1}).ToArray();
    }

    private static readonly HashSet<string> MelodicLabels=new(StringComparer.Ordinal){"Boa candidata","Conferir"};

    /// <summary>General MIDI voices (choir, voice oohs, synth voice, lead voice) or a track named after the singer's part.</summary>
    internal static bool VoiceTimbre(MidiSource source)
    {
        string name=Normalize(source.Name);
        return source.Key.Program is 52 or 53 or 54 or 85 || new[]{"voz","vocal","voice","canto","cantor","melodia","melody"}.Any(name.Contains);
    }

    private static string Describe(SourceKey key)=>$"pista {key.Track+1}, canal {key.Channel}";

    /// <summary>
    /// Two candidates that play the same line in unison or octave: the one that follows the lyrics, else the one with a
    /// voice timbre, is kept as the suggestion and the other is marked as its double. Without such a reason both stay as
    /// equal candidates to compare by ear.
    /// </summary>
    private static void ResolveDoubles(List<MelodySourceSuggestion> candidates,Features[] features,Dictionary<SourceKey,LyricsCoverage> lyrics,int ppq)
    {
        var byKey=features.ToDictionary(f=>f.Source.Key);
        for(int i=0;i<candidates.Count;i++)
        for(int j=i+1;j<candidates.Count;j++)
        {
            var a=candidates[i];var b=candidates[j];
            if(a.Label!="Boa candidata" || b.Label!="Boa candidata" || a.DoubleOf is not null || b.DoubleOf is not null)continue;
            if(!SimilarLine(byKey[a.Key],byKey[b.Key],ppq))continue;
            double shareA=lyrics.GetValueOrDefault(a.Key)?.Share??0,shareB=lyrics.GetValueOrDefault(b.Key)?.Share??0;
            bool voiceA=VoiceTimbre(byKey[a.Key].Source),voiceB=VoiceTimbre(byKey[b.Key].Source);
            int preferred=Math.Abs(shareA-shareB)>=.05?(shareA>shareB?0:1):voiceA!=voiceB?(voiceA?0:1):-1;
            if(preferred<0)continue;
            var (keep,drop)=preferred==0?(i,j):(j,i);
            var kept=candidates[keep];var dropped=candidates[drop];
            string why=Math.Abs(shareA-shareB)>=.05?"acerta mais sílabas da letra":"tem timbre de voz";
            candidates[keep]=kept with{Reason=$"Dobra em uníssono ou oitava com a {Describe(dropped.Key)}. Mantenha esta, que {why}."};
            candidates[drop]=dropped with{DoubleOf=kept.Key,Score=Math.Max(1,Math.Round(Math.Min(dropped.Score,kept.Score)-6,1)),
                Reason=$"Dobra a mesma linha da {Describe(kept.Key)} (uníssono ou oitava). Prefira aquela, que {why}."};
        }
    }

    /// <summary>
    /// The source with 90% or more of the syllables that is clearly ahead of every other melodic line becomes the
    /// default melody. A fixed-pitch guide note is never the melody: when only a guide follows the words, the voice is
    /// probably missing from the MIDI and the other candidates say so.
    /// </summary>
    private static void MarkLyricsMelody(List<MelodySourceSuggestion> candidates,Features[] features,Dictionary<SourceKey,LyricsCoverage> lyrics,int ppq)
    {
        if(lyrics.Count==0)return;
        var byKey=features.ToDictionary(f=>f.Source.Key);
        LyricsCoverage Fit(SourceKey key)=>lyrics.GetValueOrDefault(key)??new(key,0,lyrics.Values.First().Syllables);
        var melodic=candidates.Where(c=>c.Key.Channel!=10 && MelodicLabels.Contains(c.Label)).ToArray();
        // The sung line has about one attack per syllable. A riff on every eighth also covers the words, but most of
        // its attacks fall between them (in the user's karaoke files the sung lines stay above 50%, riffs below 40%).
        var best=melodic.Where(c=>c.DoubleOf is null && Fit(c.Key).Share>=.9 && Fit(c.Key).Precision>=.5)
            .OrderByDescending(c=>Math.Round(Fit(c.Key).Share,2)).ThenByDescending(c=>Fit(c.Key).Precision).ThenBy(c=>Fit(c.Key).Offset).ThenByDescending(c=>c.Score).FirstOrDefault();
        if(best is not null)
        {
            var fit=Fit(best.Key);
            // The same line doubled in unison or octave is not a rival: either one carries the same melody.
            bool Doubles(MelodySourceSuggestion c)=>c.DoubleOf==best.Key || SimilarLine(byKey[c.Key],byKey[best.Key],ppq);
            // Another melodic line just as close to the words (and as sparse between them) leaves the choice to the user.
            bool rival=melodic.Any(c=>c!=best && !Doubles(c) && Fit(c.Key).Share>=fit.Share-.1 && Fit(c.Key).Precision>=fit.Precision-.15);
            if(!rival)
            {
                int index=candidates.IndexOf(best);
                string doubled=best.Reason.StartsWith("Dobra",StringComparison.Ordinal)?" "+best.Reason:"";
                candidates[index]=best with{FollowsLyrics=true,Label="Boa candidata",Score=Math.Max(best.Score,90),
                    Reason=$"Acompanha a letra: {fit.Hits} de {fit.Syllables} sílabas caem nas notas desta fonte. É a melodia sugerida; solos e introdução podem entrar por trecho."+doubled};
                for(int other=0;other<candidates.Count;other++)
                    if(candidates[other].Key!=best.Key && candidates[other].DoubleOf is null && melodic.Contains(candidates[other]) && Doubles(candidates[other]))
                        candidates[other]=candidates[other] with{DoubleOf=best.Key,Score=Math.Max(1,Math.Round(Math.Min(candidates[other].Score,89)-6,1)),
                            Reason=$"Dobra a mesma linha da {Describe(best.Key)} (uníssono ou oitava), que fica sugerida por começar junto com as sílabas da letra."};
                return;
            }
        }
        var guide=candidates.Where(c=>c.Label=="Guia possível" && Fit(c.Key).Share>=.9).OrderByDescending(c=>Fit(c.Key).Share).FirstOrDefault();
        if(guide is null)return;
        for(int index=0;index<candidates.Count;index++)
            if(MelodicLabels.Contains(candidates[index].Label))
                candidates[index]=candidates[index] with{Reason=candidates[index].Reason+$" A letra acompanha só uma nota-guia ({Describe(guide.Key)}, altura quase fixa): a voz cantada pode não estar neste MIDI."};
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

    /// <summary>
    /// The same line played by two sources: at least 90% of the attacks of the shorter one meet an attack of the other
    /// within ±30 ticks (at 480 per quarter, scaled to the file's resolution), at the same pitch or an octave apart.
    /// A small delay or an octave doubling is still the same line.
    /// </summary>
    private static bool SimilarLine(Features left,Features right,int ppq)
    {
        if(left.Attacks.Length<12 || right.Attacks.Length<12 || left.Overlap>.3 || right.Overlap>.3)return false;
        var (shorter,longer)=left.Attacks.Length<=right.Attacks.Length?(left.Attacks,right.Attacks):(right.Attacks,left.Attacks);
        if(shorter.Length<longer.Length*.85)return false;
        long tolerance=Math.Max(1,(long)Math.Round(30d*ppq/480));
        var ticks=longer.Select(a=>a.Tick).ToArray();
        int count=0;
        foreach(var attack in shorter)
        {
            int index=Array.BinarySearch(ticks,attack.Tick-tolerance);if(index<0)index=~index;
            for(;index<ticks.Length && ticks[index]<=attack.Tick+tolerance;index++)
                if(Math.Abs(longer[index].Pitch-attack.Pitch) is 0 or 12){count++;break;}
        }
        return count>=shorter.Length*.9;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted=values.Order().ToArray();
        return sorted.Length%2==0?(sorted[sorted.Length/2-1]+sorted[sorted.Length/2])/2:sorted[sorted.Length/2];
    }

    private static string Normalize(string value)=>new string(value.Normalize(NormalizationForm.FormD)
        .Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
}
