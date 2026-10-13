using System.Security.Cryptography;
using System.Text.Json;
using Teclas.App;

internal static class MetadataChecks
{
    public static void Run(string folder, Action<bool, string> check)
    {
        string firstMidi = Path.Combine(folder, "Minha__Canção   01.mid");
        string secondMidi = Path.Combine(folder, "outra_faixa.mid");
        File.WriteAllBytes(firstMidi, [77, 84, 104, 100, 1, 2, 3]);
        File.WriteAllBytes(secondMidi, [77, 84, 104, 100, 4, 5, 6]);
        byte[] firstHash = SHA256.HashData(File.ReadAllBytes(firstMidi));
        byte[] secondHash = SHA256.HashData(File.ReadAllBytes(secondMidi));
        string path = Path.Combine(folder, "score-metadata.json");
        var state = new Settings
        {
            DefaultDiagramInversion = 0,
            Library =
            [
                new SavedMidi
                {
                    FullPath = firstMidi, Sha256 = "same-content-key", ScoreTitle = "Canção & Sol <2026>",
                    ScoreSubtitle = "Estudo — teclado • versão nº 1", ScoreComposer = "Ademir José / Autoria",
                    DefaultDiagramInversion = 2, DiagramInversion = 1, HasPendingScoreProperties = true
                },
                new SavedMidi
                {
                    FullPath = secondMidi, Sha256 = "same-content-key", ScoreTitle = "Outra obra",
                    ScoreSubtitle = "", ScoreComposer = "", DefaultDiagramInversion = 1, DiagramInversion = 0
                }
            ]
        };
        state.Save(path);
        var loaded = Settings.Load(path);
        check(loaded.Library.Count == 2 && loaded.Library[0].ScoreTitle == "Canção & Sol <2026>"
            && loaded.Library[0].ScoreSubtitle == "Estudo — teclado • versão nº 1" && loaded.Library[0].ScoreComposer == "Ademir José / Autoria",
            "Score metadata preserves Unicode and special characters");
        check(loaded.Library[0].DiagramInversion == 1 && loaded.Library[1].DiagramInversion == 0,
            "Each MIDI retains its own chord diagram inversion");
        check(loaded.DefaultDiagramInversion == 0 && loaded.Library[0].DefaultDiagramInversion == 2
            && loaded.Library[1].DefaultDiagramInversion == 1,
            "Global inversion and independent import-time snapshots roundtrip");
        check(loaded.Library[0].HasPendingScoreProperties && !loaded.Library[1].HasPendingScoreProperties,
            "Pending score properties persist independently per MIDI");
        check(loaded.Library[1].ScoreTitle == "Outra obra" && loaded.Library[1].ScoreSubtitle == "" && loaded.Library[1].ScoreComposer == "",
            "Metadata remains per file even when MIDI hashes match");
        loaded.Library[0].ScoreTitle = "Canção & Sol (revisada)";
        loaded.Library[0].ScoreComposer = "Autoria revisada";
        loaded.Library[0].DiagramInversion = 2;
        loaded.DefaultDiagramInversion = 1;
        loaded.Save(path);
        var edited = Settings.Load(path);
        check(edited.Library[0].ScoreTitle == "Canção & Sol (revisada)" && edited.Library[0].ScoreComposer == "Autoria revisada"
            && edited.Library[0].DiagramInversion == 2, "Last metadata edit survives another save and reload");
        check(edited.Library[1].ScoreTitle == "Outra obra" && edited.Library[1].ScoreSubtitle == "" && edited.Library[1].DiagramInversion == 0,
            "Editing one score does not change another score's metadata");
        check(edited.DefaultDiagramInversion == 1 && edited.Library[0].DefaultDiagramInversion == 2
            && edited.Library[1].DefaultDiagramInversion == 1,
            "Changing global inversion does not rewrite existing MIDI snapshots");
        check(edited.Library[0].FullPath == firstMidi && edited.Library[1].FullPath == secondMidi
            && File.Exists(firstMidi) && File.Exists(secondMidi)
            && SHA256.HashData(File.ReadAllBytes(firstMidi)).SequenceEqual(firstHash)
            && SHA256.HashData(File.ReadAllBytes(secondMidi)).SequenceEqual(secondHash),
            "Editing displayed score properties does not rename or rewrite MIDI originals");

        string legacyPath = Path.Combine(folder, "score-metadata-legacy.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { Library = new[] { new { FullPath = firstMidi } } }));
        var legacySettings = Settings.Load(legacyPath);
        var legacy = legacySettings.Library.Single();
        check(legacy.ScoreTitle is null && legacy.ScoreSubtitle is null && legacy.ScoreComposer is null,
            "Legacy absent metadata stays null for dynamic row defaults");
        check(legacySettings.DefaultDiagramInversion == 2 && legacy.DefaultDiagramInversion == 2
            && legacy.DiagramInversion is null, "Legacy global and row defaults are second inversion without override");
        new Settings { Library = [legacy] }.Save(legacyPath);
        var legacyResaved = Settings.Load(legacyPath).Library.Single();
        check(legacyResaved.ScoreTitle is null && legacyResaved.ScoreSubtitle is null && legacyResaved.ScoreComposer is null
            && legacyResaved.DefaultDiagramInversion == 2 && legacyResaved.DiagramInversion is null,
            "Legacy defaults survive settings-only roundtrip");

        new Settings
        {
            Library =
            [
                new SavedMidi { FullPath = firstMidi, ScoreTitle = null, ScoreSubtitle = null, ScoreComposer = null },
                new SavedMidi { FullPath = secondMidi, ScoreTitle = "", ScoreSubtitle = "", ScoreComposer = "" }
            ]
        }.Save(path);
        var empty = Settings.Load(path);
        check(empty.Library[0].ScoreTitle is null && empty.Library[0].ScoreSubtitle is null && empty.Library[0].ScoreComposer is null,
            "Explicit null metadata remains eligible for default values");
        check(empty.Library[1].ScoreTitle == "" && empty.Library[1].ScoreSubtitle == "" && empty.Library[1].ScoreComposer == "",
            "Explicit empty score fields are not replaced with defaults");
        foreach (int? inversion in new int?[] { null, 0, 1, 2 })
        {
            new Settings { Library = [new SavedMidi { FullPath = firstMidi, DiagramInversion = inversion }] }.Save(path);
            check(Settings.Load(path).Library.Single().DiagramInversion == inversion,
                $"Diagram inversion override {inversion?.ToString() ?? "default"} roundtrips");
        }
        foreach (int inversion in new[] { 0, 1, 2 })
        {
            new Settings
            {
                DefaultDiagramInversion = inversion,
                Library = [new SavedMidi { FullPath = firstMidi, DefaultDiagramInversion = inversion }]
            }.Save(path);
            var defaults = Settings.Load(path);
            check(defaults.DefaultDiagramInversion == inversion && defaults.Library.Single().DefaultDiagramInversion == inversion
                && defaults.Library.Single().DiagramInversion is null,
                $"Global and imported snapshot inversion {inversion} roundtrip without explicit override");
        }
        foreach (int inversion in new[] { -1, 3, 999 })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                DefaultDiagramInversion = inversion,
                Library = new[] { new { FullPath = firstMidi, ScoreTitle = "Título preservado", DefaultDiagramInversion = inversion, DiagramInversion = inversion } }
            }));
            var normalizedSettings = Settings.Load(path);
            var normalized = normalizedSettings.Library.Single();
            check(normalizedSettings.DefaultDiagramInversion == 2 && normalized.DefaultDiagramInversion == 2
                && normalized.DiagramInversion is null && normalized.ScoreTitle == "Título preservado",
                $"Invalid inversion {inversion} normalizes defaults and removes override without erasing metadata");
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            DefaultDiagramInversion = 0, Library = new[] { new { FullPath = firstMidi } }
        }));
        var migrated = Settings.Load(path);
        check(migrated.DefaultDiagramInversion == 0 && migrated.Library.Single().DefaultDiagramInversion == 2
            && migrated.Library.Single().DiagramInversion is null,
            "Legacy library snapshot stays second inversion even when global preference is different");
    }
}
