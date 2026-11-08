using Teclas.Core;

/// <summary>Step-by-step locks for a new MIDI: nothing is generated while a block or an unconfirmed doubt remains.</summary>
internal static class ScoreFlowChecks
{
    public static void Run(Action<bool,string> check)
    {
        ScoreFlowTrack T(string name,string hint,bool melody,int notes=100,bool percussion=false)=>new(name,hint,melody,percussion,notes);
        // The Barões case: a bass-like sax and a very high synth chosen together; the lyrics follow the accordion.
        var baroes=new ScoreFlowInput{
            Tracks=[T("Sax tenor","Baixo provável",true),T("Tom melódico","Conferir",true),T("Acordeom de tango","Boa candidata",false),T("Percussão","Percussão",false,8248,true)],
            MelodyRange=(42,113),KeyboardKeys=61,LyricsTrack="Acordeom de tango"};
        var checks=ScoreFlowRules.Evaluate(baroes);
        check(checks.Any(c=>c.Id=="melody-outside-piano" && c.Level==ScoreFlowLevel.Block),"A melody up to F8 is blocked: it is beyond the 88-key piano");
        check(checks.Any(c=>c.Id=="melody-hint:Sax tenor" && c.Pending),"A bass-like track marked as melody needs confirmation");
        check(checks.Any(c=>c.Id=="melody-lyrics" && c.Pending) && checks.Any(c=>c.Id=="melody-listen" && c.Pending),"Lyrics elsewhere and not listening need confirmation");
        check(ScoreFlowRules.Reachable(checks)==ScoreFlowStep.Melody && !ScoreFlowRules.CanGenerate(checks),"Nothing past the Melodia step is reachable");
        // "Confirmar pendências desta etapa": only the doubts of that step with a Confirmar button, never a block.
        var confirmable=ScoreFlowRules.Confirmable(checks,ScoreFlowStep.Melody);
        check(confirmable.Count>0 && confirmable.All(c=>c.Level==ScoreFlowLevel.Confirm && c.Pending && c.ConfirmLabel is not null && c.Step==ScoreFlowStep.Melody)
            && !confirmable.Any(c=>c.Id=="melody-outside-piano") && confirmable.Any(c=>c.Id=="melody-lyrics"),"Confirming a step takes only its confirmations, never its blocks");
        var afterConfirming=ScoreFlowRules.Evaluate(baroes with{Confirmed=confirmable.Select(c=>c.Id).ToHashSet()});
        check(afterConfirming.Any(c=>c.Id=="melody-outside-piano" && c.Pending) && ScoreFlowRules.Confirmable(afterConfirming,ScoreFlowStep.Melody).Count==0
            && ScoreFlowRules.Reachable(afterConfirming)==ScoreFlowStep.Melody,"After confirming, the block still holds the step");
        check(ScoreFlowRules.Confirmable(checks,ScoreFlowStep.Chords).Count==0,"Other steps are not confirmed");

        var confirmed=new HashSet<string>{"melody-hint:Sax tenor","melody-lyrics"};
        var fixedRange=baroes with{MelodyRange=(42,81),Confirmed=confirmed,HeardMarked=true};
        checks=ScoreFlowRules.Evaluate(fixedRange);
        check(checks.Where(c=>c.Step==ScoreFlowStep.Melody).All(c=>!c.Pending),"Confirmed doubts and a playable range clear the Melodia step");
        check(ScoreFlowRules.Reachable(checks)==ScoreFlowStep.Analysis && checks.Single(c=>c.Pending).Id=="analysis-missing","The analysis is the next lock");

        var wide=fixedRange with{MelodyRange=(36,100)};
        check(ScoreFlowRules.Evaluate(wide).Single(c=>c.Id=="melody-keyboard").Level==ScoreFlowLevel.Confirm,"Wider than a 61-key keyboard but within the piano is a confirmation, not a block");
        // Under half of the lyrics on the chosen melody while another part has 90%: a decision shown like a block.
        var missing=baroes with{ChosenLyricsShare=.01,LyricsTrackShare=.99};
        var decision=ScoreFlowRules.Evaluate(missing).Single(c=>c.Id=="melody-lyrics");
        check(decision.Critical && decision.Pending && decision.Action=="use-lyrics" && decision.ConfirmLabel=="Manter sem a letra" && decision.Message.Contains("Decida"),"A melody that leaves out most of the lyrics needs a decision");
        check(!ScoreFlowRules.Evaluate(missing with{Confirmed=new HashSet<string>{"melody-lyrics"}}).Single(c=>c.Id=="melody-lyrics").Pending,"Keeping it consciously clears the decision");
        check(!ScoreFlowRules.Evaluate(baroes with{ChosenLyricsShare=.6,LyricsTrackShare=.99}).Single(c=>c.Id=="melody-lyrics").Critical
            && !ScoreFlowRules.Evaluate(baroes with{ChosenLyricsShare=.1,LyricsTrackShare=.8}).Single(c=>c.Id=="melody-lyrics").Critical,"Half the lyrics, or no part with 90%, stays an ordinary confirmation");
        check(ScoreFlowRules.Evaluate(baroes with{Tracks=[T("Bateria","Percussão",true,500,true)],MelodyRange=null}).Any(c=>c.Id.StartsWith("melody-percussion") && c.Level==ScoreFlowLevel.Block),"Percussion can never be the melody");
        check(ScoreFlowRules.Evaluate(new ScoreFlowInput{Tracks=[T("Flauta","Boa candidata",false)]}).Any(c=>c.Id=="melody-none" && c.Level==ScoreFlowLevel.Block),"No marked melody is blocked");
        check(!ScoreFlowRules.Evaluate(new ScoreFlowInput{ChordsOnly=true,Tracks=[T("Flauta","Boa candidata",false)]}).Any(c=>c.Step==ScoreFlowStep.Melody),"Só cifras skips the melody checks");

        ScoreFlowChord C(double q,string name="C")=>new(q,name);
        var analysed=fixedRange with{HasAnalysis=true,ReviewPoints=3,BlockingReviewPoints=1,KeyName="Ré maior",DoubtfulChords=2,Chords=[C(0),C(4,"G"),C(4,"G7"),C(4.5,"Am"),C(8,"F")]};
        checks=ScoreFlowRules.Evaluate(analysed);
        check(checks.Any(c=>c.Id=="analysis-review" && c.Pending && c.Message.Contains("1 de ritmo")),"Review points must be checked before the chords step");
        check(checks.Any(c=>c.Id=="analysis-crowded" && c.Message.StartsWith("1 troca")),"A chord change under a beat after the previous one is flagged");
        check(checks.Any(c=>c.Id=="chords-duplicate" && c.Level==ScoreFlowLevel.Block),"Two chords on the same position are blocked");
        check(checks.Any(c=>c.Id=="chords-doubtful" && c.Pending) && checks.Any(c=>c.Id=="chords-key" && c.Pending),"Doubtful chords and the key need confirmation");
        check(ScoreFlowRules.Reachable(checks)==ScoreFlowStep.Analysis,"Unchecked review points keep the user on Ritmo e acordes");

        var all=new HashSet<string>(confirmed){"analysis-review","analysis-crowded","chords-doubtful","chords-key"};
        var ready=analysed with{Confirmed=all,Chords=[C(0),C(4,"G"),C(4.5,"Am"),C(8,"F")]};
        checks=ScoreFlowRules.Evaluate(ready);
        check(ScoreFlowRules.CanGenerate(checks) && ScoreFlowRules.Reachable(checks)==ScoreFlowStep.Generate,"With everything fixed and confirmed, generating is allowed");
    }
}
