using System.Text.Json;
using Teclas.App;

internal static class CustomDiagramChecks
{
    public static void Run(string folder, Action<bool, string> check)
    {
        var path = Path.Combine(folder, "custom-diagrams.json");
        static List<SavedChord> Chords() =>
        [
            new() { Quarter = 0, Root = "C", Quality = "", DiagramInversion = null },
            new() { Quarter = 4, Root = "A", Quality = "m", DiagramInversion = 0 },
            new() { Quarter = 8, Root = "C", Quality = "", DiagramInversion = 2 },
            new() { Quarter = 12, Root = "F", Quality = "", DiagramInversion = 1 }
        ];
        var expected = new int?[] { null, 0, 2, 1 };
        new Settings
        {
            DefaultDiagramInversion = 2,
            Library =
            [
                new()
                {
                    FullPath = Path.Combine(folder, "first.mid"), DefaultDiagramInversion = 1,
                    DiagramInversion = 0, Chords = Chords(),
                    Versions = [new() { ScorePath = Path.Combine(folder, "version.mscz"), Chords = Chords() }]
                },
                new() { FullPath = Path.Combine(folder, "second.mid"), Chords = Chords() }
            ]
        }.Save(path);
        var loaded = Settings.Load(path);
        check(loaded.Library[0].Chords.Select(c => c.DiagramInversion).SequenceEqual(expected),
            "Per-occurrence custom diagram positions preserve null, root, first and second inversion");
        check(loaded.Library[0].Chords[0].Root == loaded.Library[0].Chords[2].Root
            && loaded.Library[0].Chords[0].DiagramInversion != loaded.Library[0].Chords[2].DiagramInversion,
            "Repeated chord symbols may retain different diagram positions");
        check(loaded.Library[0].Versions[0].Chords.Select(c => c.DiagramInversion).SequenceEqual(expected),
            "Historical chord positions survive save and load");
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var chords = json.RootElement.GetProperty("Library")[0].GetProperty("Chords");
            check(chords[0].GetProperty("DiagramInversion").ValueKind == JsonValueKind.Null
                && chords[1].GetProperty("DiagramInversion").GetInt32() == 0,
                "JSON distinguishes inherited position from explicit root position");
        }
        loaded.DefaultDiagramInversion = 0;
        loaded.Library[0].DiagramInversion = 2;
        loaded.Library[0].Chords[0].DiagramInversion = 1;
        loaded.Save(path);
        var changed = Settings.Load(path);
        check(changed.Library[0].Chords[1].DiagramInversion == 0 && changed.Library[0].Chords[2].DiagramInversion == 2,
            "Changing global and score defaults preserves explicit chord positions");
        check(changed.Library[1].Chords.Select(c => c.DiagramInversion).SequenceEqual(expected),
            "Editing one score's chord positions leaves other scores unchanged");
        check(changed.Library[0].Versions[0].Chords.Select(c => c.DiagramInversion).SequenceEqual(expected),
            "Editing current positions does not mutate historical positions");
        changed.Library[0].Chords[1].DiagramInversion = null;
        changed.Save(path);
        check(Settings.Load(path).Library[0].Chords[1].DiagramInversion is null,
            "Returning an explicit root position to inheritance survives reopening");

        foreach (int invalid in new[] { -1, 3, int.MaxValue })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                DefaultDiagramInversion = 1,
                Library = new[]
                {
                    new
                    {
                        FullPath = Path.Combine(folder, "unavailable.mid"),
                        ScoreTitle = "Título preservado",
                        Chords = new[] { new { Quarter = 4.5, Root = "Bb", Quality = "m7", DiagramInversion = invalid } },
                        Versions = new[]
                        {
                            new { ScorePath = Path.Combine(folder, "unavailable.mscz"),
                                Chords = new[] { new { Quarter = 4.5, Root = "Bb", Quality = "m7", DiagramInversion = invalid } } }
                        }
                    }
                }
            }));
            var normalized = Settings.Load(path);
            var row = normalized.Library.Single();
            check(row.Chords.Single().DiagramInversion is null && row.Versions.Single().Chords.Single().DiagramInversion is null,
                $"Invalid per-chord inversion {invalid} is removed in current and historical snapshots");
            check(row.Chords[0].Quarter == 4.5 && row.Chords[0].Root == "Bb" && row.Chords[0].Quality == "m7"
                && row.ScoreTitle == "Título preservado" && normalized.DefaultDiagramInversion == 1,
                $"Invalid position {invalid} does not erase musical edits or preferences");
        }

        File.WriteAllText(path, """
        {"Library":[{"FullPath":"legacy.mid","Chords":[{"Root":"C","Quarter":0}],
          "Versions":[{"ScorePath":"legacy.mscz","Chords":[{"Root":"Am","Quarter":4}]}]}]}
        """);
        var legacy = Settings.Load(path).Library.Single();
        check(legacy.Chords[0].DiagramInversion is null && legacy.Versions[0].Chords[0].DiagramInversion is null,
            "Pre-customization sessions inherit their score's existing position");
    }
}
