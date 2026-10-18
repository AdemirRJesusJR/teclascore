using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Teclas.App;

/// <summary>The preview tab is split: controls above, score below. Dragging gives the score more or less room and is remembered.</summary>
public partial class MainWindow
{
    // Automatic size keeps at least the score properties and the player visible.
    private const double MinimumAutomaticControls = 200;
    private void InitializePreviewSplit() => Loaded += (_, _) => ApplyPreviewSplit();

    private void ApplyPreviewSplit() =>
        PreviewControlsRow.Height = settings.PreviewControlsHeight is { } height ? new GridLength(height) : GridLength.Auto;

    /// <summary>The controls never take the score's minimum room: beyond it they scroll inside their own area.</summary>
    private void PreviewTabResized(object sender, SizeChangedEventArgs e) =>
        PreviewControlsRow.MaxHeight = Math.Max(MinimumAutomaticControls, PreviewTabGrid.ActualHeight - PreviewScoreMinimumHeight - 10);

    private void PreviewSplitterDragged(object sender, DragCompletedEventArgs e)
    {
        settings.PreviewControlsHeight = Math.Round(Math.Clamp(PreviewControlsRow.ActualHeight, Settings.MinimumPreviewControlsHeight, 1200));
        ApplyPreviewSplit(); PersistPreferences();
    }

    private void PreviewSplitterReset(object sender, MouseButtonEventArgs e)
    {
        settings.PreviewControlsHeight = null; ApplyPreviewSplit(); PersistPreferences();
        Status = "Altura da partitura no tamanho automático.";
    }

    /// <summary>For smoke tests: the height the controls row uses (null: automatic).</summary>
    internal double? PreviewControlsHeight { get => settings.PreviewControlsHeight; set { settings.PreviewControlsHeight = value; ApplyPreviewSplit(); } }
}
