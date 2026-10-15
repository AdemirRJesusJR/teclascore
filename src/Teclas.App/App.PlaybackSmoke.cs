using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Teclas.App;

public partial class App
{
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, uint size);

    private async void VerifyPlayback(string reportPath, string originalMidi, string scorePath, string previewPath, string? museScorePath)
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
        { if (!condition) throw new InvalidOperationException("Playback regression: " + message); }
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        static Dictionary<string, string> UserSettings() => Directory.Exists(Settings.DataDirectory)
            ? Directory.EnumerateFiles(Settings.DataDirectory, "settings*").ToDictionary(path => path, Hash) : [];
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, TimeSpan timeout, string failure)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.Elapsed < timeout) await Task.Delay(50);
            Require(predicate(), failure + " " + window?.PlaybackStatus + " " + window?.Status);
            Flush(); window?.UpdateLayout();
        }
        try
        {
            var originals = new[] { originalMidi, scorePath, previewPath }.ToDictionary(path => path, Hash);
            var settingsBefore = UserSettings();
            string output = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
            // Keep the fixture URL below the legacy Windows Media path limit.
            string directory = Path.Combine(output, "p-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            string first = Path.Combine(directory, "01-partitura.mid"), second = Path.Combine(directory, "02-sem-partitura.mid");
            File.Copy(originalMidi, first); File.Copy(originalMidi, second);
            window = new MainWindow(true, Path.Combine(directory, "settings.json"))
            { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            if (museScorePath is not null) window.MuseScorePath = museScorePath;
            window.Show(); Flush();
            var player = (MediaPlayer)typeof(MainWindow).GetField("scorePlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            player.Volume = 0; player.IsMuted = true;
            var refresh = typeof(MainWindow).GetMethod("RefreshSelection", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // Read the actual native caption attributes back from this invisible test window.
            var handle = new WindowInteropHelper(window).Handle;
            var captionChecks = new Dictionary<string, object>();
            foreach (var theme in ThemeManager.Themes)
            {
                window.SetThemeForSmoke(theme.Id); Flush();
                Require(window.NativeCaptionThemeApplied, "The native title bar rejected its theme on this Windows installation.");
                var nativeReads = new Dictionary<string, object>();
                foreach (var (attribute, token) in new[] { (35u, "Background"), (36u, "Ink"), (34u, "Line") })
                {
                    int result = DwmGetWindowAttribute(handle, attribute, out uint actual, sizeof(uint));
                    var color = (Color)ColorConverter.ConvertFromString(theme.Palette[token]);
                    uint expected = (uint)(color.R | color.G << 8 | color.B << 16);
                    nativeReads[token] = new { hresult = $"0x{result:X8}", readable = result >= 0, actual, expected };
                    // Some DWM attributes accept Set but reject Get on Windows; report that
                    // limitation explicitly rather than claiming a successful color readback.
                    if (result >= 0) Require(actual == expected, "Native " + token + " retained another palette after switching to " + theme.Id);
                }
                int darkResult = DwmGetWindowAttribute(handle, 20, out uint dark, sizeof(uint));
                nativeReads["dark"] = new { hresult = $"0x{darkResult:X8}", readable = darkResult >= 0, actual = dark };
                if (darkResult >= 0) Require(dark == (theme.IsDark ? 1u : 0u), "Native dark-mode caption did not follow " + theme.Id);
                captionChecks[theme.Id] = new { applied = window.NativeCaptionThemeApplied, nativeReads };
            }
            checks["native-caption-theme-application"] = captionChecks;
            window.SetThemeForSmoke("graphite"); window.SetSidebarForSmoke(true);
            window.ImportForSmoke(first); window.ImportForSmoke(second);
            window.SelectPageForSmoke("library", 2); Flush(); window.UpdateLayout();
            Button Button(string name) => (Button)window.FindName(name);
            void Click(string name)
            {
                var button = Button(name);
                Require(button.IsEnabled, name + " is disabled unexpectedly.");
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            void Snapshot(string name)
            {
                // Leave room for the invisible native frame so it cannot clip the
                // requested 1100x740 client-content render at the bottom edge.
                window.Width = 1120; window.Height = 800; Flush();
                var content = (FrameworkElement)window.Content;
                content.Width = 1100; content.Height = 740;
                content.Measure(new Size(1100, 740)); content.Arrange(new Rect(0, 0, 1100, 740)); content.UpdateLayout();
                Flush(); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1100, 740, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
            }
            Require(!window.CanPlayScore && !window.CanPauseScore && !window.CanStopScore && !window.CanExportPdf
                && !Button("PlayerPlayButton").IsEnabled && !Button("PdfExportButton").IsEnabled,
                "A MIDI without a generated score enables playback or PDF export.");
            Snapshot("player-empty-compact");
            checks["no-score-disables-player-and-pdf"] = true;

            var row = window.SelectedFile!;
            row.ScorePath = scorePath; row.PreviewPath = previewPath;
            refresh.Invoke(window, null); Flush(); window.UpdateLayout();
            Require(window.CanPlayScore && window.CanExportPdf && Button("PlayerPlayButton").IsEnabled && Button("PdfExportButton").IsEnabled,
                "An existing score did not enable the actual Play and PDF buttons.");
            checks["existing-score-enables-play-and-pdf"] = true;
            // Selection changes replace the MediaPlayer to isolate delayed events.
            // Fetch and mute the current instance immediately before actual playback.
            player = (MediaPlayer)typeof(MainWindow).GetField("scorePlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            player.Volume = 0; player.IsMuted = true;
            Click("PlayerPlayButton");
            await Wait(() => !window.IsBusy, TimeSpan.FromSeconds(120), "MuseScore audio preparation did not complete.");
            await Wait(() => window.CanPauseScore, TimeSpan.FromSeconds(30), "WPF did not enter playback after opening the rendered audio.");
            await Wait(() => window.PlaybackPositionSeconds > 0.15, TimeSpan.FromSeconds(5), "Audio position did not advance while playing.");
            Require(player.Volume == 0 && player.IsMuted, "The QA player must remain muted.");
            Require(window.PlaybackDurationSeconds > 1 && window.CanSeekScore && Button("PlayerPauseButton").IsEnabled
                && Button("PlayerStopButton").IsEnabled && !Button("PlayerPlayButton").IsEnabled,
                "Playing state is not reflected in visible controls.");
            checks["real-muted-playback-opens-and-advances"] = true;
            Click("PlayerPauseButton");
            double pausedPosition = window.PlaybackPositionSeconds;
            await Task.Delay(300); Flush();
            Require(!window.CanPauseScore && window.CanPlayScore && Math.Abs(window.PlaybackPositionSeconds - pausedPosition) < 0.15,
                "Pause did not hold the playback position.");
            checks["pause-holds-position"] = true;
            var slider = (Slider)window.FindName("PlayerPositionSlider");
            double seekPosition = Math.Min(5, window.PlaybackDurationSeconds / 2);
            slider.SetCurrentValue(RangeBase.ValueProperty, seekPosition); Flush();
            await Wait(() => Math.Abs(window.PlaybackPositionSeconds - seekPosition) < 0.2, TimeSpan.FromSeconds(3), "The visible position slider did not seek.");
            checks["slider-seeks-in-paused-audio"] = true;
            Snapshot("player-score-compact");
            Click("PlayerPlayButton");
            await Wait(() => window.CanPauseScore && window.PlaybackPositionSeconds > seekPosition + 0.1,
                TimeSpan.FromSeconds(5), "Resume did not continue from the sought position.");
            Click("PlayerStopButton");
            await Wait(() => !window.CanPauseScore && window.CanPlayScore && window.PlaybackPositionSeconds < 0.1,
                TimeSpan.FromSeconds(3), "Stop did not rewind the selected score.");
            checks["resume-and-stop-reset-position"] = true;
            Click("PlayerPlayButton");
            await Wait(() => window.CanPauseScore, TimeSpan.FromSeconds(3), "A prepared score could not play again.");
            Descendants((DependencyObject)window.FindName("Library")).OfType<System.Windows.Controls.Button>().Single(b => b.Name == "RemoveLibraryCardButton" && ReferenceEquals(b.Tag, window.SelectedFile)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Flush();
            Require(window.SelectedFile?.FullPath == second && !window.CanPauseScore && !window.CanStopScore
                && !window.CanSeekScore && !window.CanPlayScore && !window.CanExportPdf && window.PlaybackDurationSeconds == 0,
                "Removing the playing row did not release playback and disable unavailable actions.");
            checks["removing-playing-midi-stops-and-releases-audio"] = true;
            Require(Directory.Exists(Path.Combine(directory, "audio-cache"))
                && Directory.EnumerateFiles(Path.Combine(directory, "audio-cache"), "*.wav", SearchOption.AllDirectories).Any(),
                "The audio cache was not written under the isolated test session.");
            checks["audio-cache-isolated-from-user-session"] = true;
            window.Close();
            Require(originals.All(pair => Hash(pair.Key) == pair.Value), "The original MIDI, score or preview was changed.");
            var settingsAfter = UserSettings();
            Require(settingsBefore.Count == settingsAfter.Count && settingsBefore.All(pair => settingsAfter.GetValueOrDefault(pair.Key) == pair.Value),
                "Player QA changed the user's actual preferences.");
            checks["sources-and-user-settings-preserved"] = true;
            listener.Flush();
            Require(string.IsNullOrWhiteSpace(bindingOutput.ToString()), "Unexpected binding diagnostic: " + bindingOutput);
            checks["binding-errors"] = 0;
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            { status = "passed", directory, appVersion = window.AppVersion, checks }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            { status = "failed", checks, error = ex.ToString(), bindingDiagnostics = bindingOutput.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally
        {
            trace.Listeners.Remove(listener); trace.Switch.Level = originalLevel;
        }
    }
}
