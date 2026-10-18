using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Confidence rating, library autopilot, review in sequence, rules per collection, command palette and the quick
    // repertoire. Generation and page drawing are replaced by stubs: no engine or MuseScore runs.
    private async void VerifyV048Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v048-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.48 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            // 1. The rating: clean score, doubtful chords, blocking points, unclear automatic melody, lyrics off the melody, error.
            var clean = ScoreConfidence.Rate(new ConfidenceInput([.9, .95, .8], 0, 0, 0, false, false, null, false, false, 2));
            var risky = ScoreConfidence.Rate(new ConfidenceInput([.4, .5, .9, .3], 1, 3, 1, true, false, .2, true, false, 8));
            Check(clean is { Score: 100, Label: "Pronta", Reasons.Count: 0 } && risky.Score < 30 && risky.Label == "Revisar" && risky.Reasons.Count == 6
                && ScoreConfidence.Rate(new ConfidenceInput([], 0, 0, 0, false, false, null, false, true, 1)).Score == 0, $"rating-weighs-the-evidence ({risky.Score}: {string.Join("; ", risky.Reasons)})");

            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            var library = (ListBox)window.FindName("Library");
            string folder = Path.Combine(work, "coletanea"); Directory.CreateDirectory(folder);
            string score = Path.Combine(work, "versos.mscz"); WriteVerseScore(score);
            string Copy(string name) { string target = Path.Combine(work, name + ".mscz"); File.Copy(score, target, true); return target; }
            string[] midis = Enumerable.Range(0, 3).Select(i => Path.Combine(folder, $"hino{i + 1}.mid")).ToArray();
            for (int i = 0; i < 3; i++) File.WriteAllBytes(midis[i], TwoPartMidi(i));
            string karaoke = Path.Combine(work, "karaoke.mid"); File.WriteAllBytes(karaoke, KaraokeMidi());
            foreach (string midi in midis.Append(karaoke)) window.ImportForSmoke(midi);
            Flush();
            MidiRow Row(string path) => window.Files.Single(r => r.FullPath == path);

            // 2. Rules per collection: the choice on one hymn goes to the other hymns of the folder with the same parts.
            var first = Row(midis[0]); library.SelectedItem = first; Flush();
            foreach (var track in first.Tracks) track.IsMelody = track.Source.Key.Track == 1;
            first.TransposeSemitones = 2; first.DefaultDiagramInversion = 1;
            window.GetType().GetMethod("RefreshSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); Flush();
            Check(window.CollectionRuleVisibility == Visibility.Visible && window.CollectionRuleHint.StartsWith("2 música(s)") && window.CollectionRuleTargets(first).Count == 2, "similar-songs-are-found " + window.CollectionRuleHint);
            Check(window.ApplyCollectionRule(first) == 2 && midis.Skip(1).Select(Row).All(r => r.Tracks.Single(t => t.IsMelody).Source.Key.Track == 1 && r.TransposeSemitones == 2 && r.DefaultDiagramInversion == 1 && r.AutoMelody),
                "choice-is-applied-to-similar-songs");
            Check(window.CollectionRuleTargets(first).Count == 0, "applied-songs-leave-the-hint");

            // 3. Autopilot: the karaoke gets the sung part as melody, the hymns are generated, one fails; the rest is rated.
            Row(midis[2]).Tracks.ToList().ForEach(t => t.IsMelody = false); // this one has no clear choice now
            window.autopilotGenerate = (rows, _) =>
            {
                var failed = new Dictionary<MidiRow, string>();
                foreach (var r in rows)
                {
                    if (r.FullPath == midis[1]) { failed[r] = "MuseScore recusou a partitura"; continue; }
                    r.ScorePath = Copy(Path.GetFileNameWithoutExtension(r.FullPath)); r.PageCount = 1; r.GeneratedAt = DateTimeOffset.Now;
                    r.Chords.Clear(); r.Chords.Add(new ChordRow { Root = "G", Quality = "", Confidence = .9 });
                    if (r.FullPath == karaoke) { r.Chords.Add(new ChordRow { Root = "C", Quality = "", Confidence = .4 }); r.Chords.Add(new ChordRow { Root = "D", Quality = "", Confidence = .5 }); }
                }
                return Task.FromResult<IReadOnlyDictionary<MidiRow, string>>(failed);
            };
            int pdfs = 0; window.autopilotPdf = (rows, target, _) => { pdfs = rows.Count; return Task.FromResult(rows.Count); };
            window.autopilotReportDialog = _ => false;
            var candidates = window.AutopilotCandidates();
            Check(candidates.Count == 4, "autopilot-takes-the-songs-without-score");
            var outcomes = await window.RunAutopilotAsync(candidates, exportPdf: true, keepAwake: false); Flush();
            string Group(string path) => outcomes.Single(o => o.Row.FullPath == path).Group;
            var sung = Row(karaoke);
            Check(sung.Tracks.Single(t => t.IsMelody).Source.Key.Track == 2 && sung.AutoMelody && Group(karaoke) == "Para revisar" && Group(midis[0]) == "Prontas"
                && Group(midis[1]) == "Falharam" && pdfs == outcomes.Count(o => o.Row.ScorePath is not null) && window.Status.Contains($"{outcomes.Count(o => o.Group == "Prontas")} pronta") && window.Status.Contains("1 falharam"),
                "autopilot-sorts-the-results " + string.Join(" | ", outcomes.Select(o => $"{Path.GetFileName(o.Row.FullPath)}:{o.Group}:{o.Detail}")));
            Check(Group(midis[2]) is "Precisam de você" or "Prontas", "unclear-melody-is-not-guessed-or-is-clear " + Group(midis[2]));

            // 4. The cards show the rating; "review first" orders by it; approval is saved.
            Check(sung.ConfidenceBadge.Contains("Revisar") || sung.ConfidenceBadge.Contains("Conferir"), "card-shows-the-rating " + sung.ConfidenceBadge);
            Check(sung.ConfidenceToolTip.Contains("acorde(s) com dúvida"), "card-explains-the-rating " + sung.ConfidenceToolTip);
            window.SmartList = "revisar-primeiro"; Flush();
            var ordered = window.LibraryView.Cast<MidiRow>().ToList();
            Check(ordered.Count >= 2 && ordered[0] == sung && ordered.All(r => r.ScorePath is not null && !r.Approved), "review-first-orders-by-rating");
            window.SmartList = ""; Flush();

            // 5. Review in sequence: approve, redo with the next melody candidate, edit.
            var rows = window.ReviewSequenceRows();
            Check(rows.Count >= 2 && rows[0] == sung, "sequence-starts-with-the-riskiest");
            MidiRow? regenerated = null; window.reviewRegenerate = r => { regenerated = r; r.ScorePath = Copy("refeita"); return Task.CompletedTask; };
            var review = new ReviewSequenceWindow(rows, r => { r.Approved = true; }, window.RedoWithNextMelodyAsync) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            review.Show(); Flush();
            var before = sung.Tracks.Single(t => t.IsMelody).Source.Key;
            await review.RedoAsync(); Flush();
            Check(regenerated == sung && sung.Tracks.Single(t => t.IsMelody).Source.Key != before && !sung.AutoMelody && review.StatusText.Contains("Gerada de novo"), "redo-uses-the-next-melody " + review.StatusText);
            review.Approve(); Flush();
            Check(sung.Approved && sung.ConfidenceBadge == "✓ Aprovada" && review.Index == 1, "approve-marks-and-moves-on");
            CaptureFolderWindow(review, Path.Combine(reports, "review-sequence.png"));
            review.Edit();
            Check(review.EditRequested == rows[1], "edit-asks-for-the-editor");
            window.SaveSessionForSmoke();
            Check(Settings.Load(settingsPath).Library.Single(s => s.FullPath == karaoke) is { Approved: true }, "approval-is-saved");

            // 6. Command palette: actions and songs found by typing, accents ignored.
            var palette = new CommandPaletteWindow(window.PaletteItems()) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            palette.Show(); Flush();
            palette.Search("revisao seq");
            Check(palette.Shown.FirstOrDefault()?.Label == "Revisão em sequência", "palette-finds-actions-without-accents");
            palette.Search("hino3");
            Check(palette.Shown.Count == 1, "palette-finds-songs");
            palette.RunSelected(); Flush();
            Check(window.SelectedFile == Row(midis[2]) && window.CurrentPage == "library", "running-a-song-selects-it");
            CommandPaletteWindow? opened = null; window.commandPaletteDialog = w => { opened = w; return true; };
            Check(window.HandleShortcut(System.Windows.Input.Key.K, System.Windows.Input.ModifierKeys.Control, null) && opened is not null, "ctrl-k-opens-the-palette");

            // 7. Quick repertoire: order, key per song, regeneration of the song in a new key, PDF, text and stage list.
            string pages = Path.Combine(work, "paginas"); Directory.CreateDirectory(pages); WhitePage(Path.Combine(pages, "p.png"));
            window.repertoirePages = (_, _, _) => Task.FromResult<IReadOnlyList<string>>([Path.Combine(pages, "p.png")]);
            var hymn = Row(midis[0]); hymn.KeyFifths = 1; hymn.KeyMinor = false; // the MIDI is in G major; the score is 2 semitones up (A)
            Check(Teclas.App.MainWindow.KeyName(hymn, 0) == "G" && Teclas.App.MainWindow.KeyName(hymn, hymn.TransposeSemitones) == "A" && Teclas.App.MainWindow.KeyName(hymn, -2) == "F", "key-names-follow-the-transposition");
            int generatedBefore = 0; window.autopilotGenerate = (list, _) => { generatedBefore = list.Count; foreach (var r in list) r.ScorePath = Copy("transposta"); return Task.FromResult<IReadOnlyDictionary<MidiRow, string>>(new Dictionary<MidiRow, string>()); };
            var setlist = await window.BuildQuickSetlistAsync("Culto de domingo", [(sung, sung.TransposeSemitones), (hymn, hymn.TransposeSemitones + 2)]); Flush();
            var stage = setlist.StageList is { } path ? new PresentationStore(path).Load().Document : null;
            Check(generatedBefore == 1 && hymn.TransposeSemitones == 4 && setlist.Pdf is { } pdf && File.Exists(pdf) && File.ReadAllBytes(pdf).Take(5).SequenceEqual("%PDF-"u8.ToArray())
                && setlist.Text.Contains("1. ") && setlist.Text.Contains("2. ") && setlist.Text.Contains("(tom B)") && stage is { Title: "Culto de domingo", Entries.Count: 2 } && stage.Entries[1].Notes == "Tom B"
                && setlist.Seconds > 0 && window.Status.Contains("Culto de domingo"), "quick-repertoire-is-built " + window.Status);
            var quick = new QuickSetlistWindow(window.Files.ToList()) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            quick.Show(); quick.Add(sung); quick.Add(hymn); quick.Add(sung); quick.Entries[1].Transpose += 1; Flush();
            Check(quick.Entries.Count == 2 && quick.Entries[1].KeyText == "Tom A#".Replace("A#", Teclas.App.MainWindow.KeyName(hymn, hymn.TransposeSemitones + 1)), "setlist-window-sets-keys " + quick.Entries[1].KeyText);
            CaptureFolderWindow(quick, Path.Combine(reports, "quick-setlist.png")); quick.Close();

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }

    /// <summary>Tempo track and two parts (melody on track 1, accompaniment on track 2), shifted so each file is different.</summary>
    private static byte[] TwoPartMidi(int shift)
    {
        static byte[] Part(int channel, int basePitch)
        {
            var body = new List<byte> { 0, (byte)(0xC0 | channel), (byte)(channel == 0 ? 73 : 0) };
            for (int i = 0; i < 24; i++) body.AddRange([0, (byte)(0x90 | channel), (byte)(basePitch + i % 5), 90, 0x83, 0x60, (byte)(0x80 | channel), (byte)(basePitch + i % 5), 0]);
            return MidiTrack(body);
        }
        var tempo = MidiTrack([0, 0xFF, 0x51, 3, 0x07, 0xA1, 0x20]);
        return [.. "MThd"u8, 0, 0, 0, 6, 0, 1, 0, 3, 1, 224, .. tempo, .. Part(0, 72 + shift), .. Part(1, 48 + shift)];
    }
}
