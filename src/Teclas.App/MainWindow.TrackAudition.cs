using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private MidiTrackPlayer? trackPlayer;
    private readonly DispatcherTimer trackAuditionTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private MidiRow? auditionMidi;
    private TrackRow? auditionTrack;
    private bool trackAuditionDisposed;
    private string trackAuditionStatus = "Use ▶ ao lado de uma pista para ouvi-la isoladamente.";

    public bool CanAuditionTracks => IsIdle && selectedFile?.Document is not null;
    public bool CanPauseTrackAudition => trackPlayer is { IsPlaying: true } or { IsPaused: true };
    public bool CanStopTrackAudition => auditionTrack is not null;
    public bool CanSeekTrackAudition => auditionTrack is not null && TrackAuditionDurationSeconds > 0;
    public Visibility TrackAuditionVisible => auditionTrack is null ? Visibility.Collapsed : Visibility.Visible;
    public double ScoreEditorMinimumHeight => auditionTrack is null ? 620 : 760;
    public string TrackAuditionTitle => auditionTrack is { } track ? $"Pista {track.Track} · Canal {track.Channel} · {track.Instrument}" : "";
    public string TrackSelectionHint => UseReviewedProfile
        ? "Ouça cada pista no botão ▶. Para escolher outra melodia, desmarque a revisão aprovada e marque a pista desejada."
        : "Ouça cada pista no botão ▶ e marque a que contém a melodia. Acompanhamento, baixo e guias de letra precisam de conferência.";
    public string TrackPauseLabel => trackPlayer?.IsPaused == true ? "▶  Continuar" : "Ⅱ  Pausar";
    public string TrackAuditionStatus { get => trackAuditionStatus; private set { trackAuditionStatus = value; Notify(); } }
    public double TrackAuditionDurationSeconds => trackPlayer?.Duration.TotalSeconds ?? 0;
    public string TrackAuditionDurationText => FormatTime(TrackAuditionDurationSeconds);
    public string TrackAuditionPositionText => FormatTime(TrackAuditionPositionSeconds);
    public double TrackAuditionPositionSeconds
    {
        get => trackPlayer?.Position.TotalSeconds ?? 0;
        set
        {
            if (!CanSeekTrackAudition || !double.IsFinite(value)) return;
            try { trackPlayer!.Seek(TimeSpan.FromSeconds(Math.Clamp(value, 0, TrackAuditionDurationSeconds))); }
            catch (Exception error) { TrackAuditionFailed(error.Message); }
            NotifyTrackAudition();
        }
    }

    private void InitializeTrackAudition(Func<IMidiOutputTransport>? outputFactory)
    {
        trackPlayer = new MidiTrackPlayer(outputFactory);
        trackPlayer.StateChanged += (_, _) => QueueTrackAuditionUpdate();
        trackPlayer.PlaybackFailed += (_, error) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.InvokeAsync(() => { if (!trackAuditionDisposed) TrackAuditionFailed(error.Message); });
        };
        trackAuditionTimer.Tick += (_, _) => NotifyTrackAudition();
        EditorTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, EditorTabs) && EditorTabs.SelectedIndex != 0) StopTrackAudition();
        };
        Closed += (_, _) =>
        {
            trackAuditionDisposed = true;
            trackAuditionTimer.Stop();
            trackPlayer.Dispose();
        };
    }

    private void QueueTrackAuditionUpdate()
    {
        if (trackAuditionDisposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (trackAuditionDisposed) return;
            if (trackPlayer?.IsPlaying == true) trackAuditionTimer.Start();
            else trackAuditionTimer.Stop();
            if (auditionTrack is not null && trackPlayer is { IsPlaying: false, IsPaused: false }
                && trackPlayer.Position >= trackPlayer.Duration && trackPlayer.Duration > TimeSpan.Zero)
                StopTrackAudition("Pista concluída. Ouça outra pista ou marque a melodia.");
            NotifyTrackAudition();
        });
    }

    private void NotifyTrackAudition()
    {
        foreach (string name in new[] { nameof(CanAuditionTracks), nameof(CanPauseTrackAudition), nameof(CanStopTrackAudition),
            nameof(CanSeekTrackAudition), nameof(TrackAuditionVisible), nameof(ScoreEditorMinimumHeight), nameof(TrackAuditionTitle), nameof(TrackSelectionHint), nameof(TrackPauseLabel),
            nameof(TrackAuditionPositionSeconds), nameof(TrackAuditionPositionText), nameof(TrackAuditionDurationSeconds), nameof(TrackAuditionDurationText) }) Notify(name);
    }

    private void OnTrackAuditionSelectionChanged()
    {
        if (auditionMidi is not null && !ReferenceEquals(auditionMidi, selectedFile)) StopTrackAudition();
        NotifyTrackAudition();
    }

    private void TrackAuditionClick(object sender, RoutedEventArgs e)
    {
        if (!CanAuditionTracks || selectedFile is not { Document: { } document } row
            || sender is not FrameworkElement { Tag: TrackRow track } || !row.Tracks.Contains(track)) return;
        if (ReferenceEquals(track, auditionTrack)) { StopTrackAudition(); return; }
        StopTrackAudition();
        ResetPlayback();
        PlaybackStatus = SelectedScoreExists ? "Ouça esta partitura sem abrir o editor do MuseScore." : "Gere uma partitura para ouvir aqui no aplicativo.";
        NotifyPlaybackState();
        try
        {
            var sequence = MidiAuditionSequence.Create(document, track.Source.Key);
            auditionMidi = row; auditionTrack = track; track.IsAuditioning = true;
            trackPlayer!.Play(sequence);
            TrackAuditionStatus = "Ouvindo somente esta pista, a partir da primeira nota."
                + (sequence.Warnings.Count == 0 ? "" : " " + string.Join(" ", sequence.Warnings));
            trackAuditionTimer.Start();
            NotifyTrackAudition();
            Dispatcher.InvokeAsync(() =>
            {
                if (!trackAuditionDisposed && ReferenceEquals(auditionTrack, track)) TrackAuditionPanel.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
        catch (Exception error) { TrackAuditionFailed(error.Message); }
    }

    private void PauseTrackAuditionClick(object sender, RoutedEventArgs e)
    {
        if (!CanPauseTrackAudition) return;
        try
        {
            if (trackPlayer!.IsPaused)
            {
                trackPlayer.Resume();
                TrackAuditionStatus = "Ouvindo somente esta pista.";
            }
            else
            {
                trackPlayer.Pause();
                TrackAuditionStatus = "Pista pausada. Continuar retoma do mesmo ponto.";
            }
            NotifyTrackAudition();
        }
        catch (Exception error) { TrackAuditionFailed(error.Message); }
    }

    private void StopTrackAuditionClick(object sender, RoutedEventArgs e) => StopTrackAudition();

    private void StopTrackAudition(string message = "Reprodução da pista parada. Use ▶ para ouvir uma pista.")
    {
        if (auditionTrack is null) return;
        trackAuditionTimer.Stop();
        auditionTrack.IsAuditioning = false; auditionTrack = null; auditionMidi = null;
        trackPlayer?.Stop();
        TrackAuditionStatus = message;
        NotifyTrackAudition();
    }

    private void TrackAuditionFailed(string details)
    {
        StopTrackAudition();
        TrackAuditionStatus = "Não foi possível ouvir a pista. " + details;
        Status = TrackAuditionStatus;
        NotifyTrackAudition();
    }
}
