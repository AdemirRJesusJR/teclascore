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
            Check(window.FindName("CombineTracksCheckBox") is CheckBox && window.FindName("PlayMarkedTracksButton") is null, "mix-controls-exist-without-play-marked-button");
            var click = typeof(MainWindow).GetMethod("TrackAuditionClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Listen(TrackRow track) => click.Invoke(window, [new Button { Tag = track }, new RoutedEventArgs()]);
            var player = (MidiAuditionPlayer)typeof(MainWindow).GetField("trackPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            TrackRow piano = row.Tracks[0], flute = row.Tracks[1];
            int Index(TrackRow track) => player.Sequence!.MixedSources.ToList().IndexOf(track.Source.Key);
            bool Audible(TrackRow track) => player.Sequence is { IsLiveMix: true } && Index(track) >= 0 && !player.MutedSources.Contains(Index(track));

            Listen(piano); await Wait(() => window.IsIdle && piano.IsAuditioning && player.IsPlaying, "first track did not start");
            Check(Audible(piano) && !Audible(flute) && player.Sequence!.MixedSources.Count == 2, "first-play-prepares-every-track-and-mutes-the-others");
            await Task.Delay(300);
            var mix = player.Sequence; long generation = player.PlaybackGeneration; var before = player.Position;
            Listen(flute);
            // Dynamic mixer: the click switches the flute on at once, with no preparation, busy state or restart.
            Check(window.IsIdle && flute.IsAuditioning && piano.IsAuditioning && Audible(flute) && Audible(piano), "second-track-joins-immediately");
            Check(ReferenceEquals(player.Sequence, mix) && player.IsPlaying && player.Position >= before && window.CanAuditionTracks, "adding-a-track-never-stops-or-restarts-the-music");
            Check(window.TrackAuditionTitle.StartsWith("2 pistas juntas", StringComparison.Ordinal) && player.Position >= TimeSpan.FromSeconds(0.25),
                "adding-a-track-keeps-both-and-the-place-in-the-song");
            Listen(piano);
            Check(window.IsIdle && !piano.IsAuditioning && flute.IsAuditioning && !Audible(piano) && Audible(flute), "stop-on-one-track-removes-only-it");
            Check(player.IsPlaying && ReferenceEquals(player.Sequence, mix) && window.CanStopTrackAudition, "removing-a-track-keeps-the-others-playing");
            Listen(piano); Check(Audible(piano) && Audible(flute) && player.IsPlaying, "a-removed-track-can-come-back-while-playing");
            Listen(piano); Listen(flute); Flush();
            Check(!flute.IsAuditioning && !window.CanStopTrackAudition && !player.IsPlaying, "removing-the-last-track-stops");

            window.CombineTrackAudition = false;
            Listen(piano); await Wait(() => window.IsIdle && piano.IsAuditioning, "solo play failed");
            Listen(flute); await Wait(() => window.IsIdle && flute.IsAuditioning && !piano.IsAuditioning && Audible(flute) && !Audible(piano), "solo mode did not replace");
            Check(true, "combine-off-keeps-the-solo-behaviour");
            Check(!Settings.Load(settingsPath).TrackAuditionCombine, "combine-choice-is-remembered");
            ((Button)window.FindName("TrackStopButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Flush();

            piano.IsMelody = true; flute.IsMelody = true; Flush();
            Check(window.CanPlayMarkedTracks && window.CanUseTrackMainButton, "marked-tracks-enable-the-main-button");
            ((Button)window.FindName("TrackPauseButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Wait(() => window.IsIdle && piano.IsAuditioning && flute.IsAuditioning && Audible(piano) && Audible(flute), "marked tracks did not play together");
            Check(true, "main-button-plays-the-marked-tracks-together");
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
