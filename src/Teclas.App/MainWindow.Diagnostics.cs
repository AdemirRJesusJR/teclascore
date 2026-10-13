using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanDiagnoseScore => IsIdle && SelectedScoreExists;
    public bool CanSuggestMelody => IsIdle && selectedFile?.Document is not null;
    public string MelodySuggestionSummary => selectedFile is { Document: not null } row
        && row.Tracks.Where(track => track.Source.Key.Channel != 10).OrderBy(track => track.MelodySuggestion?.Rank).FirstOrDefault() is { MelodySuggestion: { } suggestion } candidateTrack
        ? $"Primeira fonte para conferir: pista {candidateTrack.Track}, canal {candidateTrack.Channel} · {candidateTrack.Instrument} · {suggestion.Label}. Compare os trechos antes de escolher."
        : "Compare as pistas e escolha a que contém a melodia.";

    private void NotifyDiagnostics()
    {
        Notify(nameof(CanDiagnoseScore)); Notify(nameof(CanSuggestMelody)); Notify(nameof(MelodySuggestionSummary));
    }

    private async Task DiagnoseSavedScore(MidiRow row)
    {
        if (row.ScorePath is not { } path || !File.Exists(path)) return;
        Status = "Conferindo a estrutura, o ritmo e a apresentação da partitura…";
        var token = cancellation!.Token;
        var report = await Task.Run(() => NativeScoreDiagnostics.Analyze(path, token), token);
        token.ThrowIfCancellationRequested();
        if (row.ScorePath == path) row.DiagnosticReport = report;
    }

    private async void DiagnoseScoreClick(object sender, RoutedEventArgs e)
    {
        if (!CanDiagnoseScore || selectedFile is not { } row) return;
        // Always inspect the current saved file so edits made outside the app are not hidden by a stale report.
        row.DiagnosticReport = null;
        await RunOperation(async () =>
        {
            await DiagnoseSavedScore(row);
            Status = "Diagnóstico concluído. Confira os pontos encontrados antes de ajustar a partitura.";
        }, operationTitle: "Diagnosticando a partitura", operationItem: row.ScoreTitle);
        if (operationsDisposed || row.DiagnosticReport is not { } report || selectedFile != row) return;
        var dialog = new ScoreDiagnosticsWindow(report, row.IsNativeScore) { Owner = this };
        if (dialog.ShowDialog() == true && row.IsNativeScore) ImproveNativeScoreClick(sender, e);
    }

    private void MelodySuggestionsClick(object sender, RoutedEventArgs e)
    {
        if (!CanSuggestMelody || selectedFile is not { Document: not null } row || !CommitEdits()) return;
        StopTrackAudition(); ResetPlayback();
        var dialog = new MelodySuggestionsWindow(row, TrackAuditionSpeed,
            (track, speed, token) => PlaySuggestedMelody(row, track, speed, token),
            () => StopTrackAudition(), () => trackPlayer?.IsPlaying == true) { Owner = this };
        EventHandler<MidiPlaybackErrorEventArgs> onFailure = (_, error) =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.InvokeAsync(() => dialog.ReportPlaybackFailure(error.Message));
        };
        trackPlayer!.PlaybackFailed += onFailure;
        try
        {
            if (dialog.ShowDialog() == true && dialog.ChosenTrack is { } selected) ApplySuggestedMelody(row, selected);
        }
        finally
        {
            trackPlayer!.PlaybackFailed -= onFailure;
            StopTrackAudition();
            if (trackPlayer is not null) trackPlayer.PlaybackRate = settings.TrackAuditionSpeed;
            NotifyPlaybackState();
        }
    }

    private void ApplySuggestedMelody(MidiRow row, TrackRow selected)
    {
        if (selectedFile != row || !row.Tracks.Contains(selected) || selected.Source.Key.Channel == 10) return;
        if (!row.UseReviewedProfile && selected.IsMelody && row.Tracks.Count(track => track.IsMelody) == 1)
        {
            Status = "Esta pista já é a melodia selecionada. Seus ajustes foram mantidos.";
            return;
        }
        RememberVersion(row);
        loading = true;
        try
        {
            row.UseReviewedProfile = false;
            foreach (var track in row.Tracks) track.IsMelody = ReferenceEquals(track, selected);
        }
        finally { loading = false; }
        CurrentFilter = "all"; InvalidateAnalysis(row); RefreshSelection(); PersistPreferences();
        TrackGrid.SelectedItem = selected; TrackGrid.ScrollIntoView(selected);
        Status = $"Pista {selected.Track}, canal {selected.Channel}, escolhida por você. Analise os acordes ou gere a partitura para atualizar.";
    }

    private async Task<string> PlaySuggestedMelody(MidiRow row, TrackRow track, double speed, CancellationToken token)
    {
        if (row.Document is not { } document || !row.Tracks.Contains(track) || track.MelodySuggestion is not { } suggestion)
            return "Esta pista não está disponível.";
        StopTrackAudition(); ResetPlayback();
        var start = ReviewTimeAtQuarter(document, (double)suggestion.SampleStartTick / document.Ppq);
        var end = ReviewTimeAtQuarter(document, (double)suggestion.SampleEndTick / document.Ppq);
        var sequence = await Task.Run(() => MidiAuditionSequence.Create(document, track.Source.Key, trimLeadingSilence: false).Until(end), token);
        token.ThrowIfCancellationRequested();
        if (trackAuditionDisposed || selectedFile != row) throw new OperationCanceledException(token);
        if (start >= sequence.Duration) return "Não há notas disponíveis neste trecho.";
        auditionMidi = row; auditionTrack = track; track.IsAuditioning = true;
        trackPlayer!.PlaybackRate = speed; trackPlayer.Play(sequence, start);
        trackAuditionTimer.Start(); NotifyTrackAudition();
        return $"Ouvindo pista {track.Track}, canal {track.Channel} · trecho em {FormatTime(start.TotalSeconds)} · {speed:P0}.";
    }
}
