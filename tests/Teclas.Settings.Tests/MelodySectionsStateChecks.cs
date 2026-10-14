using System.Text.Json;
using Teclas.App;

internal static class MelodySectionsStateChecks
{
    internal static void Run(string folder, Action<bool,string> check)
    {
        string path = Path.Combine(folder, "melody-sections.json");
        static SavedMelodySection Section(int first, int last, int track, int channel = 1, int program = 0) =>
            new() { FirstMeasure = first, LastMeasure = last, Source = new() { Track = track, Channel = channel, Program = program } };
        var sections = new List<SavedMelodySection> { Section(1, 8, 3), Section(9, 32, 2, 2, 73) };
        var current = new SavedMidi
        {
            FullPath = Path.Combine(folder, "disconnected.mid"), Sha256 = "same-midi",
            MelodySections = sections, UseReviewedProfile = true,
            Versions = [new() { ScorePath = "before.mscz", MelodySections = [Section(1, 16, 2, 2, 73)] },
                        new() { ScorePath = "after.mscz", MelodySections = Settings.CloneMelodySections(sections) }]
        };
        new Settings { Library = [current], FollowScorePlayback = false }.Save(path);
        var loaded = Settings.Load(path);
        var row = loaded.Library.Single();
        check(JsonSerializer.Serialize(row.MelodySections) == JsonSerializer.Serialize(sections),
            "Source ranges retain exact inclusive measures and track/channel/program identity");
        check(row.UseReviewedProfile && row.MelodySections.Count == 2,
            "Reviewed mode preserves dormant manual ranges without discarding user choices");
        check(!File.Exists(row.FullPath) && row.MelodySections.Count == 2,
            "Disconnected original preserves explicit melody ranges");
        check(row.Versions[0].MelodySections.Count == 1 && row.Versions[1].MelodySections[1].FirstMeasure == 9,
            "Each score version retains its own measure ranges");
        row.MelodySections[0].Source.Track = 99; row.MelodySections[1].LastMeasure = 80;
        check(row.Versions[1].MelodySections[0].Source.Track == 3 && row.Versions[1].MelodySections[1].LastMeasure == 32,
            "Current range changes do not mutate historical ranges or source keys");
        var cloned = Settings.CloneMelodySections(sections);
        sections[0].Source.Program = 74; sections[0].FirstMeasure = 9;
        check(cloned[0].Source.Program == 0 && cloned[0].FirstMeasure == 1,
            "Snapshot clones both range and nested source values");
        check(!loaded.FollowScorePlayback, "Score page-follow preference persists disabled");
        foreach (string property in new[] { "", ",\"MelodySections\":null" })
        {
            File.WriteAllText(path, "{\"Library\":[{\"FullPath\":\"legacy.mid\"" + property +
                ",\"Versions\":[{\"ScorePath\":\"legacy.mscz\"" + property + "}]}]}");
            var old = Settings.Load(path);
            check(old.Library[0].MelodySections.Count == 0 && old.Library[0].Versions[0].MelodySections.Count == 0,
                "Legacy or null range lists retain the original whole-score behavior");
            check(old.FollowScorePlayback, "Legacy score page-follow defaults enabled");
        }
        // Preserve recoverable user evidence: never silently shift or erase invalid ranges.
        var invalid = new[] { Section(0, 3, 1), Section(2, 7, 2), Section(900, 800, 3),
            new SavedMelodySection { FirstMeasure = 10, LastMeasure = 12, Source = null! } };
        File.WriteAllText(path, JsonSerializer.Serialize(new { Library = new[] { new {
            FullPath = "invalid.mid", MelodySections = invalid,
            Versions = new[] { new { ScorePath = "invalid.mscz", MelodySections = invalid } }
        } } }));
        var preserved = Settings.Load(path).Library.Single();
        check(preserved.MelodySections.Count == 4 && preserved.MelodySections[0].FirstMeasure == 0 &&
              preserved.MelodySections[2].FirstMeasure == 900,
            "Out-of-range and overlapping values remain visible for explicit correction");
        check(preserved.MelodySections[3].Source.Track == -1 && preserved.Versions[0].MelodySections[3].Source.Channel == 0,
            "Null source becomes a visible invalid source instead of silently choosing an instrument");
        check(preserved.Versions[0].MelodySections[1].FirstMeasure == 2,
            "Historical ranges are never renumbered during migration");
    }
}
