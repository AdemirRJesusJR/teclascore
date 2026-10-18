using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    internal Func<ChordSheetWindow, bool?> chordSheetDialog = window => window.ShowDialog();

    /// <summary>Position remembered for a chord in the library (null: the default position).</summary>
    internal int? RememberedInversion(string root, string quality) =>
        DiagramPositionPropagation.Shape(root, quality, 0) is { } shape && chordPreferences.TryGetValue(shape, out int inversion) ? inversion : null;

    private void ChordSheetClick(object sender, RoutedEventArgs e) =>
        chordSheetDialog(new ChordSheetWindow(settings.DefaultDiagramInversion, RememberedInversion) { Owner = this });
}
