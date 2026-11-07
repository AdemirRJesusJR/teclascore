using System.Windows;

namespace Teclas.App;

/// <summary>The preview tab stacks the docked player above the score. The player keeps its natural (compact) height;
/// a height saved by older versions, when a splitter resized it, is ignored.</summary>
public partial class MainWindow
{
    // The player never takes the score's minimum room: beyond it the controls scroll inside their own area.
    private const double MinimumAutomaticControls = 120;
    private void InitializePreviewSplit() => Loaded += (_, _) => PreviewControlsRow.Height = GridLength.Auto;

    private void PreviewTabResized(object sender, SizeChangedEventArgs e) =>
        PreviewControlsRow.MaxHeight = Math.Max(MinimumAutomaticControls, PreviewTabGrid.ActualHeight - PreviewScoreMinimumHeight - 10);
}
