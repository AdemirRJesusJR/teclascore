using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // No renderer runs, no media URI is opened and no sound is played: preparation and the count-in device are replaced.
    private async void VerifyMetronomeUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "metronome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        MainWindow? window = null;
        var checks = new Dictionary<string, bool>();
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var trace = PresentationTraceSources.DataBindingSource; var previousLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning; trace.Listeners.Add(listener);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
        timeout.Tick += (_, _) =>
        { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Metronome smoke timed out." })); Shutdown(1); };
        timeout.Start();
        object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, hidden)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, hidden)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, hidden)!.SetValue(window, value);
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() { Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window?.UpdateLayout(); }
        async Task Wait(Func<bool> predicate)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException("Metronome transition failed. " + window?.Status);
            Flush();
        }
        Task Idle() => Wait(() => window!.IsIdle);
        var beats = Enumerable.Range(0, 16).Select(i => new MetronomeBeat(i * .5, i % 4 == 0, i % 4 + 1, 4)).ToArray();
        void LoadedState(bool playing)
        {
            Invoke("ResetPlayback", false);
            Set("playbackScoreIdentity", Invoke("SelectedScoreIdentity"));
            Set("audioLoaded", true); Set("loadedAudioPath", "synthetic-player-never-opened");
            Set("audioPlaying", playing); Set("playbackDuration", 8d); Set("loadedBeats", beats);
        }
        bool Shortcut(Key key, ModifierKeys modifiers, IInputElement? focus = null) => window!.HandleShortcut(key, modifiers, focus);
        try
        {
            string source = Path.Combine(work, "score.mscz"), page = Path.Combine(work, "page-1.png");
            string renderer = Path.Combine(work, "synthetic-renderer.exe"), settingsPath = Path.Combine(work, "settings.json");
            V018Score(source); V018Preview(page);
            await File.WriteAllTextAsync(renderer, "Synthetic identity only; never execute.");
            string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            new Settings { MuseScorePath = renderer, FollowScorePlayback = false, OutputDirectory = Path.Combine(work, "output") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            window.Show(); Flush();
            var row = new MidiRow { FullPath = source, NativeSourceSha256 = hash, ScorePath = source, PreviewPath = page, PageCount = 1, ScoreTitle = "Fixture" };
            window.Files.Add(row); ((ListBox)window.FindName("Library")).SelectedItem = row;
            window.SelectPageForSmoke("library", 2); Flush();
            Check(!window.MetronomeEnabled && !window.CountInEnabled, "metronome-and-count-in-start-disabled");
            var repeat = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("LoopRepeatToggle");
            Check(repeat.IsEnabled && window.FindName("ClearLoopButton") is null && window.FindName("LoopRepeatCheckBox") is null, "repeat-is-an-icon-toggle-without-clear-button");
            repeat.IsChecked = true; Flush(); await Task.Delay(50); Flush();
            Check(repeat.IsChecked == false && !window.LoopEnabled && window.PlaybackStatus.Contains("Marque o início A"), "repeat-without-markers-explains-and-stays-off");
            typeof(MainWindow).GetField("loopStart", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, (double?)1);
            typeof(MainWindow).GetField("loopEnd", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, (double?)3);
            repeat.IsChecked = true; Flush();
            Check(window.LoopEnabled && repeat.IsChecked == true, "repeat-turns-on-with-markers");
            repeat.IsChecked = false; Flush();
            Check(!window.LoopEnabled && !window.CanClearLoop && window.LoopStartText == "A · —", "turning-repeat-off-clears-markers");
            Check(window.FindName("MetronomeCheckBox") is CheckBox { IsEnabled: true } && window.FindName("CountInCheckBox") is CheckBox, "preview-shows-metronome-controls");

            int audioRequests = 0, beatRequests = 0, mixRequests = 0;
            double requestedSpeed = 0;
            Set("scoreAudioPreparation", new Func<ScoreStudyAudio, string, double, IProgress<string>?, CancellationToken, Task<ScoreAudio>>(
                (_, _, _, _, token) => { token.ThrowIfCancellationRequested(); audioRequests++; return Task.FromResult(new ScoreAudio("", TimeSpan.FromSeconds(8))); }));
            Set("metronomeBeatsPreparation", new Func<ScoreStudyAudio, string, double, IProgress<string>?, CancellationToken, Task<IReadOnlyList<MetronomeBeat>>>(
                (_, _, speed, _, _) => { beatRequests++; requestedSpeed = speed; return Task.FromResult<IReadOnlyList<MetronomeBeat>>(beats); }));
            Set("metronomeAudioPreparation", new Func<ScoreStudyAudio, ScoreAudio, IReadOnlyList<MetronomeBeat>, IProgress<string>?, CancellationToken, Task<ScoreAudio>>(
                (_, audio, _, _, _) => { mixRequests++; return Task.FromResult(audio); }));
            Invoke("PlayScoreClick", window, new RoutedEventArgs()); await Idle();
            Check(audioRequests == 1 && beatRequests == 0 && mixRequests == 0, "disabled-metronome-adds-no-preparation");
            window.MetronomeEnabled = true;
            Check(Settings.Load(settingsPath).ScoreMetronome, "metronome-preference-is-saved");
            window.ScorePlaybackSpeed = .5;
            Invoke("PlayScoreClick", window, new RoutedEventArgs()); await Idle();
            Check(audioRequests == 2 && beatRequests == 1 && mixRequests == 1 && requestedSpeed == .5, "metronome-prepares-beats-and-clicks-at-study-speed");
            window.MetronomeEnabled = false; window.CountInEnabled = true;
            Invoke("PlayScoreClick", window, new RoutedEventArgs()); await Idle();
            Check(audioRequests == 3 && beatRequests == 2 && mixRequests == 1, "count-in-alone-needs-beats-but-no-click-audio");
            Set("metronomeBeatsPreparation", new Func<ScoreStudyAudio, string, double, IProgress<string>?, CancellationToken, Task<IReadOnlyList<MetronomeBeat>>>(
                (_, _, _, _, _) => Task.FromException<IReadOnlyList<MetronomeBeat>>(new InvalidDataException("synthetic beat failure"))));
            Invoke("PlayScoreClick", window, new RoutedEventArgs()); await Idle();
            Check(audioRequests == 4, "beat-failure-does-not-block-audio-preparation");
            window.ScorePlaybackSpeed = 1;

            var counted = new List<byte[]>(); TaskCompletionSource? device = null; CancellationToken deviceToken = default;
            Set("countInPlayback", new Func<byte[], CancellationToken, Task>((wave, token) =>
            {
                counted.Add(wave); deviceToken = token; device = new TaskCompletionSource();
                token.Register(() => device.TrySetCanceled(token));
                return device.Task;
            }));
            LoadedState(false);
            Invoke("PlayScoreClick", window, new RoutedEventArgs()); Flush();
            Check(window.IsCountingIn && !Field<bool>("audioPlaying") && !window.CanPlayScore && window.CanStopScore && !window.CanPauseScore, "count-in-holds-the-music-and-allows-stop");
            Check(window.PlaybackStatus.StartsWith("Contagem", StringComparison.Ordinal), "count-in-is-announced");
            int expectedBytes = 44 + (int)Math.Ceiling((4 * .5 - .04) * ScoreMetronome.CountInSampleRate) * 2;
            Check(counted.Count == 1 && counted[0].Length == expectedBytes, "count-in-plays-one-bar-at-the-beat-interval");
            device!.SetResult(); await Wait(() => Field<bool>("audioPlaying"));
            Check(!window.IsCountingIn && window.PlaybackStatus == "Reproduzindo a partitura selecionada.", "music-starts-after-count-in");

            Invoke("StopPlayback", "Synthetic stop"); LoadedState(false);
            Invoke("PlayScoreClick", window, new RoutedEventArgs()); Flush();
            Check(window.IsCountingIn, "second-count-in-started");
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Flush();
            Check(!window.IsCountingIn && deviceToken.IsCancellationRequested && !Field<bool>("audioPlaying"), "escape-cancels-count-in-without-starting-music");
            await Task.Delay(50); Flush();
            Check(!Field<bool>("audioPlaying"), "cancelled-count-in-never-starts-later");

            window.CountInEnabled = false; LoadedState(false);
            Check(Shortcut(Key.Space, ModifierKeys.None) && Field<bool>("audioPlaying"), "space-plays");
            Check(Shortcut(Key.Space, ModifierKeys.None) && !Field<bool>("audioPlaying") && Field<bool>("audioLoaded"), "space-pauses");
            Check(!Shortcut(Key.Space, ModifierKeys.None, (IInputElement)window.FindName("LibrarySearchBox")), "space-types-in-text-fields");
            Check(!Shortcut(Key.Space, ModifierKeys.None, (IInputElement)window.FindName("MetronomeCheckBox")) && !Field<bool>("audioPlaying"), "space-still-activates-focused-controls");
            Set("audioPlaying", true);
            Check(Shortcut(Key.Escape, ModifierKeys.None) && !Field<bool>("audioPlaying"), "escape-stops-playback");
            Check(!Shortcut(Key.Escape, ModifierKeys.None), "escape-is-free-when-nothing-plays");
            Check(Shortcut(Key.M, ModifierKeys.Control) && window.MetronomeEnabled && !Field<bool>("audioLoaded"), "ctrl-m-toggles-metronome-and-reloads-audio");
            Check(Shortcut(Key.M, ModifierKeys.Control | ModifierKeys.Shift) && window.CountInEnabled, "ctrl-shift-m-toggles-count-in");
            Check(Settings.Load(settingsPath) is { ScoreMetronome: true, ScoreCountIn: true }, "shortcut-preferences-are-saved");
            Check(Shortcut(Key.D1, ModifierKeys.Control) && window.CurrentPage == "dashboard", "ctrl-1-opens-overview");
            Check(Shortcut(Key.D3, ModifierKeys.Control) && window.CurrentPage == "production", "ctrl-3-opens-production");
            Check(Shortcut(Key.OemComma, ModifierKeys.Control) && window.CurrentPage == "settings", "ctrl-comma-opens-settings");
            Check(Shortcut(Key.F, ModifierKeys.Control) && window.CurrentPage == "library", "ctrl-f-opens-library-search");
            Check(Shortcut(Key.D2, ModifierKeys.Control) && window.CurrentPage == "library", "ctrl-2-opens-library");
            Check(!Shortcut(Key.Left, ModifierKeys.Control, (IInputElement)window.FindName("LibrarySearchBox")), "ctrl-arrows-edit-text");
            var shortcuts = new KeyboardShortcutsWindow(Teclas.App.MainWindow.KeyboardShortcuts);
            Check(shortcuts.Content is StackPanel { Children.Count: 4 } && Teclas.App.MainWindow.KeyboardShortcuts.Count >= 12, "shortcut-list-window-builds");
            shortcuts.Close();
            Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))) == hash, "score-content-remains-unchanged");
            Flush(); Check(string.IsNullOrWhiteSpace(bindingOutput.ToString()), "no-binding-warnings");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, audioRequests, beatRequests, mixRequests, nativeProcesses = 0, mediaUrisOpened = 0, soundsPlayed = 0 },
                new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString(), bindings = bindingOutput.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); trace.Listeners.Remove(listener); trace.Switch.Level = previousLevel; }
    }
}
