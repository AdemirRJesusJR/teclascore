using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Synthetic folder only; no sound is played and no file is copied, moved or changed.
    private async void VerifyMidiFolderUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "midi-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        MainWindow? window = null;
        var checks = new Dictionary<string, bool>();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "MIDI folder smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException("MIDI folder transition failed. " + window?.Status);
            Flush();
        }
        try
        {
            string folder = Path.Combine(work, "Repertório"), nested = Path.Combine(folder, "Forró"), settingsPath = Path.Combine(work, "settings.json");
            Directory.CreateDirectory(nested);
            string first = Path.Combine(folder, "Asa Branca.mid"), second = Path.Combine(nested, "Xote das Meninas.kar"), third = Path.Combine(nested, "São João.midi");
            foreach (var path in new[] { first, second, third }) await File.WriteAllBytesAsync(path, TinyMidi());
            await File.WriteAllTextAsync(Path.Combine(folder, "notas.txt"), "not a midi");
            var hidden = Path.Combine(folder, "oculto.mid"); await File.WriteAllBytesAsync(hidden, TinyMidi());
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
            var hashes = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

            var flat = MidiFolderScanner.Scan(folder, includeSubfolders: false);
            Check(flat.Entries.Select(e => e.Name).SequenceEqual(["Asa Branca.mid"]), "scanner-lists-only-visible-midis-of-the-folder");
            var deep = MidiFolderScanner.Scan(folder, includeSubfolders: true);
            Check(deep.Entries.Count == 3 && deep.Entries.Count(e => e.RelativeFolder == "Forró") == 2, "scanner-includes-subfolders-with-relative-names");
            Check(MidiFolderScanner.Matches(deep.Entries.Single(e => e.Name.StartsWith("São")), "sao joao") &&
                  MidiFolderScanner.Matches(deep.Entries.Single(e => e.Name.StartsWith("Xote")), "forro xote") &&
                  !MidiFolderScanner.Matches(deep.Entries[0], "xote"), "filter-ignores-accents-and-matches-folder-words");
            try { MidiFolderScanner.Scan("relativo", true); Check(false, "relative-folder-accepted"); } catch (ArgumentException) { Check(true, "relative-folder-rejected"); }

            new Settings { MidiFolder = folder, MidiFolderIncludeSubfolders = true, OutputDirectory = Path.Combine(work, "output") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            window.Show(); Flush();
            var header = (FrameworkElement)window.FindName("WorkspaceHeader");
            Check(window.FindName("MidiFolderButton") is Button { IsEnabled: true } folderButton && folderButton.Style == window.FindResource("PrimaryButton"),
                "header-midi-folder-is-the-primary-action");
            Check(!Descendants(header).OfType<Button>().Any(b => b.Content?.ToString()?.Contains("Importar arquivos") == true), "header-no-longer-imports-everything");
            window.ImportForSmoke(first); Flush();
            Check(window.Files.Count == 1, "one-midi-already-in-library");

            MidiFolderWindow? dialog = null;
            var field = typeof(MainWindow).GetField("midiFolderDialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var accepted = new TaskCompletionSource();
            field.SetValue(window, new Func<MidiFolderWindow, bool?>(w =>
            {
                dialog = w; w.ShowActivated = false; w.Opacity = 0; w.Show();
                var frame = new DispatcherFrame();
                w.Closed += (_, _) => frame.Continue = false;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await Wait(() => w.ScanTask is { IsCompleted: true });
                        var items = w.Items;
                        Check(items.Count == 3, "dialog-lists-folder-midis");
                        Check(items.Single(i => i.Name == "Asa Branca.mid") is { InLibrary: true, CanSelect: false, State: "Na biblioteca" }, "existing-card-is-marked-and-not-selectable");
                        items.Single(i => i.Name == "Asa Branca.mid").IsSelected = true;
                        Check(!items.Single(i => i.Name == "Asa Branca.mid").IsSelected, "existing-card-cannot-be-checked");
                        items.Single(i => i.Name.StartsWith("Xote")).IsSelected = true;
                        CaptureFolderWindow(w, Path.Combine(reports, "midi-folder-window.png"));
                        w.Accept();
                        Check(w.SelectedPaths.SequenceEqual([Path.GetFullPath(second)]), "only-checked-midis-are-returned");
                        accepted.TrySetResult();
                    }
                    catch (Exception error) { accepted.TrySetException(error); w.Close(); }
                });
                Dispatcher.PushFrame(frame);
                return null;
            }));
            typeof(MainWindow).GetMethod("OpenMidiFolderClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
            await accepted.Task;
            await Wait(() => window.IsIdle && window.Files.Count == 2);
            Check(window.Files.Select(f => f.FullPath).OrderBy(p => p).SequenceEqual(new[] { Path.GetFullPath(first), Path.GetFullPath(second) }.OrderBy(p => p)), "checked-midi-becomes-a-card-and-others-do-not");
            Check(window.Files.All(f => f.FullPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)), "cards-reference-files-in-place");
            Check(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))
                .OrderBy(p => p.Key).SequenceEqual(hashes.OrderBy(p => p.Key)), "folder-files-unchanged-and-nothing-copied");
            Check(Settings.Load(settingsPath) is { MidiFolderIncludeSubfolders: true } saved && saved.MidiFolder == folder, "folder-choice-is-remembered");
            var looseAccepted = new TaskCompletionSource();
            field.SetValue(window, new Func<MidiFolderWindow, bool?>(w =>
            {
                w.ShowActivated = false; w.Opacity = 0; w.Show();
                var frame = new DispatcherFrame();
                w.Closed += (_, _) => frame.Continue = false;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await Wait(() => w.ScanTask is { IsCompleted: true });
                        Check(Descendants(w).OfType<Button>().Any(b => b.Content?.ToString() == "Escolher arquivos…"), "folder-window-offers-loose-files");
                        w.AcceptFiles([third, third]);
                        Check(w.SelectedPaths.SequenceEqual([Path.GetFullPath(third)]), "loose-files-are-returned-once");
                        looseAccepted.TrySetResult();
                    }
                    catch (Exception error) { looseAccepted.TrySetException(error); w.Close(); }
                });
                Dispatcher.PushFrame(frame);
                return null;
            }));
            typeof(MainWindow).GetMethod("OpenMidiFolderClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
            await looseAccepted.Task;
            await Wait(() => window.IsIdle && window.Files.Count == 3);
            Check(window.Files.Any(f => string.Equals(f.FullPath, Path.GetFullPath(third), StringComparison.OrdinalIgnoreCase)), "loose-file-becomes-a-card");
            // Library search: chords and keys match exactly; text words still match names.
            var asa = window.Files.Single(f => f.FullPath == Path.GetFullPath(first));
            asa.Chords.Add(new ChordRow { Root = "F#", Quality = "dim" }); asa.Chords.Add(new ChordRow { Root = "D", Quality = "m" });
            asa.KeyFifths = -1; asa.KeyMinor = true;
            bool Finds(string text) { window.SearchText = text; Flush(); return window.LibraryView.Cast<MidiRow>().Contains(asa); }
            Check(Finds("F#dim") && Finds("Dm") && Finds("Ré menor") && Finds("asa Dm"), "search-finds-chords-keys-and-text");
            Check(!Finds("D") && !Finds("Am7") && !Finds("Sol maior"), "search-chords-and-keys-are-exact");
            Check(Finds("ASA BRANCA.MID") && Finds("asa e branca") && Finds("Asa Branca") && !Finds("asa e xote"), "search-letters-inside-phrases-are-words");
            window.SearchText = ""; Flush();
            Check(asa.MelodyWarningVisibility == (asa.MelodyWarning.Length > 0 ? Visibility.Visible : Visibility.Collapsed)
                && (asa.MelodyWarning.Length == 0 || asa.MelodyWarning.StartsWith("⚠", StringComparison.Ordinal)), "card-melody-warning-is-consistent");

            // A later visit: files changed since then are new; generated cards say so.
            string score = Path.Combine(work, "asa.mscz"); File.WriteAllText(score, "synthetic"); asa.ScorePath = score;
            var live = (Settings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(live.MidiFolderVisits.ContainsKey(Path.GetFullPath(folder)), "folder-visit-is-remembered");
            live.MidiFolderVisits[Path.GetFullPath(folder)] = DateTime.Now.AddDays(-1);
            string fourth = Path.Combine(nested, "Baião novo.mid"); File.WriteAllBytes(fourth, TinyMidi());
            var revisit = new TaskCompletionSource();
            field.SetValue(window, new Func<MidiFolderWindow, bool?>(w =>
            {
                w.ShowActivated = false; w.Opacity = 0; w.Show();
                var frame = new DispatcherFrame(); w.Closed += (_, _) => frame.Continue = false;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await Wait(() => w.ScanTask is { IsCompleted: true });
                        Check(w.Items.Single(i => i.Name == "Asa Branca.mid").State == "Partitura gerada", "folder-shows-generated-cards");
                        Check(w.Items.Single(i => i.Name == "Baião novo.mid") is { NewSinceVisit: true, State: "Novo desde a última visita" }
                            && w.Items.Where(i => i.InLibrary).All(i => !i.NewSinceVisit), "folder-marks-files-new-since-last-visit");
                        revisit.TrySetResult(); w.Close();
                    }
                    catch (Exception error) { revisit.TrySetException(error); w.Close(); }
                });
                Dispatcher.PushFrame(frame); return null;
            }));
            typeof(MainWindow).GetMethod("OpenMidiFolderClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
            await revisit.Task;
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, copiedFiles = 0, soundsPlayed = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }

    private static void CaptureFolderWindow(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        int w = (int)Math.Ceiling(content.ActualWidth + 40), h = (int)Math.Ceiling(content.ActualHeight + 40);
        var drawing = new System.Windows.Media.DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(content), null, new Rect(20, 20, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    private static byte[] TinyMidi() =>
    [
        .. "MThd"u8, 0, 0, 0, 6, 0, 0, 0, 1, 1, 224,
        .. "MTrk"u8, 0, 0, 0, 13, 0, 0x90, 60, 80, 0x83, 0x60, 0x80, 60, 0, 0, 0xff, 0x2f, 0
    ];
}
