using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// The preview player is docked above the score in two short rows (transport and study, with the editing, review and
/// file tools in menus), so it never scrolls away. Score focus hides the side columns to give the page the whole width.
/// </summary>
public partial class MainWindow
{
    private bool scoreFocusMode, previewAlignmentQueued;

    private void InitializePreviewLayout()
    {
        // The player no longer has a speed box (v1.1): preview playback is always at normal speed. Practice and
        // the study audio keep their own speed choices.
        settings.ScorePlaybackSpeed = 1;
        MainScoreViewer.TwoPages = settings.ScoreTwoPages;
        MainScoreViewer.TwoPagesChanged += (_, enabled) => { settings.ScoreTwoPages = enabled; PersistPreferences(); };
        Loaded += (_, _) => QueuePreviewAlignment();
        ScoreEditorScrollViewer.SizeChanged += (_, _) => QueuePreviewAlignment();
        EditorTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, EditorTabs)) { NotifyScoreFocusLayout(); QueuePreviewAlignment(); } };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(CurrentPage) or nameof(SelectedFile))) return;
            NotifyScoreFocusLayout();
            QueuePreviewAlignment();
        };
    }

    private bool IsOnGenerateStep => CurrentPage == "library" && EditorTabs.SelectedIndex == TabOfStep(ScoreFlowStep.Generate);

    /// <summary>Score focus applies to the Gerar step only. The user's choice is kept: other steps and pages show the side
    /// columns, and coming back to Gerar hides them again while the toggle stays on.</summary>
    public bool IsScoreFocusApplied => scoreFocusMode && IsOnGenerateStep;

    private void NotifyScoreFocusLayout()
    {
        foreach (var name in new[] { nameof(IsScoreFocusApplied), nameof(LibraryColumnWidth), nameof(LibraryGapWidth), nameof(ProgressPanelVisibility),
            nameof(ProgressPanelColumnWidth), nameof(ProgressPanelGapWidth) }) Notify(name);
    }

    /// <summary>Short windows only: when the editor cannot show everything, scroll its outer viewport so the docked player
    /// sits at the top and the page keeps its reading room. Tall windows have nothing to scroll and are left alone.</summary>
    private void QueuePreviewAlignment()
    {
        if (previewAlignmentQueued || Dispatcher.HasShutdownStarted) return;
        previewAlignmentQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            previewAlignmentQueued = false;
            if (!IsLoaded || operationsDisposed || CurrentPage != "library" || EditorTabs.SelectedIndex != 2
                || !ScoreEditorScrollViewer.IsVisible) return;
            ScoreEditorScrollViewer.UpdateLayout();
            if (ScoreEditorScrollViewer.ScrollableHeight <= 0) return;
            double playerTop = PreviewPlayerPanel.TranslatePoint(new Point(0, 0), ScoreEditorScrollViewer).Y;
            double target = ScoreEditorScrollViewer.VerticalOffset + playerTop - 4;
            ScoreEditorScrollViewer.ScrollToVerticalOffset(Math.Clamp(target, 0, ScoreEditorScrollViewer.ScrollableHeight));
        }));
    }

    /// <summary>Saved by older versions, when the player could be expanded or compacted. Kept so old settings still load and round-trip.</summary>
    public bool IsPreviewControlsCompact
    {
        get => settings.IsPreviewControlsCompact;
        set
        {
            if (settings.IsPreviewControlsCompact == value) return;
            settings.IsPreviewControlsCompact = value;
            Notify(nameof(IsPreviewControlsCompact));
            PersistPreferences();
        }
    }

    public string PreviewPlayLabel => "▶  Reproduzir";
    public string PreviewPauseLabel => "Ⅱ";
    public string PreviewStopLabel => "■";
    // The page's own reading viewport keeps a minimum; the controls around it stay compact.
    public double PreviewScoreMinimumHeight => 240;

    /// <summary>Score focus chosen by the user (the toggle in the Gerar step). It hides the song list and the progress panel
    /// only while the Gerar step is shown; see <see cref="IsScoreFocusApplied"/>. Kept for the session, not persisted.</summary>
    public bool IsScoreFocusMode
    {
        get => scoreFocusMode;
        set
        {
            if (scoreFocusMode == value) return;
            scoreFocusMode = value;
            Notify(nameof(IsScoreFocusMode));
            NotifyScoreFocusLayout();
        }
    }

    public GridLength LibraryColumnWidth => IsScoreFocusApplied ? new GridLength(0) : new GridLength(292);
    public GridLength LibraryGapWidth => IsScoreFocusApplied ? new GridLength(0) : new GridLength(16);
}
