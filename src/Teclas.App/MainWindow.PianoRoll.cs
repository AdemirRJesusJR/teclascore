using System.Windows;
using System.Windows.Media;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// The piano roll under the score: while the score plays, its notes come down onto a keyboard in time with the audio
/// (melody in blue, the chord diagrams in amber), with the chord being played and the end of the score. The blocks are
/// placed on the playback clock through the measure timeline, so repeats come down again and tempo changes stretch them.
/// </summary>
public partial class MainWindow
{
    internal const string PianoRollIdleHint = "Toque a partitura (▶) e as notas descem sobre o teclado no ritmo da música.";
    private FallingNotesView? pianoRollView;
    private ScorePlaybackMap? pianoRollMap;
    private string? pianoRollIdentity;
    private bool pianoRollLoading, pianoRollFrames;
    private double pianoRollShown = double.NaN;
    internal FallingNotesView? PianoRollView => pianoRollView;

    public bool PianoRollEnabled
    {
        get => settings.PianoRoll;
        set
        {
            if (settings.PianoRoll == value) return;
            settings.PianoRoll = value; Notify(); NotifyPianoRollLayout(); PersistPreferences();
            UpdatePianoRoll();
            // Turned on with the audio already loaded: the measure timeline is still needed to place the notes.
            if (value && audioLoaded && IsIdle && !preparingScoreSynchronization
                && (scorePlaybackMap is null || scoreMapIdentity != SelectedScoreIdentity() || scorePlaybackMap.Speed != ScorePlaybackSpeed))
                PrepareScoreSynchronizationClick(this, new RoutedEventArgs());
        }
    }

    /// <summary>Shown once the notes are placed on a playback timeline (the first Play or Preparar): before that the
    /// page keeps all its room.</summary>
    public Visibility PianoRollVisibility => settings.PianoRoll && SelectedScoreExists && pianoRollMap is not null ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The page keeps its usual minimum; only while the roll shows it may shrink to <see cref="PianoRollScoreRoom"/>.</summary>
    public double PreviewScoreViewerMinimumHeight => PianoRollVisibility == Visibility.Visible ? PianoRollScoreRoom - 4 : PreviewScoreMinimumHeight;
    private void NotifyPianoRollLayout() { Notify(nameof(PianoRollVisibility)); Notify(nameof(PreviewScoreViewerMinimumHeight)); }

    private void InitializePianoRoll()
    {
        pianoRollView = new FallingNotesView([]) { VisibleSpan = 3, MinimumScale = 70, FullKeyboard = true, ShowChordName = true, Hint = PianoRollIdleHint };
        PianoRollHost.Content = pianoRollView;
        Closed += (_, _) => StopPianoRollFrames();
    }

    internal const double PianoRollMinimumHeight = 76, PianoRollScoreRoom = 160;
    private void PreviewScoreAreaResized(object sender, SizeChangedEventArgs e) => ResizePianoRoll();

    internal const string PianoRollOffMessage = "Piano roll desligado. Para religar, use o botão de piano roll ao lado de Notas caindo ou Configurações › Estudo.";

    /// <summary>The ✕ on the roll itself: turns it off (the toolbar toggle and Configurações turn it back on).</summary>
    private void PianoRollCloseClick(object sender, RoutedEventArgs e) { PianoRollEnabled = false; Status = PianoRollOffMessage; }

    /// <summary>About a third of the score area, leaving the page at least <see cref="PianoRollScoreRoom"/> pixels.</summary>
    private void ResizePianoRoll()
    {
        double area = PreviewScoreArea.ActualHeight;
        if (area <= 0) return;
        PianoRollPanel.Height = Math.Max(PianoRollMinimumHeight, Math.Min(Math.Clamp(Math.Round(area * .38), 120, 300), area - PianoRollScoreRoom - PianoRollPanel.Margin.Top));
    }

    /// <summary>Called with the playback position updates; reads the score's notes once per score and playback map.
    /// While the audio plays, every rendered frame moves the notes.</summary>
    internal void UpdatePianoRoll(double? seconds = null)
    {
        if (pianoRollView is not { } view) return;
        if (!settings.PianoRoll || !SelectedScoreExists) { StopPianoRollFrames(); return; }
        if (scorePlaybackMap is not { CanSynchronize: true } map || scoreMapIdentity != SelectedScoreIdentity() || CurrentScorePath is not { } path)
        {
            StopPianoRollFrames();
            if (pianoRollMap is not null) { pianoRollMap = null; pianoRollIdentity = null; view.Load([]); NotifyPianoRollLayout(); }
            view.Hint = PianoRollIdleHint;
            return;
        }
        if (!ReferenceEquals(pianoRollMap, map) || pianoRollIdentity != scoreMapIdentity)
        {
            if (pianoRollLoading) return;
            pianoRollLoading = true;
            string? identity = scoreMapIdentity;
            var timeline = map.MeasureTimeline;
            int inversion = selectedFile?.EffectiveDiagramInversion ?? settings.DefaultDiagramInversion;
            view.Hint = "Preparando o piano roll…";
            _ = Task.Run(() =>
            {
                var model = karaokeModel(path);
                return PianoRollTimeline.Build(FallingNotes.FromScore(model, inversion), PracticeSession.MeasureStarts(model), timeline);
            }).ContinueWith(task =>
            {
                pianoRollLoading = false;
                pianoRollMap = map; pianoRollIdentity = identity; pianoRollShown = double.NaN;
                var blocks = task.IsCompletedSuccessfully ? task.Result : [];
                view.Load(blocks, timeline.Select(o => o.StartSeconds).ToArray(), timeline.Count == 0 ? null : timeline[^1].EndSeconds);
                view.Hint = blocks.Count > 0 ? "" : task.IsCompletedSuccessfully ? "Esta partitura não tem notas para o piano roll."
                    : "Não foi possível ler as notas desta partitura para o piano roll.";
                ResizePianoRoll(); NotifyPianoRollLayout();
                UpdatePianoRoll(seconds);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        ShowPianoRoll(seconds ?? PlaybackPositionSeconds);
        if (audioPlaying) StartPianoRollFrames(); else StopPianoRollFrames();
    }

    private void ShowPianoRoll(double position)
    {
        if (pianoRollView is not { } view || position == pianoRollShown) return;
        pianoRollShown = position;
        view.Show(position, true, true);
    }

    private void StartPianoRollFrames()
    {
        if (pianoRollFrames) return;
        pianoRollFrames = true;
        CompositionTarget.Rendering += PianoRollFrame;
    }

    private void StopPianoRollFrames()
    {
        if (!pianoRollFrames) return;
        pianoRollFrames = false;
        CompositionTarget.Rendering -= PianoRollFrame;
    }

    private void PianoRollFrame(object? sender, EventArgs e)
    {
        if (!audioPlaying || !ReferenceEquals(pianoRollMap, scorePlaybackMap)) { StopPianoRollFrames(); return; }
        if (PianoRollPanel.IsVisible) ShowPianoRoll(PlaybackPositionSeconds);
    }
}
