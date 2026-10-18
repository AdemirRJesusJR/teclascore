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
    private readonly List<TrackRow> auditionTracks = [];
    private TrackRow? auditionTrack => auditionTracks.Count > 0 ? auditionTracks[0] : null;
    private TimeSpan auditionTrim;
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
    public bool CanPlayMarkedTracks => CanAuditionTracks && selectedFile is { Document: not null } row && row.Tracks.Any(track => track.IsMelody);
    /// <summary>When on, ▶ on another track adds it to the audition instead of replacing it.</summary>
    public bool CombineTrackAudition
    {
        get => settings.TrackAuditionCombine;
        set { if (settings.TrackAuditionCombine == value) return; settings.TrackAuditionCombine = value; Notify(); PersistPreferences(); }
    }
    public bool CanPauseTrackAudition => trackPlayer is { IsPlaying: true } or { IsPaused: true };
    public bool CanStopTrackAudition => auditionTrack is not null;
    public bool CanSeekTrackAudition => auditionTrack is not null && TrackAuditionDurationSeconds > 0;
    public Visibility TrackAuditionVisible => auditionTrack is null ? Visibility.Collapsed : Visibility.Visible;
    // Room for the track grid plus wrapped tool buttons at the minimum window width (expanded sidebar).
    // The preview tab fits the window: its controls scroll on their own and the split gives the score more room.
    public double ScoreEditorMinimumHeight => EditorTabs?.SelectedIndex == 2 ? 760 : auditionTrack is null ? 760 : 900;
    public string TrackAuditionTitle => auditionTracks.Count switch
    {
        0 => "",
        1 => $"Pista {auditionTracks[0].Track} · Canal {auditionTracks[0].Channel} · {auditionTracks[0].Instrument}",
        _ => $"{auditionTracks.Count} pistas juntas · " + string.Join(" + ", auditionTracks.Select(track => track.Instrument))
    };
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
        foreach (string name in new[] { nameof(CanAuditionTracks), nameof(CanPlayMarkedTracks), nameof(CanPauseTrackAudition), nameof(CanStopTrackAudition),
            nameof(CanSeekTrackAudition), nameof(TrackAuditionVisible), nameof(ScoreEditorMinimumHeight), nameof(TrackAuditionTitle), nameof(TrackSelectionHint), nameof(TrackPauseLabel),
            nameof(TrackAuditionPositionSeconds), nameof(TrackAuditionPositionText), nameof(TrackAuditionDurationSeconds), nameof(TrackAuditionDurationText),
            nameof(LyricsTrackHint), nameof(LyricsTrackVisibility) }) Notify(name);
    }

    private void OnTrackAuditionSelectionChanged()
    {
        OnLibraryPlaybackSelectionChanged();
        if (auditionMidi is not null && (!ReferenceEquals(auditionMidi, selectedFile) || auditionMidi.UsesNativeScoreWorkflow)) StopTrackAudition();
        NotifyTrackAudition();
    }

    private async void TrackAuditionClick(object sender, RoutedEventArgs e)
    {
        if (!CanAuditionTracks || selectedFile is not { Document: not null } row
            || sender is not FrameworkElement { Tag: TrackRow track } || !row.Tracks.Contains(track)) return;
        if (CombineTrackAudition && auditionTracks.Count > 0 && ReferenceEquals(auditionMidi, row))
        {
            // Mixer behaviour: ▶ adds this track to what is playing, ■ removes it; the music keeps its place.
            var next = auditionTracks.Contains(track) ? auditionTracks.Where(item => !ReferenceEquals(item, track)).ToList()
                : row.Tracks.Where(item => auditionTracks.Contains(item) || ReferenceEquals(item, track)).ToList();
            if (next.Count == 0) { StopTrackAudition(); return; }
            await PlayTracksAsync(row, next, CurrentAuditionMoment(), trackPlayer?.IsPaused == true);
            return;
        }
        if (auditionTracks.Count == 1 && ReferenceEquals(track, auditionTrack)) { StopTrackAudition(); return; }
        await PlayTracksAsync(row, [track], TimeSpan.Zero, keepPaused: false);
    }

    private async void PlayMarkedTracksClick(object sender, RoutedEventArgs e)
    {
        if (!CanPlayMarkedTracks || selectedFile is not { Document: not null } row) return;
        await PlayTracksAsync(row, row.Tracks.Where(track => track.IsMelody).ToList(), TimeSpan.Zero, keepPaused: false);
    }

    /// <summary>Original-time moment of the current audition, so a new mix resumes at the same place in the song.</summary>
    private TimeSpan CurrentAuditionMoment() => (trackPlayer?.Position ?? TimeSpan.Zero) + auditionTrim;

    private async Task PlayTracksAsync(MidiRow row, IReadOnlyList<TrackRow> tracks, TimeSpan moment, bool keepPaused)
    {
        if (row.Document is not { } document || tracks.Count == 0) return;
        StopTrackAudition();
        ResetPlayback();
        PlaybackStatus = SelectedScoreExists ? "Ouça esta partitura sem abrir o editor do MuseScore." : "Gere uma partitura para ouvir aqui no aplicativo.";
        NotifyPlaybackState();
        string label = tracks.Count == 1 ? $"Pista {tracks[0].Track} · Canal {tracks[0].Channel} · {tracks[0].Instrument}" : $"{tracks.Count} pistas juntas";
        await RunAudioOperation(async () =>
        {
            var token = cancellation!.Token;
            Status = tracks.Count == 1 ? "Separando as notas e preparando o andamento da pista…" : "Juntando as pistas escolhidas no mesmo andamento…";
            TrackAuditionStatus = tracks.Count == 1 ? "Preparando esta pista para ouvir…" : "Preparando as pistas para ouvir juntas…";
            try
            {
                var keys = tracks.Select(track => track.Source.Key).ToArray();
                var sequence = keys.Length == 1
                    ? await midiPreparationCache.GetOrCreateAsync(document, keys[0],
                        cancellationToken => Task.Run(() => MidiAuditionSequence.Create(document, keys[0]), cancellationToken), token)
                    : await Task.Run(() => MidiAuditionSequence.CreateMix(document, keys, token), token);
                token.ThrowIfCancellationRequested();
                if (trackAuditionDisposed || row.UsesNativeScoreWorkflow || !ReferenceEquals(row, selectedFile)) throw new OperationCanceledException();
                auditionMidi = row; auditionTracks.AddRange(tracks); auditionTrim = sequence.LeadingSilenceTrimmed;
                foreach (var track in tracks) track.IsAuditioning = true;
                var start = moment - sequence.LeadingSilenceTrimmed;
                trackPlayer!.Play(sequence, start < TimeSpan.Zero || start >= sequence.Duration ? TimeSpan.Zero : start);
                if (keepPaused) trackPlayer.Pause();
                TrackAuditionStatus = (tracks.Count == 1 ? "Ouvindo somente esta pista, a partir da primeira nota."
                        : $"Ouvindo {tracks.Count} pistas juntas. ▶ em outra pista soma; ■ em uma delas tira só ela.")
                    + (sequence.Warnings.Count == 0 ? "" : " " + string.Join(" ", sequence.Warnings));
                Status = tracks.Count == 1 ? "Pista pronta. A reprodução acontece aqui no aplicativo." : "Pistas prontas. A reprodução acontece aqui no aplicativo.";
                if (!keepPaused) trackAuditionTimer.Start();
                NotifyTrackAudition();
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (!trackAuditionDisposed && auditionTracks.Count > 0) TrackAuditionPanel.BringIntoView();
                }, DispatcherPriority.Loaded);
            }
            catch (OperationCanceledException)
            {
                StopTrackAudition();
                TrackAuditionStatus = "Preparação cancelada. Use ▶ para tentar novamente.";
                throw;
            }
            catch (Exception error) { TrackAuditionFailed(error.Message); throw; }
        }, operationTitle: tracks.Count == 1 ? "Preparando pista para ouvir" : "Preparando pistas para ouvir juntas", operationItem: $"{row.Name} · {label}");
    }

    private void PauseTrackAuditionClick(object sender, RoutedEventArgs e)
    {
        if (!CanPauseTrackAudition) return;
        try
        {
            if (trackPlayer!.IsPaused)
            {
                trackPlayer.Resume();
                TrackAuditionStatus = auditionTracks.Count > 1 ? $"Ouvindo {auditionTracks.Count} pistas juntas." : "Ouvindo somente esta pista.";
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
        foreach (var track in auditionTracks) track.IsAuditioning = false;
        auditionTracks.Clear(); auditionMidi = null; auditionTrim = TimeSpan.Zero;
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
