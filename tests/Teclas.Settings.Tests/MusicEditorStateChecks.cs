using Teclas.App;

internal static class MusicEditorStateChecks
{
    internal static void Run(string folder, Action<bool,string> check)
    {
        string path = Path.Combine(folder, "musical-edits.json");
        var original = new SavedScoreVersion { ScorePath = Path.Combine(folder,"before.mscz"), HasMusicalEdits = false };
        var edited = new SavedScoreVersion { ScorePath = Path.Combine(folder,"edited.mscz"), HasMusicalEdits = true };
        new Settings { Library = [new SavedMidi {
            FullPath=Path.Combine(folder,"unavailable-original.mid"), ScorePath=edited.ScorePath, HasMusicalEdits=true,
            Versions=[original,edited] }] }.Save(path);
        var restored = Settings.Load(path).Library.Single();
        check(restored.HasMusicalEdits && restored.ScorePath == edited.ScorePath, "Edited score remains the active saved source even when original MIDI is unavailable");
        check(!restored.Versions[0].HasMusicalEdits && restored.Versions[1].HasMusicalEdits, "History distinguishes MIDI-generated and musically edited versions");
        restored.HasMusicalEdits=false; restored.ScorePath=original.ScorePath;
        new Settings { Library=[restored] }.Save(path);
        var previous=Settings.Load(path).Library.Single();
        check(!previous.HasMusicalEdits && previous.Versions[1].HasMusicalEdits, "Restoring the MIDI workflow does not erase the edited version's protection");
        File.WriteAllText(path,"""{"Library":[{"FullPath":"legacy.mid","ScorePath":"legacy.mscz","Versions":[{"ScorePath":"legacy.mscz"}]}]}""");
        var legacy=Settings.Load(path).Library.Single();
        check(!legacy.HasMusicalEdits && !legacy.Versions[0].HasMusicalEdits, "Older sessions remain compatible without changing their generation workflow");
    }
}
