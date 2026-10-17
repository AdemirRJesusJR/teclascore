using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Local synthetic MIDI and recording transports only: no user music or audible output.
    private async void VerifyLibraryPlaybackUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        await Task.Yield();
        string report = Path.GetFullPath(reportPath), directory = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(directory);
        string fixtureDirectory = Path.Combine(directory, "library-midi-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDirectory);
        var checks = new Dictionary<string, bool>();
        var outputs = new List<AuditionAuditOutput>();
        MainWindow? window = null;
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var trace = PresentationTraceSources.DataBindingSource;
        var priorLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning; trace.Listeners.Add(listener);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) =>
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Library MIDI verification exceeded 60 seconds." }));
            window?.Close(); Shutdown(1);
        };
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Library MIDI regression: " + name); checks.Add(name, true); }
        object? Invoke(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, hidden)!.Invoke(window, arguments);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, hidden)!.GetValue(window)!;
        void FieldSet(string name, object? value) => typeof(MainWindow).GetField(name, hidden)!.SetValue(window, value);
        void Flush() { Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window?.UpdateLayout(); }
        async Task Wait(Func<bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException("Library MIDI transition did not complete. " + window?.Status);
            Flush();
        }
        static IEnumerable<DependencyObject> Children(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i); yield return child;
                foreach (var descendant in Children(child)) yield return descendant;
            }
        }
        static Rect VisibleBounds(FrameworkElement element, Window window)
        {
            var visible = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
            for (DependencyObject? parent = element; parent is not null && !ReferenceEquals(parent, window); parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not FrameworkElement ancestor) continue;
                visible.Intersect(ancestor.TransformToAncestor(window).TransformBounds(new Rect(ancestor.RenderSize)));
                if (VisualTreeHelper.GetClip(ancestor) is { } clip)
                    visible.Intersect(ancestor.TransformToAncestor(window).TransformBounds(clip.Bounds));
            }
            return visible;
        }
        Button Button(MidiRow row, string name)
        {
            var list = (ListBox)window!.FindName("Library"); list.ScrollIntoView(row); Flush();
            return Children((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(row)).OfType<Button>().Single(button => button.Name == name);
        }
        void Click(MidiRow row, string name)
        {
            var button = Button(row, name);
            if (!button.IsEnabled) throw new InvalidOperationException("Disabled library MIDI control: " + name);
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        int NoteCount() => outputs.Sum(output => output.Messages.Count(message => (message & 0xf0) == 0x90 && (message >> 16 & 127) > 0));
        bool Silent() => outputs.All(output => output.ActiveNotes == 0);
        static byte[] SyntheticMidi(byte pitch)
        {
            byte[] track = [0, 0xc0, 24, 0, 0xc1, 40, 0, 0xc9, 0, 0, 0x90, pitch, 90, 0, 0x91, 67, 80,
                0, 0x99, 36, 100, 0xa5, 0x40, 0x80, pitch, 0, 0, 0x81, 67, 0, 0, 0x89, 36, 0, 0, 0xff, 0x2f, 0];
            return [0x4d, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xe0, 0x4d, 0x54, 0x72, 0x6b,
                0, 0, 0, (byte)track.Length, .. track];
        }
        try
        {
            timeout.Start();
            string firstPath = Path.Combine(fixtureDirectory, "01 - MIDI completo.mid"), secondPath = Path.Combine(fixtureDirectory, "02 - outro MIDI.mid");
            File.WriteAllBytes(firstPath, SyntheticMidi(60)); File.WriteAllBytes(secondPath, SyntheticMidi(62));
            string firstHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(firstPath)));
            window = new MainWindow(true, Path.Combine(fixtureDirectory, "settings.json"), () =>
            {
                if (outputs.Any(output => !output.Disposed)) throw new InvalidOperationException("Synthetic exclusive MIDI device already reserved.");
                var output = new AuditionAuditOutput(); outputs.Add(output); return output;
            })
                { Width = 1100, Height = 740, ShowInTaskbar = false, ShowActivated = false };
            window.Show(); Flush(); window.SetThemeForSmoke("graphite");
            window.ImportForSmoke(firstPath); window.ImportForSmoke(secondPath);
            window.SelectPageForSmoke("library"); Flush();
            var first = window.Files[0]; var second = window.Files[1];
            var library = (ListBox)window.FindName("Library");
            var splitter = (GridSplitter)window.FindName("LibraryFiltersSplitter");
            var filtersRow = (RowDefinition)window.FindName("LibraryFiltersRow");
            var cardsRow = (RowDefinition)window.FindName("LibraryCardsRow");
            void SplitKey(Key key, bool release = true)
            {
                splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                Flush();
                if (release) splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
                    { RoutedEvent = Keyboard.KeyUpEvent });
            }
            double originalFiltersHeight = filtersRow.ActualHeight;
            SplitKey(Key.Up);
            Check(filtersRow.ActualHeight < originalFiltersHeight - 9 && filtersRow.Height.IsStar && cardsRow.Height.IsStar,
                "library-splitter-keyboard-resizes-real-rows-proportionally");
            SplitKey(Key.Down);
            Check(Math.Abs(filtersRow.ActualHeight - originalFiltersHeight) < 1, "library-splitter-down-restores-height");
            for (int step = 0; step < 60; step++) SplitKey(Key.Down, false);
            Check(cardsRow.ActualHeight >= 149.5 && library.ActualHeight >= 149.5, "library-splitter-reserves-readable-card-area");
            for (int step = 0; step < 60; step++) SplitKey(Key.Up, false);
            Check(filtersRow.ActualHeight >= 95.5, "library-splitter-preserves-filter-access-at-upper-limit");
            for (int step = 0; step < 10; step++) SplitKey(Key.Down, false);
            SplitKey(Key.Down);
            double savedShare = Settings.Load(Path.Combine(fixtureDirectory,"settings.json")).LibraryFiltersShare;
            Check(Math.Abs(savedShare - filtersRow.ActualHeight / (filtersRow.ActualHeight + cardsRow.ActualHeight)) < .001,
                "library-splitter-persists-user-adjustment");
            window.Height = 900; Flush(); window.Height = 740; Flush();
            Check(filtersRow.ActualHeight >= 95.5 && cardsRow.ActualHeight >= 149.5 && splitter.ActualHeight >= 14,
                "library-splitter-remains-accessible-after-window-resize");
            var native = new MidiRow { FullPath = Path.Combine(fixtureDirectory, "native.mscz") };
            window.Files.Add(native); Flush();
            Check(first.ScorePath is null && first.AnalysisPath is null && first.Document?.Sources.Count == 3, "library-player-available-before-score-or-analysis");
            Check(NoteCount() == 0 && outputs.Count == 0 && first.LibraryPlayback.CanPlay, "import-and-selection-never-start-audio");
            Check(native.LibraryMidiPlaybackVisibility == Visibility.Collapsed, "native-score-card-hides-source-midi-controls");
            Click(first, "PlayLibraryMidiButton");
            Check(window.OperationVisibility == Visibility.Collapsed, "full-midi-preparation-never-opens-central-dialog");
            await Wait(() => !window.IsBusy && first.LibraryPlayback.IsPlaying);
            var player = Field<MidiAuditionPlayer>("libraryMidiPlayer");
            var firstTimeline = player.Sequence;
            Check(player.Sequence?.IsFullDocument == true && player.Sequence.Channels.SequenceEqual([1, 2, 10])
                && outputs.SelectMany(output => output.Messages).Where(message => (message & 0xf0) == 0x90 && (message >> 16 & 127) > 0)
                    .Select(message => (int)(message & 15) + 1).Distinct().Order().SequenceEqual([1, 2, 10]), "card-play-sends-all-instruments-and-percussion-without-generation");
            Check(!first.LibraryPlayback.CanPlay && first.LibraryPlayback.CanPause && first.LibraryPlayback.CanStop && second.LibraryPlayback.CanPlay,
                "transport-controls-belong-to-playing-card");
            Check(player.PlaybackRate == 1, "full-midi-uses-original-tempo-independently-of-study-speed");
            Click(first, "PauseLibraryMidiButton"); double paused = player.Position.TotalSeconds;
            await Task.Delay(80);
            Check(first.LibraryPlayback.IsPaused && Silent() && Math.Abs(player.Position.TotalSeconds - paused) < .01,
                "card-pause-silences-all-channels-and-retains-position");
            Click(first, "PlayLibraryMidiButton");
            Check(first.LibraryPlayback.IsPlaying && player.Position.TotalSeconds >= paused && !window.IsBusy, "card-play-resumes-paused-midi-without-preparation");
            long previousLibraryGeneration = player.PlaybackGeneration;
            Click(second, "PlayLibraryMidiButton"); await Wait(() => !window.IsBusy && second.LibraryPlayback.IsPlaying);
            Check(!first.LibraryPlayback.CanStop && ReferenceEquals(window.SelectedFile, second) && player.Sequence?.Events.Any(item => (item.Message & 0xf0) == 0x90 && (item.Message >> 8 & 127) == 62) == true,
                "playing-another-card-stops-previous-source-and-switches-owner");
            var errorEvent = typeof(MidiAuditionPlayer).GetField("PlaybackFailed", hidden)!;
            ((EventHandler<MidiPlaybackErrorEventArgs>?)errorEvent.GetValue(player))?.Invoke(player, new("Synthetic stale library error", previousLibraryGeneration)); Flush();
            Check(second.LibraryPlayback.IsPlaying && player.IsPlaying && !window.Status.Contains("Synthetic stale", StringComparison.Ordinal),
                "late-error-from-previous-library-generation-cannot-stop-current-card");
            Click(second, "StopLibraryMidiButton");
            Check(Silent() && !second.LibraryPlayback.CanStop && player.Position == TimeSpan.Zero, "card-stop-silences-and-rewinds");
            Click(first, "PlayLibraryMidiButton"); await Wait(() => !window.IsBusy && first.LibraryPlayback.IsPlaying);
            Check(ReferenceEquals(player.Sequence, firstTimeline) && window.OperationVisibility == Visibility.Collapsed,
                "returning-to-previous-card-reuses-prepared-midi-without-overlay");
            library.SelectedItem = second; Flush();
            Check(Silent() && !first.LibraryPlayback.CanStop, "selection-change-stops-library-playback");
            Click(first, "PlayLibraryMidiButton"); await Wait(() => !window.IsBusy && first.LibraryPlayback.IsPlaying);
            await (Task)Invoke("RunOperation", (Func<Task>)(() => Task.CompletedTask), null, "Teste de operação", null)!;
            Check(Silent() && !first.LibraryPlayback.CanStop, "new-operation-stops-library-playback");
            Click(first, "PlayLibraryMidiButton"); await Wait(() => !window.IsBusy && first.LibraryPlayback.IsPlaying);
            var track = first.Tracks[0];
            Invoke("TrackAuditionClick", new Button { Tag = track }, new RoutedEventArgs());
            await Wait(() => !window.IsBusy && track.IsAuditioning);
            Check(!first.LibraryPlayback.CanStop && !player.IsPlaying, "isolated-track-play-stops-full-midi");
            var trackPlayer = Field<MidiAuditionPlayer>("trackPlayer"); long previousTrackGeneration = trackPlayer.PlaybackGeneration;
            Click(first, "PlayLibraryMidiButton"); await Wait(() => !window.IsBusy && first.LibraryPlayback.IsPlaying);
            Check(!track.IsAuditioning && !Field<MidiAuditionPlayer>("trackPlayer").IsPlaying, "full-midi-play-stops-isolated-track");
            Check(outputs.Count(output => !output.Disposed) == 1, "switching-real-ui-players-releases-exclusive-device-before-opening-next");
            ((EventHandler<MidiPlaybackErrorEventArgs>?)errorEvent.GetValue(trackPlayer))?.Invoke(trackPlayer, new("Synthetic stale track error", previousTrackGeneration)); Flush();
            Check(first.LibraryPlayback.IsPlaying && player.IsPlaying && !window.Status.Contains("Synthetic stale", StringComparison.Ordinal),
                "late-isolated-track-error-cannot-stop-new-full-midi-owner");
            FieldSet("audioPlaying", true); // Exercise audio lifecycle without opening any sound file.
            Click(first, "StopLibraryMidiButton"); Click(first, "PlayLibraryMidiButton"); await Wait(() => !window.IsBusy && first.LibraryPlayback.IsPlaying);
            Check(!Field<bool>("audioPlaying"), "full-midi-play-resets-score-audio-owner");
            ((EventHandler<MidiPlaybackErrorEventArgs>?)errorEvent.GetValue(player))?.Invoke(player, new("Synthetic current output error", player.PlaybackGeneration)); Flush();
            Check(!first.LibraryPlayback.CanStop && Silent() && first.LibraryPlayback.Status.Contains("Synthetic current output error", StringComparison.Ordinal),
                "current-output-error-stops-its-owner-and-remains-visible");
            Click(first, "PlayLibraryMidiButton"); await Wait(() => !window.IsBusy && first.LibraryPlayback.IsPlaying);
            Invoke("RemoveLibraryItem", first); Flush();
            Check(!window.Files.Contains(first) && Silent() && !first.LibraryPlayback.CanStop, "removing-playing-card-stops-without-deleting-source");
            window.Files.Add(first); library.SelectedItem = first; Flush();
            var factory = Field<Func<MidiDocument, CancellationToken, Task<MidiAuditionSequence>>>("librarySequenceFactory");
            FieldSet("librarySequenceFactory", (Func<MidiDocument, CancellationToken, Task<MidiAuditionSequence>>)(async (document, token) =>
            { await Task.Delay(Timeout.Infinite, token); return MidiAuditionSequence.CreateFull(document, token); }));
            Click(first, "PlayLibraryMidiButton"); await Wait(() => first.LibraryPlayback.IsPreparing);
            int beforeCancel = NoteCount(); Invoke("RequestOperationCancellation"); await Wait(() => !window.IsBusy);
            Check(NoteCount() == beforeCancel && Silent() && !first.LibraryPlayback.CanStop, "cancelled-preparation-never-starts-stale-audio");
            var late = new TaskCompletionSource<MidiAuditionSequence>(TaskCreationOptions.RunContinuationsAsynchronously);
            FieldSet("librarySequenceFactory", (Func<MidiDocument, CancellationToken, Task<MidiAuditionSequence>>)((_, _) => late.Task));
            Click(first, "PlayLibraryMidiButton"); await Wait(() => first.LibraryPlayback.IsPreparing);
            library.SelectedItem = second; late.SetResult(MidiAuditionSequence.CreateFull(first.Document!)); await Wait(() => !window.IsBusy);
            Check(Silent() && !first.LibraryPlayback.CanStop && NoteCount() == beforeCancel, "late-preparation-after-selection-change-is-discarded");
            FieldSet("librarySequenceFactory", (Func<MidiDocument, CancellationToken, Task<MidiAuditionSequence>>)((_, _) => Task.FromException<MidiAuditionSequence>(new InvalidDataException("Synthetic preparation failure"))));
            Click(first, "PlayLibraryMidiButton"); await Wait(() => !window.IsBusy);
            Check(first.LibraryPlayback.Status.Contains("Synthetic preparation failure", StringComparison.Ordinal) && !first.HasOperationError && Silent(),
                "playback-failure-is-visible-without-marking-music-as-failed");
            FieldSet("librarySequenceFactory", factory);
            Click(first, "PlayLibraryMidiButton"); await Wait(() => !window.IsBusy && first.LibraryPlayback.IsPlaying);
            foreach (string theme in new[] { "graphite", "light" })
            {
                window.SetThemeForSmoke(theme); library.SelectedItem = first; Flush();
                var listScroll = Children(library).OfType<ScrollViewer>().First();
                listScroll.ScrollToEnd(); Flush();
                var play = Button(first, "PlayLibraryMidiButton"); var pause = Button(first, "PauseLibraryMidiButton"); var stop = Button(first, "StopLibraryMidiButton");
                var item = (FrameworkElement)library.ItemContainerGenerator.ContainerFromItem(first);
                var a = play.TransformToAncestor(item).TransformBounds(new Rect(play.RenderSize));
                var b = stop.TransformToAncestor(item).TransformBounds(new Rect(stop.RenderSize));
                Check(a.Left >= 0 && b.Right <= item.ActualWidth && a.Height >= 28 && b.Bottom <= item.ActualHeight,
                    "compact-card-controls-fit-minimum-window-" + theme);
                Check(new[] { play, pause, stop }.All(button => VisibleBounds(button, window).Height >= 27.5
                    && VisibleBounds(button, window).Width >= 27.5) && library.ActualHeight >= 150,
                    "card-controls-visible-inside-real-scroll-viewport-" + theme);
                Check(VirtualizingPanel.GetScrollUnit(library) == ScrollUnit.Pixel && pause.Focusable && stop.Focusable
                    && KeyboardNavigation.GetIsTabStop(pause) && KeyboardNavigation.GetIsTabStop(stop),
                    "card-controls-support-pixel-scroll-and-keyboard-navigation-" + theme);
                window.Activate(); stop.Focus(); Flush();
                Check(stop.IsFocused, "card-stop-receives-keyboard-focus-" + theme);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(directory, "library-midi-" + theme + "-1100.png")); png.Save(stream);
            }
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(firstPath))) == firstHash && first.ScorePath is null && first.AnalysisPath is null,
                "library-midi-original-file-and-score-workflow-remain-unchanged");
            var beforeClear=window.Files.ToArray();
            Invoke("ClearLibrary");Flush();
            Check(window.Files.Count==0 && Silent() && !first.LibraryPlayback.CanStop && window.CanUndoLibraryRemoval,
                "bulk-clear-stops-playing-midi-and-exposes-undo");
            Invoke("UndoRemoveLibraryItemClick",window,new RoutedEventArgs());Flush();
            Check(window.Files.SequenceEqual(beforeClear) && Silent(),"bulk-undo-restores-cards-without-starting-audio");
            window.Close(); Flush();
            Check(Silent() && outputs.All(output => output.Disposed), "window-close-silences-and-disposes-all-midi-transports");
            var reopened = new MainWindow(true, Path.Combine(fixtureDirectory,"settings.json")) { ShowInTaskbar = false, ShowActivated = false };
            try
            {
                var restoredFilters = (RowDefinition)reopened.FindName("LibraryFiltersRow");
                var restoredCards = (RowDefinition)reopened.FindName("LibraryCardsRow");
                Check(Math.Abs(restoredFilters.Height.Value / (restoredFilters.Height.Value + restoredCards.Height.Value) - savedShare) < .001,
                    "library-splitter-restores-proportion-on-next-launch");
            }
            finally { reopened.Close(); }
            Check(string.IsNullOrWhiteSpace(bindingOutput.ToString()), "library-player-bindings-have-no-warnings-or-errors");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
            timeout.Stop(); Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString(), bindings = bindingOutput.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            timeout.Stop(); window?.Close(); Shutdown(1);
        }
        finally { trace.Listeners.Remove(listener); trace.Switch.Level = priorLevel; }
    }
}
