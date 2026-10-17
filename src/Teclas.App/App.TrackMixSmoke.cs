using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    private sealed class SilentMidiOutput : IMidiOutputTransport
    {
        public int NoteOns;
        public void Send(uint message) { if ((message & 0xF0) == 0x90 && ((message >> 16) & 127) > 0) Interlocked.Increment(ref NoteOns); }
        public void Reset() { }
        public void Dispose() { }
    }

    // Fake MIDI output: nothing is sent to the Windows synthesizer.
    private async void VerifyTrackMixUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "track-mix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        MainWindow? window = null;
        var checks = new Dictionary<string, bool>();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Track mix smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException(what + " " + window?.TrackAuditionStatus);
            Flush();
        }
        try
        {
            string midi = Path.Combine(work, "duas-pistas.mid"), settingsPath = Path.Combine(work, "settings.json");
            // Track 1: piano on channel 1 from 0 s; track 2: flute on channel 2 from 0.5 s, both lasting several seconds.
            byte[] Track(params byte[][] events) { var body = events.SelectMany(e => e).Concat(new byte[] { 0, 0xff, 0x2f, 0 }).ToArray();
                return [.. "MTrk"u8, (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body]; }
            File.WriteAllBytes(midi, [.. "MThd"u8, 0, 0, 0, 6, 0, 1, 0, 2, 1, 224,
                .. Track([0, 0xC0, 0], [0, 0x90, 60, 90], [0x8f, 0x00, 0x80, 60, 0]),
                .. Track([0, 0xC1, 73], [0x83, 0x60, 0x91, 79, 90], [0x8b, 0x20, 0x81, 79, 0])]);
            new Settings { OutputDirectory = Path.Combine(work, "output") }.Save(settingsPath);
            var outputs = new List<SilentMidiOutput>();
            window = new MainWindow(true, settingsPath, () => { var output = new SilentMidiOutput(); lock (outputs) outputs.Add(output); return output; })
                { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            window.Show(); Flush();
            window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single();
            ((ListBox)window.FindName("Library")).SelectedItem = row;
            window.SelectPageForSmoke("library", 0); Flush();
            Check(row.Tracks.Count == 2 && window.CombineTrackAudition, "two-tracks-and-combine-on-by-default");
            Check(window.FindName("CombineTracksCheckBox") is CheckBox && window.FindName("PlayMarkedTracksButton") is Button { IsEnabled: false }, "mix-controls-exist");
            var click = typeof(MainWindow).GetMethod("TrackAuditionClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Listen(TrackRow track) => click.Invoke(window, [new Button { Tag = track }, new RoutedEventArgs()]);
            var player = (MidiAuditionPlayer)typeof(MainWindow).GetField("trackPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            TrackRow piano = row.Tracks[0], flute = row.Tracks[1];

            Listen(piano); await Wait(() => window.IsIdle && piano.IsAuditioning && player.IsPlaying, "first track did not start");
            await Task.Delay(300);
            Listen(flute); await Wait(() => window.IsIdle && flute.IsAuditioning && piano.IsAuditioning && player.Sequence?.MixedSources.Count == 2, "second track was not added");
            Check(window.TrackAuditionTitle.StartsWith("2 pistas juntas", StringComparison.Ordinal) && player.Position >= TimeSpan.FromSeconds(0.25),
                "adding-a-track-keeps-both-and-the-place-in-the-song");
            Listen(piano); await Wait(() => window.IsIdle && !piano.IsAuditioning && flute.IsAuditioning, "removing a track failed");
            Check(player.IsPlaying && player.Sequence?.MixedSources.Count == 0 && window.CanStopTrackAudition, "stop-on-one-track-removes-only-it");
            Listen(flute); await Wait(() => window.IsIdle && !flute.IsAuditioning && !window.CanStopTrackAudition, "removing the last track did not stop");

            window.CombineTrackAudition = false;
            Listen(piano); await Wait(() => window.IsIdle && piano.IsAuditioning, "solo play failed");
            Listen(flute); await Wait(() => window.IsIdle && flute.IsAuditioning && !piano.IsAuditioning, "solo mode did not replace");
            Check(true, "combine-off-keeps-the-solo-behaviour");
            Check(!Settings.Load(settingsPath).TrackAuditionCombine, "combine-choice-is-remembered");

            piano.IsMelody = true; flute.IsMelody = true; Flush();
            Check(window.CanPlayMarkedTracks, "marked-tracks-enable-the-button");
            typeof(MainWindow).GetMethod("PlayMarkedTracksClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
            await Wait(() => window.IsIdle && piano.IsAuditioning && flute.IsAuditioning && player.Sequence?.MixedSources.Count == 2, "marked tracks did not play together");
            Check(true, "play-marked-plays-the-selected-tracks-together");
            ((Button)window.FindName("TrackStopButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Flush();
            Check(!row.Tracks.Any(track => track.IsAuditioning) && !player.IsPlaying, "stop-ends-the-whole-mix");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, realSynthesizerUsed = false }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
