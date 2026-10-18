using System.Windows;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Guided review of doubtful chords, one at a time: hear the passage of the MIDI around the chord, hear the chosen chord
/// and each alternative in the diagram position, then keep it or use another. Changes apply to the card as you go.
/// </summary>
public sealed class ChordReviewWindow : Window
{
    private readonly IReadOnlyList<ChordRow> chords;
    private readonly Action<double> listenPassage;
    private readonly Action<string, string, int?> listenChord;
    private readonly Action<ChordRow, ChordAlternative> use;
    private readonly TextBlock heading, detail;
    private readonly WrapPanel choices;
    private readonly Button back, keep;
    private int index;
    public int Index => index;
    public int Changed { get; private set; }

    /// <summary>Chords worth a second look: low support from the notes, or alternatives offered by the analysis.</summary>
    public static IReadOnlyList<ChordRow> Doubtful(IEnumerable<ChordRow> chords) =>
        chords.Where(c => c.Confidence is > 0 and < ConversionQueueSummary.LowConfidence || ChordAlternative.Parse(c.Alternatives).Count > 0).ToArray();

    public ChordReviewWindow(IReadOnlyList<ChordRow> chords, Action<double> listenPassage, Action<string, string, int?> listenChord, Action<ChordRow, ChordAlternative> use)
    {
        Title = "Revisar cifras com dúvida"; Width = 620; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; DialogUi.Theme(this);
        this.chords = chords; this.listenPassage = listenPassage; this.listenChord = listenChord; this.use = use;
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        heading = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold }; panel.Children.Add(heading);
        detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) }; panel.Children.Add(detail);
        var listen = new WrapPanel { Margin = new Thickness(-4, 0, 0, 8) }; panel.Children.Add(listen);
        listen.Children.Add(DialogUi.Button("▶ Ouvir o trecho do MIDI", (_, _) => { if (Current is { } c) listenPassage(c.Quarter); }));
        choices = new WrapPanel { Margin = new Thickness(-4, 0, 0, 8) }; panel.Children.Add(choices);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(buttons);
        back = DialogUi.Button("◀ Anterior", (_, _) => Move(-1)); buttons.Children.Add(back);
        keep = DialogUi.Button("Manter e seguir ▶", (_, _) => Move(1), true); buttons.Children.Add(keep);
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; buttons.Children.Add(close);
        Show(0);
    }

    private ChordRow? Current => index < chords.Count ? chords[index] : null;

    private void Show(int at)
    {
        index = Math.Clamp(at, 0, chords.Count);
        choices.Children.Clear();
        back.IsEnabled = index > 0;
        if (Current is not { } chord)
        {
            heading.Text = "Revisão concluída";
            detail.Text = Changed == 0 ? "Nenhuma cifra foi trocada." : $"{Changed} cifra(s) trocada(s). Gere de novo para atualizar a partitura e os diagramas.";
            keep.IsEnabled = false; return;
        }
        keep.IsEnabled = true;
        heading.Text = $"Cifra {index + 1} de {chords.Count}: {chord.Root}{chord.Quality}";
        detail.Text = (chord.Confidence is > 0 and < ConversionQueueSummary.LowConfidence ? $"Apoio das notas: {chord.Confidence:P0}. " : "") +
            (chord.Recommendation.Length > 0 ? chord.Recommendation + " " : "") + $"Posição: tempo {chord.Quarter:0.#} do MIDI.";
        Add($"▶ {chord.Root}{chord.Quality}", "Ouvir a cifra escolhida", () => listenChord(chord.Root, chord.Quality, chord.DiagramInversion));
        foreach (var alternative in ChordAlternative.Parse(chord.Alternatives))
        {
            Add($"▶ {alternative.Label}", "Ouvir esta alternativa", () => listenChord(alternative.Root, alternative.Quality, chord.DiagramInversion));
            Add($"Usar {alternative.Label}", "Trocar por esta alternativa e seguir", () => { use(chord, alternative); Changed++; Move(1); });
        }
    }

    private void Add(string text, string tip, Action action)
    {
        var button = DialogUi.Button(text, (_, _) => action()); button.ToolTip = tip; choices.Children.Add(button);
    }

    internal void Move(int step) => Show(index + step);
    internal void UseFirstAlternative() { if (Current is { } c && ChordAlternative.Parse(c.Alternatives).FirstOrDefault() is { } a) { use(c, a); Changed++; Move(1); } }
}
