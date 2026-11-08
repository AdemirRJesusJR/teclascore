using Teclas.Core;

internal static class MelodySectionAdvisorChecks
{
    public static void Run(Action<bool,string> check)
    {
        var voice=new SourceKey(1,1,0);var flute=new SourceKey(2,2,73);
        var measures=Enumerable.Range(0,12).Select(i=>(i*4.0,i*4.0+4)).ToList();
        IEnumerable<SectionNote> Bars(SourceKey source,int first,int last,int pitch)=>
            Enumerable.Range(first*4,(last-first+1)*4).Select(q=>new SectionNote(source,q,q+.9,pitch+q%3));
        // Voz nos compassos 1-4 e 9-12 com sílabas; flauta sozinha nos compassos 5-8.
        var notes=Bars(voice,0,3,67).Concat(Bars(flute,4,7,79)).Concat(Bars(voice,8,11,67)).ToList();
        var syllables=Enumerable.Range(0,16).Select(q=>(double)q).Concat(Enumerable.Range(32,16).Select(q=>(double)q)).ToList();
        var proposal=MelodySectionAdvisor.Propose(measures,notes,syllables,[voice,flute]);
        check(proposal.Select(s=>(s.FirstMeasure,s.LastMeasure,s.Source.Track)).SequenceEqual([(1,4,1),(5,8,2),(9,12,1)]),
            "Section advisor follows voice, solo and voice again");

        // A flauta toca junto em um compasso isolado com nota a mais: uma troca de um compasso não compensa o custo.
        var doubled=notes.Concat(Bars(flute,1,1,81)).Concat(Bars(flute,1,1,84)).ToList();
        var stable=MelodySectionAdvisor.Propose(measures,doubled,syllables,[voice,flute]);
        check(stable.Count==3 && stable[0].LastMeasure==4,"An isolated measure does not switch the part (Viterbi switch cost)");

        // Compassos em que ninguém toca mantêm a parte corrente, sem custo.
        var gap=notes.Where(n=>n.Start<36||n.Start>=40).ToList();
        var withGap=MelodySectionAdvisor.Propose(measures,gap,syllables,[voice,flute]);
        check(withGap.Select(s=>s.Source.Track).SequenceEqual([1,2,1]),"Silent measures keep the current part");

        // Uma só parte: nada a propor.
        check(MelodySectionAdvisor.Propose(measures,Bars(voice,0,11,67).ToList(),syllables,[voice,flute]).Count==0,
            "One-part songs get no proposal");
        check(MelodySectionAdvisor.Propose(measures,notes,syllables,[voice,flute]).SequenceEqual(proposal),"Section advice is deterministic");
    }
}
