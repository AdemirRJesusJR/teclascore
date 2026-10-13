using System.IO;
using System.Text.Json;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public IReadOnlyList<ScoreReviewPoint> ReviewPoints => selectedFile?.ReviewPoints ?? [];
    public bool CanReviewScore => IsIdle && selectedFile?.Document is not null && ReviewPoints.Count > 0;

    private void ParseReviewPoints(MidiRow row, JsonElement result)
    {
        row.ReviewPoints.Clear();
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("reviewPoints", out var points) || points.ValueKind != JsonValueKind.Array) return;
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object || !Number(point, "quarter", out var quarter) || quarter < 0 ||
                !point.TryGetProperty("measure", out var measureElement) || measureElement.ValueKind != JsonValueKind.Number || !measureElement.TryGetInt32(out var measure) || measure < 1 ||
                !Number(point, "beat", out var beat) || beat < 1 ||
                !Number(point, "measureStartQuarter", out var start) || start < 0 ||
                !Number(point, "measureEndQuarter", out var end) || end <= start || quarter < start || quarter >= end) continue;
            row.ReviewPoints.Add(new ScoreReviewPoint
            {
                Quarter = quarter, Measure = measure, Beat = beat, MeasureStartQuarter = start, MeasureEndQuarter = end,
                Kind = Text(point, "kind"), Message = Text(point, "message"),
                Blocking = point.TryGetProperty("blocking", out var blocking) && blocking.ValueKind == JsonValueKind.True
            });
        }
        Notify(nameof(ReviewPoints)); Notify(nameof(CanReviewScore));
        static bool Number(JsonElement item, string name, out double value)
        {
            value = 0;
            return item.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out value) && double.IsFinite(value);
        }
        static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private void RestoreReviewPoints(MidiRow row)
    {
        row.ReviewPoints.Clear();
        if (row.AnalysisPath is null || !File.Exists(row.AnalysisPath)) return;
        try { using var document = JsonDocument.Parse(File.ReadAllText(row.AnalysisPath)); ParseReviewPoints(row, document.RootElement); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void ReviewScoreClick(object sender, RoutedEventArgs e)
    {
        if (!CanReviewScore || selectedFile is not { Document: { } } row) return;
        CommitEdits();
        StopTrackAudition(); ResetPlayback();
        var window = new ReviewWindow(row, PlayReviewMeasure,
            () => StopTrackAudition("Revisão: reprodução do compasso parada."),
            () => trackPlayer?.Position ?? TimeSpan.Zero,
            () => trackPlayer?.IsPlaying == true,
            (point, track) => ReviewRegionEnd(row, point)) { Owner = this };
        try
        {
            window.ShowDialog();
            if (window.ChordToEdit is { } point)
            {
                var chord = row.Chords.Where(item => item.Quarter <= point.Quarter + .00001).OrderBy(item => item.Quarter).LastOrDefault()
                    ?? row.Chords.OrderBy(item => Math.Abs(item.Quarter - point.Quarter)).FirstOrDefault();
                EditorTabs.SelectedIndex = 1;
                if (chord is not null) { ChordGrid.SelectedItem = chord; ChordGrid.ScrollIntoView(chord); ChordGrid.Focus(); }
                Status = $"Confira a cifra do compasso {point.Measure}. Gere a partitura para aplicar qualquer alteração.";
            }
        }
        finally
        {
            StopTrackAudition();
            if (trackPlayer is not null) trackPlayer.PlaybackRate = settings.TrackAuditionSpeed;
            NotifyPlaybackState();
        }
    }

    private async Task<string> PlayReviewMeasure(MidiRow row, ScoreReviewPoint point, TrackRow track, double speed, CancellationToken token)
    {
        if (row.Document is not { } document || !row.Tracks.Contains(track)) return "A fonte MIDI não está disponível.";
        StopTrackAudition(); ResetPlayback();
        try
        {
            var sequence = await Task.Run(() => MidiAuditionSequence.Create(document, track.Source.Key, trimLeadingSilence: false)
                .Until(ReviewRegionEnd(row, point)), token);
            token.ThrowIfCancellationRequested();
            if (trackAuditionDisposed || !ReferenceEquals(row, selectedFile)) throw new OperationCanceledException();
            var start = ReviewTimeAtQuarter(document, point.MeasureStartQuarter);
            if (start >= sequence.Duration) return "Esta pista já terminou antes deste compasso. Escolha outra pista.";
            auditionMidi = row; auditionTrack = track; track.IsAuditioning = true;
            trackPlayer!.PlaybackRate = speed;
            trackPlayer.Play(sequence, start);
            trackAuditionTimer.Start(); NotifyTrackAudition();
            return $"Ouvindo compasso {point.Measure} · pista {track.Track}, canal {track.Channel} · velocidade {speed:P0}."
                + (sequence.Warnings.Count == 0 ? "" : " " + string.Join(" ", sequence.Warnings));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            token.ThrowIfCancellationRequested();
            if (trackAuditionDisposed) throw new OperationCanceledException();
            TrackAuditionFailed(error.Message);
            return "Não foi possível reproduzir o compasso. " + error.Message;
        }
    }

    private static TimeSpan ReviewRegionEnd(MidiRow row, ScoreReviewPoint point) =>
        row.Document is { } document ? ReviewTimeAtQuarter(document, point.MeasureEndQuarter) : TimeSpan.Zero;

    internal static TimeSpan ReviewTimeAtQuarter(MidiDocument document, double quarter)
    {
        var target = Math.Max(0, quarter * document.Ppq);
        double lastTick = 0, seconds = 0; var tempo = 500000;
        foreach (var change in document.TempoChanges.Where(item => item.Tick >= 0 && item.MicrosecondsPerQuarter > 0)
            .OrderBy(item => item.Tick).ThenBy(item => item.Track).ThenBy(item => item.Order))
        {
            if (change.Tick > target) break;
            seconds += (change.Tick - lastTick) * tempo / document.Ppq / 1_000_000;
            lastTick = change.Tick; tempo = change.MicrosecondsPerQuarter;
        }
        return TimeSpan.FromSeconds(seconds + (target - lastTick) * tempo / document.Ppq / 1_000_000);
    }
}
