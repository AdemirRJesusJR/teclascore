using System.Windows;
using System.Windows.Threading;

namespace Teclas.App;

public partial class MainWindow
{
    private bool previewAlignmentQueued;

    private void InitializePreviewLayout()
    {
        Loaded += (_, _) => QueuePreviewAlignment();
        ScoreEditorScrollViewer.SizeChanged += (_, _) => QueuePreviewAlignment();
        EditorTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, EditorTabs)) QueuePreviewAlignment();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CurrentPage) or nameof(SelectedFile)) QueuePreviewAlignment();
        };
    }

    public bool IsPreviewControlsCompact
    {
        get => settings.IsPreviewControlsCompact;
        set
        {
            if (settings.IsPreviewControlsCompact == value) return;
            settings.IsPreviewControlsCompact = value;
            foreach (var name in new[] { nameof(IsPreviewControlsCompact), nameof(ExpandedPreviewControlsVisibility),
                nameof(CompactPreviewControlsVisibility), nameof(PreviewControlsToggleLabel), nameof(PreviewPlayLabel),
                nameof(PreviewPauseLabel), nameof(PreviewStopLabel), nameof(PreviewTransportPadding),
                nameof(PreviewStatusWrapping), nameof(PreviewScoreMinimumHeight), nameof(ScoreEditorMinimumHeight) }) Notify(name);
            // This changes presentation only: audio, A–B, speed and page following
            // keep their existing controls, bindings and state.
            PersistPreferences();
            QueuePreviewAlignment(resetExpanded: true);
        }
    }

    public Visibility ExpandedPreviewControlsVisibility => IsPreviewControlsCompact ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CompactPreviewControlsVisibility => IsPreviewControlsCompact ? Visibility.Visible : Visibility.Collapsed;
    public string PreviewControlsToggleLabel => IsPreviewControlsCompact ? "Expandir controles" : "Compactar controles";
    public string PreviewPlayLabel => IsPreviewControlsCompact ? "▶" : "▶  Reproduzir";
    public string PreviewPauseLabel => IsPreviewControlsCompact ? "Ⅱ" : "Ⅱ  Pausar";
    public string PreviewStopLabel => IsPreviewControlsCompact ? "■" : "■  Parar";
    public Thickness PreviewTransportPadding => IsPreviewControlsCompact ? new(7, 4, 7, 4) : new(9, 6, 9, 6);
    public TextWrapping PreviewStatusWrapping => IsPreviewControlsCompact ? TextWrapping.NoWrap : TextWrapping.Wrap;
    // Compact the surrounding controls, never the page's own reading viewport.
    public double PreviewScoreMinimumHeight => 240;

    private void QueuePreviewAlignment(bool resetExpanded = false)
    {
        if ((!IsPreviewControlsCompact && !resetExpanded) || previewAlignmentQueued || Dispatcher.HasShutdownStarted) return;
        previewAlignmentQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            previewAlignmentQueued = false;
            if (!IsLoaded || operationsDisposed || CurrentPage != "library" || EditorTabs.SelectedIndex != 2
                || !ScoreEditorScrollViewer.IsVisible) return;
            ScoreEditorScrollViewer.UpdateLayout();
            if (!IsPreviewControlsCompact) { ScoreEditorScrollViewer.ScrollToTop(); return; }
            // Scroll only this editor's outer viewport. The page viewer, library
            // list, page selection and playback position are left untouched.
            double playerTop = PreviewPlayerPanel.TranslatePoint(new Point(0, 0), ScoreEditorScrollViewer).Y;
            double target = ScoreEditorScrollViewer.VerticalOffset + playerTop - 4;
            ScoreEditorScrollViewer.ScrollToVerticalOffset(Math.Clamp(target, 0, ScoreEditorScrollViewer.ScrollableHeight));
        }));
    }

    private void TogglePreviewControlsClick(object sender, RoutedEventArgs e) => IsPreviewControlsCompact = !IsPreviewControlsCompact;
}
