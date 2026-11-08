using System.Text.Json;
using Teclas.App;

var testRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "qa"));
var folder = Path.Combine(testRoot, "teclas-settings-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
try
{
    var path = Path.Combine(folder, "user", "settings.json");
    var missing = Settings.Load(path);
    Check(missing.ThemeId == "classic" && missing.CurrentPage == "dashboard", "Fresh installation defaults");
    Check(missing.ThemeMode == "system", "Fresh installation follows the Windows light or dark mode");
    Check(missing.TrackAuditionSpeed == 1.0, "Fresh installation starts track audition at normal speed");
    Check(missing.Library.Count == 0 && !File.Exists(path), "Read does not write state");
    File.WriteAllText(Path.Combine(folder, "old.json"), "{\"PythonPath\":\"Z:/custom/python.exe\",\"MuseScorePath\":\"Z:/custom/musescore.exe\",\"OutputDirectory\":\"Z:/partituras\"}");
    var old = Settings.Load(Path.Combine(folder, "old.json"));
    Check(old.PythonPath == "Z:/custom/python.exe" && old.MuseScorePath == "Z:/custom/musescore.exe", "Temporarily unavailable user tools retained");
    Check(old.OutputDirectory == "Z:/partituras" && old.ThemeId == "classic", "Old settings migrate without erasing paths");
    Check(old.ThemeMode == "light", "Older settings keep the light version of their palette");
    Check(old.TrackAuditionSpeed == 1.0, "Settings without the audition speed field migrate to normal speed");
    Check(old.ComfortableDiagramMaximumSpan == 12, "Legacy settings adopt an octave for voicing suggestions");
    var reachPath = Path.Combine(folder, "voicing-reach.json");
    foreach (int reach in new[] { 7, 9, 10, 12 })
    {
        new Settings { ComfortableDiagramMaximumSpan = reach, ThemeId = "graphite" }.Save(reachPath);
        Check(Settings.Load(reachPath).ComfortableDiagramMaximumSpan == reach, $"Voicing reach {reach} persists after reopening");
    }
    File.WriteAllText(reachPath, "{\"ComfortableDiagramMaximumSpan\":-1,\"ThemeId\":\"graphite\"}");
    var invalidReach = Settings.Load(reachPath);
    Check(invalidReach.ComfortableDiagramMaximumSpan == 12 && invalidReach.ThemeId == "graphite", "Invalid reach defaults safely without erasing the theme");

    var advicePath = Path.Combine(folder, "harmonic-advice.json");
    new Settings { Library = [new SavedMidi
    {
        FullPath = "Z:/harmonia.mid",
        Chords = [new SavedChord { Root = "C", Quality = "6", Recommendation = "O baixo tocado apoia esta fundamental.", Alternatives = "Am7" }],
        Versions = [new SavedScoreVersion { ScorePath = "Z:/harmonia.mscz",
            Chords = [new SavedChord { Root = "A", Quality = "m7", Recommendation = "Confira as mesmas notas.", Alternatives = "C6" }] }]
    }] }.Save(advicePath);
    var advice = Settings.Load(advicePath).Library[0];
    Check(advice.Chords[0].Recommendation == "O baixo tocado apoia esta fundamental." && advice.Chords[0].Alternatives == "Am7",
        "Chord recommendation and alternatives roundtrip in current session");
    Check(advice.Versions[0].Chords[0].Recommendation == "Confira as mesmas notas." && advice.Versions[0].Chords[0].Alternatives == "C6",
        "Chord recommendation and alternatives roundtrip in version history");
    foreach (var fields in new[] { "", ",\"Recommendation\":null,\"Alternatives\":null" })
    {
        File.WriteAllText(advicePath, "{\"Library\":[{\"FullPath\":\"Z:/old.mid\",\"Chords\":[{\"Root\":\"C\"" + fields +
            "}],\"Versions\":[{\"ScorePath\":\"Z:/old.mscz\",\"Chords\":[{\"Root\":\"C\"" + fields + "}]}]}]}");
        var compatible = Settings.Load(advicePath).Library[0];
        Check(compatible.Chords[0].Recommendation == "" && compatible.Chords[0].Alternatives == "",
            "Missing or null harmonic advice defaults to empty in current session");
        Check(compatible.Versions[0].Chords[0].Recommendation == "" && compatible.Versions[0].Chords[0].Alternatives == "",
            "Missing or null harmonic advice defaults to empty in version history");
    }

    var speedPath = Path.Combine(folder, "speed.json");
    foreach (double speed in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5 })
    {
        new Settings { TrackAuditionSpeed = speed }.Save(speedPath);
        Check(Settings.Load(speedPath).TrackAuditionSpeed == speed, $"Audition speed {speed} roundtrips");
    }
    foreach (double speed in new[] { -1.0, 0.0, 0.249, 0.4, 1.51 })
    {
        File.WriteAllText(speedPath, JsonSerializer.Serialize(new { TrackAuditionSpeed = speed, ThemeId = "graphite" }));
        var invalidSpeed = Settings.Load(speedPath);
        Check(invalidSpeed.TrackAuditionSpeed == 1.0 && invalidSpeed.ThemeId == "graphite",
            $"Invalid or unsupported audition speed {speed} becomes normal without erasing other preferences");
    }

    var state = new Settings
    {
        PythonPath = "Z:/meu python.exe", MuseScorePath = "Z:/MuseScore.exe", OutputDirectory = "Z:/canções",
        ThemeId = "graphite", ThemeMode = "system", IsSidebarCollapsed = true, WindowLeft = -1560, WindowTop = 20,
        WindowWidth = 1420, WindowHeight = 920, WindowMaximized = true, CurrentPage = "library",
        CurrentFilter = "review", SearchText = "Canção", EditorTab = 1, SelectedMidiPath = "Z:/canção.mid", TrackAuditionSpeed = 0.75,
        Library = [new SavedMidi
        {
            FullPath = "Z:/canção.mid", Sha256 = "abc", UseReviewedProfile = true,
            MelodySources = [new SavedSource { Track = 2, Channel = 3, Program = 74 }],
            Chords = [new SavedChord { Quarter = 2.5, Root = "F#", Quality = "m7", Confidence = 0.75 }],
            KeyFifths = 3, KeyMinor = true, Workspace = "Z:/job", AnalysisPath = "Z:/analysis.json",
            ScorePath = "Z:/partitura.mscz", PreviewPath = "Z:/preview.png", PageCount = 3,
            GeneratedAt = new DateTimeOffset(2026, 10, 12, 10, 30, 0, TimeSpan.FromHours(-3)),
            HasOperationError = true, Status = "Conferir cifras"
        }]
    };
    state.Save(path);
    var first = File.ReadAllText(path);
    var restored = Settings.Load(path);
    Check(JsonSerializer.Serialize(state) == JsonSerializer.Serialize(restored), "All preferences and complete musical session roundtrip");
    Check(!File.Exists(path + ".bak"), "Initial atomic save needs no prior backup");
    state.ThemeId = "indigo"; state.Library[0].Chords[0].Quality = "7";
    state.Save(path);
    Check(File.ReadAllText(path + ".bak") == first, "Backup contains exact previous valid session");
    Check(Settings.Load(path).ThemeId == "indigo", "Second generation current settings readable");
    Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "Atomic save leaves no temporary files");

    File.WriteAllText(path, "{broken user state");
    var recovered = Settings.Load(path);
    Check(recovered.ThemeId == "graphite" && recovered.LoadWarning.Contains("recuperadas"), "Corrupt primary recovers backup visibly");
    Check(File.ReadAllText(path) == "{broken user state", "Load does not modify corrupt evidence");
    recovered.Save(path);
    Check(File.ReadAllText(path + ".bak") == first, "Recovery save never replaces valid backup with corrupt primary");
    var corrupt = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*");
    Check(corrupt.Length == 1 && File.ReadAllText(corrupt[0]) == "{broken user state", "Corrupt original preserved verbatim");
    Check(Settings.Load(path).LoadWarning.Length == 0, "Repaired primary cleanly reloads");

    var beforeLock = File.ReadAllBytes(path);
    var beforeBackup = File.ReadAllBytes(path + ".bak");
    bool failed = false;
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        try { state.Save(path); } catch (IOException) { failed = true; }
    }
    Check(failed, "Unwritable settings propagates failure for UI to keep window open");
    Check(File.ReadAllBytes(path).SequenceEqual(beforeLock), "Failed save preserves current settings bytes");
    Check(File.ReadAllBytes(path + ".bak").SequenceEqual(beforeBackup), "Failed save preserves backup bytes");
    Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "Failed save cleans own temporary file");

    var invalid = Path.Combine(folder, "invalid.json");
    File.WriteAllText(invalid, """
    {"ThemeId":"unknown","CurrentPage":"unknown","CurrentFilter":"unknown","SearchText":null,
      "PythonPath":null,"MuseScorePath":null,"OutputDirectory":null,"EditorTab":99,"WindowWidth":-2,
      "Library":[null,{"FullPath":"A.mid","MelodySources":null,"Chords":null},
        {"FullPath":"a.MID"},{"FullPath":""}]}
    """);
    var normalized = Settings.Load(invalid);
    Check(normalized.ThemeId == "classic" && normalized.CurrentPage == "dashboard" && normalized.CurrentFilter == "all", "Unknown UI enums normalized");
    Check(normalized.SearchText == "" && normalized.EditorTab == 3 && normalized.WindowWidth is null, "Null and invalid UI values safe");
    Check(normalized.Library.Count == 1 && normalized.Library[0].Chords.Count == 0 && normalized.Library[0].MelodySources.Count == 0, "Null lists and duplicate paths normalized");
    File.WriteAllText(invalid, "null");
    Check(Settings.Load(invalid).LoadWarning.Length > 0, "JSON null warned instead of crashing");
    File.WriteAllText(invalid + ".bak", "broken backup");
    var broken = Settings.Load(invalid);
    Check(broken.LoadWarning.Length > 0 && broken.Library.Count == 0, "Both corrupt generations produce safe visible defaults");
    File.Delete(invalid);
    Check(Settings.Load(invalid).LoadWarning.Length > 0, "Missing primary with corrupt backup is visible");

    MusicEditorStateChecks.Run(folder, Check);
    ArrangementStateChecks.Run(folder, Check);
    MelodyPriorityStateChecks.Run(folder, Check);
    MelodySectionsStateChecks.Run(folder, Check);
    MetadataChecks.Run(folder, Check);
    CustomDiagramChecks.Run(folder, Check);
    ArtistMetadataChecks.Run(folder, Check);
    OutputDirectoryChecks.Run(folder, Check);

    // Pasta acompanhada: off in fresh and older settings; both choices survive reopening.
    var watchPath = Path.Combine(folder, "folder-watch.json");
    Check(Settings.Load(watchPath) is { WatchMidiFolder: false, WatchMidiFolderAutopilot: false }, "Fresh installations do not watch the MIDI folder");
    File.WriteAllText(watchPath, "{\"MidiFolder\":\"Z:/midis\",\"MidiFolderIncludeSubfolders\":false,\"ThemeId\":\"ocean\"}");
    var olderWatch = Settings.Load(watchPath);
    Check(!olderWatch.WatchMidiFolder && !olderWatch.WatchMidiFolderAutopilot && olderWatch.MidiFolder == "Z:/midis" && !olderWatch.MidiFolderIncludeSubfolders,
        "Older settings keep their MIDI folder and start without watching it");
    olderWatch.WatchMidiFolder = true; olderWatch.WatchMidiFolderAutopilot = true; olderWatch.Save(watchPath);
    Check(Settings.Load(watchPath) is { WatchMidiFolder: true, WatchMidiFolderAutopilot: true, MidiFolder: "Z:/midis" }, "Watching and autopilot choices persist");
    File.WriteAllText(watchPath, "{\"MidiFolder\":null,\"WatchMidiFolder\":true}");
    Check(Settings.Load(watchPath) is { MidiFolder: "", WatchMidiFolder: true }, "A null MIDI folder loads as empty");

    // Coalesced background saving: many quick changes, one snapshot and one write.
    var autoPath = Path.Combine(folder, "auto", "settings.json");
    var live = new Settings { ThemeId = "classic", Library = [new SavedMidi { FullPath = "Z:/a.mid", Versions = [new SavedScoreVersion { ScorePath = "Z:/a.mscz" }] }] };
    int captured = 0;
    using (var autosave = new SettingsAutosave(autoPath, () => { captured++; return SettingsAutosave.Snapshot(live); }, delay: TimeSpan.FromMilliseconds(150)))
    {
        for (int i = 0; i < 25; i++) { live.SearchText = "busca " + i; autosave.Request(); }
        Check(autosave.IsPending && !File.Exists(autoPath) && captured == 0, "Requests wait for the quiet period before taking a snapshot");
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (autosave.Writes == 0 && wait.ElapsedMilliseconds < 5000) Thread.Sleep(20);
        Check(autosave.Writes == 1 && captured == 1 && autosave.Requests == 25, $"25 requests became one snapshot and one write ({captured} snapshots, {autosave.Writes} writes)");
        Check(Settings.Load(autoPath).SearchText == "busca 24", "The coalesced write holds the latest state");

        // Load flushes a pending save of the same file, so readers always see the current state.
        live.ThemeId = "ocean"; autosave.Request();
        Check(Settings.Load(autoPath).ThemeId == "ocean" && !autosave.IsPending, "Load writes the pending change before reading");

        // Flush on closing: the pending change is written synchronously, without waiting for the delay.
        live.ThemeId = "indigo"; autosave.Request(); autosave.Flush();
        Check(autosave.Writes == 3 && JsonSerializer.Deserialize<Settings>(File.ReadAllText(autoPath))!.ThemeId == "indigo", "Flush on closing writes at once");

        // Nothing changed: the snapshot is identical and the file is left untouched.
        var stamp = File.GetLastWriteTimeUtc(autoPath); autosave.Request(); autosave.Flush();
        Check(File.GetLastWriteTimeUtc(autoPath) == stamp && !File.Exists(autoPath + ".corrupt-"), "An identical save does not rewrite the file");

        // A change that keeps arriving is still written within the maximum delay.
        int before = autosave.Writes; var steady = System.Diagnostics.Stopwatch.StartNew();
        using (var busy = new SettingsAutosave(Path.Combine(folder, "auto", "steady.json"), () => SettingsAutosave.Snapshot(live), delay: TimeSpan.FromMilliseconds(200), maximumDelay: TimeSpan.FromMilliseconds(600)))
        {
            while (steady.ElapsedMilliseconds < 1500) { live.SearchText = "contínua " + steady.ElapsedMilliseconds; busy.Request(); Thread.Sleep(50); }
            Check(busy.Writes >= 1, "Continuous changes are saved within the maximum delay");
        }
    }

    // The snapshot is independent of later changes on the UI thread.
    var snapshot = SettingsAutosave.Snapshot(live);
    live.Library.Add(new SavedMidi { FullPath = "Z:/b.mid" }); live.Library[0].Versions.Add(new SavedScoreVersion { ScorePath = "Z:/a2.mscz" });
    live.Library[0].ScoreTitle = "Mudou depois"; live.TempoGoals["x"] = 90;
    Check(snapshot.Library.Count == 1 && snapshot.Library[0].Versions.Count == 1 && snapshot.Library[0].ScoreTitle is null && snapshot.TempoGoals.Count == 0,
        "Snapshot lists and cards do not follow later edits");
    Check(snapshot.SearchText == live.SearchText && snapshot.ThemeId == live.ThemeId, "Snapshot copies every preference");

    // A background write that fails is reported and Flush raises it; the file and its backup stay intact.
    var failPath = Path.Combine(folder, "auto", "fail.json");
    new Settings { ThemeId = "light" }.Save(failPath); new Settings { ThemeId = "graphite" }.Save(failPath);
    var failBytes = File.ReadAllBytes(failPath);
    Exception? reported = null;
    using (var failing = new SettingsAutosave(failPath, () => SettingsAutosave.Snapshot(new Settings { ThemeId = "ocean" }), failed: error => reported = error))
    using (var blocker = new FileStream(failPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        failing.Request();
        bool thrown = false; try { failing.Flush(); } catch (IOException) { thrown = true; }
        Thread.Sleep(100);
        Check(thrown && reported is IOException, "A failed background save is reported");
    }
    Check(File.ReadAllBytes(failPath).SequenceEqual(failBytes), "A failed background save preserves the file");
    Console.WriteLine($"PASS: {checks} persistence checks");
}
finally
{
    // This GUID directory is created by this process and contains only synthetic test data.
    var resolved = Path.GetFullPath(folder);
    var temporaryRoot = testRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("teclas-settings-tests-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected test cleanup target");
    Directory.Delete(resolved, true);
}
