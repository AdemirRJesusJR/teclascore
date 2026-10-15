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

    public sealed record TrackSpeedOption(double Value, string Label);
    public IReadOnlyList<TrackSpeedOption> TrackSpeedOptions { get; } =
    [new(0.25, "25%"), new(0.5, "50%"), new(0.75, "75%"), new(1.0, "100% · normal"), new(1.25, "125%"), new(1.5, "150%")];
    public double TrackAuditionSpeed
    {
        get => settings.TrackAuditionSpeed;
        set
        {
            var speed = Settings.NormalizeTrackAuditionSpeed(value);
            if (settings.TrackAuditionSpeed == speed) return;
            if (trackPlayer is not null) trackPlayer.PlaybackRate = speed;
            settings.TrackAuditionSpeed = speed;
            Notify();
            PersistPreferences();
        }
    }

    public bool CanAuditionTracks => CanEditMidi;
    public bool CanPauseTrackAudition => trackPlayer is { IsPlaying: true } or { IsPaused: true };
    public bool CanStopTrackAudition => auditionTrack is not null;
    public bool CanSeekTrackAudition => auditionTrack is not null && TrackAuditionDurationSeconds > 0;
    public Visibility TrackAuditionVisible => auditionTrack is null ? Visibility.Collapsed : Visibility.Visible;
    public double ScoreEditorMinimumHeight => EditorTabs?.SelectedIndex == 2 ? 1120 : auditionTrack is null ? 710 : 850;
    public string TrackAuditionTitle => auditionTrack is { } track ? $"Pista {track.Track} · Canal {track.Channel} · {track.Instrument}" : "";
    public string TrackSelectionHint => selectedFile?.UsesNativeScoreWorkflow==true
        ? selectedFile.IsNativeScore ? "Ouça e ajuste a partitura atual no Editor musical." : "Ouça e ajuste a partitura atual no Editor musical. Para voltar às pistas do MIDI, restaure uma versão anterior às edições no Histórico."
        : UseReviewedProfile
        ? "Ouça cada pista no botão ▶. Para escolher outra melodia, desmarque a revisão aprovada e marque a pista desejada."
        : "Ouça no botão ▶ e marque uma ou mais pistas de introdução, solo e melodia. Ajuste a prioridade para os trechos sobrepostos; acompanhamento e baixo precisam de conferência.";
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
        InitializeLibraryPlayback(outputFactory);
        trackPlayer = new MidiTrackPlayer(outputFactory) { PlaybackRate = settings.TrackAuditionSpeed };
        trackPlayer.StateChanged += (_, _) => QueueTrackAuditionUpdate();
        trackPlayer.PlaybackFailed += (_, error) =>
        {
            if (!Dispatcher.HasShutdownStarted && error.Generation == trackPlayer.PlaybackGeneration)
                Dispatcher.InvokeAsync(() =>
                {
                    if (!trackAuditionDisposed && error.Generation == trackPlayer.PlaybackGeneration && auditionTrack is not null)
                        TrackAuditionFailed(error.Message);
                });
        };
        trackAuditionTimer.Tick += (_, _) => NotifyTrackAudition();
        EditorTabs.SelectionChanged += (_, e) =>
        {
            Notify(nameof(ScoreEditorMinimumHeight));
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
        OnLibraryPlaybackSelectionChanged();
        if (auditionMidi is not null && (!ReferenceEquals(auditionMidi, selectedFile) || auditionMidi.UsesNativeScoreWorkflow)) StopTrackAudition();
        NotifyTrackAudition();
    }

    private async void TrackAuditionClick(object sender, RoutedEventArgs e)
    {
        if (!CanAuditionTracks || selectedFile is not { Document: { } document } row
            || sender is not FrameworkElement { Tag: TrackRow track } || !row.Tracks.Contains(track)) return;
        if (ReferenceEquals(track, auditionTrack)) { StopTrackAudition(); return; }
        StopTrackAudition();
        ResetPlayback();
        PlaybackStatus = SelectedScoreExists ? "Ouça esta partitura sem abrir o editor do MuseScore." : "Gere uma partitura para ouvir aqui no aplicativo.";
        NotifyPlaybackState();
        await RunAudioOperation(async () =>
        {
            var token = cancellation!.Token;
            Status = "Separando as notas e preparando o andamento da pista…";
            TrackAuditionStatus = "Preparando esta pista para ouvir…";
            try
            {
                var sequence = await midiPreparationCache.GetOrCreateAsync(document, track.Source.Key,
                    cancellationToken => Task.Run(() => MidiAuditionSequence.Create(document, track.Source.Key), cancellationToken), token);
                token.ThrowIfCancellationRequested();
                if (trackAuditionDisposed || row.UsesNativeScoreWorkflow || !ReferenceEquals(row, selectedFile)) throw new OperationCanceledException();
                auditionMidi = row; auditionTrack = track; track.IsAuditioning = true;
                trackPlayer!.Play(sequence);
                TrackAuditionStatus = "Ouvindo somente esta pista, a partir da primeira nota."
                    + (sequence.Warnings.Count == 0 ? "" : " " + string.Join(" ", sequence.Warnings));
                Status = "Pista pronta. A reprodução acontece aqui no aplicativo.";
                trackAuditionTimer.Start();
                NotifyTrackAudition();
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (!trackAuditionDisposed && ReferenceEquals(auditionTrack, track)) TrackAuditionPanel.BringIntoView();
                }, DispatcherPriority.Loaded);
            }
            catch (OperationCanceledException)
            {
                StopTrackAudition();
                TrackAuditionStatus = "Preparação da pista cancelada. Use ▶ para tentar novamente.";
                throw;
            }
            catch (Exception error) { TrackAuditionFailed(error.Message); throw; }
        }, operationTitle: "Preparando pista para ouvir", operationItem: $"{row.Name} · Pista {track.Track} · Canal {track.Channel} · {track.Instrument}");
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
        StopLibraryPlayback();
        if (auditionTrack is null) return;
        trackAuditionTimer.Stop();
        auditionTrack.IsAuditioning = false; auditionTrack = null; auditionMidi = null;
        trackPlayer?.Stop(releaseOutput: true);
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
