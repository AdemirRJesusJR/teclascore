using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Teclas.App;

public partial class App
{
    // Exercises the production import controller used by file-picker and file-drop handlers.
    private async void VerifyImport(string reportPath, string originalFirst, string originalSecond)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new Dictionary<string, object>();
        MainWindow? window = null;
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var trace = PresentationTraceSources.DataBindingSource;
        var originalLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning;
        trace.Listeners.Add(listener);
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Import regression: " + message); }
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        static Dictionary<string, string> UserSettings() => Directory.Exists(Settings.DataDirectory)
            ? Directory.EnumerateFiles(Settings.DataDirectory, "settings*").ToDictionary(path => path, Hash)
            : [];
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            var originalHashes = new Dictionary<string, string> { [originalFirst] = Hash(originalFirst), [originalSecond] = Hash(originalSecond) };
            var userSettings = UserSettings();
            string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string first = Path.Combine(directory, "01-primeiro.mid"), second = Path.Combine(directory, "02-segundo.mid"),
                third = Path.Combine(directory, "03-terceiro.mid"), fourth = Path.Combine(directory, "04-quarto.mid"),
                fifth = Path.Combine(directory, "05-quinto.mid");
            File.Copy(originalFirst, first); File.Copy(originalSecond, second);
            File.Copy(originalFirst, third); File.Copy(originalSecond, fourth); File.Copy(originalFirst, fifth);
            string unsupported = Path.Combine(directory, "leia-me.txt");
            File.WriteAllText(unsupported, "Unsupported file fixture, intentionally not a MIDI.");

