using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // Revisão em sequência 2.0 (1.9.0): review points over the page (synthetic measure map), the list with chord
    // alternatives (1/2/0), P, the counter, the thumbnails, A with chord changes (regenerates), R and E. No engine runs.
    private async void VerifyV190ReviewUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v190-review-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null; ReviewSequenceWindow? review = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v1.90 review smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            string score = Path.Combine(work, "versos.mscz"); WriteVerseScore(score);
            string Copy(string name) { string target = Path.Combine(work, name + ".mscz"); File.Copy(score, target, true); return target; }
            var rows = new List<MidiRow>();
            foreach (var (name, shift) in new[] { ("Asa Branca", 1), ("Xote das Meninas", 2), ("Sabiá", 3) })
            {
                string path = Path.Combine(work, name + ".mid"); File.WriteAllBytes(path, TwoPartMidi(shift));
                window.ImportForSmoke(path); Flush(); rows.Add(window.Files.Single(r => r.FullPath == path));
            }
            string pagesDir = Path.Combine(work, "paginas"); Directory.CreateDirectory(pagesDir);
            foreach (string name in new[] { "asa-1.png", "asa-2.png", "xote-1.png", "sabia-1.png" }) WhitePage(Path.Combine(pagesDir, name));
            foreach (var (row, i) in rows.Select((r, i) => (r, i)))
            {
                row.ScorePath = Copy("score-" + i); row.PageCount = i == 0 ? 2 : 1; row.GeneratedAt = DateTimeOffset.Now;
                row.PreviewPath = Path.Combine(pagesDir, i == 0 ? "asa-1.png" : i == 1 ? "xote-1.png" : "sabia-1.png");
                foreach (var t in row.Tracks) t.IsMelody = t.Source.Key.Track == 1;
            }
            // Asa Branca: a good chord, a doubtful one with two alternatives, a doubtful one with one alternative, a rhythm point.
            rows[0].Chords.Add(new ChordRow { Root = "G", Quality = "", Confidence = .95, Quarter = 0 });
            rows[0].Chords.Add(new ChordRow { Root = "A", Quality = "m7", Confidence = .5, Quarter = 4, Alternatives = "Am, C6" });
            rows[0].Chords.Add(new ChordRow { Root = "D", Quality = "", Confidence = .4, Quarter = 9, Alternatives = "D7" });
            rows[0].ReviewPoints.Add(new ScoreReviewPoint { Quarter = 8, Measure = 3, Beat = 1, MeasureStartQuarter = 8, MeasureEndQuarter = 12, Kind = "rhythm_refined", Message = "Ataques próximos foram ajustados no compasso 3." });
            rows[1].Chords.Add(new ChordRow { Root = "C", Quality = "", Confidence = .9, Quarter = 0 });
            rows[2].Chords.Add(new ChordRow { Root = "F", Quality = "", Confidence = .6, Quarter = 0, Alternatives = "Dm7" });
            window.RefreshAllConfidence(); Flush();
            var ordered = window.ReviewSequenceRows();
            Check(ordered.Count == 3 && ordered[0] == rows[0], "sequence-starts-with-the-riskiest " + string.Join(",", ordered.Select(r => r.ScoreTitle)));

            // The engine report (bars, empty measures, warnings) and the measure map (four measures on page 1, two seconds each).
            var bars = new (double Start, double End)[] { (0, 4), (4, 8), (8, 12), (12, 16) };
            var reportOf = new ScoreValidationReport(["Armadura sugerida com confiança limitada; a tonalidade precisa de conferência musical."], 92, ["letra apertada em 2 compassos"], [2], 4) { OriginQuarters = 0, Bars = bars };
            var measures = Enumerable.Range(1, 4).Select(m => new ScoreMeasureRegion(m, new ScorePageRegion(1, .08 + .21 * (m - 1), .22, .2, .09))).ToArray();
            var timeline = Enumerable.Range(1, 4).Select(m => new ScorePlaybackOccurrence(m - 1, m, 2.0 * (m - 1), 2.0 * m, null)).ToArray();
            var map = new ScorePlaybackMap(rows[0].ScorePath!, "sha", 1.0, [rows[0].PreviewPath!], measures, [], timeline, [], true) { MeasureTimeline = timeline };
            int mapsAsked = 0, chordsChosen = 0; var regenerated = new List<MidiRow>(); var redone = new List<MidiRow>();
            var services = new ReviewSequenceServices
            {
                PrepareMap = (_, _) => { mapsAsked++; return Task.FromResult(map); },
                ChooseChord = (row, chord, alternative) => { chordsChosen++; window.ApplyChordAlternative(row, chord, alternative); },
                Regenerate = row => { regenerated.Add(row); row.HasPendingScoreProperties = false; return Task.FromResult("Gerada de novo com as cifras escolhidas (teste)."); },
                ReadReport = _ => reportOf
            };
            review = new ReviewSequenceWindow(ordered, r => { r.Approved = true; }, row => { redone.Add(row); return Task.FromResult("Refeita (teste)."); }, (_, _) => Task.FromResult(Path.Combine(work, "nenhum.wav")), services)
            { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            review.Show(); Flush(); await Task.Delay(50); Flush();

            // 1. Points: two doubtful chords, the rhythm point, the empty measure, the general warnings; counter and thumbnails.
            var points = review.Points;
            Check(points.Count == 6 && points[0].Kind == ReviewSequenceKind.Chord && points[0].Measure == 2 && points[0].Alternatives.Count == 2
                && points[1].Kind == ReviewSequenceKind.EmptyMeasures && points[1].Measure == 2
                && points[2].Kind == ReviewSequenceKind.Chord && points[2].Measure == 3 && points[3].Kind == ReviewSequenceKind.Rhythm && points[3].Measure == 3
                && points[4].Kind == ReviewSequenceKind.General && points[5].Kind == ReviewSequenceKind.General,
                "points-in-score-order " + string.Join(" | ", points.Select(p => $"{p.Kind}:{p.Measure}:{p.Title}")));
            Check(review.CounterText.StartsWith("1 de 3") && review.CounterText.Contains("min"), "counter-shows-position-and-minutes " + review.CounterText);
            Check(review.ThumbnailCount == 2, "thumbnails-of-the-next-songs " + review.ThumbnailCount);
            Check(review.HasMap && mapsAsked >= 2 && review.HighlightCount == 4, $"map-highlights-the-measures-with-points map={review.HasMap} asked={mapsAsked} highlights={review.HighlightCount}");
            Check(review.CurrentPoint == points[0] && review.PageCount == 2, "first-point-selected-and-pages-found");
            Check(points[0].Options.Select(o => o.Label).SequenceEqual(["1 · Am", "2 · C6"]) && Descendants<System.Windows.Controls.Button>(review).Count(b => b.Content is "1 · Am" or "2 · C6" or "1 · D7") == 3, "alternative-buttons-show-their-keys");

            // 2. Keys 1/2 choose a chord alternative; P moves to the next point; 0 keeps a chord; Space would start at the point.
            string before = rows[0].Chords[1].Root + rows[0].Chords[1].Quality;
            Check(review.ChooseAlternative(0) && rows[0].Chords[1].Root == "A" && rows[0].Chords[1].Quality == "m" && chordsChosen == 1 && points[0].Chosen == "Am" && rows[0].HasPendingScoreProperties,
                $"key-1-applies-the-first-alternative {before}→{rows[0].Chords[1].Root}{rows[0].Chords[1].Quality} chosen={chordsChosen}");
            Check(review.StatusText.Contains("Am7 → Am") && review.StatusText.Contains("gerada de novo"), "status-explains-the-change " + review.StatusText);
            review.NextPoint(); Flush();
            Check(review.CurrentPoint == points[1] && review.StatusText.Contains("c. 2"), "p-selects-the-next-point " + review.StatusText);
            review.NextPoint(); Flush();
            Check(review.CurrentPoint == points[2] && Math.Abs(review.StartSecondsOfCurrentPoint() - 3.6) < .01, $"space-would-start-at-the-point-measure {review.StartSecondsOfCurrentPoint()}");
            Check(!review.ChooseAlternative(1) && review.StatusText.Contains("1 alternativa"), "key-2-without-a-second-alternative-explains " + review.StatusText);
            review.KeepCurrentChord(); Flush();
            Check(points[2].Chosen == "manter" && points[2].Done && points[2].StateText == "✓ mantida", "key-0-keeps-the-chord");
            review.SelectPoint(0); Flush();
            CaptureFolderWindow(review, Path.Combine(reports, "revisao-em-sequencia.png"));
            await review.TogglePlayAsync(); Flush();
            Check(!review.IsPlaying || review.StatusText.Contains("Ouvindo"), "space-reports-the-audio-state " + review.StatusText);

            // 3. A with chord changes regenerates instead of approving; the next song is shown.
            await review.ApproveAsync(); Flush(); await Task.Delay(50); Flush();
            Check(regenerated.SequenceEqual([rows[0]]) && !rows[0].Approved && review.Approved == 0 && review.Index == 1 && review.Current == ordered[1] && review.StatusText.Contains("Gerada de novo"),
                $"approve-with-chord-changes-regenerates index={review.Index} approved={review.Approved} {review.StatusText}");
            Check(review.CounterText.StartsWith("2 de 3") && review.ThumbnailCount == 1, "counter-and-thumbnails-follow " + review.CounterText);

            // 4. A without changes approves; R redoes; E asks for the editor.
            var second = review.Current!;
            await review.ApproveAsync(); Flush();
            Check(second.Approved && review.Approved == 1 && review.Index == 2, "approve-marks-and-moves-on");
            await review.RedoAsync(); Flush();
            Check(redone.SequenceEqual([ordered[2]]) && review.StatusText == "Refeita (teste).", "r-redoes-with-the-next-melody " + review.StatusText);
            review.Edit();
            Check(review.EditRequested == ordered[2], "e-asks-for-the-editor");
            Check(Teclas.App.MainWindow.KeyboardShortcuts.Any(s => s.Keys.Contains("1 / 2") && s.Action.Contains("Revisão")), "shortcut-list-mentions-the-review-keys");

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            review = null; window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            review?.Close(); window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
