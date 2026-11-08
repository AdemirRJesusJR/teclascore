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
    // The Melodia tab no longer carries the player (it is docked below the scroll area), so it needs less room.
    public double ScoreEditorMinimumHeight => EditorTabs?.SelectedIndex switch { 0 or 3 => 560, 2 => 480, _ => 700 };
    /// <summary>The track player is docked at the bottom of the editor card, outside its scroll area, while the Melodia tab is open.</summary>
    public Visibility TrackPlayerDockVisibility => CanEditMidi && EditorTabs?.SelectedIndex is 0 or 3 ? Visibility.Visible : Visibility.Collapsed;
    public string TrackAuditionTitle => auditionTracks.Count switch
    {
        0 => "Nenhuma pista tocando",
        1 => $"Pista {auditionTracks[0].Track} · Canal {auditionTracks[0].Channel} · {auditionTracks[0].Instrument}",
        _ => $"{auditionTracks.Count} pistas juntas · " + string.Join(" + ", auditionTracks.Select(track => track.Instrument))
    };
    public string TrackSelectionHint => selectedFile?.UsesNativeScoreWorkflow==true
        ? selectedFile.IsNativeScore ? "Ouça e ajuste a partitura atual no Editor musical." : "Ouça e ajuste a partitura atual no Editor musical. Para voltar às pistas do MIDI, restaure uma versão anterior às edições no Histórico."
        : UseReviewedProfile
        ? "Ouça cada pista no botão ▶. Para escolher outra melodia, desmarque a revisão aprovada e marque a pista desejada."
        : "Ouça no botão ▶ e marque uma ou mais pistas de introdução, solo e melodia. Ajuste a prioridade para os trechos sobrepostos; acompanhamento e baixo precisam de conferência.";
    public string TrackPauseLabel => trackPlayer?.IsPaused == true ? "▶  Continuar" : "Ⅱ  Pausar";
    /// <summary>The fixed player's main button: pause/resume while a track plays; when idle it plays the tracks marked as melody.</summary>
    public string TrackPauseGlyph => trackPlayer is { IsPlaying: true } ? "" : "";
    public bool CanUseTrackMainButton => CanPauseTrackAudition || auditionTrack is null && CanPlayMarkedTracks;
    public string TrackMainButtonToolTip => CanPauseTrackAudition ? (trackPlayer?.IsPaused == true ? "Continuar" : "Pausar") : CanPlayMarkedTracks ? "Ouvir as pistas marcadas como melodia" : "Use ▶ ao lado de uma pista para ouvi-la";
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
        trackAuditionTimer.Tick += (_, _) => { StopFinishedLiveTracks(); NotifyTrackAudition(); };
        EditorTabs.SelectionChanged += (_, e) =>
        {
            Notify(nameof(ScoreEditorMinimumHeight));
            Notify(nameof(TrackPlayerDockVisibility));
            if (ReferenceEquals(e.Source, EditorTabs) && EditorTabs.SelectedIndex is not (0 or 3)) StopTrackAudition();
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
        UpdateTrackActivity();
        foreach (string name in new[] { nameof(CanAuditionTracks), nameof(CanPlayMarkedTracks), nameof(CanPauseTrackAudition), nameof(CanStopTrackAudition),
            nameof(CanSeekTrackAudition), nameof(TrackAuditionVisible), nameof(TrackPlayerDockVisibility), nameof(ScoreEditorMinimumHeight), nameof(TrackAuditionTitle), nameof(TrackSelectionHint), nameof(TrackPauseLabel), nameof(TrackPauseGlyph), nameof(CanUseTrackMainButton), nameof(TrackMainButtonToolTip),
            nameof(TrackAuditionPositionSeconds), nameof(TrackAuditionPositionText), nameof(TrackAuditionDurationSeconds), nameof(TrackAuditionDurationText),
            nameof(LyricsTrackHint), nameof(LyricsTrackVisibility), nameof(CollectionRuleHint), nameof(CollectionRuleVisibility) }) Notify(name);
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
            // Mixer behaviour: ▶ adds this track to what is playing, ■ removes it; the music never stops.
            var next = auditionTracks.Contains(track) ? auditionTracks.Where(item => !ReferenceEquals(item, track)).ToList()
                : row.Tracks.Where(item => auditionTracks.Contains(item) || ReferenceEquals(item, track)).ToList();
            if (next.Count == 0) { StopTrackAudition(); return; }
            if (TrySwitchLiveTracks(row, next)) return;
            await PlayTracksAsync(row, next, CurrentAuditionMoment(), trackPlayer?.IsPaused == true);
            return;
        }
        if (auditionTracks.Count == 1 && ReferenceEquals(track, auditionTrack)) { StopTrackAudition(); return; }
        await PlayTracksAsync(row, [track], moment: null, keepPaused: false);
    }

    /// <summary>Plays the tracks marked as melody together (the fixed player's main button when nothing is playing).</summary>
    private async void PlayMarkedTracksClick(object sender, RoutedEventArgs e)
    {
        if (!CanPlayMarkedTracks || selectedFile is not { Document: not null } row) return;
        await PlayTracksAsync(row, row.Tracks.Where(track => track.IsMelody).ToList(), moment: null, keepPaused: false);
    }

    /// <summary>Original-time moment of the current audition, so a new mix resumes at the same place in the song.</summary>
    private TimeSpan CurrentAuditionMoment() => (trackPlayer?.Position ?? TimeSpan.Zero) + auditionTrim;

    /// <summary>
    /// The song's tracks prepared once on one timeline. Turning a track on or off only mutes or unmutes it in
    /// the player, so the other tracks keep sounding and the grid never waits for a new preparation.
    /// </summary>
    private MidiAuditionSequence? liveMix;
    private MidiDocument? liveMixDocument;

    private MidiAuditionSequence? LiveMixFor(MidiRow row, IReadOnlyList<TrackRow> tracks) =>
        liveMix is { } mix && row.Document is { } document && ReferenceEquals(liveMixDocument, document)
        && tracks.All(track => mix.MixedSources.Contains(track.Source.Key)) ? mix : null;

    private bool PlayingLiveMix(MidiRow row) =>
        ReferenceEquals(auditionMidi, row) && liveMix is not null && ReferenceEquals(trackPlayer?.Sequence, liveMix);

    private IEnumerable<int> MutedIndexes(MidiAuditionSequence mix, IReadOnlyList<TrackRow> tracks)
    {
        var audible = tracks.Select(track => track.Source.Key).ToHashSet();
        return Enumerable.Range(0, mix.MixedSources.Count).Where(index => !audible.Contains(mix.MixedSources[index]));
    }

    /// <summary>Changes which tracks sound without stopping, restarting or waiting. False when the live mix cannot do it.</summary>
    private bool TrySwitchLiveTracks(MidiRow row, IReadOnlyList<TrackRow> tracks)
    {
        if (!PlayingLiveMix(row) || LiveMixFor(row, tracks) is not { } mix) return false;
        try { trackPlayer!.SetMutedSources(MutedIndexes(mix, tracks)); }
        catch (Exception error) { TrackAuditionFailed(error.Message); return true; }
        foreach (var track in auditionTracks.Except(tracks)) track.IsAuditioning = false;
        foreach (var track in tracks) track.IsAuditioning = true;
        var added = tracks.Except(auditionTracks).ToList();
        auditionTracks.Clear(); auditionTracks.AddRange(tracks);
        MarkFlowHeard(row, tracks);
        TrackAuditionStatus = LiveStatus(mix);
        // A track turned on during a stretch where it has no notes is on, just silent: say when it comes in.
        if (added.Count == 1 && trackPlayer!.IsPlaying && WaitText(mix, added[0], trackPlayer.Position) is { Length: > 0 } wait)
            TrackAuditionStatus = $"{added[0].Instrument} (canal {added[0].Channel}) está ligada, mas{wait} — até lá, ouça as outras pistas.";
        NotifyTrackAudition();
        return true;
    }

    private static int SourceIndex(MidiAuditionSequence mix, TrackRow track)
    {
        for (int index = 0; index < mix.MixedSources.Count; index++) if (mix.MixedSources[index] == track.Source.Key) return index;
        return -1;
    }

    /// <summary>Empty while the track sounds or comes in within a second; otherwise " só entra em 0:57" or " já terminou".</summary>
    private static string WaitText(MidiAuditionSequence mix, TrackRow track, TimeSpan position)
    {
        int index = SourceIndex(mix, track);
        if (index < 0 || mix.IsSourceSounding(index, position)) return "";
        var next = mix.NextSourceNote(index, position);
        if (next is null) return " já terminou";
        return next.Value - position > TimeSpan.FromSeconds(1) ? " só entra em " + FormatTime(next.Value.TotalSeconds) : "";
    }

    /// <summary>Marks which turned-on tracks are sounding right now, and when the silent ones come in.</summary>
    private void UpdateTrackActivity()
    {
        var mix = auditionMidi is { } row && PlayingLiveMix(row) ? liveMix : null;
        var position = trackPlayer?.Position ?? TimeSpan.Zero;
        foreach (var track in auditionMidi?.Tracks ?? [])
        {
            bool on = track.IsAuditioning && mix is not null;
            int index = on ? SourceIndex(mix!, track) : -1;
            track.IsSounding = on && index >= 0 && mix!.IsSourceSounding(index, position);
            string wait = on ? WaitText(mix!, track, position).Trim() : "";
            track.AuditionWait = wait.Length == 0 ? "" : " · " + wait;
        }
    }

    private string LiveStatus(MidiAuditionSequence mix) =>
        (auditionTracks.Count == 1 ? "Ouvindo esta pista. ▶ em outra pista soma na hora, sem parar a música."
            : $"Ouvindo {auditionTracks.Count} pistas juntas. ▶ em outra pista soma; ■ em uma delas tira só ela.")
        + (mix.Warnings.Count == 0 ? "" : " " + string.Join(" ", mix.Warnings));

    /// <summary>When the audible tracks have all finished, the rest of the song would only be silence.</summary>
    private void StopFinishedLiveTracks()
    {
        if (auditionMidi is not { } row || !PlayingLiveMix(row) || trackPlayer is not { IsPlaying: true } player || liveMix is not { } mix) return;
        var end = auditionTracks.Select(track => mix.MixedSources.ToList().IndexOf(track.Source.Key)).Where(index => index >= 0)
            .Select(index => mix.SourceEnds[index]).DefaultIfEmpty(mix.Duration).Max();
        if (player.Position >= end) StopTrackAudition(auditionTracks.Count == 1 ? "Pista concluída. Ouça outra pista ou marque a melodia." : "Pistas concluídas. Ouça outra pista ou marque a melodia.");
    }

    /// <param name="moment">Original-time place in the song; null starts at the first note of these tracks.</param>
    private async Task PlayTracksAsync(MidiRow row, IReadOnlyList<TrackRow> tracks, TimeSpan? moment, bool keepPaused)
    {
        if (row.Document is not { } document || tracks.Count == 0) return;
        if (LiveMixFor(row, tracks) is { } ready) { StartLiveMix(row, ready, tracks, moment, keepPaused); return; }
        StopTrackAudition();
        string label = tracks.Count == 1 ? $"Pista {tracks[0].Track} · Canal {tracks[0].Channel} · {tracks[0].Instrument}" : $"{tracks.Count} pistas juntas";
        await RunAudioOperation(async () =>
        {
            var token = cancellation!.Token;
            Status = "Preparando as pistas desta música para ouvir e misturar…";
            TrackAuditionStatus = "Preparando as pistas para ouvir…";
            try
            {
                var all = row.Tracks.Select(track => track.Source.Key).ToArray();
                var required = tracks.Select(track => track.Source.Key).ToHashSet();
                var mix = await Task.Run(() => MidiAuditionSequence.CreateLiveMix(document, all, required, token), token);
                token.ThrowIfCancellationRequested();
                if (trackAuditionDisposed || row.UsesNativeScoreWorkflow || !ReferenceEquals(row, selectedFile)) throw new OperationCanceledException();
                liveMix = mix; liveMixDocument = document;
                Status = "Pistas prontas. A reprodução acontece aqui no aplicativo.";
            }
            catch (OperationCanceledException)
            {
                StopTrackAudition();
                TrackAuditionStatus = "Preparação cancelada. Use ▶ para tentar novamente.";
                throw;
            }
            catch (Exception error) { TrackAuditionFailed(error.Message); throw; }
        }, operationTitle: tracks.Count == 1 ? "Preparando pista para ouvir" : "Preparando pistas para ouvir juntas", operationItem: $"{row.Name} · {label}");
        // Started after the operation releases the busy state, which would otherwise stop the audition again.
        if (LiveMixFor(row, tracks) is { } prepared && ReferenceEquals(row, selectedFile) && !trackAuditionDisposed)
            StartLiveMix(row, prepared, tracks, moment, keepPaused);
    }

    private void StartLiveMix(MidiRow row, MidiAuditionSequence mix, IReadOnlyList<TrackRow> tracks, TimeSpan? moment, bool keepPaused)
    {
        StopLibraryPlayback();
        foreach (var track in auditionTracks) { track.IsAuditioning = false; track.IsSounding = false; track.AuditionWait = ""; }
        auditionTracks.Clear();
        ResetPlayback();
        PlaybackStatus = SelectedScoreExists ? "Ouça esta partitura sem abrir o editor do MuseScore." : "Gere uma partitura para ouvir aqui no aplicativo.";
        NotifyPlaybackState();
        try
        {
            var sources = mix.MixedSources.ToList();
            var start = moment is { } at ? at - mix.LeadingSilenceTrimmed
                : tracks.Select(track => mix.SourceStarts[sources.IndexOf(track.Source.Key)]).Min();
            if (start < TimeSpan.Zero || start >= mix.Duration) start = TimeSpan.Zero;
            auditionMidi = row; auditionTracks.AddRange(tracks); auditionTrim = mix.LeadingSilenceTrimmed;
            foreach (var track in tracks) track.IsAuditioning = true;
            trackPlayer!.Play(mix, start, MutedIndexes(mix, tracks));
            if (keepPaused) trackPlayer.Pause();
            MarkFlowHeard(row, tracks);
            TrackAuditionStatus = LiveStatus(mix);
            if (!keepPaused) trackAuditionTimer.Start();
            NotifyTrackAudition();
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (!trackAuditionDisposed && auditionTracks.Count > 0) TrackAuditionPanel.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
        catch (Exception error) { TrackAuditionFailed(error.Message); }
    }

    private void PauseTrackAuditionClick(object sender, RoutedEventArgs e)
    {
        if (!CanPauseTrackAudition)
        {
            if (auditionTrack is null && CanPlayMarkedTracks) PlayMarkedTracksClick(sender, e);
            return;
        }
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
        foreach (var track in auditionTracks) { track.IsAuditioning = false; track.IsSounding = false; track.AuditionWait = ""; }
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