            window = new MainWindow(true) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            window.Show(); Flush();
            var library = (Selector)window.FindName("Library");
            var tabs = (TabControl)window.FindName("EditorTabs");
            var import = typeof(MainWindow).GetMethod("ImportAndOpenLibraryAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("The shared import-and-open controller was not found.");
            async Task Import(params string[] paths)
            {
                await (Task)import.Invoke(window, [paths])!;
                Flush(); window.UpdateLayout();
                Require(window.IsIdle, "The import controller returned while the app was still busy.");
            }
            void Opened(string path, int count)
            {
                Require(window.CurrentPage == "library" && window.LibraryVisible == Visibility.Visible, "Successful import did not open the library.");
                Require(window.Files.Count == count && window.SelectedFile?.FullPath == path, "The wrong imported MIDI is selected.");
                Require(ReferenceEquals(library.SelectedItem, window.SelectedFile), "The editor and list have different selections.");
                Require(window.CurrentFilter == "all" && window.SearchText == "" && tabs.SelectedIndex == 0,
                    "Search, category filter or editor tab hides the imported MIDI.");
                Require(window.LibraryView.Cast<MidiRow>().Contains(window.SelectedFile!), "The selected MIDI is hidden from the library.");
                Require(window.SelectedFile?.Document is not null, "The fixture was not imported as a valid MIDI.");
            }

            window.SelectPageForSmoke("dashboard");
            await Import(first);
            Opened(first, 1);
            checks["dashboard-import-opens-and-selects-midi"] = true;

            var edited = window.SelectedFile!;
            edited.KeyFifths = -2; edited.KeyMinor = true;
            edited.Chords.Add(new() { Quarter = 0, Root = "D", Quality = "m7", Confidence = 0.9 });
            window.SelectPageForSmoke("library", 1); Flush(); window.UpdateLayout();
            var chordGrid = (DataGrid)window.FindName("ChordGrid");
            chordGrid.CurrentCell = new DataGridCellInfo(edited.Chords[0], chordGrid.Columns[1]);
            chordGrid.ScrollIntoView(edited.Chords[0], chordGrid.Columns[1]);
            Require(chordGrid.BeginEdit(), "The chord cell could not enter edit mode.");
            Flush(); chordGrid.UpdateLayout();
            var editor = Descendants(chordGrid).OfType<TextBox>().Single();
            editor.Text = "F#";
            Require(edited.Chords[0].Root == "D", "The test no longer exercises a pending chord edit.");
            await Import(second);
            Opened(second, 2);
            Require(edited.Chords.Count == 1 && edited.Chords[0].Root == "F#" && edited.Chords[0].Quality == "m7"
                && edited.KeyFifths == -2 && edited.KeyMinor, "Import lost existing musical edits.");
            checks["pending-chord-edit-committed-before-import"] = true;
            checks["existing-musical-edits-preserved"] = true;

            window.SelectPageForSmoke("settings", 2);
            window.SearchText = "__no_match__"; window.CurrentFilter = "error";
            await Import(third);
            Opened(third, 3);
            checks["new-import-clears-old-search-filter-and-selection"] = true;

            window.SelectPageForSmoke("about", 2);
            window.SearchText = "__no_match__"; window.CurrentFilter = "error";
            await Import(first, first);
            Opened(first, 3);
            Require(ReferenceEquals(window.SelectedFile, edited) && edited.Chords[0].Root == "F#", "Duplicate import replaced the existing edited row.");
            checks["duplicate-only-import-reopens-existing-midi"] = true;

            window.SelectPageForSmoke("production", 2);
            window.SearchText = "__no_match__"; window.CurrentFilter = "generated";
            await Import(first, fourth, fifth);
            Opened(fourth, 5);
            checks["batch-selects-first-new-midi-over-earlier-duplicate"] = true;

            window.SelectPageForSmoke("settings", 2);
            var selectionBefore = window.SelectedFile;
            await Import(unsupported);
            Require(window.CurrentPage == "settings" && ReferenceEquals(window.SelectedFile, selectionBefore)
                && window.Files.Count == 5 && tabs.SelectedIndex == 2, "Unsupported files changed navigation or selection.");
            checks["unsupported-file-does-not-navigate"] = true;

            await Import(first);
            string editedSourceHash = Hash(first);
            var remove = typeof(MainWindow).GetMethod("RemoveLibraryItemClick", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("The remove-from-library action was not found.");
            var undo = typeof(MainWindow).GetMethod("UndoRemoveLibraryItemClick", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("The undo-library-removal action was not found.");
            Require(window.CanRemoveLibraryItem, "An idle selected library row cannot be removed.");
            remove.Invoke(window, [window, new RoutedEventArgs()]); Flush();
            Require(window.Files.Count == 4 && !window.Files.Contains(edited) && window.SelectedFile?.FullPath == second,
                "Removal did not select the next visible MIDI.");
            Require(window.CanUndoLibraryRemoval && File.Exists(first) && Hash(first) == editedSourceHash,
                "Removing a library row deleted or altered its source file.");
            checks["remove-selects-next-midi-and-keeps-source-file"] = true;
            window.SearchText = "__no_match__"; window.CurrentFilter = "generated";
            undo.Invoke(window, [window, new RoutedEventArgs()]); Flush();
            Require(window.Files.Count == 5 && ReferenceEquals(window.Files[0], edited) && ReferenceEquals(window.SelectedFile, edited)
                && window.CurrentFilter == "all" && window.SearchText == "" && edited.Chords[0].Root == "F#"
                && edited.Chords[0].Quality == "m7" && edited.KeyFifths == -2 && !window.CanUndoLibraryRemoval,
                "Undo lost the original position, musical edits or visible selection.");
            checks["undo-restores-row-position-edits-and-visible-selection"] = true;

            var setBusy = typeof(MainWindow).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setBusy.Invoke(window, [true]);
            try
            {
                Require(!window.CanRemoveLibraryItem, "Removal remains enabled during processing.");
                remove.Invoke(window, [window, new RoutedEventArgs()]);
                Require(window.Files.Count == 5 && window.Files.Contains(edited), "Removal changed the library during processing.");
            }
            finally { setBusy.Invoke(window, [false]); }
            checks["busy-operation-blocks-library-removal"] = true;

            Require(originalHashes.All(pair => Hash(pair.Key) == pair.Value), "An original MIDI was modified.");
            var currentSettings = UserSettings();
            Require(userSettings.Count == currentSettings.Count && userSettings.All(pair => currentSettings.GetValueOrDefault(pair.Key) == pair.Value),
                "Import QA changed the user's actual preferences.");
            checks["original-midis-and-user-settings-preserved"] = true;
            listener.Flush();
            Require(string.IsNullOrWhiteSpace(bindingOutput.ToString()), "Unexpected binding diagnostic: " + bindingOutput);
            checks["binding-errors"] = 0;

            // Capture the actual library immediately after the same production flow.
            await Import(fourth);
            Opened(fourth, 5);
            var content = (FrameworkElement)window.Content;
            content.Width = 1100; content.Height = 740;
            content.Measure(new Size(1100, 740)); content.Arrange(new Rect(0, 0, 1100, 740)); content.UpdateLayout();
            Flush(); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1100, 740, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string screenshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "import-library.png");
            using (var stream = File.Create(screenshot)) encoder.Save(stream);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                status = "passed", directory, screenshot, appVersion = window.AppVersion,
                selectedFile = window.SelectedFile?.Name, checks
            }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { status = "failed", checks, error = ex.ToString(), bindingDiagnostics = bindingOutput.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally
        {
            trace.Listeners.Remove(listener); trace.Switch.Level = originalLevel;
        }
    }
}
