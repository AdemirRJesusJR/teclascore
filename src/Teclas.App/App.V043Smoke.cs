using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Study audio, keyboard range, duplicates, night stage, repertoire length and high contrast. No engine or MuseScore runs
    // (the native study-audio export is checked when TECLAS_TEST_MUSESCORE points to MuseScore).
    private async void VerifyV043Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v043-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null; PresentationWindow? stage = null;
        string originalTheme = ThemeManager.Current.Id;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("TECLAS_TEST_MUSESCORE") is { Length: > 0 } ? 240 : 60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.43 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
            if (!predicate()) throw new TimeoutException(what);
            Flush();
        }
        try
        {
            // Study audio pieces: muted melody copy, count-in score, MP3 frames joined and the count-in trimmed.
            string score = Path.Combine(work, "versos.mscz"); WriteVerseScore(score);
            string muted = Path.Combine(work, "mudo.mscz"); StudyAudioExport.MuteMelody(score, muted);
            System.Xml.Linq.XDocument mutedXml;
            using (var zip = System.IO.Compression.ZipFile.OpenRead(muted)) using (var entry = zip.Entries.First(e => e.FullName.EndsWith(".mscx")).Open()) mutedXml = System.Xml.Linq.XDocument.Load(entry);
            Check(mutedXml.Descendants("Note").All(n => (string?)n.Element("play") == "0") && NativeMusicEditor.Open(muted, CancellationToken.None).Measures.SelectMany(m => m.Harmonies).Count() == 2,
                "accompaniment-copy-silences-only-the-notes");
            var countIn = System.Xml.Linq.XDocument.Parse(StudyAudioExport.CountInMusicXml(new ScoreTempo(90, 6, 8)));
            Check(countIn.Descendants("note").Count() == 2 && countIn.Descendants("dot").Count() == 2 && (string?)countIn.Descendants("midi-program").Single() == "116", "count-in-score-follows-the-meter");
            byte[] Frame(byte fill) { var f = new byte[417]; f[0] = 0xFF; f[1] = 0xFB; f[2] = 0x90; f[3] = 0x44; for (int i = 4; i < f.Length; i++) f[i] = fill; return f; }
            var xing = Frame(0); "Xing"u8.ToArray().CopyTo(xing, 36);
            byte[] first = [.. "ID3"u8.ToArray(), 4, 0, 0, 0, 0, 0, 0, .. xing, .. Frame(1), .. Frame(2), .. Frame(3)], second = [.. Frame(9), .. Frame(8)];
            var joined = StudyAudioExport.Concatenate(first, second, 0.05);
            Check(joined.Length == 417 * 4 && joined[4] == 1 && joined[417 + 4] == 2 && joined[2 * 417 + 4] == 9, "mp3-join-drops-tags-and-trims-the-count-in");
            if (Environment.GetEnvironmentVariable("TECLAS_TEST_MUSESCORE") is { Length: > 0 } museScore && File.Exists(museScore))
            {
                string mp3 = Path.Combine(work, "estudo.mp3");
                await StudyAudioExport.ExportAsync(museScore, score, mp3, new(.75, true, true), Path.Combine(work, "audio"), CancellationToken.None);
                var frames = StudyAudioExport.Frames(File.ReadAllBytes(mp3)).ToArray();
                double seconds = frames.Sum(f => f.Seconds);
                Check(frames.Length > 100 && seconds > 2 * 2 / .75 + 4 * .5 / .75, $"native-study-audio ({seconds:0.0} s)");
            }

            string settingsPath = Path.Combine(work, "settings.json"); new Settings { OutputDirectory = Path.Combine(work, "saida") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            string midi = Path.Combine(work, "musica.mid"); File.WriteAllBytes(midi, SyntheticTwoTrackMidi()); window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single(); ((ListBox)window.FindName("Library")).SelectedItem = row; Flush();
            row.ScorePath = score; window.CurrentFilter = "all"; Flush();
            var dialogs = new List<Window>();
            window.studyAudioDialog = w => { dialogs.Add(w); return true; };
            string mp3Target = Path.Combine(work, "celular.mp3"); StudyAudioOptions? used = null;
            window.studyAudioTarget = _ => mp3Target;
            window.studyAudioExporter = (_, target, options, _) => { used = options; File.WriteAllBytes(target, [0xFF, 0xFB]); return Task.CompletedTask; };
            ((Button)window.FindName("StudyAudioButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => used is not null && File.Exists(mp3Target), "study audio");
            Check(used is { CountIn: true, AccompanimentOnly: false } && window.Status.Contains("Áudio de estudo salvo"), "study-audio-is-saved-from-the-preview");
            var audioWindow = new StudyAudioWindow(.75) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }; audioWindow.Show(); Flush();
            Check(audioWindow.Options.Speed == .75 && audioWindow.Options.CountIn, "study-audio-window-starts-at-the-current-speed");
            CaptureFolderWindow(audioWindow, Path.Combine(reports, "study-audio.png")); audioWindow.Close();

            // Keyboard range: the synthetic melody fits 61 keys; a two-octave-high melody suggests going down.
            Check(KeyboardRange.Of(61) == (36, 96) && KeyboardRange.OctaveToFit(90, 100, 61) == -1 && KeyboardRange.OctaveToFit(20, 109, 88) is null && KeyboardRange.OctaveToFit(40, 80, 49) == 0, "keyboard-ranges");
            foreach (var track in row.Tracks) track.IsMelody = track.Track == row.Tracks.First().Track;
            Flush();
            Check(window.FitsKeyboard(row) && window.KeyboardFitMessage == "", "melody-fits-61-keys");
            row.TransposeSemitones = 36; window.KeyboardKeys = 49; Flush();
            Check(!window.FitsKeyboard(row) && window.KeyboardFitMessage.Contains("49 teclas") && window.KeyboardFitMessage.Contains("↓ Oitava"), "too-high-melody-suggests-an-octave " + window.KeyboardFitMessage);
            window.SmartList = "fora-do-teclado"; Flush();
            Check(((ListBox)window.FindName("Library")).Items.Count == 1, "smart-list-shows-songs-outside-the-keyboard");
            window.SmartList = ""; row.TransposeSemitones = 0; window.KeyboardKeys = 61; Flush();

            // Duplicates: the same file twice and a renamed copy; the card with a score stays; undo brings them back.
            string copy = Path.Combine(work, "outra pasta"); Directory.CreateDirectory(copy);
            string same = Path.Combine(copy, "musica.mid"); File.Copy(midi, same); window.ImportForSmoke(same); Flush();
            string renamed = Path.Combine(work, "Musica (1).mid"); var other = SyntheticTwoTrackMidi(); other[^10] = 9; File.WriteAllBytes(renamed, other); window.ImportForSmoke(renamed); Flush();
            var groups = window.FindDuplicates();
            Check(groups.Count == 1 && groups[0].Reason == "Mesmo arquivo" && groups[0].Items.Count == 2, "same-file-is-found " + string.Join(";", groups.Select(g => g.Reason + g.Items.Count)));
            Check(DuplicateFinder.Normalize("Asa Branca (1) - cópia") == DuplicateFinder.Normalize("asa branca") && DuplicateFinder.Normalize("Asa Branca") != DuplicateFinder.Normalize("Asa Branca 2"), "copy-marks-are-ignored-in-names");
            DuplicatesWindow? duplicates = null;
            window.duplicatesDialog = w => { duplicates = w; return true; };
            ((Button)window.FindName("FindDuplicatesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
            Check(duplicates is not null && duplicates.ToRemove.SequenceEqual(new[] { same }) && window.Files.Count == 2 && window.Files.Contains(row), "duplicate-without-score-leaves-the-library");
            Check(File.Exists(same), "duplicate-file-stays-on-disk");
            duplicates!.ShowActivated = false; duplicates.Opacity = 0;

            // Night stage and repertoire length.
            var white = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 }, 8);
            var night = ScoreViewer.Night(white); var pixels = new byte[2]; night.CopyPixels(pixels, 2, 0);
            Check(pixels[0] == 24 && pixels[1] == 230, "night-page-is-light-on-dark");
            string pages = Path.Combine(work, "paginas"); Directory.CreateDirectory(pages); WhitePage(Path.Combine(pages, "pagina-1.png"));
            bool? savedNight = null;
            stage = new PresentationWindow(new PresentationStore(Path.Combine(work, "presentation.json")), [new(midi, score, "Versos", null), new(renamed, score, "Outra", null)],
                (_, _, _) => Task.FromResult(Path.Combine(pages, "pagina-1.png")))
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Owner = window, DurationOf = item => item.Title == "Versos" ? 150 : 95, NightInitial = true, SaveNight = value => savedNight = value };
            stage.pedalDevice = () => null;
            stage.Show(); Flush(); stage.AddForSmoke(); Flush();
            ((ComboBox)LogicalChildren(stage).OfType<ComboBox>().First(c => c.Name == "PresentationLibrary")).SelectedIndex = 1; stage.AddForSmoke(); Flush();
            await Wait(() => stage.Viewer.PageCount == 1, "presentation page");
            Check(stage.Viewer.NightMode, "night-mode-is-remembered");
            var info = (TextBlock)LogicalChildren(stage).OfType<TextBlock>().First(t => t.Name == "PresentationDuration");
            Check(info.Text.StartsWith("2 música(s) · 4:05 no total") && info.Text.Contains("tocadas 1 (2:30)") && info.Text.Contains("faltam 1:35"), "repertoire-length " + info.Text);
            Check(MidiDuration.Seconds(MidiReader.Read(midi)) > 0 && MidiDuration.Format(65) == "1:05", "midi-length");
            CaptureFolderWindow(stage, Path.Combine(reports, "night-stage.png"));
            stage.SetNight(false, save: true);
            Check(savedNight == false && !stage.Viewer.NightMode, "night-mode-choice-is-saved");
            stage.Close(); stage = null;

            // High contrast: the sixth theme, chosen at startup when Windows is in high contrast and no theme was picked.
            Check(ThemeManager.StartupId(null, true) == ThemeManager.ContrastId && ThemeManager.StartupId("ocean", true) == "ocean" && ThemeManager.StartupId("classic", false) == "classic", "windows-high-contrast-is-followed");
            ThemeManager.Apply(ThemeManager.ContrastId); Flush();
            Check(((SolidColorBrush)Current.Resources["Accent"]).Color == Color.FromRgb(255, 229, 0) && ((SolidColorBrush)Current.Resources["InputBorder"]).Color == Colors.White, "high-contrast-palette-is-applied");
            CaptureFolderWindow(window, Path.Combine(reports, "high-contrast.png"));
            ThemeManager.Apply(originalTheme); Flush();

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            ThemeManager.Apply(originalTheme);
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            stage?.Close(); window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
