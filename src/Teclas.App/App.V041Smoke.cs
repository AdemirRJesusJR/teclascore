using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Verses 1 and 2, score → text chord sheet, key for the singer, current chord box, score pages for a tablet and the
    // parallel queue. No engine or MuseScore runs.
    private async void VerifyV041Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v041-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.41 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            // A small score: G/B and C chords, verse 1 in measures 1–2 and verse 2 in measure 1 (a repeat).
            string score = Path.Combine(work, "versos.mscz"); WriteVerseScore(score);
            var model = NativeMusicEditor.Open(score, CancellationToken.None);
            var syllables = KaraokeLyrics.FromScore(model);
            Check(syllables.Count(s => s.Verse == 1) == 3 && syllables.Count(s => s.Verse == 2) == 2, "verse-two-is-read-from-the-score");
            Check(KaraokeLyrics.ForPass(syllables, 1).Select(s => s.Text).SequenceEqual(new[] { "Ca", "sa", "mar" })
                && KaraokeLyrics.ForPass(syllables, 2).Select(s => s.Text).SequenceEqual(new[] { "Lu", "a", "mar" }), "karaoke-sings-verse-two-the-second-time");
            var regions = new[] { new ScoreMeasureRegion(1, new(1, .1, .1, .4, .1)), new ScoreMeasureRegion(2, new(1, .5, .1, .4, .1)) };
            Check(KaraokeLyrics.PageTexts(syllables, regions).Single() == "Casa mar\n\nLua", "lyrics-screen-shows-both-verses " + KaraokeLyrics.PageTexts(syllables, regions).Single().Replace("\n", "|"));
            Check(model.Measures.SelectMany(m => m.Harmonies).First().Bass == "B", "slash-bass-is-read-from-the-score");

            // Score → text chord sheet: chords over the syllables, verse 2 as its own block.
            string sheet = ChordSheetWriter.FromScore(model, "Versos");
            var lines = sheet.Replace("\r", "").Split('\n');
            Check(lines[0] == "Versos" && lines.Contains("G/B  C") && lines.Contains("Casa mar") && lines.Contains("2ª vez:") && lines.Contains("Lua"), "score-becomes-a-text-chord-sheet\n" + sheet);
            var textWindow = new ChordSheetTextWindow(sheet, "Versos") { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            string saved = Path.Combine(work, "cifra.txt"); textWindow.saveTarget = _ => saved; textWindow.Show(); Flush();
            Check(textWindow.Save() == saved && File.ReadAllText(saved).Contains("Casa mar"), "text-chord-sheet-is-saved");
            CaptureFolderWindow(textWindow, Path.Combine(reports, "chord-sheet-text.png")); textWindow.Close();

            // Key for the singer: G3–C5 voice, melody C5–E6 (a soprano line) → sung an octave lower, already fits.
            var fits = VoiceRange.Suggest(72, 88, 55, 72);
            Check(fits is { Outside: 0, Octave: -1 } && Math.Abs(fits.Semitones) <= 5, $"soprano-line-is-sung-an-octave-lower ({fits})");
            var wide = VoiceRange.Suggest(60, 79, 57, 74);
            Check(wide.Outside == 2, "too-wide-melody-reports-what-is-left-outside");
            var transpose = new TransposeWindow(0, 0, false, (64, 81), 55, 72) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            transpose.Show(); Flush();
            transpose.SuggestForVoice((64, 81)); Flush();
            Check(transpose.Semitones == VoiceRange.Suggest(64, 81, 55, 72).Semitones && transpose.VoiceResult.Contains("Cabe inteira") && (int)transpose.TargetKey!.SelectedValue == ((transpose.Semitones % 12) + 12) % 12, "transpose-window-suggests-the-key " + transpose.VoiceResult);
            CaptureFolderWindow(transpose, Path.Combine(reports, "voice-key.png")); transpose.Close();

            // Current chord: the last chord at or before the position, and the box over the preview.
            var chords = new[] { (1, 0d), (1, 2d), (2, 0d) };
            Check(Teclas.App.MainWindow.CurrentChordIndex(chords, 1, 1.5) == 0 && Teclas.App.MainWindow.CurrentChordIndex(chords, 1, 2) == 1 && Teclas.App.MainWindow.CurrentChordIndex(chords, 3, 0) == 2, "current-chord-follows-the-position");
            string settingsPath = Path.Combine(work, "settings.json"); new Settings { OutputDirectory = Path.Combine(work, "saida") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            window.CurrentChordEnabled = true; Flush();
            Check(Settings.Load(settingsPath).CurrentChord && window.FindName("CurrentChordCheckBox") is CheckBox { IsChecked: true }, "current-chord-choice-is-saved");
            var viewer = (ScoreViewer)window.FindName("MainScoreViewer");
            viewer.SetCurrentChord("G/B", new KeyboardDiagramPreview().GetImage("G", "", 2, 0, "B"), "depois: C"); Flush();
            Check(((Border)viewer.FindName("ChordBar")).Visibility == Visibility.Visible && ((TextBlock)viewer.FindName("ChordName")).Text == "G/B", "current-chord-box-shows");
            viewer.SetCurrentChord(null, null, ""); Flush();
            Check(((Border)viewer.FindName("ChordBar")).Visibility == Visibility.Collapsed, "current-chord-box-hides");

            // Tablet: the page image only with the code; the page offers the score view.
            string page = Path.Combine(work, "pagina-1.png"); WhitePage(page);
            using (var server = new RemoteControlServer(_ => { }, 0, "123456") { State = new RemoteState("Versos", 1, 1, 1, 1), PageImage = page })
            {
                var image = server.Respond("/page.png?k=123456&v=1");
                Check(image.Type == "image/png" && image.Body.Length == new FileInfo(page).Length, "tablet-gets-the-page-image");
                Check(server.Respond("/page.png?k=000000").Status.StartsWith("403") && server.Answer("/?k=123456").Body.Contains("Ver a partitura aqui"), "page-image-needs-the-code-and-page-offers-the-view");
                server.PageImage = Path.Combine(work, "segredo.txt");
                Check(server.Respond("/page.png?k=123456").Status.StartsWith("404"), "only-png-pages-are-served");
            }

            // Parallel queue lanes.
            Check(ConversionQueueService.SuggestedParallel == (Environment.ProcessorCount >= 4 ? 2 : 1), "queue-lanes-follow-the-processor");

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

    /// <summary>Two 4/4 measures: G/B then C; verse 1 "Ca-sa | mar", verse 2 "Lu-a" in measure 1.</summary>
    private static void WriteVerseScore(string path)
    {
        string Lyric(string text, string syllabic, int verse) => "<Lyrics>" + (verse == 2 ? "<no>1</no>" : "") + (syllabic.Length > 0 ? $"<syllabic>{syllabic}</syllabic>" : "") + $"<text>{text}</text></Lyrics>";
        string Note(int pitch, int tpc, string lyrics, string duration = "half") => $"<Chord><durationType>{duration}</durationType>{lyrics}<Note><pitch>{pitch}</pitch><tpc>{tpc}</tpc></Note></Chord>";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false));
        writer.Write("<museScore version='4.70'><Score><Division>480</Division><metaTag name='workTitle'>Versos</metaTag><Part><Staff id='1'><StaffType group='pitched'/></Staff>" +
            "<Instrument id='piano'><Channel><program value='0'/></Channel></Instrument></Part><Staff id='1'>" +
            "<Measure><voice><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig><Harmony><harmonyInfo><root>15</root><bass>19</bass></harmonyInfo></Harmony>" +
            Note(67, 15, Lyric("Ca", "begin", 1) + Lyric("Lu", "begin", 2)) + Note(65, 13, Lyric("sa", "end", 1) + Lyric("a", "end", 2)) + "</voice></Measure>" +
            "<Measure><voice><Harmony><harmonyInfo><root>14</root></harmonyInfo></Harmony>" + Note(64, 18, Lyric("mar", "", 1), "whole") + "</voice></Measure>" +
            "</Staff></Score></museScore>");
    }
}
