using System.IO;
using System.Text.Json;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public IReadOnlyList<ScoreReviewPoint> ReviewPoints => selectedFile?.ReviewPoints ?? [];
    public bool CanReviewScore => CanEditMidi && ReviewPoints.Count > 0;

    private void ApplyEngineReviewFailure(MidiRow row, Exception error)
    {
        if (row.UsesNativeScoreWorkflow || error is not EngineOperationException engine) return;
        var details = JsonSerializer.SerializeToElement(engine.Details);
        if (details.TryGetProperty("reviewPoints", out var points) && points.ValueKind == JsonValueKind.Array && points.GetArrayLength() > 0)
            ParseReviewPoints(row, details);
    }

    private void ParseReviewPoints(MidiRow row, JsonElement result)
    {
        row.ReviewPoints.Clear();
        row.MelodyMergeSummary="";
        if(result.ValueKind==JsonValueKind.Object && result.TryGetProperty("melodyMerge",out var merge) && merge.ValueKind==JsonValueKind.Object)
        {
            row.MelodyMergeSummary=Text(merge,"message");
            if(merge.TryGetProperty("sources",out var sources) && sources.ValueKind==JsonValueKind.Array)
                row.MelodyMergeSummary+=" " + string.Join(" · ",sources.EnumerateArray().Where(source=>source.ValueKind==JsonValueKind.Object)
                    .Select(source=>$"Pista {(Number(source,"track",out var track)?track+1:0)}/canal {(Number(source,"channel",out var channel)?channel:0)}: {(Number(source,"keptAttacks",out var kept)?kept:0)} ataques usados"));
        }
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
        row.MelodyMergeSummary="";
        if (row.UsesNativeScoreWorkflow || row.AnalysisPath is null || !File.Exists(row.AnalysisPath)) return;
        try { using var document = JsonDocument.Parse(File.ReadAllText(row.AnalysisPath)); ParseReviewPoints(row, document.RootElement); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private async void ReviewScoreClick(object sender, RoutedEventArgs e)
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
        if (window.MelodyToEdit is { } melodyPoint && selectedFile == row && CanEditMelodySections)
            await OpenMelodySections(melodyPoint.Measure);
    }

    private async Task<string> PlayReviewMeasure(MidiRow row, ScoreReviewPoint point, TrackRow track, double speed, CancellationToken token)
    {
        if (row.UsesNativeScoreWorkflow || row.Document is not { } document || !row.Tracks.Contains(track)) return "A fonte MIDI não está disponível.";
        StopTrackAudition(); ResetPlayback();
        try
        {
            var sequence = await Task.Run(() => MidiAuditionSequence.Create(document, track.Source.Key, trimLeadingSilence: false)
                .Until(ReviewRegionEnd(row, point)), token);
            token.ThrowIfCancellationRequested();
            if (trackAuditionDisposed || row.UsesNativeScoreWorkflow || !ReferenceEquals(row, selectedFile)) throw new OperationCanceledException();
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
