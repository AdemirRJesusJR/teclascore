using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Synthetic files and isolated settings only. No conversion process or audio device is started.
    private async void VerifyOutputDirectoryUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        await Task.Yield();
        string report = Path.GetFullPath(reportPath), directory = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(directory);
        string fixture = Path.Combine(directory, "output-preference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var trace = PresentationTraceSources.DataBindingSource;
        var priorLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning; trace.Listeners.Add(listener);
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Output preference regression: " + name); checks.Add(name, true); }
        object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, hidden)!.Invoke(window, args);
        void SetField(string name, object? value) => typeof(MainWindow).GetField(name, hidden)!.SetValue(window, value);
        void Flush() { Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window?.UpdateLayout(); }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        MainWindow Open(string path)
        {
            var opened = new MainWindow(true, path, () => throw new InvalidOperationException("Output preference QA must not open audio."))
            { Width = 1100, Height = 740, ShowInTaskbar = false, ShowActivated = false,
                Left = SystemParameters.VirtualScreenLeft, Top = SystemParameters.VirtualScreenTop };
            opened.Show(); return opened;
        }
        void Save() { ((Button)window!.FindName("SaveOutputDirectoryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush(); }
        void Draft(string value) { ((TextBox)window!.FindName("OutputDirectoryBox")).Text = value; Flush(); }
        static Rect VisibleBounds(FrameworkElement element, Window root)
        {
            var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            for (DependencyObject? current = element; current is not null && !ReferenceEquals(current, root); current = VisualTreeHelper.GetParent(current))
            {
                if (current is not FrameworkElement ancestor) continue;
                bounds.Intersect(ancestor.TransformToAncestor(root).TransformBounds(new Rect(ancestor.RenderSize)));
                if (VisualTreeHelper.GetClip(ancestor) is { } clip)
                    bounds.Intersect(ancestor.TransformToAncestor(root).TransformBounds(clip.Bounds));
            }
            return bounds;
        }
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) =>
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Output preference verification exceeded 60 seconds." }));
            window?.Close(); Shutdown(1);
        };
        try
        {
            timeout.Start();
            string preferences = Path.Combine(fixture, "settings.json"), oldOutput = Path.Combine(fixture, "existing"), nextOutput = Path.Combine(fixture, "Saída 🎹", "novas");
            Directory.CreateDirectory(oldOutput);
            string oldScore = Path.Combine(oldOutput, "preserved.mscz"), source = Path.Combine(fixture, "synthetic.mid");
            File.WriteAllBytes(oldScore, [11, 22, 33, 44]);
            byte[] track = [0, 0xc0, 0, 0, 0x90, 60, 90, 0x83, 0x60, 0x80, 60, 0, 0, 0xff, 0x2f, 0];
            File.WriteAllBytes(source, [0x4d, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xe0,
                0x4d, 0x54, 0x72, 0x6b, 0, 0, 0, (byte)track.Length, .. track]);
            string sourceHash = Hash(source), scoreHash = Hash(oldScore);
            new Settings { OutputDirectory = oldOutput, PythonPath = Environment.ProcessPath!, MuseScorePath = Environment.ProcessPath! }.Save(preferences);
            window = Open(preferences); Flush(); window.ImportForSmoke(source);
            var row = window.Files.Single(); row.Tracks[0].IsMelody = true;
            row.ScorePath = oldScore; row.Versions.Add(new SavedScoreVersion { ScorePath = oldScore });
            window.SelectPageForSmoke("settings"); Flush();
            var card = (FrameworkElement)window.FindName("OutputDirectoryCard"); card.BringIntoView(); Flush();
            Check(window.OutputDirectory == oldOutput && window.OutputDirectoryDraft == oldOutput, "existing-preference-is-displayed-without-changing-it");
            await (Task)Invoke("EnqueueConversion", row, CancellationToken.None)!;
            var queue = (ConversionQueueService)Invoke("GetConversionQueue")!;
            string RecipeDestination(ConversionQueueItem item) => JsonSerializer.Deserialize<ConversionQueueRecipe>(
                File.ReadAllText(Path.Combine(item.Workspace, "recipe.json")))!.OutputDirectory;
            Check(queue.Items.Count == 1 && RecipeDestination(queue.Items[0]) == oldOutput, "first-queued-generation-captures-existing-destination");
            Draft(nextOutput);
            Check(window.OutputDirectory == oldOutput && Settings.Load(preferences).OutputDirectory == oldOutput && !Directory.Exists(nextOutput),
                "typing-a-draft-does-not-apply-save-or-create-output");
            window.SetThemeForSmoke("light"); Flush();
            Check(Settings.Load(preferences).OutputDirectory == oldOutput, "unrelated-preference-save-does-not-commit-output-draft");
            foreach (string value in new[] { "", "relative-output", @"C:\invalid?name" })
            {
                Draft(value); Save();
                Check(window.OutputDirectoryHasError && window.OutputDirectoryStatus.Length > 0
                    && window.OutputDirectory == oldOutput && Settings.Load(preferences).OutputDirectory == oldOutput,
                    "invalid-draft-keeps-last-saved-output-" + checks.Count);
            }
            Draft(nextOutput); Save();
            Check(window.OutputDirectory == nextOutput && Settings.Load(preferences).OutputDirectory == nextOutput && !window.OutputDirectoryHasError,
                "save-button-persists-new-default");
            Check(!Directory.Exists(nextOutput), "save-button-does-not-create-output-directory");
            Check(row.ScorePath == oldScore && row.Versions.Single().ScorePath == oldScore && Hash(oldScore) == scoreHash,
                "save-keeps-existing-score-history-and-bytes");
            await (Task)Invoke("EnqueueConversion", row, CancellationToken.None)!;
            Check(queue.Items.Count == 2 && RecipeDestination(queue.Items[0]) == oldOutput && RecipeDestination(queue.Items[1]) == nextOutput,
                "next-generation-uses-new-default-and-existing-queue-keeps-original-destination");
            Check(Directory.Exists(nextOutput) && !Directory.EnumerateFileSystemEntries(nextOutput).Any(),
                "using-output-creates-directory-and-cleans-owned-write-probe");
            var pdfDialog = await (Task<SaveFileDialog?>)Invoke("CreateScoreExportDialogAsync",
                "Salvar partitura em PDF", "Documento PDF|*.pdf", "Intérprete - Música.pdf", ".pdf")!;
            Check(pdfDialog is not null && pdfDialog.FileName == Path.Combine(nextOutput, "Intérprete - Música.pdf")
                && pdfDialog.InitialDirectory == nextOutput && pdfDialog.DefaultDirectory == nextOutput,
                "pdf-export-of-old-score-uses-saved-folder-and-absolute-filename");
            Check(pdfDialog!.OverwritePrompt && pdfDialog.AddExtension && pdfDialog.DefaultExt.TrimStart('.') == "pdf",
                "pdf-export-keeps-extension-and-overwrite-confirmation");
            var zipDialog = await (Task<SaveFileDialog?>)Invoke("CreateScoreExportDialogAsync",
                "Salvar pacote", "Pacote ZIP|*.zip", "Partituras de teclado.zip", ".zip")!;
            Check(zipDialog is not null && zipDialog.FileName == Path.Combine(nextOutput, "Partituras de teclado.zip")
                && zipDialog.InitialDirectory == nextOutput && zipDialog.DefaultDirectory == nextOutput && zipDialog.OverwritePrompt,
                "musescore-zip-export-uses-saved-folder");
            string freshOutput = Path.Combine(fixture, "exportação", "ainda inexistente"); Draft(freshOutput); Save();
            Check(!Directory.Exists(freshOutput), "saving-export-default-does-not-create-it");
            var folderDialog = await (Task<OpenFolderDialog?>)Invoke("CreateBatchPdfFolderDialogAsync")!;
            Check(folderDialog is not null && folderDialog.FolderName == freshOutput && folderDialog.InitialDirectory == freshOutput
                && folderDialog.DefaultDirectory == freshOutput && Directory.Exists(freshOutput),
                "batch-pdf-prepares-and-selects-new-saved-folder");
            Check(!Directory.EnumerateFileSystemEntries(freshOutput).Any(), "preparing-dialog-leaves-no-files-or-write-probes");
            pdfDialog = await (Task<SaveFileDialog?>)Invoke("CreateScoreExportDialogAsync",
                "Salvar partitura em PDF", "Documento PDF|*.pdf", "Outro título.pdf", ".pdf")!;
            Check(pdfDialog!.FileName == Path.Combine(freshOutput, "Outro título.pdf"), "next-export-uses-latest-saved-folder");
            Check(row.ScorePath == oldScore && Hash(oldScore) == scoreHash && Hash(source) == sourceHash
                && RecipeDestination(queue.Items[0]) == oldOutput && RecipeDestination(queue.Items[1]) == nextOutput,
                "export-preparation-preserves-originals-history-and-captured-queue-destinations");
            Draft(nextOutput); Save();
            string blocked = Path.Combine(fixture, "not-a-directory"); File.WriteAllText(blocked, "preserved blocker");
            Draft(blocked); Save();
            Check(!window.OutputDirectoryHasError && File.ReadAllText(blocked) == "preserved blocker",
                "saving-preference-does-not-probe-or-alter-existing-target-file");
            var blockedDialog = await (Task<SaveFileDialog?>)Invoke("CreateScoreExportDialogAsync",
                "Salvar partitura em PDF", "Documento PDF|*.pdf", "Música.pdf", ".pdf")!;
            Check(blockedDialog is null && window.IsIdle && window.Status.Contains("indisponível", StringComparison.Ordinal),
                "unavailable-export-folder-reports-error-without-fallback-dialog");
            Check(await (Task<OpenFolderDialog?>)Invoke("CreateBatchPdfFolderDialogAsync")! is null
                && File.ReadAllText(blocked) == "preserved blocker", "batch-folder-failure-does-not-fall-back-or-overwrite");
            using (var cancellation = new CancellationTokenSource())
            {
                SetField("cancellation", cancellation);
                bool rejected = false;
                try { await (Task)Invoke("Generate", row)!; }
                catch (IOException error) { rejected = error.Message.Contains("Configurações", StringComparison.Ordinal) && error.Message.Contains("indisponível", StringComparison.Ordinal); }
                finally { SetField("cancellation", null); }
                Check(rejected && row.ScorePath == oldScore && row.Versions.Count == 1 && !row.HasPendingScoreProperties,
                    "unavailable-output-fails-before-generation-changes-existing-score-or-history");
            }
            string canceledDestination = Path.Combine(fixture, "must-not-create-on-cancel"); window.OutputDirectory = canceledDestination;
            bool canceled = false;
            try { await (Task<string>)Invoke("PrepareOutputDirectoryAsync", new CancellationToken(true))!; }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && !Directory.Exists(canceledDestination), "canceled-operation-does-not-create-output");
            window.OutputDirectory = nextOutput;
            SetField("persistencePath", Path.Combine(blocked, "settings.json")); Draft(Path.Combine(fixture, "unsaved")); Save();
            Check(window.OutputDirectoryHasError && window.OutputDirectory == nextOutput && Settings.Load(preferences).OutputDirectory == blocked,
                "settings-write-failure-rolls-back-active-output-and-keeps-draft-for-retry");
            SetField("persistencePath", preferences); Draft(nextOutput); Save();
            foreach (string theme in new[] { "graphite", "light" })
            {
                window.SetThemeForSmoke(theme); Flush(); card.BringIntoView(); Flush();
                foreach (string name in new[] { "OutputDirectoryBox", "BrowseOutputDirectoryButton", "SaveOutputDirectoryButton", "OutputDirectoryStatusText" })
                {
                    var control = (FrameworkElement)window.FindName(name); var visible = VisibleBounds(control, window);
                    Check(visible.Height >= control.ActualHeight - 1 && visible.Width >= control.ActualWidth - 1,
                        "output-controls-visible-at-minimum-window-" + theme + "-" + name);
                }
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var image = File.Create(Path.Combine(directory, "output-directory-" + theme + "-1100.png")); encoder.Save(image);
            }
            Draft(Path.Combine(fixture, "discard-on-close"));
            window.Close(); window = null; Flush();
            Check(Settings.Load(preferences).OutputDirectory == nextOutput, "closing-does-not-commit-unsaved-draft");
            window = Open(preferences); Flush();
            Check(window.OutputDirectory == nextOutput && window.OutputDirectoryDraft == nextOutput, "saved-default-restores-on-reopen");
            Check(window.Files.Single().ScorePath == oldScore && window.Files.Single().Versions.Single().ScorePath == oldScore
                && Hash(oldScore) == scoreHash && Hash(source) == sourceHash, "reopening-preserves-existing-output-and-original-midi");
            Check(bindingOutput.ToString().Length == 0, "output-preference-has-no-wpf-binding-errors");
            window.Close(); window = null;
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString(), bindings = bindingOutput.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); trace.Listeners.Remove(listener); trace.Switch.Level = priorLevel; }
    }
}
