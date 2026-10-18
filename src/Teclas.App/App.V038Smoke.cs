using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Guided review of doubtful chords and the optional second voice. No engine, MuseScore or MIDI device runs.
    private void VerifyV038Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v038-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        MainWindow? window = null;
        try
        {
            // MIDI time of a chord follows the tempo changes: ♩ = 120 for 4 quarters, then ♩ = 60.
            var document = new MidiDocument("x.mid", "", 1, 480, [], [], []) { TempoChanges = [new(0, 500000), new(4 * 480, 1000000)] };
            Check(Math.Abs(Teclas.App.MainWindow.QuarterSeconds(document, 2) - 1) < 1e-9 && Math.Abs(Teclas.App.MainWindow.QuarterSeconds(document, 6) - 4) < 1e-9, "chord-time-follows-tempo");

            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            string midi = Path.Combine(work, "cifras.mid"); File.WriteAllBytes(midi, SyntheticTwoTrackMidi());
            window.ImportForSmoke(midi); Flush();
            var row = window.Files.Single(); ((ListBox)window.FindName("Library")).SelectedItem = row; Flush();
            window.SelectPageForSmoke("library", 1); Flush();

            // Review: only doubtful chords, passage and alternatives heard, the choice applied, the count reported.
            row.Chords.Add(new ChordRow { Quarter = 0, Root = "C", Quality = "", Confidence = .95 });
            row.Chords.Add(new ChordRow { Quarter = 4, Root = "A", Quality = "m", Confidence = .4, Alternatives = "C6" });
            row.Chords.Add(new ChordRow { Quarter = 8, Root = "G", Quality = "", Confidence = .9, Alternatives = "Em7" });
            Flush();
            Check(ChordReviewWindow.Doubtful(row.Chords).Count == 2 && window.ChordReviewLabel.Contains("(2)"), "review-lists-doubtful-chords");
            var passages = new List<TimeSpan>();
            typeof(MainWindow).GetField("reviewPassagePlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new Action<MidiDocument, TimeSpan>((_, at) => passages.Add(at)));
            typeof(MainWindow).GetField("chordPreviewOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new Func<IMidiOutputTransport?>(() => null));
            ChordReviewWindow? review = null;
            window.chordReviewDialog = dialog =>
            {
                review = dialog; dialog.ShowActivated = false; dialog.Opacity = 0; dialog.Show(); Flush();
                ((Button)LogicalChildren(dialog).OfType<Button>().First(b => (b.Content as string)?.StartsWith("▶ Ouvir o trecho") == true)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                CaptureFolderWindow(dialog, Path.Combine(reports, "chord-review.png"));
                dialog.UseFirstAlternative();   // Am → C6
                dialog.Move(1);                 // keep G
                dialog.Close(); return true;
            };
            typeof(MainWindow).GetMethod("ChordReviewClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]); Flush();
            Check(passages.Count == 1 && review is { Changed: 1 } && row.Chords[1] is { Root: "C", Quality: "6" } && row.Chords[2].Root == "G", "review-applies-the-choice");
            Check(window.Status.Contains("1 trocada"), "review-reports-changes");

            // Second voice: one part, never the melody at the same time, saved and sent to the engine.
            var tracks = row.Tracks.ToArray();
            Check(tracks.Length >= 2, "fixture-has-two-parts");
            tracks[0].IsMelody = true; tracks[1].IsSecondVoice = true; Flush();
            Check(tracks[1].IsSecondVoice && !tracks[1].IsMelody && row.Tracks.Count(t => t.IsSecondVoice) == 1, "second-voice-is-one-part");
            tracks[0].IsSecondVoice = true; Flush();
            Check(tracks[0].IsSecondVoice && !tracks[0].IsMelody && !tracks[1].IsSecondVoice, "choosing-another-moves-the-second-voice");
            tracks[1].IsMelody = true; tracks[0].IsSecondVoice = true; Flush();
            var sources = (JsonArray)typeof(MainWindow).GetMethod("SecondVoiceSources", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [row])!;
            Check(sources.Count == 1 && (int?)sources[0]!["track"] == tracks[0].Source.Key.Track, "second-voice-goes-to-the-engine");
            window.SaveSessionForSmoke();
            Check(Settings.Load(settingsPath).Library.Single().SecondVoiceSources.Count == 1, "second-voice-is-saved");
            CaptureFolderWindow(window, Path.Combine(reports, "second-voice-tab.png"));
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
    }
}
