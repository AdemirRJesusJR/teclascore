using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Cifras tab: one-by-one review of the doubtful chords, hearing the MIDI passage and each alternative.</summary>
public partial class MainWindow
{
    internal Func<ChordReviewWindow, bool?> chordReviewDialog = window => window.ShowDialog();
    internal Action<MidiDocument, TimeSpan>? reviewPassagePlayer = null;
    private MidiTrackPlayer? reviewPlayer;
    private readonly DispatcherTimer reviewStop = new() { Interval = TimeSpan.FromSeconds(4) };
    public int DoubtfulChordCount => selectedFile is { UsesNativeScoreWorkflow: false } row ? ChordReviewWindow.Doubtful(row.Chords).Count : 0;
    public string ChordReviewLabel => DoubtfulChordCount == 0 ? "Revisar cifras" : $"Revisar cifras com dúvida ({DoubtfulChordCount})";

    /// <summary>Seconds of a MIDI position given in quarters, following its tempo changes.</summary>
    public static double QuarterSeconds(MidiDocument document, double quarter)
    {
        double ppq = Math.Max(1, document.Ppq), target = quarter * ppq, seconds = 0, tick = 0, tempo = 500000;
        foreach (var change in document.TempoChanges.Where(t => t.MicrosecondsPerQuarter > 0).OrderBy(t => t.Tick))
        {
            if (change.Tick >= target) break;
            seconds += (change.Tick - tick) / ppq * tempo / 1e6; tick = change.Tick; tempo = change.MicrosecondsPerQuarter;
        }
        return seconds + (target - tick) / ppq * tempo / 1e6;
    }

    private void ChordReviewClick(object sender, RoutedEventArgs e)
    {
        if (selectedFile is not { UsesNativeScoreWorkflow: false } row || !CommitEdits()) return;
        var doubtful = ChordReviewWindow.Doubtful(row.Chords.OrderBy(c => c.Quarter));
        if (doubtful.Count == 0) { Status = "Nenhuma cifra com dúvida nesta música."; return; }
        var dialog = new ChordReviewWindow(doubtful, quarter => PlayPassage(row, quarter), (root, quality, inversion) => PreviewChord(root, quality, inversion), UseChordAlternative) { Owner = this };
        chordReviewDialog(dialog);
        StopPassage();
        Notify(nameof(DoubtfulChordCount)); Notify(nameof(ChordReviewLabel));
        if (dialog.Changed > 0) Status = $"Revisão de cifras: {dialog.Changed} trocada(s). Gere de novo para atualizar a partitura.";
    }

    /// <summary>Plays four seconds of the whole MIDI starting one second before the chord.</summary>
    private void PlayPassage(MidiRow row, double quarter)
    {
        if (row.Document is not { } document) return;
        var start = TimeSpan.FromSeconds(Math.Max(0, QuarterSeconds(document, quarter) - 1));
        if (reviewPassagePlayer is { } fake) { fake(document, start); return; }
        try
        {
            StopTrackAudition();
            reviewPlayer ??= new MidiTrackPlayer();
            reviewPlayer.Play(MidiAuditionSequence.CreateFull(document), start);
            reviewStop.Stop(); reviewStop.Tick -= StopPassageTick; reviewStop.Tick += StopPassageTick; reviewStop.Start();
        }
        catch (InvalidOperationException error) { Status = error.Message; }
    }

    private void StopPassageTick(object? sender, EventArgs e) => StopPassage();
    private void StopPassage() { reviewStop.Stop(); try { reviewPlayer?.Stop(); } catch (InvalidOperationException) { } }
}
