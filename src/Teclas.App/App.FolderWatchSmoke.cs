using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Pasta acompanhada: a synthetic folder; generation is a stub, so no engine, MuseScore or sound runs.
    private async void VerifyFolderWatchUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "folder-watch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        MainWindow? window = null;
        var checks = new Dictionary<string, bool>();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Folder watch smoke timed out. " + window?.Status })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name + " · " + window?.Status); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task<bool> WaitFor(Func<bool> predicate, double seconds = 10)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(25);
            Flush();
            return predicate();
        }
        // Long enough for the watcher (quiet period 300 ms, then a second look) to have delivered anything it would.
        async Task Settle() { await Task.Delay(1500); await WaitFor(() => window!.FolderWatchPendingCount == 0 && window.IsIdle, 10); }
        static byte[] Midi(byte pitch) { var bytes = TinyMidi(); bytes[24] = pitch; bytes[29] = pitch; return bytes; }
        try
        {
            string folder = Path.Combine(work, "Pasta vigiada"), settingsPath = Path.Combine(work, "settings.json");
            Directory.CreateDirectory(folder);
            string old = Path.Combine(folder, "Antiga.mid"), whileClosed = Path.Combine(folder, "Chegou fechado.mid");
            File.WriteAllBytes(old, Midi(50));
            // The state saved when the app last closed knew only the old file; another one arrived after that.
            new MidiFolderWatchSnapshot { Folder = folder, IncludeSubfolders = false, SavedAt = DateTimeOffset.Now.AddHours(-1), Files = [old] }
                .Save(Path.Combine(work, "pasta-acompanhada.json"));
            File.WriteAllBytes(whileClosed, Midi(52));
            new Settings
            {
                MidiFolder = folder, MidiFolderIncludeSubfolders = false, WatchMidiFolder = true, OutputDirectory = Path.Combine(work, "saida"),
                PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe")
            }.Save(settingsPath);

            // 1. Opening the app catches up with what arrived while it was closed, and only that.
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.folderWatchQuietPeriod = TimeSpan.FromMilliseconds(300);
            int reportWindows = 0; window.autopilotReportDialog = _ => { reportWindows++; return false; };
            window.Show(); Flush();
            string pageBefore = window.CurrentPage;
            bool Has(string path) => window.Files.Any(f => string.Equals(f.FullPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            Check(await WaitFor(() => Has(whileClosed)), "file-that-arrived-while-closed-becomes-a-card");
            Check(!Has(old), "files-already-in-the-folder-are-not-imported-in-bulk");
            Check(window.IsWatchingMidiFolder, "watching-starts-with-the-app");
            Check(window.Status.StartsWith("Pasta acompanhada:", StringComparison.Ordinal) && window.FindName("FooterOperationStatus") is TextBlock footer && footer.Text == window.Status,
                "footer-shows-a-discreet-notice");

            // 2. A file being written is read only after it is complete; then it becomes a card.
            string fresh = Path.Combine(folder, "Nova canção.mid");
            var bytes = Midi(55);
            using (var stream = new FileStream(fresh, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, 12); stream.Flush();
                await Task.Delay(1500); Flush();
                Check(!Has(fresh), "file-still-being-written-is-not-read");
                stream.Write(bytes, 12, bytes.Length - 12);
            }
            Check(await WaitFor(() => Has(fresh)), "new-file-becomes-a-card");
            var freshRow = window.Files.Single(f => string.Equals(f.FullPath, fresh, StringComparison.OrdinalIgnoreCase));
            Check(freshRow.Document is not null && freshRow.ScoreTitle.Length > 0, "card-is-read-and-organized-like-a-normal-import");
            Check(window.CurrentPage == pageBefore, "arrivals-do-not-change-the-page");
            Check(window.Status.Contains("Nova canção", StringComparison.Ordinal) || window.Status.Contains(freshRow.ScoreTitle, StringComparison.Ordinal), "notice-names-the-new-song");

            // 3. Repeats never duplicate: same content under another name, a rewrite of the same file, other file types.
            int count = window.Files.Count;
            File.Copy(fresh, Path.Combine(folder, "Nova canção - cópia.mid"));
            File.WriteAllBytes(fresh, bytes);
            File.WriteAllText(Path.Combine(folder, "letra.txt"), "não é MIDI");
            await Settle();
            Check(window.Files.Count == count, $"repeated-file-is-not-duplicated ({window.Files.Count} != {count})");
            var direct = window.ImportQuietlyAsync([fresh, whileClosed]);
            await WaitFor(() => direct.IsCompleted);
            Check(direct.Result.All(i => !i.Added) && window.Files.Count == count, "quiet-import-skips-cards-already-in-the-library");

            // 4. A download that is renamed when it finishes becomes a card too.
            string partial = Path.Combine(folder, "Baixada.mid.part"), downloaded = Path.Combine(folder, "Baixada.mid");
            File.WriteAllBytes(partial, Midi(57));
            await Task.Delay(400);
            File.Move(partial, downloaded);
            Check(await WaitFor(() => Has(downloaded)), "renamed-download-becomes-a-card");
            // A card the user removed, or a file that was there before watching, does not come back when the file is rewritten.
            window.RemoveLibraryItems([window.Files.Single(f => string.Equals(f.FullPath, downloaded, StringComparison.OrdinalIgnoreCase))]);
            File.WriteAllBytes(downloaded, Midi(58)); File.WriteAllBytes(old, Midi(51));
            await Settle();
            Check(!Has(downloaded) && !Has(old), "rewritten-known-files-do-not-come-back");

            // 5. With the autopilot option, new cards go to the autopilot, quietly (no report window).
            var generated = new List<MidiRow>();
            string score = Path.Combine(work, "stub.mscz"); WriteVerseScore(score);
            window.autopilotGenerate = (rows, _) =>
            {
                foreach (var row in rows)
                {
                    row.ScorePath = Path.Combine(work, Path.GetFileNameWithoutExtension(row.FullPath) + ".mscz"); File.Copy(score, row.ScorePath, true);
                    row.PageCount = 1; row.GeneratedAt = DateTimeOffset.Now; row.Chords.Clear(); row.Chords.Add(new ChordRow { Root = "C", Quality = "", Confidence = .95 });
                    generated.Add(row);
                }
                return Task.FromResult<IReadOnlyDictionary<MidiRow, string>>(new Dictionary<MidiRow, string>());
            };
            var settingsAutopilot = (CheckBox)window.FindName("WatchMidiFolderAutopilotCheckBox");
            Check(settingsAutopilot.IsEnabled && window.FindName("WatchMidiFolderCheckBox") is CheckBox { IsChecked: true }, "settings-page-shows-both-choices");
            window.WatchMidiFolderAutopilot = true; Flush();
            Check(settingsAutopilot.IsChecked == true && Settings.Load(settingsPath).WatchMidiFolderAutopilot, "autopilot-choice-is-saved");
            string karaoke = Path.Combine(folder, "Cantada.kar");
            File.WriteAllBytes(karaoke, KaraokeMidi());
            Check(await WaitFor(() => Has(karaoke) && window.LastFolderWatchAutopilot.Any(o => string.Equals(o.Row.FullPath, karaoke, StringComparison.OrdinalIgnoreCase)), 20),
                "new-card-goes-to-the-autopilot");
            var outcome = window.LastFolderWatchAutopilot.Single(o => string.Equals(o.Row.FullPath, karaoke, StringComparison.OrdinalIgnoreCase));
            Check(generated.Any(r => r.FullPath == outcome.Row.FullPath) && outcome.Row.ScorePath is not null && outcome.Group is "Prontas" or "Para revisar",
                $"autopilot-generates-the-clear-melody ({outcome.Group}: {outcome.Detail})");
            Check(reportWindows == 0 && window.IsIdle && window.OperationVisibility != Visibility.Visible, "autopilot-runs-without-windows-or-overlay");
            Check(!generated.Any(r => r.FullPath == freshRow.FullPath || r.FullPath == old), "autopilot-takes-only-the-new-cards");

            // 6. Turning it off stops watching; the choices are saved and older settings start off.
            window.WatchMidiFolderEnabled = false; Flush();
            Check(!window.IsWatchingMidiFolder && !settingsAutopilot.IsEnabled, "turning-off-stops-watching");
            string later = Path.Combine(folder, "Depois.mid"); File.WriteAllBytes(later, Midi(59));
            await Settle();
            Check(!Has(later), "folder-is-ignored-when-off");
            var saved = Settings.Load(settingsPath);
            Check(!saved.WatchMidiFolder && saved.WatchMidiFolderAutopilot, "choices-are-saved");
            string legacy = Path.Combine(work, "antigo.json"); File.WriteAllText(legacy, "{\"MidiFolder\":\"C:\\\\Musicas\",\"ThemeId\":\"ocean\"}");
            Check(Settings.Load(legacy) is { WatchMidiFolder: false, WatchMidiFolderAutopilot: false, MidiFolder: @"C:\Musicas" }, "older-settings-start-with-watching-off");

            // 7. The Pasta de MIDIs window turns it back on; it starts from the files the folder has now.
            MidiFolderWindow? dialog = null;
            var field = typeof(MainWindow).GetField("midiFolderDialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var closed = new TaskCompletionSource();
            field.SetValue(window, new Func<MidiFolderWindow, bool?>(w =>
            {
                dialog = w; w.ShowActivated = false; w.Opacity = 0; w.Show();
                var frame = new DispatcherFrame(); w.Closed += (_, _) => frame.Continue = false;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await WaitFor(() => w.ScanTask is { IsCompleted: true });
                        var boxes = Descendants(w).OfType<CheckBox>().ToList();
                        var watch = boxes.Single(b => b.Content as string == "Acompanhar esta pasta");
                        var autopilot = boxes.Single(b => b.Content as string == "Gerar sozinho com o Piloto automático");
                        Check(watch.IsChecked == false && autopilot.IsChecked == true && !autopilot.IsEnabled && watch.ToolTip is string { Length: > 0 },
                            "folder-window-shows-the-choices-with-autopilot-only-after-watching");
                        watch.IsChecked = true; watch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Check(autopilot.IsEnabled && w.WatchFolder && w.WatchAutopilot, "watching-enables-the-autopilot-choice");
                        CaptureFolderWindow(w, Path.Combine(reports, "folder-watch-window.png"));
                        closed.TrySetResult(); w.Close();
                    }
                    catch (Exception error) { closed.TrySetException(error); w.Close(); }
                });
                Dispatcher.PushFrame(frame); return null;
            }));
            typeof(MainWindow).GetMethod("OpenMidiFolderClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
            await closed.Task;
            Check(await WaitFor(() => window.IsWatchingMidiFolder) && Settings.Load(settingsPath) is { WatchMidiFolder: true, WatchMidiFolderAutopilot: true }, "folder-window-turns-watching-on");
            await Settle();
            Check(!Has(later), "turning-on-again-does-not-import-what-is-already-there");
            string last = Path.Combine(folder, "Última.midi"); File.WriteAllBytes(last, Midi(60));
            Check(await WaitFor(() => Has(last)), "watching-again-brings-new-files");
            await WaitFor(() => window.IsIdle && window.FolderWatchPendingCount == 0, 20);

            // 8. Closing saves what the folder had, for the next catch-up.
            window.Close(); window = null; Flush();
            var snapshot = MidiFolderWatchSnapshot.Load(Path.Combine(work, "pasta-acompanhada.json"));
            Check(snapshot is not null && snapshot.Matches(folder, false) && new[] { old, whileClosed, fresh, downloaded, later, last }.All(p => snapshot.Files.Contains(p, StringComparer.OrdinalIgnoreCase)),
                "closing-saves-the-known-files");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, copiedFiles = 0, soundsPlayed = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
