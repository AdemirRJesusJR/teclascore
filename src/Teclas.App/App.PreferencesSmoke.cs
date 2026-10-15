using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Teclas.App;

public partial class App
{
    // Explicit QA entry point: only writes below a fresh, user-specified report folder.
    // The production settings path is never used by any window in this routine.
    private void VerifyPreferences(string reportPath, string originalMidi)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new Dictionary<string, object>();
        var opened = new List<MainWindow>();
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var source = PresentationTraceSources.DataBindingSource;
        var originalLevel = source.Switch.Level;
        source.Switch.Level = SourceLevels.Warning;
        source.Listeners.Add(listener);
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Preferences regression: " + message); }
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        MainWindow Open(string settingsPath)
        {
            var window = new MainWindow(true, settingsPath)
            {
                Opacity = 0, ShowInTaskbar = false, ShowActivated = false
            };
            opened.Add(window);
            window.Show(); Flush(); window.UpdateLayout();
            return window;
        }
        void Close(MainWindow window)
        {
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close(); Flush();
            Require(closed, "Closing was canceled: " + window.PersistenceError);
        }

        try
        {
            string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "restart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string midi = Path.Combine(directory, "repertorio.mid");
            File.Copy(originalMidi, midi);
            string originalHash = Hash(originalMidi);
            string settingsPath = Path.Combine(directory, "settings.json");
            string chosenOutput = Path.Combine(directory, "partituras");
            var first = Open(settingsPath);
            Require(first.CurrentPage == "dashboard" && first.DashboardVisible == Visibility.Visible,
                "A new session must open on Visão geral.");
            checks["fresh-session-starts-on-dashboard"] = true;
            first.ImportForSmoke(midi);
            var row = first.Files.Single();
            Require(row.Document is not null && row.Tracks.Count > 0, "The MIDI fixture cannot exercise melody selections.");
            first.UseReviewedProfile = false;
            foreach (var track in row.Tracks) track.IsMelody = false;
            row.Tracks[0].IsMelody = true;
            row.KeyFifths = -2; row.KeyMinor = true;
            row.Chords.Add(new() { Quarter = 0, Root = "D", Quality = "m7", Confidence = 0.91 });
            row.Chords.Add(new() { Quarter = 4, Root = "G", Quality = "7", Confidence = 0.84 });
            first.PythonPath = Environment.ProcessPath!;
            first.MuseScorePath = Environment.ProcessPath!;
            first.OutputDirectory = chosenOutput;
            first.SetThemeForSmoke("graphite");
            first.SetSidebarForSmoke(true);
            first.IsPreviewControlsCompact = true;
            first.TrackAuditionSpeed = .75;
            first.ScorePlaybackSpeed = .5;
            first.FollowScorePlayback = false;
            first.Width = 1170; first.Height = 780;
            first.Left = Math.Max(SystemParameters.WorkArea.Left, 32);
            first.Top = Math.Max(SystemParameters.WorkArea.Top, 28);
            first.SearchText = "repertorio";
            first.CurrentFilter = "review";
            first.SelectPageForSmoke("library", 1);
            Flush(); first.UpdateLayout();
            var grid = (DataGrid)first.FindName("ChordGrid");
            grid.SelectedItem = row.Chords[0];
            grid.CurrentCell = new DataGridCellInfo(row.Chords[0], grid.Columns[1]);
            grid.ScrollIntoView(row.Chords[0], grid.Columns[1]);
            Require(grid.BeginEdit(), "The real chord grid could not enter edit mode.");
            Flush(); grid.UpdateLayout();
            var editor = Descendants(grid).OfType<TextBox>().Single();
            editor.Text = "F#"; // LostFocus binding remains pending until normal close commits it.
            Require(row.Chords[0].Root == "D", "The fixture does not exercise a pending edit.");
            double expectedWidth = first.Width, expectedHeight = first.Height, expectedLeft = first.Left, expectedTop = first.Top;
            Close(first);
            Require(File.Exists(settingsPath), "Closing did not create the isolated preferences file.");
            checks["native-close-saves-pending-grid-edit"] = true;

            var second = Open(settingsPath);
            Require(second.Files.Count == 1 && second.SelectedFile is not null, "The library or selection did not return after reopening.");
            var restored = second.Files.Single();
            Require(second.SelectedTheme.Id == "graphite" && second.IsSidebarCollapsed, "Theme or compact sidebar was forgotten.");
            Require(second.PythonPath == Environment.ProcessPath && second.MuseScorePath == Environment.ProcessPath
                && second.OutputDirectory == chosenOutput, "Tool paths or output folder were forgotten.");
            Require(second.CurrentPage == "dashboard" && second.DashboardVisible == Visibility.Visible
                && second.SearchText == "repertorio" && second.CurrentFilter == "review"
                && ((TabControl)second.FindName("EditorTabs")).SelectedIndex == 1,
                "Startup must show Visão geral while preserving search, filter and editor tab.");
            Require(second.IsPreviewControlsCompact && second.TrackAuditionSpeed == .75 && second.ScorePlaybackSpeed == .5
                && !second.FollowScorePlayback, "Preview layout or study preferences were changed by dashboard startup.");
            Require(!restored.UseReviewedProfile && restored.Tracks[0].IsMelody && restored.Tracks.Count(t => t.IsMelody) == 1,
                "The user's melody selection was not restored.");
            Require(restored.KeyFifths == -2 && restored.KeyMinor && restored.Chords.Count == 2
                && restored.Chords[0].Root == "F#" && restored.Chords[0].Quality == "m7" && restored.Chords[1].Quarter == 4,
                "Edited chords or key were not restored.");
            Require(Math.Abs(second.Width - expectedWidth) < 2 && Math.Abs(second.Height - expectedHeight) < 2
                && Math.Abs(second.Left - expectedLeft) < 2 && Math.Abs(second.Top - expectedTop) < 2,
                "Normal window dimensions or position were not restored.");
            checks["dashboard-startup-preserves-all-other-preferences"] = true;
            checks["melody-and-musical-edits-restored"] = true;
            checks["window-bounds-restored"] = true;
            Close(second);

            foreach (string previousPage in new[] { "dashboard", "library", "production", "settings", "about" })
            {
                var navigating = Open(settingsPath);
                navigating.SelectPageForSmoke(previousPage, 1);
                Require(navigating.CurrentPage == previousPage, "Normal in-session navigation was forced back to the dashboard.");
                Close(navigating);
                Require(Settings.Load(settingsPath).CurrentPage == previousPage, "The saved session does not exercise the requested last page.");
                var reopened = Open(settingsPath);
                Require(reopened.CurrentPage == "dashboard" && reopened.DashboardVisible == Visibility.Visible
                    && reopened.LibraryVisible == Visibility.Collapsed && reopened.ProductionVisible == Visibility.Collapsed
                    && reopened.SettingsVisible == Visibility.Collapsed && reopened.AboutVisible == Visibility.Collapsed,
                    "Visão geral must be the only startup page after closing " + previousPage + ".");
                Require(reopened.Files.Count == 1 && reopened.SelectedFile?.FullPath == midi
                    && reopened.SearchText == "repertorio" && reopened.CurrentFilter == "review"
                    && ((TabControl)reopened.FindName("EditorTabs")).SelectedIndex == 1
                    && reopened.SelectedTheme.Id == "graphite" && reopened.IsSidebarCollapsed && reopened.IsPreviewControlsCompact,
                    "Starting on the dashboard must not clear the previous work or appearance.");
                Close(reopened);
                checks["starts-on-dashboard-after-" + previousPage] = true;
            }
            checks["normal-navigation-remains-available"] = true;

            listener.Flush();
            Require(string.IsNullOrEmpty(bindingOutput.ToString()), "Roundtrip exposed data-binding errors: " + bindingOutput);

            var invalid = Open(settingsPath);
            invalid.SelectPageForSmoke("library", 1); Flush(); invalid.UpdateLayout();
            var invalidGrid = (DataGrid)invalid.FindName("ChordGrid");
            var firstChord = invalid.Files.Single().Chords[0];
            invalidGrid.CurrentCell = new DataGridCellInfo(firstChord, invalidGrid.Columns[0]);
            invalidGrid.ScrollIntoView(firstChord, invalidGrid.Columns[0]);
            Require(invalidGrid.BeginEdit(), "Could not exercise invalid pending edit.");
            Flush(); invalidGrid.UpdateLayout();
            var invalidEditor = Descendants(invalidGrid).OfType<TextBox>().Single();
            invalidEditor.Text = "not-a-number";
            string beforeInvalid = Hash(settingsPath);
            bool invalidClosed = false;
            invalid.Closed += (_, _) => invalidClosed = true;
            invalid.Close(); Flush();
            Require(!invalidClosed && Hash(settingsPath) == beforeInvalid,
                "Closing with an invalid edit must remain open without overwriting the last complete session.");
            Require(!string.IsNullOrWhiteSpace(invalid.PersistenceError), "A failed save needs an actionable visible explanation.");
            invalidGrid.CancelEdit(DataGridEditingUnit.Cell); invalidGrid.CancelEdit(DataGridEditingUnit.Row);
            // This deliberate invalid numeric edit logs a conversion warning in WPF.
            listener.Flush(); bindingOutput.GetStringBuilder().Clear();
            Close(invalid);
            checks["invalid-pending-edit-preserves-last-save"] = true;

            // A MIDI changed on disk must not inherit stale analysis or user edits from old notes.
            using (var append = new FileStream(midi, FileMode.Append)) append.WriteByte(0);
            var changed = Open(settingsPath);
            Require(changed.Files.Count == 1 && changed.Files[0].Document is not null, "A changed valid MIDI disappeared.");
            Require(changed.Files[0].AnalysisPath is null && changed.Files[0].Chords.Count == 0,
                "A changed MIDI reused its old musical edits.");
            Close(changed);
            checks["changed-midi-invalidates-stale-musical-edits"] = true;

            File.Move(midi, midi + ".removed");
            var missing = Open(settingsPath);
            Require(missing.Files.Count == 1 && missing.Files[0].Document is null,
                "A missing MIDI should remain visible as an unavailable library entry.");
            Close(missing);
            checks["missing-midi-remains-reviewable"] = true;

            Require(Hash(originalMidi) == originalHash, "The original MIDI was altered by the QA routine.");
            checks["original-midi-preserved"] = true;
            listener.Flush();
            Require(string.IsNullOrEmpty(bindingOutput.ToString()), "Native restart exposed data-binding errors: " + bindingOutput);
            checks["native-binding-errors"] = 0;
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { status = "passed", directory, checks }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { status = "failed", checks, error = ex.ToString(), bindingDiagnostics = bindingOutput.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(1);
        }
        finally
        {
            source.Listeners.Remove(listener); source.Switch.Level = originalLevel;
        }
    }
}
