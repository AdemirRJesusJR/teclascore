using Teclas.App;

internal static class ArrangementStateChecks
{
    internal static void Run(string folder, Action<bool,string> check)
    {
        string path=Path.Combine(folder,"arrangements.json");
        var original=new SavedScoreVersion {ScorePath="Z:/original.mscz",Title="Melodia"};
        var beginner=new SavedScoreVersion {ScorePath="Z:/beginner.mscz",Title="Melodia",HasMusicalEdits=true,
            ArrangementSummary="Iniciante · pauta 1 · voz 1"};
        var intermediate=new SavedScoreVersion {ScorePath="Z:/intermediate.mscz",Title="Melodia",HasMusicalEdits=true,
            ArrangementSummary="Intermediário · pauta 2 · voz 1"};
        new Settings {Library=[new SavedMidi {FullPath="Z:/missing.mid",ScorePath=beginner.ScorePath,HasMusicalEdits=true,
            ArrangementSummary=beginner.ArrangementSummary,Versions=[original,beginner,intermediate]}]}.Save(path);
        var row=Settings.Load(path).Library.Single();
        check(row.ArrangementSummary==beginner.ArrangementSummary && row.HasMusicalEdits,
            "Active arrangement survives restart without its original MIDI");
        check(row.Versions[0].ArrangementSummary=="" && row.Versions[1].ArrangementSummary==beginner.ArrangementSummary &&
            row.Versions[2].ArrangementSummary==intermediate.ArrangementSummary,
            "Original and differently targeted arrangement versions remain distinct");
        check(row.Versions[1].Label.Contains("Iniciante · pauta 1 · voz 1") && !row.Versions[0].Label.Contains("Iniciante"),
            "History labels identify arrangement level and target");
        row.ArrangementSummary="";row.HasMusicalEdits=false;row.ScorePath=original.ScorePath;
        new Settings {Library=[row]}.Save(path);
        var restored=Settings.Load(path).Library.Single();
        check(restored.ArrangementSummary=="" && restored.Versions[1].ArrangementSummary==beginner.ArrangementSummary,
            "Restoring the original keeps the previous arrangements in history");
        foreach(string value in new[]{"",",\"ArrangementSummary\":null"})
        {
            File.WriteAllText(path,"{\"Library\":[{\"FullPath\":\"legacy.mscz\""+value+
                ",\"Versions\":[{\"ScorePath\":\"legacy.mscz\""+value+"}]}]}");
            var legacy=Settings.Load(path).Library.Single();
            check(legacy.ArrangementSummary=="" && legacy.Versions[0].ArrangementSummary=="",
                "Legacy or null arrangement metadata does not change existing library state");
        }
    }
}
