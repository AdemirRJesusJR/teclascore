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
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    private sealed class AuditionAuditOutput : IMidiOutputTransport
    {
        private readonly object gate = new();
        private readonly List<uint> messages = [];
        private readonly HashSet<(int Channel, int Pitch)> active = [];
        public int Resets { get; private set; }
        public bool Disposed { get; private set; }
        public uint[] Messages { get { lock (gate) return messages.ToArray(); } }
        public int ActiveNotes { get { lock (gate) return active.Count; } }
        public void Send(uint message)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                messages.Add(message);
                int kind = (int)(message & 0xF0), channel = (int)(message & 15), pitch = (int)((message >> 8) & 127);
                if (kind == 0x90 && (message >> 16 & 127) > 0) active.Add((channel, pitch));
                else if (kind == 0x80 || kind == 0x90) active.Remove((channel, pitch));
                else if (kind == 0xB0 && pitch is 120 or 123) active.RemoveWhere(note => note.Channel == channel);
            }
        }
        public void Reset() { lock (gate) { Resets++; active.Clear(); } }
        public void Dispose() { lock (gate) { Disposed = true; active.Clear(); } }
    }

    // UI/controller scheduling uses a silent recording transport. A separate native
    // driver check sends controls only, without NoteOn or system-volume changes.
    /// <summary>Two sources (piano on channel 1, flute on channel 2), eight seconds each at 120 BPM.</summary>
    private static byte[] SyntheticTwoTrackMidi()
    {
        static byte[] Track(int channel, int program, int basePitch)
        {
            var body = new List<byte> { 0, (byte)(0xC0 | channel), (byte)program };
            for (int i = 0; i < 16; i++)
            {
                body.AddRange([0, (byte)(0x90 | channel), (byte)(basePitch + i % 5), 90]);
                body.AddRange([0x83, 0x60, (byte)(0x80 | channel), (byte)(basePitch + i % 5), 0]);
            }
            body.AddRange([0, 0xFF, 0x2F, 0]);
            return [.. "MTrk"u8, (byte)(body.Count >> 24), (byte)(body.Count >> 16), (byte)(body.Count >> 8), (byte)body.Count, .. body];
        }
        return [.. "MThd"u8, 0, 0, 0, 6, 0, 1, 0, 2, 1, 224, .. Track(0, 0, 60), .. Track(1, 73, 72)];
    }

    private async void VerifyTrackAudition(string reportPath, string originalMidi)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new Dictionary<string, object>();
        var layoutBounds = new Dictionary<string, object>();
        var outputs = new List<AuditionAuditOutput>();
        MainWindow? window = null;
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var trace = PresentationTraceSources.DataBindingSource;
        var originalLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning; trace.Listeners.Add(listener);
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Track audition regression: " + message); }
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        static Dictionary<string, string> UserSettings() => Directory.Exists(Settings.DataDirectory)
            ? Directory.EnumerateFiles(Settings.DataDirectory, "settings*").ToDictionary(path => path, Hash) : [];
        static bool NoteOn(uint message) => (message & 0xF0) == 0x90 && (message >> 16 & 127) > 0;
        int NoteCount() { lock (outputs) return outputs.Sum(output => output.Messages.Count(NoteOn)); }
        int ResetCount() { lock (outputs) return outputs.Sum(output => output.Resets); }
        bool Silent() { lock (outputs) return outputs.All(output => output.ActiveNotes == 0); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
            Require(predicate(), message + " " + window?.TrackAuditionStatus); Flush(); window?.UpdateLayout();
        }
        try
        {
            string output = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
            string directory = Path.Combine(output, "track-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            // --synthetic: a generated two-track MIDI replaces the private reviewed MIDI, so CI can run this check.
            bool synthetic = originalMidi == "--synthetic";
            if (synthetic) { originalMidi = Path.Combine(directory, "sintetico-original.mid"); File.WriteAllBytes(originalMidi, SyntheticTwoTrackMidi()); }
            string originalHash = Hash(originalMidi);
            var settingsBefore = UserSettings();
            // Verify the actual driver accepts control traffic without sending any
            // NoteOn, or changing the system/device-wide volume setting.
            using (var native = new WinMmMidiOutputTransport())
            {
                native.Send(MidiAuditionSequence.Message(12, 1, 0));
                native.Send(MidiAuditionSequence.Message(11, 1, 7, 0));
                native.Send(MidiAuditionSequence.Message(11, 1, 11, 0));
                native.Send(MidiAuditionSequence.Message(11, 1, 64, 0));
                native.Send(MidiAuditionSequence.Message(11, 1, 123, 0));
                native.Send(MidiAuditionSequence.Message(11, 1, 120, 0));
                native.Reset();
            }
            checks["native-winmm-driver-accepts-controls-reset-close-without-note-on"] = true;
            string first = Path.Combine(directory, "01-pistas.mid"), second = Path.Combine(directory, "02-pistas.mid");
            File.Copy(originalMidi, first); File.Copy(originalMidi, second);
            string isolatedSettings = Path.Combine(directory, "settings.json");
            AuditionAuditOutput CreateOutput()
            {
                var transport = new AuditionAuditOutput();
                lock (outputs) outputs.Add(transport);
                return transport;
            }
            MainWindow CreateWindow() => new(true, isolatedSettings, CreateOutput)
                { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            window = CreateWindow();
            window.Show(); Flush();
            window.SetThemeForSmoke("graphite");
            window.ImportForSmoke(first); window.ImportForSmoke(second);
            window.SelectPageForSmoke("library", 0); Flush(); window.UpdateLayout();
            var row = window.SelectedFile!;
            Require((synthetic || row.HasReviewedProfile && row.UseReviewedProfile) && row.Tracks.Count >= 2 && row.AnalysisPath is null,
                "Use a reviewed MIDI with at least two sources to exercise listening before analysis.");
            var originalSelection = row.Tracks.Select(track => track.IsMelody).ToArray();
            var grid = (DataGrid)window.FindName("TrackGrid");
            var firstTrack = row.Tracks[0]; var secondTrack = row.Tracks[1];
            MidiAuditionPlayer Player() => (MidiAuditionPlayer)typeof(MainWindow)
                .GetField("trackPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var speedCombo = (ComboBox)window.FindName("TrackSpeedCombo");
            void SetSpeed(double speed)
            {
                Require(speedCombo.IsEnabled, "The track speed selector is unexpectedly disabled.");
                speedCombo.SetCurrentValue(Selector.SelectedValueProperty, speed); Flush();
                Require(window.TrackAuditionSpeed == speed && Player().PlaybackRate == speed,
                    "The speed selector did not update both the preference and the active MIDI player.");
            }
            Button ListenButton(TrackRow track)
            {
                grid.ScrollIntoView(track); Flush(); grid.UpdateLayout();
                return Descendants(grid).OfType<Button>().Single(button => button.Name == "TrackListenButton" && ReferenceEquals(button.Tag, track));
            }
            void Click(Button button)
            {
                Require(button.IsEnabled, button.Name + " is disabled unexpectedly.");
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            }
            void ClickNamed(string name) => Click((Button)window.FindName(name));
            async Task Listen(TrackRow track)
            {
                int notes = NoteCount();
                Click(ListenButton(track));
                await Wait(() => window.IsIdle && track.IsAuditioning && window.CanStopTrackAudition && NoteCount() > notes, "Listening did not start for the selected source.");
            }
            void Snapshot(string name, int width, int height, bool collapsed, bool scrollToBottom = false)
            {
                window.SetSidebarForSmoke(collapsed);
                window.Width = width + 20; window.Height = height + 60; Flush();
                var content = (FrameworkElement)window.Content;
                content.HorizontalAlignment = HorizontalAlignment.Left;
                content.VerticalAlignment = VerticalAlignment.Top;
                content.Width = width; content.Height = height;
                content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                Flush(); content.UpdateLayout();
                DependencyObject? ancestor = (TabControl)window.FindName("EditorTabs");
                while (ancestor is not null && ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
                var editorScroll = (ScrollViewer?)ancestor ?? throw new InvalidOperationException("Editor ScrollViewer not found.");
                if (scrollToBottom) editorScroll.ScrollToBottom(); else editorScroll.ScrollToTop();
                Flush(); content.UpdateLayout();
                var panel = (FrameworkElement)window.FindName("TrackAuditionPanel");
                var gridBounds = grid.TransformToAncestor(content).TransformBounds(new Rect(grid.RenderSize));
                var panelBounds = panel.TransformToAncestor(content).TransformBounds(new Rect(panel.RenderSize));
                Require(gridBounds.Bottom <= panelBounds.Top + 0.5,
                    $"Track grid overlaps audition controls in {name}: grid bottom {gridBounds.Bottom:0.##}, panel top {panelBounds.Top:0.##}.");
                layoutBounds[name] = new { gridBottom = gridBounds.Bottom, panelTop = panelBounds.Top,
                    panelBottom = panelBounds.Bottom, editorOffset = editorScroll.VerticalOffset };
                // A VisualBrush uses content coordinates and avoids including the
                // invisible native window's current client offset in this capture.
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                    context.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
            }

            Require(window.CanAuditionTracks && window.CanSelectTracks == synthetic && grid.IsEnabled && ListenButton(firstTrack).IsEnabled,
                "Reviewed profiles disable the listening button together with melody editing.");
            checks["reviewed-profile-allows-listening-before-analysis"] = true;
            Require(window.TrackAuditionSpeed == 1.0 && Player().PlaybackRate == 1.0 && Equals(speedCombo.SelectedValue, 1.0),
                "A new session did not start at normal speed.");
            var speedValues = speedCombo.Items.Cast<object>().Select(item => (double)item.GetType().GetProperty("Value")!.GetValue(item)!).ToArray();
            Require(speedValues.SequenceEqual(new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5 }), "The speed selector has missing, extra, or out-of-order options.");
            checks["speed-default-and-six-options"] = true;
            await Listen(firstTrack);
            Require(row.AnalysisPath is null && !File.Exists(row.ScorePath) && window.TrackAuditionVisible == Visibility.Visible,
                "Track listening depends on a generated score or failed to reveal its controls.");
            checks["track-listening-needs-no-score-or-analysis"] = true;
            int resetsBeforeSpeed = ResetCount();
            double positionBeforeSpeed = Player().Position.TotalSeconds;
            SetSpeed(0.5);
            Require(Player().IsPlaying && firstTrack.IsAuditioning && ResetCount() == resetsBeforeSpeed
                && Player().Position.TotalSeconds >= positionBeforeSpeed && Player().Position.TotalSeconds - positionBeforeSpeed < 0.2,
                "Changing speed during playback restarted or stopped the selected track.");
            checks["live-speed-change-retains-position-and-source"] = true;
            ClickNamed("TrackPauseButton");
            double pausedAt = window.TrackAuditionPositionSeconds;
            await Task.Delay(100); Flush();
            Require(window.TrackPauseLabel.Contains("Continuar") && Silent()
                && Math.Abs(window.TrackAuditionPositionSeconds - pausedAt) < 0.03, "Pause did not hold position and silence the transport.");
            checks["pause-silences-track-and-retains-position"] = true;
            int notesBeforePausedSpeed = NoteCount();
            SetSpeed(0.75);
            Require(Player().IsPaused && Silent() && NoteCount() == notesBeforePausedSpeed
                && Math.Abs(Player().Position.TotalSeconds - pausedAt) < 0.03,
                "Changing the paused playback speed emitted notes or moved the position.");
            checks["paused-speed-change-retains-position-and-silence"] = true;
            var slider = (Slider)window.FindName("TrackPositionSlider");
            double position = Math.Min(1, window.TrackAuditionDurationSeconds / 2);
            slider.SetCurrentValue(RangeBase.ValueProperty, position); Flush();
            Require(Math.Abs(window.TrackAuditionPositionSeconds - position) < 0.05 && Silent(), "Seeking a paused track changed the wrong position or emitted notes.");
            checks["paused-slider-seeks-without-emitting-notes"] = true;
            Snapshot("track-audition-compact-collapsed", 1100, 740, true);
            Snapshot("track-audition-compact-collapsed-bottom", 1100, 740, true, true);
            Snapshot("track-audition-compact-expanded", 1100, 740, false);
            Snapshot("track-audition-compact-expanded-bottom", 1100, 740, false, true);
            Snapshot("track-audition-wide", 1400, 900, false);
            checks["track-grid-and-controls-do-not-overlap"] = true;
            ClickNamed("TrackPauseButton");
            await Wait(() => window.TrackPauseLabel.Contains("Pausar"), "Pause button did not resume the track.");
            window.CombineTrackAudition = false; // this block checks the solo mode; mixing has its own smoke
            int beforeSwitchReset = ResetCount();
            await Listen(secondTrack);
            Require(!firstTrack.IsAuditioning && secondTrack.IsAuditioning && ResetCount() > beforeSwitchReset,
                "Changing the source left the previous track playing.");
            checks["switching-source-resets-previous-track"] = true;
            Require(window.TrackAuditionSpeed == 0.75 && Player().PlaybackRate == 0.75 && Equals(speedCombo.SelectedValue, 0.75),
                "Switching the source reset the selected playback speed.");
            checks["speed-retained-when-switching-source"] = true;
            Require(originalSelection.SequenceEqual(row.Tracks.Select(track => track.IsMelody)) && (synthetic || row.UseReviewedProfile),
                "Listening changed the user's melody choices or reviewed-profile preference.");
            checks["listening-does-not-change-melody-selection"] = true;
            ClickNamed("TrackStopButton");
            Require(Silent() && !window.CanStopTrackAudition && !row.Tracks.Any(track => track.IsAuditioning), "Stop did not end every track note.");
            checks["stop-clears-active-track-and-notes"] = true;

            await Listen(firstTrack);
            ((TabControl)window.FindName("EditorTabs")).SelectedIndex = 1; Flush();
            Require(!window.CanStopTrackAudition && Silent(), "Leaving the melody tab did not stop audition.");
            checks["leaving-melody-tab-stops-audition"] = true;
            window.SelectPageForSmoke("library", 0); Flush();
            await Listen(firstTrack); window.SelectPageForSmoke("dashboard"); Flush();
            Require(!window.CanStopTrackAudition && Silent(), "Leaving the library page did not stop audition.");
            checks["leaving-library-stops-audition"] = true;
            window.SelectPageForSmoke("library", 0); Flush();
            await Listen(firstTrack);
            var setBusy = typeof(MainWindow).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setBusy.Invoke(window, [true]); Flush();
            Require(!window.CanAuditionTracks && !window.CanStopTrackAudition && Silent(), "Processing did not stop and disable audition.");
            setBusy.Invoke(window, [false]); Flush();
            checks["starting-operation-stops-and-disables-audition"] = true;
            await Listen(firstTrack);
            ((Selector)window.FindName("Library")).SelectedItem = window.Files[1]; Flush();
            Require(!window.CanStopTrackAudition && Silent(), "Changing MIDI selection did not stop audition.");
            checks["changing-midi-stops-audition"] = true;
            ((Selector)window.FindName("Library")).SelectedItem = row; Flush();
            await Listen(firstTrack); Descendants((DependencyObject)window.FindName("Library")).OfType<Button>().Single(b => b.Name == "RemoveLibraryCardButton" && ReferenceEquals(b.Tag, row)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!window.CanStopTrackAudition && Silent() && window.Files.Count == 1 && File.Exists(first),
                "Removing the playing MIDI left notes active or deleted the source file.");
            checks["removing-playing-midi-stops-with-source-preserved"] = true;
            await Listen(window.SelectedFile!.Tracks[0]);
            window.Close(); Flush();
            Require(outputs.Count > 0 && outputs.All(transport => transport.Disposed && transport.ActiveNotes == 0),
                "Closing the window did not dispose and silence the MIDI transport.");
            checks["closing-app-disposes-and-silences-transport"] = true;
            Require(Settings.Load(isolatedSettings).TrackAuditionSpeed == 0.75,
                "Closing the window did not save the selected playback speed to its isolated settings.");
            window = CreateWindow(); window.Show(); Flush();
            speedCombo = (ComboBox)window.FindName("TrackSpeedCombo");
            Require(window.TrackAuditionSpeed == 0.75 && Player().PlaybackRate == 0.75 && Equals(speedCombo.SelectedValue, 0.75)
                && !window.CanStopTrackAudition && Silent() && window.Files.Count == 1,
                "Reopening did not restore playback speed and library without starting audio.");
            window.Close(); Flush();
            Require(outputs.All(transport => transport.Disposed && transport.ActiveNotes == 0), "The reopened window left its transport open.");
            checks["speed-restored-after-real-window-close-and-reopen"] = true;
            Require(Hash(originalMidi) == originalHash && Hash(first) == originalHash && Hash(second) == originalHash,
                "Audition modified a MIDI file.");
            var settingsAfter = UserSettings();
            Require(settingsBefore.Count == settingsAfter.Count && settingsBefore.All(pair => settingsAfter.GetValueOrDefault(pair.Key) == pair.Value),
                "Audition QA changed the user's real settings.");
            checks["original-midis-and-user-settings-preserved"] = true;
            listener.Flush();
            Require(string.IsNullOrWhiteSpace(bindingOutput.ToString()), "Unexpected binding errors: " + bindingOutput);
            checks["binding-errors"] = 0;
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { status = "passed", directory, appVersion = window.AppVersion, checks, layoutBounds }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { status = "failed", checks, layoutBounds, error = error.ToString(), bindingDiagnostics = bindingOutput.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { trace.Listeners.Remove(listener); trace.Switch.Level = originalLevel; }
    }
}
