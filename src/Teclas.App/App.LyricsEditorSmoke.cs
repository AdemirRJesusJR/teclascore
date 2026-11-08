using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Editar › Letra… (no MuseScore: the saver writes the checked draft directly): correct, move and paste syllables,
    // cancel changes nothing, saving makes a new version with the old one in the History and the original untouched.
    private async void VerifyLyricsEditorUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "lyrics-editor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "lyrics editor smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Invoke(object target, string name, params object?[] args) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            string source = Path.Combine(work, "Casa azul.mscz"); LyricsScore(source);
            string originalHash = Hash(source);
            var row = new MidiRow { FullPath = source, NativeSourceSha256 = originalHash, ScorePath = source, PageCount = 1, ScoreTitle = "Casa azul", GeneratedAt = DateTimeOffset.UtcNow, Status = "Importada" };
            window.Files.Add(row); ((ListBox)window.FindName("Library")).SelectedItem = row; Flush();
            Check(window.FindName("LyricsEditorButton") is Button { IsEnabled: true } && window.CanEditLyrics, "menu-item-enabled-for-a-score");

            // 1. Cancel: nothing is saved.
            LyricsEditorWindow? opened = null;
            window.lyricsEditorDialog = dialog => { opened = dialog; return false; };
            int saves = 0;
            window.lyricsSaver = (model, edits, _, _) =>
            {
                saves++;
                string target = Path.Combine(work, "saida", $"Casa azul - letra {saves}.mscz"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                NativeMusicEditor.WriteDraft(model, edits, target);
                return Task.FromResult(new NativeScoreResult(target, [], NativeScoreDocument.Inspect(target), NativeScoreDocument.MusicalFingerprint(target)));
            };
            Invoke(window, "LyricsEditorClick", window, new RoutedEventArgs());
            for (int i = 0; i < 100 && opened is null; i++) { await Task.Delay(30); Flush(); }
            Check(opened is not null && opened.Document.Slots.Count == 6 && opened.VerseTexts.FirstOrDefault() == "Casa azul" && saves == 0 && row.ScorePath == source,
                "cancel-keeps-the-score " + string.Join(" | ", opened?.VerseTexts ?? []));

            // 2. Correct, move and paste in the window, then save a new version.
            window.lyricsEditorDialog = dialog =>
            {
                opened = dialog;
                dialog.Select(1); dialog.SyllableText = "za"; dialog.Commit();
                Check(dialog.Document.Slots[1].Text == "za" && dialog.VerseTexts[0] == "Caza azul", "window-corrects-a-syllable");
                dialog.Select(1); dialog.SyllableText = "sa"; dialog.Commit();
                dialog.Select(3); dialog.Act(i => dialog.Document.Move(i, 1), 1);
                Check(dialog.Selected == 4 && dialog.Document.Slots[4].Text == "zul" && dialog.SyllableText == "zul", "window-moves-a-syllable-and-follows-it");
                dialog.Act(i => dialog.Document.Move(i, 1), 1);
                dialog.Select(5); dialog.Act(i => dialog.Document.Move(i, 1), 1);
                Check(dialog.StatusText.Contains("nota livre"), "window-explains-a-refused-move " + dialog.StatusText);
                dialog.Select(4); dialog.PastedText = "lu a"; dialog.Paste();
                Check(dialog.Document.Slots[4].Text == "lu" && dialog.Document.Slots[5].Text == "a" && dialog.StatusText.Contains("Letra distribuída"), "window-pastes-from-the-selected-note");
                dialog.Opacity = 1; dialog.ShowInTaskbar = false; dialog.Show(); Flush();
                var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                image.Render((Visual)dialog.Content); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(reports, "lyrics-editor.png"))) png.Save(file);
                dialog.Close();
                return true;
            };
            Invoke(window, "LyricsEditorClick", window, new RoutedEventArgs());
            for (int i = 0; i < 200 && (saves == 0 || !window.IsIdle || row.ScorePath == source || !window.Status.Contains("Histórico")); i++) { await Task.Delay(30); Flush(); }
            Check(saves == 1 && row.ScorePath != source && File.Exists(row.ScorePath) && Hash(source) == originalHash, "save-makes-a-new-version-and-keeps-the-original " + window.Status);
            Check(row.Versions.Any(v => string.Equals(v.ScorePath, source, StringComparison.OrdinalIgnoreCase)) && row.HasMusicalEdits && window.Status.Contains("Histórico"),
                "previous-version-stays-in-history " + string.Join(";", row.Versions.Select(v => v.ScorePath)) + " | " + row.HasMusicalEdits + " | " + window.Status);
            var saved = LyricsEditorDocument.FromModel(NativeMusicEditor.Open(row.ScorePath!));
            Check(saved.Slots.Select(s => s.Text).SequenceEqual(new[] { "Ca", "sa", "a", "", "lu", "a" }) && saved.Slots[2].Continues == false,
                "saved-score-has-the-edited-lyrics " + string.Join(",", saved.Slots.Select(s => s.Text)));
            Check(NativeScoreDocument.Inspect(row.ScorePath!).NoteCount == NativeScoreDocument.Inspect(source).NoteCount, "notes-unchanged");
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

    /// <summary>"Ca-sa a-zul" over C C E F | G(half) rest A.</summary>
    private static void LyricsScore(string path)
    {
        static string C(int pitch, int tpc, string length, string lyric = "") => $"<Chord><durationType>{length}</durationType>{lyric}<Note><pitch>{pitch}</pitch><tpc>{tpc}</tpc></Note></Chord>";
        static string L(string text, string syllabic) => $"<Lyrics><syllabic>{syllabic}</syllabic><text>{text}</text></Lyrics>";
        string bars = "<Measure><voice><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>" + C(72, 14, "quarter", L("Ca", "begin")) + C(72, 14, "quarter", L("sa", "end"))
            + C(76, 18, "quarter", L("a", "begin")) + C(77, 13, "quarter", L("zul", "end")) + "</voice></Measure>"
            + "<Measure><voice>" + C(79, 15, "half") + "<Rest><durationType>quarter</durationType></Rest>" + C(81, 17, "quarter") + "</voice></Measure>";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false));
        writer.Write($"<museScore version='4.70'><programVersion>4.7.5</programVersion><Score><metaTag name='workTitle'>Casa azul</metaTag><Part><Staff id='1'><StaffType group='pitched'><name>stdNormal</name></StaffType></Staff><Instrument id='piano'><trackName>Piano</trackName></Instrument></Part><Staff id='1'>{bars}</Staff></Score></museScore>");
    }
}
