using System.Collections.Concurrent;
using Teclas.Core;

/// <summary>The watched folder: files are delivered once they finished arriving; the snapshot tells what came meanwhile.</summary>
static class MidiFolderWatchChecks
{
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "teclas-watch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var delivered = new ConcurrentQueue<string>();
        bool Delivered(string name) => delivered.Any(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
        bool WaitFor(Func<bool> condition, int milliseconds = 6000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < milliseconds) Thread.Sleep(20);
            return condition();
        }
        try
        {
            try { using var bad = new MidiFolderWatcher("relativa", false, _ => { }); check(false, "watch: relative folder accepted"); }
            catch (ArgumentException) { check(true, "watch: relative folder rejected"); }

            using (var watcher = new MidiFolderWatcher(folder, false, paths => { foreach (var p in paths) delivered.Enqueue(p); }, quietPeriod: TimeSpan.FromMilliseconds(200)))
            {
                check(watcher.Folder == Path.GetFullPath(folder) && !watcher.IncludeSubfolders, "watch: folder and subfolder choice are kept");
                // A file still open for writing is not delivered; once closed it is, a single time.
                string slow = Path.Combine(folder, "Lenta.mid");
                using (var stream = new FileStream(slow, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(new byte[10]); stream.Flush();
                    Thread.Sleep(1200);
                    check(!Delivered("Lenta.mid"), "watch: file being written is not delivered");
                    stream.Write(new byte[30]);
                }
                check(WaitFor(() => Delivered("Lenta.mid")), "watch: finished file is delivered");
                Thread.Sleep(600);
                check(delivered.Count(p => p.EndsWith("Lenta.mid", StringComparison.OrdinalIgnoreCase)) == 1, "watch: a file is delivered once");

                // Other file types, hidden files and subfolders (when not included) are ignored; renames to .mid count.
                File.WriteAllText(Path.Combine(folder, "notas.txt"), "x");
                string hidden = Path.Combine(folder, "oculta.mid"); File.WriteAllBytes(hidden, [1, 2]); File.SetAttributes(hidden, FileAttributes.Hidden);
                Directory.CreateDirectory(Path.Combine(folder, "sub")); File.WriteAllBytes(Path.Combine(folder, "sub", "dentro.mid"), [1, 2]);
                string part = Path.Combine(folder, "Baixando.mid.crdownload"); File.WriteAllBytes(part, [1, 2, 3]);
                File.Move(part, Path.Combine(folder, "Baixada.KAR"));
                check(WaitFor(() => Delivered("Baixada.KAR")), "watch: a download renamed to MIDI is delivered");
                Thread.Sleep(800);
                check(!Delivered("notas.txt") && !Delivered("oculta.mid") && !Delivered("dentro.mid") && !Delivered("Baixando.mid.crdownload"), "watch: other files, hidden files and subfolders are ignored");

                // A file found by a scan goes through the same wait; an empty file waits until it has content.
                string scanned = Path.Combine(folder, "Encontrada.midi"); File.WriteAllBytes(scanned, [9]);
                string empty = Path.Combine(folder, "Vazia.mid"); File.WriteAllBytes(empty, []);
                watcher.Enqueue(scanned); watcher.Enqueue(Path.Combine(folder, "nao-midi.txt"));
                check(WaitFor(() => Delivered("Encontrada.midi")), "watch: enqueued scan results are delivered");
                check(!Delivered("Vazia.mid") && watcher.PendingCount >= 1, "watch: an empty file keeps waiting");
                File.WriteAllBytes(empty, [1, 2, 3, 4]);
                check(WaitFor(() => Delivered("Vazia.mid")), "watch: the file is delivered once it has content");
            }
            int count = delivered.Count;
            File.WriteAllBytes(Path.Combine(folder, "Depois.mid"), [1]);
            Thread.Sleep(800);
            check(delivered.Count == count, "watch: nothing is delivered after dispose");

            using (var deep = new MidiFolderWatcher(folder, true, paths => { foreach (var p in paths) delivered.Enqueue(p); }, quietPeriod: TimeSpan.FromMilliseconds(200)))
            {
                File.WriteAllBytes(Path.Combine(folder, "sub", "Funda.mid"), [1, 2]);
                check(WaitFor(() => Delivered("Funda.mid")) && deep.IncludeSubfolders, "watch: subfolders are watched when included");
            }

            // Snapshot: what the folder had when the app closed; a scan then shows what arrived meanwhile.
            string state = Path.Combine(folder, "estado", "pasta-acompanhada.json");
            var scan = MidiFolderScanner.Scan(folder, false);
            var snapshot = new MidiFolderWatchSnapshot { Folder = folder, IncludeSubfolders = false, SavedAt = DateTimeOffset.Now, Files = scan.Entries.Select(e => e.FullPath).ToList() };
            snapshot.Save(state);
            File.WriteAllBytes(Path.Combine(folder, "Nova.mid"), [1]);
            var loaded = MidiFolderWatchSnapshot.Load(state);
            check(loaded is not null && loaded.Matches(folder + "\\", false) && !loaded.Matches(folder, true) && !loaded.Matches(Path.Combine(folder, "sub"), false), "watch: snapshot belongs to one folder and subfolder choice");
            var arrived = loaded!.NewFiles(MidiFolderScanner.Scan(folder, false));
            check(arrived.Count == 1 && arrived[0].EndsWith("Nova.mid", StringComparison.Ordinal), "watch: snapshot shows only what arrived meanwhile");
            File.WriteAllText(state, "{quebrado");
            check(MidiFolderWatchSnapshot.Load(state) is null && MidiFolderWatchSnapshot.Load(Path.Combine(folder, "nao-existe.json")) is null, "watch: missing or broken snapshot is ignored");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
