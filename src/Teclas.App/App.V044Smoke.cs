using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Chord staff and solfège options, booklet printing, falling notes and the temporary-files cleanup. No engine or
    // MuseScore runs.
    private async void VerifyV044Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v044-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.44 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        T Invoke<T>(object target, string name, params object[] args) =>
            (T)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args)!;
        try
        {
            string settingsPath = Path.Combine(work, "settings.json"); new Settings { OutputDirectory = Path.Combine(work, "saida") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            string midi = Path.Combine(work, "musica.mid"); File.WriteAllBytes(midi, SyntheticTwoTrackMidi()); window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single(); ((ListBox)window.FindName("Library")).SelectedItem = row; Flush();

            // Chord staff (per song) and solfège names (app-wide): saved, sent to the engine, shown in the previews.
            window.ChordStaff = true; window.NoteNames = "solfejo"; Flush();
            var options = Invoke<JsonObject>(window, "QueueGenerationOptions", row);
            Check(options["chordStaff"]?.GetValue<bool>() == true && options["noteNames"]?.GetValue<string>() == "solfejo", "chord-staff-and-solfege-go-to-the-engine");
            window.SaveSessionForSmoke();
            var saved = Settings.Load(settingsPath);
            Check(saved.Library.Single().ChordStaff && saved.NoteNames == "solfejo" && window.FindName("ChordStaffCheckBox") is CheckBox { IsChecked: true }, "choices-are-saved");
            Check(KeyboardDiagramPreview.CLabel == "Dó", "previews-use-solfege");
            var chartRow = new MidiRow { FullPath = "cifra", DefaultDiagramInversion = 2 }; chartRow.Chords.Add(new ChordRow { Root = "C" });
            var chart = new ChordChartWindow(chartRow) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }; chart.Show(); Flush();
            CaptureFolderWindow(chart, Path.Combine(reports, "solfege-chart.png")); chart.Close();
            window.NoteNames = "letras"; Flush();
            Check(KeyboardDiagramPreview.CLabel == "C", "letters-come-back");

            // Booklet: page order for 5 pages (8 with blanks) and the PDF of composed sheets.
            var sides = BookletImposition.Sides(5);
            Check(sides.Count == 4 && sides[0] == new BookletSide(null, 0) && sides[1] == new BookletSide(1, null) && sides[2] == new BookletSide(null, 2) && sides[3] == new BookletSide(3, 4),
                "booklet-order " + string.Join(" ", sides.Select(s => $"[{s.Left},{s.Right}]")));
            Check(BookletImposition.Sides(4).SequenceEqual(new[] { new BookletSide(3, 0), new BookletSide(1, 2) }), "four-pages-on-one-sheet");
            string score = Path.Combine(work, "versos.mscz"); WriteVerseScore(score); row.ScorePath = score; Flush();
            string pages = Path.Combine(work, "paginas"); Directory.CreateDirectory(pages);
            for (int i = 1; i <= 3; i++) WhitePage(Path.Combine(pages, $"pagina-{i}.png"));
            window.bookletPages = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(Enumerable.Range(1, 3).Select(i => Path.Combine(pages, $"pagina-{i}.png")).ToArray());
            string booklet = Path.Combine(work, "livreto.pdf");
            await window.BuildBookletPdf(score, "Versos", booklet); Flush();
            Check(File.Exists(booklet) && File.ReadAllBytes(booklet).Take(5).SequenceEqual("%PDF-"u8.ToArray()) && window.Status.Contains("3 páginas em 1 folha"), "booklet-pdf-is-written " + window.Status);
            string side = Path.Combine(work, "lado.png"); Teclas.App.MainWindow.ComposeBookletSide(Path.Combine(pages, "pagina-1.png"), null, side);
            var decoded = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(side));
            Check(Math.Abs(decoded.PixelWidth - 1754) <= 1 && Math.Abs(decoded.PixelHeight - 1240) <= 1, $"booklet-sheet-is-a4-landscape ({decoded.PixelWidth}x{decoded.PixelHeight})");

            // Falling notes: melody and chords as blocks, keys lit at the right moment, notes sounding through MIDI.
            var notes = FallingNotes.FromScore(NativeMusicEditor.Open(score, CancellationToken.None));
            Check(notes.Count(n => !n.Chord) == 3 && notes.Count(n => n.Chord) == 6 && notes.First(n => !n.Chord) is { Start: 0, Length: 2, Pitch: 67 }, "falling-notes-from-the-score " + string.Join(",", notes.Select(n => $"{(n.Chord ? "c" : "m")}{n.Pitch}@{n.Start}")));
            Check(FallingNotes.KeyRange(notes) is { Low: 48, High: 71 }, "keyboard-covers-the-notes");
            var clicks = new RecordingMidiOutput();
            var falling = new FallingNotesWindow("Versos", notes, 120, () => clicks) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            falling.Show(); Flush();
            falling.Driven = true; falling.Toggle(); falling.Advance(.5); Flush();
            Check(falling.View.LitKeys.Contains(67) && clicks.Messages.Any(m => (m & 0xF0) == 0x90 && (m >> 8 & 0x7F) == 67), "melody-key-lights-and-sounds");
            falling.Advance(2.5); Flush();
            Check(falling.View.LitKeys.Contains(65) && clicks.Messages.Any(m => (m & 0xFF) == 0x80 && (m >> 8 & 0x7F) == 67) && !clicks.Messages.Any(m => (m & 0xFF) == 0x81), "keys-follow-the-music");
            falling.Advance(1); falling.Toggle(); falling.View.Show(1, true, true); Flush();
            CaptureFolderWindow(falling, Path.Combine(reports, "falling-notes.png")); falling.Close();

            // Temporary files: only the cache and unused job folders inside this session's data folder go.
            string data = Path.GetDirectoryName(settingsPath)!;
            string cache = Path.Combine(data, "audio-cache", "x"); Directory.CreateDirectory(cache); File.WriteAllBytes(Path.Combine(cache, "audio.wav"), new byte[300_000]);
            string unused = Path.Combine(data, "jobs", "velho"); Directory.CreateDirectory(unused); File.WriteAllBytes(Path.Combine(unused, "analysis.json"), new byte[1000]);
            string kept = Path.Combine(data, "jobs", "atual"); Directory.CreateDirectory(kept); File.WriteAllText(Path.Combine(kept, "analysis.json"), "{}"); row.AnalysisPath = Path.Combine(kept, "analysis.json");
            var folders = window.TemporaryFolders();
            Check(folders.All(f => Path.GetFullPath(f).StartsWith(Path.GetFullPath(data), StringComparison.OrdinalIgnoreCase)) && folders.Contains(unused) && !folders.Contains(kept), "only-unused-temporary-folders-are-listed");
            await window.RefreshTempFilesStatus();
            Check(window.TempFilesStatus.Contains("Quase nada") || window.TempFilesStatus.Contains("KB") || window.TempFilesStatus.Contains("MB"), "space-is-measured " + window.TempFilesStatus);
            window.confirmTempCleanup = _ => true;
            long freed = await window.CleanTemporaryFiles(); Flush();
            Check(freed >= 301_000 && !File.Exists(Path.Combine(cache, "audio.wav")) && !Directory.Exists(unused) && File.Exists(Path.Combine(kept, "analysis.json")) && File.Exists(midi) && File.Exists(score),
                "cleanup-keeps-analyses-in-use-and-the-music " + freed);

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            KeyboardDiagramPreview.CLabel = "C";
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
