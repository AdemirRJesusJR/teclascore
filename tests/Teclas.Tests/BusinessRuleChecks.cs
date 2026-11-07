using Teclas.Core;

/// <summary>Approved rules: harmonic doubt and a missing melody lower the rating; library-wide positions keep a song's own choices.</summary>
internal static class BusinessRuleChecks
{
    public static void Run(Action<bool,string> check)
    {
        ConfidenceInput Input(double[] chords,bool incomplete=false)=>new(chords,0,0,0,false,false,null,false,false,2,incomplete);
        check(ScoreConfidence.DoubtfulChord==.75,"Doubtful chord threshold matches the engine (batch_harmony.DOUBTFUL_CHORD)");
        var contested=ScoreConfidence.Rate(Input([.95,.7,.93,.9]));
        check(contested.Score<100 && contested.Reasons.Any(r=>r.Contains("acorde(s) com dúvida")),"A contested chord (70%) counts as doubtful");
        check(ScoreConfidence.Rate(Input([.95,0,.93])).Score==100,"A chord typed by the user (confidence 0) is not doubtful");
        var missing=ScoreConfidence.Rate(Input([.95,.93],incomplete:true));
        check(missing.Label=="Revisar" && missing.Reasons.Any(r=>r.Contains("falta parte da melodia")),"A MIDI diagnosed without its melody is never rated ready");
        check(ScoreConfidence.Rate(Input([.95,.93])).Label=="Pronta","A clean score stays ready");

        ChordPositionEntry E(string root,string quality="",int? inversion=null)=>new(root,quality,inversion);
        var shapeE=DiagramPositionPropagation.Shape("E","",0)!.Value;
        var changes=new[]{new ChordPositionChange(shapeE,0)};
        var other=new[]{E("E"),E("E","",2),E("E","",1),E("Fb"),E("E","m",2)};
        var plan=DiagramPositionPropagation.Plan(other,0,changes,new Dictionary<ChordShape,int>{[shapeE]=2},out int kept);
        check(plan.Keys.OrderBy(i=>i).SequenceEqual([0,1,3]) && plan.Values.All(v=>v==0),"Default and inherited positions follow the new library choice");
        check(kept==1 && !plan.ContainsKey(2),"A position picked on purpose in the other song is kept");
        check(!plan.ContainsKey(4),"Other qualities are untouched");
        DiagramPositionPropagation.Plan(other,0,changes,null,out kept);
        check(kept==2,"Without a remembered position every explicit choice is kept");
    }
}
