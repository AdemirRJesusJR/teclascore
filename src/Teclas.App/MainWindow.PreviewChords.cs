using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Editing chords from the score: right click a measure in the preview to change its chord symbols.</summary>
public partial class MainWindow
{
    internal Func<string, NativeMusicModel> previewChordModel = path => NativeMusicEditor.Open(path, CancellationToken.None);
    internal Func<MeasureChordsWindow, bool?> measureChordsDialog = window => window.ShowDialog();

    private void InitializePreviewChords() => MainScoreViewer.MeasureContextRequested += async (_, measure) => await EditMeasureChords(measure);

    /// <summary>
    /// The score's chord symbols are the card's chords in the same order (one per chord, top staff); a measure's symbols are
    /// found by position. When the counts differ (simplified chords, edited score) the table stays the way to edit.
    /// </summary>
    internal async Task EditMeasureChords(int measure)
    {
        if (measure == 0) { Status = "Para editar cifras clicando na partitura, prepare o acompanhamento (botão Preparar, em Páginas)."; return; }
        if (!IsIdle || selectedFile is not { UsesNativeScoreWorkflow: false } row || CurrentScorePath is not { } path || row.Chords.Count == 0) return;
        NativeMusicModel model;
        try { model = await Task.Run(() => previewChordModel(path)); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Xml.XmlException or InvalidOperationException)
        { Status = "Não foi possível ler as cifras da partitura: " + error.Message; return; }
        var harmonies = model.Measures.SelectMany(m => m.Harmonies).Where(h => h.Quarter is not null).ToArray();
        int top = harmonies.Length == 0 ? 1 : harmonies.Min(h => h.Staff);
        var ordered = harmonies.Where(h => h.Staff == top).OrderBy(h => h.Measure).ThenBy(h => h.Quarter).ToArray();
        var chords = row.Chords.OrderBy(c => c.Quarter).ToArray();
        if (ordered.Length != chords.Length || row.SimplifiedChords)
        { Status = "As cifras desta partitura não correspondem uma a uma à tabela (cifras simplificadas ou partitura editada). Use a aba Cifras."; return; }
        var here = ordered.Select((harmony, index) => (harmony, chord: chords[index])).Where(item => item.harmony.Measure == measure).Select(item => item.chord).ToArray();
        if (here.Length == 0) { Status = $"O compasso {measure} não tem cifra."; return; }
        var dialog = new MeasureChordsWindow(measure, here, (root, quality, inversion) => PreviewChord(root, quality, inversion ?? row.EffectiveDiagramInversion)) { Owner = this };
        if (measureChordsDialog(dialog) != true || dialog.Changes.Count == 0) return;
        RememberVersion(row);
        foreach (var (chord, root, quality) in dialog.Changes) { chord.Root = root; chord.Quality = quality; }
        if (row.ScorePath is not null) row.HasPendingScoreProperties = true;
        RefreshSelection(); PersistPreferences(); RefreshChordAlternatives();
        Status = $"Compasso {measure}: {string.Join(", ", dialog.Changes.Select(change => change.Root + change.Quality))}. Gere de novo para atualizar a partitura e os diagramas.";
    }
}

/// <summary>The chord symbols of one measure, each with root, type and a button to hear it.</summary>
public sealed class MeasureChordsWindow : Window
{
    private static readonly string[] Roots = ["C", "C#", "Db", "D", "D#", "Eb", "E", "F", "F#", "Gb", "G", "G#", "Ab", "A", "A#", "Bb", "B"];
    private readonly List<(ChordRow Chord, ComboBox Root, ComboBox Quality)> editors = [];
    public IReadOnlyList<(ChordRow Chord, string Root, string Quality)> Changes { get; private set; } = [];
    internal IReadOnlyList<(ChordRow Chord, ComboBox Root, ComboBox Quality)> Editors => editors;

    public MeasureChordsWindow(int measure, IReadOnlyList<ChordRow> chords, Action<string, string, int?> listen)
    {
        Title = $"Cifras do compasso {measure}"; Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; DialogUi.Theme(this);
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = $"Compasso {measure}", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Troque a nota e o tipo; ouça antes de aplicar. A partitura é atualizada ao gerar de novo.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
        foreach (var chord in chords)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) }; panel.Children.Add(line);
            var root = new ComboBox { ItemsSource = Roots, SelectedItem = Roots.Contains(chord.Root) ? chord.Root : "C", Width = 72, Margin = new Thickness(0, 0, 8, 0) };
            var quality = new ComboBox { ItemsSource = KeyboardDiagramVoicing.Qualities.Select(q => new { Value = q, Label = q.Length == 0 ? "maior" : q }).ToArray(),
                DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = chord.Quality, Width = 120, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(root, "Nota da cifra"); AutomationProperties.SetName(quality, "Tipo da cifra");
            line.Children.Add(root); line.Children.Add(quality);
            line.Children.Add(DialogUi.Button("▶ Ouvir", (_, _) => listen((string)root.SelectedItem, (string?)quality.SelectedValue ?? "", chord.DiagramInversion)));
            editors.Add((chord, root, quality));
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(buttons);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        var apply = DialogUi.Button("Aplicar", (_, _) => { Accept(); DialogResult = true; }, true); apply.IsDefault = true; buttons.Children.Add(apply);
    }

    /// <summary>Collects the chords whose root or type changed.</summary>
    internal void Accept() => Changes = editors
        .Select(e => (e.Chord, Root: (string)e.Root.SelectedItem, Quality: (string?)e.Quality.SelectedValue ?? ""))
        .Where(e => e.Root != e.Chord.Root || e.Quality != e.Chord.Quality).ToArray();
}
