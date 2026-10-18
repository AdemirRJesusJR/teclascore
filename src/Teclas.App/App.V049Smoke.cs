using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Typed chord chart, chord notation, melody by sections, titles sheet, projection export and retries with adjustment.
    // Generation is replaced by stubs: no engine or MuseScore runs.
    private async void VerifyV049Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v049-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.49 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var previousNotation = ChordNotation.Current;
        try
        {
            // 1a. The chord chart text: measures, held slots, repeats, any notation; errors in words.
            var (parsed, errors) = ChordChartText.Parse("| C | G/B | Am F |\n| % | C . . G7M | Bm7(b5) |", 4, 0);
            Check(errors.Count == 0 && parsed.Select(c => (c.Quarter, c.Root + c.Quality + (c.Bass is { } b ? "/" + b : ""))).SequenceEqual(
                [(0.0, "C"), (4.0, "G/B"), (8.0, "Am"), (10.0, "F"), (12.0, "Am"), (14.0, "F"), (16.0, "C"), (19.0, "Gmaj7"), (20.0, "Bm7b5")]), "chart-text-is-read " + string.Join(" ", parsed.Select(c => $"{c.Quarter}:{c.Root}{c.Quality}")));
            Check(ChordChartText.Parse("| C | Xyz |", 4, 0).Errors.Single().Contains("Compasso 2"), "unknown-chords-are-explained");
            string formatted = ChordChartText.Format(parsed, 4, 0);
            Check(ChordChartText.Parse(formatted, 4, 0).Chords.SequenceEqual(parsed), "chart-text-round-trips " + formatted);

            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            var library = (ListBox)window.FindName("Library");
            string score = Path.Combine(work, "versos.mscz"); WriteVerseScore(score);

            // 1b. Typed chords replace the song's chords (bass included), keep a version and generate again; the bass is saved.
            string sectionsMidi = Path.Combine(work, "Grupo Exemplo - Hino da Flauta.mid"); File.WriteAllBytes(sectionsMidi, SectionsMidi());
            window.ImportForSmoke(sectionsMidi); Flush();
            var song = window.Files.Single(r => r.FullPath == sectionsMidi); library.SelectedItem = song; Flush();
            foreach (var t in song.Tracks) t.IsMelody = t.Source.Key.Track == 1;
            song.ScorePath = score; song.Chords.Add(new ChordRow { Quarter = 0, Root = "D", Quality = "", Confidence = .4 });
            song.HasPendingScoreProperties = false;
            MidiRow? generated = null; window.diagramGeneration = r => { generated = r; return Task.CompletedTask; };
            await window.ApplyTypedChordsAsync(song, ChordChartText.Parse("| C | G/B | Am F | C |", 4, 0).Chords); Flush();
            Check(generated == song && song.Chords.Count == 5 && song.Chords[1] is { Root: "G", Bass: "B" } && song.Versions.Count == 1 && window.Status.Contains("Cifra digitada aplicada"), $"typed-chords-are-applied gen={generated == song} n={song.Chords.Count} b={song.Chords[1].Bass} v={song.Versions.Count} " + window.Status);
            Check(window.ChartText(song).StartsWith("| C | G/B | Am F | C |"), "chart-shows-the-current-chords " + window.ChartText(song));
            window.SaveSessionForSmoke();
            Check(Settings.Load(settingsPath).Library.Single(s => s.FullPath == sectionsMidi).Chords.Any(c => c.Bass == "B"), "slash-bass-is-saved");
            var chordJson = Teclas.App.MainWindow.WithBass(new JsonObject { ["root"] = "G" }, song.Chords[1]);
            Check(chordJson["bass"]?.GetValue<string>() == "B" && Teclas.App.MainWindow.WithBass(new JsonObject(), song.Chords[0])["bass"] is null, "bass-goes-to-the-engine-only-when-known");

            // 2. Notation: preferences, preview, engine request, reading scores written in another notation.
            window.NotationMajor7 = "7M"; window.NotationHalfDiminished = "m7(b5)"; window.NotationDiminished = "°"; window.NotationAccidentals = "flats"; Flush();
            Check(ChordNotation.Current is { Major7: "7M", HalfDiminished: "m7(b5)", Diminished: "°", Accidentals: "flats" } && window.NotationPreview.Contains("C7M") && window.NotationPreview.Contains("Bm7(b5)") && window.NotationPreview.Contains("Gb°") && window.NotationPreview.Contains("Bb"),
                "notation-preview " + window.NotationPreview);
            var request = new JsonObject(); window.AddNotation(request);
            Check(request["chordNotation"]?["texts"]?["maj7"]?.GetValue<string>() == "7M" && request["chordNotation"]?["accidentals"]?.GetValue<string>() == "flats", "notation-goes-to-the-engine");
            string written = Path.Combine(work, "escrita.mscz"); WriteNotationScore(written);
            var model = NativeMusicEditor.Open(written, CancellationToken.None);
            var harmonies = model.Measures.SelectMany(m => m.Harmonies).ToList();
            Check(harmonies.Select(h => h.Quality).SequenceEqual(["maj7", "m7b5", "dim"]), "scores-in-other-notation-are-understood " + string.Join(",", harmonies.Select(h => h.Quality)));
            Check(ChordSheetWriter.FromScore(model, "").Contains("C7M") && PracticeSession.ChordsFromScore(model).Count == 3, "chord-sheet-and-practice-use-the-notation");
            Check(ChordNotation.Canonical("ø") == "m7b5" && ChordNotation.Canonical("Δ") == "maj7" && ChordNotation.Display("maj9") == "7M(9)", "notation-aliases");
            int applied = 0; window.autopilotGenerate = (rows, _) => { applied = rows.Count; return Task.FromResult<IReadOnlyDictionary<MidiRow, string>>(new Dictionary<MidiRow, string>()); };
            Check(window.NotationTargets().Contains(song) && await window.ApplyNotationToLibraryAsync(window.NotationTargets()) == applied && applied >= 1, "notation-is-applied-to-the-library");
            window.autopilotGenerate = null;
            window.NotationMajor7 = "maj7"; window.NotationHalfDiminished = "m7b5"; window.NotationDiminished = "dim"; window.NotationAccidentals = "auto";

            // 3. Melody by sections: the flute solo in measures 5–6 is proposed between the voice; confirmed, it is applied.
            var measures = Enumerable.Range(0, 8).Select(i => new MelodyMeasure(i + 1, i * 4, i * 4 + 4, 4, 4, false)).ToArray();
            var map = new MelodyMeasureMap(song.SourceSha256!, 0, measures);
            var proposal = Teclas.App.MainWindow.ProposeSections(song, map);
            Check(proposal.Select(s => (s.FirstMeasure, s.LastMeasure, s.Source.Track)).SequenceEqual([(1, 4, 1), (5, 6, 2), (7, 8, 1)]),
                "sections-follow-the-solo " + string.Join(" ", proposal.Select(s => $"{s.FirstMeasure}-{s.LastMeasure}:{s.Source.Track}")));
            window.autoSectionsDialog = _ => true;
            var appliedSections = await window.ProposeAndApplySectionsAsync(song, map); Flush();
            Check(appliedSections.Count == 3 && song.MelodySections.Count == 3 && song.Tracks.Single(t => t.IsMelody).Source.Key.Track == 1, "sections-are-applied " + window.Status);
            var sectionsWindow = new MelodySectionsWindow(map, song.Tracks.ToArray(), [], null) { Propose = () => Teclas.App.MainWindow.ProposeSections(song, map), ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            sectionsWindow.Show(); Flush();
            Check(sectionsWindow.UseProposal() == 3 && sectionsWindow.Sections.Count == 3, "sections-window-proposes");
            sectionsWindow.Close();
            Check(MelodySectionAdvisor.Propose(measures.Select(m => (m.StartQuarter, m.EndQuarter)).ToList(),
                Enumerable.Range(0, 32).Select(i => new SectionNote(new SourceKey(1, 1, 0), i, i + .9, 72)).ToList(), [], [new SourceKey(1, 1, 0), new SourceKey(2, 2, 73)]).Count == 0, "one-part-songs-get-no-proposal");

            // 4. Titles sheet: fill from the file name, paste a column, save to the library.
            string plain = Path.Combine(work, "musica.mid"); File.WriteAllBytes(plain, TwoPartMidi(7)); window.ImportForSmoke(plain); Flush();
            var sheet = new MetadataSheetWindow(window.Files) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            sheet.Show(); Flush();
            var line = sheet.Rows.Single(r => r.Row == song); line.Title = ""; line.Artist = "";
            sheet.FillFromFileNames();
            Check(line.Title == "Hino da Flauta" && line.Artist == "Grupo Exemplo", $"filled-from-the-file-name ({line.Artist} / {line.Title})");
            int plainIndex = sheet.Rows.ToList().FindIndex(r => r.Row.FullPath == plain);
            sheet.Paste("Canção Colada\tArtista Colado\tCompositor\tColetânea", plainIndex, 0);
            var plainLine = sheet.Rows[plainIndex];
            Check(plainLine is { Title: "Canção Colada", Artist: "Artista Colado", Composer: "Compositor", Collection: "Coletânea" }, "paste-fills-the-row");
            CaptureFolderWindow(sheet, Path.Combine(reports, "titles-sheet.png"));
            int changed = window.ApplyMetadataSheet(sheet.Rows); sheet.Close();
            var plainRow = window.Files.Single(r => r.FullPath == plain);
            Check(changed == 1 && plainRow.ScoreTitle == "Canção Colada" && plainRow.ScoreArtist == "Artista Colado" && plainRow.Collection == "Coletânea" && song.ScoreArtist == "Grupo Exemplo", "sheet-is-saved " + window.Status);

            // 5. Projection: stanzas from the chord sheet, OpenLyrics with inline chords, Holyrics text; songs without words are skipped.
            var stanzas = ProjectionExport.Stanzas("{title: X}\n\n[C]Casa [G]mar\n[F]Lua\n[C] | [G]\n\n{comment: 2ª vez}\n[C]Sol");
            Check(stanzas.Count == 2 && stanzas[0].Count == 2 && stanzas[1][0] == "[C]Sol", "stanzas-skip-instrumental-lines");
            string openLyrics = ProjectionExport.OpenLyrics(stanzas, "Hino", "Grupo");
            var xml = System.Xml.Linq.XDocument.Parse(openLyrics);
            Check(xml.Root!.Name.LocalName == "song" && xml.Descendants().Count(e => e.Name.LocalName == "chord") == 4 && xml.Descendants().Count(e => e.Name.LocalName == "verse") == 2 && ProjectionExport.Holyrics(stanzas).StartsWith("Casa mar\r\nLua"), "openlyrics-and-holyrics");
            string silent = Path.Combine(work, "sem-letra.mscz"); WriteNotationScore(silent);
            plainRow.ScorePath = silent;
            var exported = await window.ExportProjectionAsync([song, plainRow]); Flush();
            Check(exported.Exported == 1 && exported.Skipped.Single().Contains("sem letra") && File.Exists(Path.Combine(exported.Folder, "OpenLP", "Hino da Flauta.xml")) && File.Exists(Path.Combine(exported.Folder, "Holyrics", "Hino da Flauta.txt")), "projection-files-are-written " + window.Status);

            // 6. Retries with adjustment: one song works without folding repeats, one only as chords, one keeps failing.
            string MidiCopy(string name, int shift) { string p = Path.Combine(work, name + ".mid"); File.WriteAllBytes(p, TwoPartMidi(shift)); window.ImportForSmoke(p); Flush(); return p; }
            string pa = MidiCopy("falha-a", 9), pb = MidiCopy("falha-b", 10), pc = MidiCopy("falha-c", 11);
            var a = window.Files.Single(r => r.FullPath == pa); var b = window.Files.Single(r => r.FullPath == pb); var c = window.Files.Single(r => r.FullPath == pc);
            foreach (var row in new[] { a, b, c }) { row.FoldRepeats = true; row.HasOperationError = true; foreach (var t in row.Tracks) t.IsMelody = t.Source.Key.Track == 1; }
            var attempts = new List<string>();
            window.generationAttempt = (rows, _) =>
            {
                var failed = new Dictionary<MidiRow, string>();
                foreach (var row in rows)
                {
                    attempts.Add($"{Path.GetFileNameWithoutExtension(row.FullPath)}:{row.FoldRepeats}:{row.ChordsOnly}");
                    bool ok = row == a && !row.FoldRepeats || row == b && row.ChordsOnly;
                    if (ok) row.ScorePath = score; else failed[row] = "MuseScore recusou";
                }
                return Task.FromResult<IReadOnlyDictionary<MidiRow, string>>(failed);
            };
            var remaining = await window.RetryFailuresAsync([a, b, c]); Flush();
            Check(remaining.Keys.SequenceEqual([c]) && window.LastAdjustments[a] == "sem dobrar as repetições" && window.LastAdjustments[b] == "sem dobrar as repetições e só com cifras"
                && !a.HasOperationError && c.HasOperationError && c.FoldRepeats && !c.ChordsOnly && !a.FoldRepeats && b.ChordsOnly && attempts.Count == 3 + 3 + 2,
                "failures-are-retried-with-adjustments " + string.Join(" ", attempts) + " | " + window.Status);
            Check(window.Status.Contains("2 de 3") && window.Status.Contains("Continuam falhando"), "retry-reports-what-changed");

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            ChordNotation.Current = previousNotation;
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            ChordNotation.Current = previousNotation;
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }

    /// <summary>Voice (track 1) in measures 1–4 and 7–8, flute solo (track 2) in measures 5–6; low chords on track 3.</summary>
    private static byte[] SectionsMidi()
    {
        var voice = new List<WrittenNote>(); var flute = new List<WrittenNote>(); var chords = new List<WrittenNote>();
        for (int beat = 0; beat < 32; beat++)
        {
            int measure = beat / 4;
            if (measure is 4 or 5) flute.Add(new(beat, .9, 79 + beat % 3)); else voice.Add(new(beat, .9, 67 + beat % 5));
            if (beat % 4 == 0) foreach (int p in new[] { 48, 52, 55 }) chords.Add(new(beat, 3.9, p, 60));
        }
        return SimpleMidiWriter.Write(90, 4, [new WrittenTrack("Voz", 0, 52, voice), new WrittenTrack("Flauta", 1, 73, flute), new WrittenTrack("Acordes", 2, 0, chords)]);
    }

    /// <summary>Three measures with chords written in another notation (7M, m7(b5), °) and no lyrics.</summary>
    private static void WriteNotationScore(string path)
    {
        string Harmony(int root, string name) => $"<Harmony><harmonyInfo><name>{name}</name><root>{root}</root></harmonyInfo></Harmony>";
        string Note(int pitch) => $"<Chord><durationType>whole</durationType><Note><pitch>{pitch}</pitch><tpc>14</tpc></Note></Chord>";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false));
        writer.Write("<museScore version='4.70'><Score><Division>480</Division><metaTag name='workTitle'>Escrita</metaTag><Part><Staff id='1'><StaffType group='pitched'/></Staff>" +
            "<Instrument id='piano'><Channel><program value='0'/></Channel></Instrument></Part><Staff id='1'>" +
            "<Measure><voice><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>" + Harmony(14, "7M") + Note(64) + "</voice></Measure>" +
            "<Measure><voice>" + Harmony(19, "m7(b5)") + Note(62) + "</voice></Measure>" +
            "<Measure><voice>" + Harmony(20, "°") + Note(66) + "</voice></Measure>" +
            "</Staff></Score></museScore>");
    }
}
