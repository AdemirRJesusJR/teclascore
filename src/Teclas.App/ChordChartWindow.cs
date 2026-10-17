using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>One tile per chord of the song, with its keyboard diagram in the chosen position and final key.</summary>
public sealed record ChordChartEntry(string Symbol, string Root, string Quality, int Inversion, int Occurrences, string Notes);

/// <summary>Study sheet of every chord in the song. Built from the current chords, so it never drifts from the score.</summary>
public sealed class ChordChartWindow : Window
{
    private static readonly string[] Sharps = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly string[] Flats = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];
    private readonly FrameworkElement sheet;
    public IReadOnlyList<ChordChartEntry> Entries { get; }

    public static IReadOnlyList<ChordChartEntry> BuildEntries(MidiRow row)
    {
        int fallback = row.EffectiveDiagramInversion, transpose = row.TransposeSemitones;
        return row.Chords
            .Select(chord => row.SimplifiedChords ? new ChordRow { Root = chord.Root, Quality = ChordSimplifier.Simplify(chord.Quality), DiagramInversion = chord.DiagramInversion } : chord)
            .Select(chord => (chord, Shape: DiagramPositionPropagation.Shape(chord.Root, chord.Quality, transpose), Inversion: chord.DiagramInversion ?? fallback))
            .Where(item => item.Shape is not null)
            .GroupBy(item => (item.Shape!.Value, item.Inversion))
            .Select(group =>
            {
                var first = group.First();
                string root = Spell(first.chord.Root, first.Shape!.Value.PitchClass);
                string description = KeyboardDiagramPreview.Describe(first.chord.Root, first.chord.Quality, first.Inversion, transpose);
                // Keep only the keys, low to high: the symbol above is already in the printed key.
                int keys = description.IndexOf("Grave", StringComparison.Ordinal), end = description.IndexOf(". Teclado", StringComparison.Ordinal);
                string notes = keys >= 0 ? description[keys..(end > keys ? end : description.Length)] : description;
                return new ChordChartEntry(root + first.Shape.Value.Quality, first.chord.Root, first.chord.Quality, first.Inversion, group.Count(), notes);
            }).ToArray();
    }

    /// <summary>Printed root in the final key, keeping the original preference for sharps or flats.</summary>
    internal static string Spell(string originalRoot, int pitchClass) =>
        (originalRoot.Contains('b') || originalRoot.Contains('♭') ? Flats : Sharps)[((pitchClass % 12) + 12) % 12];

    public ChordChartWindow(MidiRow row)
    {
        Title = "Acordes da música"; Width = 900; Height = 700; MinWidth = 520; MinHeight = 420; DialogUi.Theme(this);
        Entries = BuildEntries(row);
        var previews = new KeyboardDiagramPreview();
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, -4, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var save = DialogUi.Button("Salvar imagem (PNG)", (_, _) => SavePng()); save.Name = "SaveChordChartButton"; footer.Children.Add(save);
        var print = DialogUi.Button("Imprimir", (_, _) => Print()); footer.Children.Add(print);
        var close = DialogUi.Button("Fechar", (_, _) => Close(), true); close.IsCancel = true; footer.Children.Add(close);

        var page = new StackPanel { Margin = new Thickness(18), VerticalAlignment = VerticalAlignment.Top };
        // A light sheet keeps the PNG and the printout readable in any app theme.
        page.Background = Brushes.White; TextElement.SetForeground(page, Brushes.Black);
        page.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        page.Children.Add(new TextBlock { Text = $"Acordes desta música · {Entries.Count} formas · teclados na posição escolhida" + (row.TransposeSemitones != 0 ? $", no tom final ({row.TransposeSemitones:+0;-0} semitons)" : ""),
            FontSize = 12, Margin = new Thickness(0, 4, 0, 14), TextWrapping = TextWrapping.Wrap });
        var tiles = new WrapPanel();
        foreach (var entry in Entries)
        {
            var tile = new Border { Width = 200, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            tile.BorderBrush = Brushes.LightGray;
            var stack = new StackPanel(); tile.Child = stack;
            stack.Children.Add(new TextBlock { Text = entry.Symbol, FontSize = 20, FontWeight = FontWeights.SemiBold });
            if (previews.GetImage(entry.Root, entry.Quality, entry.Inversion, row.TransposeSemitones) is { } image)
                stack.Children.Add(new Image { Source = image, Height = 58, Stretch = Stretch.Uniform, Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Left });
            stack.Children.Add(new TextBlock { Text = ScoreMetadataDefaults.InversionLabel(entry.Inversion) + $" · {entry.Occurrences}×", FontSize = 11 });
            stack.Children.Add(new TextBlock { Text = entry.Notes, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            AutomationProperties.SetName(tile, $"{entry.Symbol}, {entry.Notes}");
            tiles.Children.Add(tile);
        }
        if (Entries.Count == 0) page.Children.Add(new TextBlock { Text = "Esta música ainda não tem cifras. Analise os acordes primeiro." });
        page.Children.Add(tiles);
        sheet = page;
        root.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private RenderTargetBitmap Render()
    {
        sheet.UpdateLayout();
        int width = (int)Math.Ceiling(Math.Max(1, sheet.ActualWidth)), height = (int)Math.Ceiling(Math.Max(1, sheet.ActualHeight));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(sheet), null, new Rect(0, 0, width, height));
        }
        // 192 dpi doubles the pixels for a sharp printout; the layout stays in device-independent units.
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32); bitmap.Render(visual);
        return bitmap;
    }

    internal string? SavePng(string? path = null)
    {

        if (path is null)
        {
            var dialog = new SaveFileDialog { Title = "Salvar acordes da música", Filter = "Imagem PNG|*.png", FileName = Title + ".png", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return null;
            path = dialog.FileName;
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render()));
        using (var output = File.Create(path)) encoder.Save(output);
        return path;
    }

    private void Print()
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        // Print in a light page regardless of the app theme.
        var image = new Image { Source = Render(), Stretch = Stretch.Uniform, Width = dialog.PrintableAreaWidth - 48, Height = dialog.PrintableAreaHeight - 48 };
        var page = new Border { Background = Brushes.White, Padding = new Thickness(24), Child = image };
        page.Measure(new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight)); page.Arrange(new Rect(page.DesiredSize));
        dialog.PrintVisual(page, Title);
    }
}
