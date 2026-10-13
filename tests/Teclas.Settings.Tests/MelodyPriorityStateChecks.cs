using System.Text.Json;
using Teclas.App;

internal static class MelodyPriorityStateChecks
{
    internal static void Run(string folder, Action<bool,string> check)
    {
        string path = Path.Combine(folder, "melody-priority.json");
        static SavedSource Source(int track, int channel = 1, int program = 0) => new() { Track = track, Channel = channel, Program = program };
        static string Order(IEnumerable<SavedSource> sources) => string.Join("|", sources.Select(source => $"{source.Track}:{source.Channel}:{source.Program}"));
        SavedSource first = Source(6, 4), second = Source(2, 1, 48), third = Source(2, 1, 0);
        var library = new SavedMidi
        {
            FullPath = Path.Combine(folder, "unavailable-multiple.mid"),
            MelodySources = [first, second, third],
            MelodySourcePriority = [second, first, third],
            Versions =
            [
                new SavedScoreVersion { ScorePath = "before.mscz", MelodySources = [first, second, third], MelodySourcePriority = [first, second, third] },
                new SavedScoreVersion { ScorePath = "after.mscz", MelodySources = [second, first, third], MelodySourcePriority = [second, first, third] }
            ]
        };
        new Settings { Library = [library] }.Save(path);
        var restored = Settings.Load(path).Library.Single();
        check(Order(restored.MelodySourcePriority) == "2:1:48|6:4:0|2:1:0", "Manual multi-source priority retains exact sequence and program identity");
        check(Order(restored.MelodySources) == "6:4:0|2:1:48|2:1:0", "Priority does not alter saved track inclusion");
        check(!File.Exists(restored.FullPath) && restored.MelodySourcePriority.Count == 3, "Disconnected MIDI retains custom source priority");
        check(Order(restored.Versions[0].MelodySourcePriority) == "6:4:0|2:1:48|2:1:0" &&
              Order(restored.Versions[1].MelodySourcePriority) == "2:1:48|6:4:0|2:1:0", "Each score history version retains its own source order");
        restored.MelodySourcePriority.Reverse();
        new Settings { Library = [restored] }.Save(path);
        var twice = Settings.Load(path).Library.Single();
        check(Order(twice.MelodySourcePriority) == "2:1:0|6:4:0|2:1:48" &&
              Order(twice.Versions[0].MelodySourcePriority) == "6:4:0|2:1:48|2:1:0", "Changing current order does not mutate historical order");
        foreach (string property in new[] { "", ",\"MelodySourcePriority\":null" })
        {
            File.WriteAllText(path, "{\"Library\":[{\"FullPath\":\"legacy.mid\",\"MelodySources\":[{\"Track\":5,\"Channel\":1,\"Program\":0}]" +
                property + ",\"Versions\":[{\"ScorePath\":\"legacy.mscz\"" + property + "}]}]}");
            var legacy = Settings.Load(path).Library.Single();
            check(legacy.MelodySourcePriority.Count == 0 && legacy.Versions[0].MelodySourcePriority.Count == 0,
                "Old or null source priority remains compatible and uses deterministic fallback");
            check(legacy.MelodySources.Single().Track == 5, "Priority migration preserves the previously selected source");
        }
        SavedSource[] malformed =
        [
            second, null!, second, Source(-1), Source(0, 0), Source(0, 17), Source(0, 1, -1),
            Source(0, 1, 128), Source(65535), first, third
        ];
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Library = new[] { new {
                FullPath = "normalization.mid", MelodySourcePriority = malformed,
                Versions = new[] { new { ScorePath = "normalization.mscz", MelodySourcePriority = malformed } }
            } }
        }));
        var normalized = Settings.Load(path).Library.Single();
        check(Order(normalized.MelodySourcePriority) == "2:1:48|6:4:0|2:1:0", "Invalid and duplicate priority entries are removed without sorting valid choices");
        check(Order(normalized.Versions[0].MelodySourcePriority) == Order(normalized.MelodySourcePriority),
            "Priority validation also protects restored history");
        var clone = Settings.NormalizeMelodySourcePriority([first]);
        first.Track = 20;
        check(clone.Single().Track == 6, "Normalized priority entries are independent copies");
    }
}
