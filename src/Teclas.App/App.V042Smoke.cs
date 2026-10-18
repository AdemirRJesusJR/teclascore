using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Songbook PDF, ChordPro, transposing a pasted sheet, chords to learn, similar songs, stage count-in and the weekly
    // backup. No engine or MuseScore runs.
    private async void VerifyV042Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v042-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null; PresentationWindow? stage = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.42 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20);
            if (!predicate()) throw new TimeoutException(what);
            Flush();
        }
        try
        {
            string score = Path.Combine(work, "versos.mscz"); WriteVerseScore(score);
            var model = NativeMusicEditor.Open(score, CancellationToken.None);

            // ChordPro: chords in brackets before their syllables, verse 2 as a comment block.
            string pro = ChordSheetWriter.ToChordPro(model, "Versos", "Artista");
            Check(pro.StartsWith("{title: Versos}") && pro.Contains("{artist: Artista}") && pro.Contains("[G/B]Casa [C]mar") && pro.Contains("{comment: 2ª vez}") && pro.Contains("[G/B]Lua"), "chordpro-places-the-chords\n" + pro);
            var textWindow = new ChordSheetTextWindow(ChordSheetWriter.FromScore(model, "Versos"), "Versos", pro) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            string cho = Path.Combine(work, "versos.cho"); textWindow.saveTarget = _ => cho; textWindow.Show(); Flush();
            Check(textWindow.SaveChordPro() == cho && File.ReadAllText(cho) == pro, "chordpro-file-is-saved"); textWindow.Close();

            // Pasted sheet: transposed and simplified chords keep their columns; labels and lyrics stay.
            const string pasted = "C  G/B (2x)  Am\n\nC              G7        Am7\nAsa branca que voa no sertão\nBb7M   F°   D7/9";
            var up = ChordSheetParser.Transform(pasted, 2, false).Replace("\r", "").Split('\n');
            Check(up[0] == "D  A/C# (2x) Bm" && up[2] == "D              A7        Bm7" && up[3] == "Asa branca que voa no sertão" && up[4] == "C7M    G°   E7/9", "sheet-transposes-in-place\n" + string.Join("\n", up));
            var simple = ChordSheetParser.Transform(pasted, 0, true).Replace("\r", "").Split('\n');
            Check(simple[2] == "C              G         Am" && simple[4] == "Bb     Fdim D", "sheet-simplifies-to-triads\n" + string.Join("\n", simple));
            var sheetWindow = new ChordSheetWindow(2, (_, _) => null) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }; sheetWindow.Show(); Flush();
            sheetWindow.SetText(pasted); Flush(); sheetWindow.Shift(-1); sheetWindow.Shift(-1); Flush();
            Check(sheetWindow.Sheet.Distinct.First().Symbol == "Bb" && sheetWindow.Text.Contains("Asa branca"), "sheet-window-transposes " + sheetWindow.Sheet.Distinct.First().Symbol);
            CaptureFolderWindow(sheetWindow, Path.Combine(reports, "sheet-transpose.png")); sheetWindow.Close();

            // Songbook pages: A4 at 150 dpi, the heading and the monospaced chord lines.
            var pages = Teclas.App.MainWindow.RenderSongbookPages(ChordSheetWriter.FromScore(model, "Versos — Artista"), Path.Combine(work, "caderno"));
            var first = new System.Windows.Media.Imaging.PngBitmapDecoder(new Uri(pages[0]), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            Check(pages.Count == 1 && Math.Abs(first.PixelWidth - 1241) <= 1 && Math.Abs(first.PixelHeight - 1754) <= 1, $"songbook-page-is-a4 ({first.PixelWidth}x{first.PixelHeight})");
            File.Copy(pages[0], Path.Combine(reports, "songbook-page.png"), true);
            var many = Teclas.App.MainWindow.RenderSongbookPages("Longa\n\n" + string.Join("\n", Enumerable.Range(1, 120).Select(i => "C  G  Am  F " + i)), Path.Combine(work, "longa"));
            Check(many.Count == 3, $"long-sheet-continues-on-more-pages ({many.Count})");

            // Chords to learn and similar songs.
            ChordShape S(int pc, string q = "") => new(pc, q);
            var songs = new[]
            {
                new SongChords("a", "Estudada", new HashSet<ChordShape> { S(0), S(7), S(5) }, true),
                new SongChords("b", "Falta Am", new HashSet<ChordShape> { S(0), S(7), S(9, "m") }, false),
                new SongChords("c", "Falta Am e Dm", new HashSet<ChordShape> { S(9, "m"), S(2, "m"), S(0) }, false),
                new SongChords("d", "Em Ré", new HashSet<ChordShape> { S(2), S(9), S(7) }, false),
            };
            var learn = ChordLibraryStats.ToLearn(songs, new HashSet<ChordShape>());
            Check(learn[0] is { Name: "Am", Unlocks: 1, Songs: 2, Known: false } && learn.Single(c => c.Name == "C").Known, "chords-to-learn-order-by-what-they-unlock " + string.Join(",", learn.Select(c => $"{c.Name}:{c.Unlocks}:{c.Songs}")));
            Check(ChordLibraryStats.ToLearn(songs, new HashSet<ChordShape> { S(9, "m") }).Single(c => c.Name == "Am").Known, "a-drilled-chord-counts-as-known");
            var similar = ChordLibraryStats.Similar(songs[0], songs);
            Check(similar.Any(s => s.Song.Id == "d" && s.Transpose == 2 && s.Score == 1) && similar.Any(s => s.Song.Id == "b" && s.Transpose == 0), "similar-songs-also-in-another-key " + string.Join(",", similar.Select(s => $"{s.Song.Id}:{s.Score:0.00}:{s.Transpose}")));

            string settingsPath = Path.Combine(work, "settings.json"), output = Path.Combine(work, "saida");
            new Settings { OutputDirectory = output }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            var midi = Path.Combine(work, "acompanhamento.mid"); File.WriteAllBytes(midi, SyntheticTwoTrackMidi()); window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single();
            row.Chords.Add(new ChordRow { Root = "C" }); row.Chords.Add(new ChordRow { Quarter = 4, Root = "A", Quality = "m" });
            Check(window.ChordsToLearn().Count == 2, "library-chords-are-counted");
            PracticeWindow? drill = null;
            window.practiceDialog = w => { drill = w; return true; };
            window.DrillChord(window.ChordsToLearn().First(c => c.Name == "Am"));
            Check(drill is { Title: "Praticar · Acorde Am" }, "one-click-drill-opens-the-practice");
            Window? learnWindow = null; window.chordStudyDialog = w => { learnWindow = w; return true; };
            ((Button)window.FindName("ChordsToLearnButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
            Check(learnWindow is ChordsToLearnWindow, "chords-to-learn-window-opens");
            learnWindow!.ShowActivated = false; learnWindow.Opacity = 0; learnWindow.Show(); Flush();
            CaptureFolderWindow(learnWindow, Path.Combine(reports, "chords-to-learn.png")); learnWindow.Close();

            // Songbook PDF from the library (one song, with index).
            string pdf = Path.Combine(work, "caderno.pdf");
            await window.BuildSongbookPdf([new(score, "Versos", "Artista", null)], "Caderno", true, pdf); Flush();
            Check(File.Exists(pdf) && File.ReadAllBytes(pdf).Take(5).SequenceEqual("%PDF-"u8.ToArray()) && window.Status.Contains("Caderno de cifras salvo: 1 músicas"), "songbook-pdf-is-written " + window.Status);

            // Stage: tempo in the title and one measure of clicks, also from the phone.
            Check(ScoreTempo.Read(score) is { Bpm: 120, Beats: 4 } && new ScoreTempo(90, 6, 8) is { Beats: 2, BeatSeconds: 1 }, "tempo-and-compound-meter");
            var clicks = new RecordingMidiOutput();
            await StageCountIn.PlayAsync(clicks, new ScoreTempo(600, 3, 4), CancellationToken.None);
            var ons = clicks.Messages.Where(m => (m & 0xF0) == 0x90).ToArray();
            Check(ons.Length == 3 && (ons[0] >> 8 & 0x7F) == 76 && (ons[1] >> 8 & 0x7F) == 77 && (ons[0] & 0x0F) == 9, "count-in-clicks-with-an-accent");
            string pagesFolder = Path.Combine(work, "paginas"); Directory.CreateDirectory(pagesFolder); WhitePage(Path.Combine(pagesFolder, "pagina-1.png"));
            var stageClicks = new RecordingMidiOutput();
            stage = new PresentationWindow(new PresentationStore(Path.Combine(work, "presentation.json")), [new(midi, score, "Versos", null)],
                (_, _, _) => Task.FromResult(Path.Combine(pagesFolder, "pagina-1.png")))
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Owner = window, TempoOf = item => new ScoreTempo(600, 4, 4) };
            stage.countInOutput = () => stageClicks; stage.pedalDevice = () => null;
            stage.Show(); Flush(); stage.AddForSmoke(); Flush();
            await Wait(() => stage.Viewer.PageCount == 1, "presentation page");
            Check(((TextBlock)LogicalChildren(stage).OfType<TextBlock>().First(t => t.Name == "PresentationCurrent")).Text.Contains("♩ = 600 · 4/4"), "stage-shows-the-tempo");
            stage.RemoteCommand("count"); await Wait(() => stage.CountIns == 1 && stageClicks.Messages.Count(m => (m & 0xF0) == 0x90) == 4, "count-in");
            Check(RemoteControlServer.Commands.Contains("count"), "phone-starts-the-count-in");
            stage.Close(); stage = null;

            // Weekly backup: due after seven days, the newest four kept.
            string backups = Path.Combine(work, "copias"); Directory.CreateDirectory(backups);
            window.autoBackupFolderPicker = () => backups;
            window.AutoBackupEnabled = true; Flush();
            await Wait(() => Settings.Load(settingsPath).LastAutoBackup is not null, "first automatic backup");
            var start = Settings.Load(settingsPath).LastAutoBackup!.Value;
            Check(await window.RunAutoBackupIfDue(now: start.AddDays(3)) is null, "backup-waits-a-week");
            for (int week = 1; week <= 5; week++) Check(await window.RunAutoBackupIfDue(now: start.AddDays(7 * week)) is not null, "weekly-backup-" + week);
            var kept = Directory.GetFiles(backups, "TeclaScore-automatico-*.teclasbackup");
            Check(kept.Length == Teclas.App.MainWindow.AutoBackupKept, $"newest-four-backups-are-kept ({kept.Length})");
            var inspected = await LibraryBackupService.InspectAsync(kept.OrderBy(f => f).Last(), null, CancellationToken.None);
            Check(inspected.Manifest.LibraryJson.Contains("acompanhamento.mid"), "backup-holds-the-library");

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            stage?.Close(); window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
