using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Chord sheet pasted as text: the chords it uses, shown with keyboard diagrams (and the remembered positions).</summary>
public sealed class ChordSheetWindow : Window
{
    private readonly TextBox text, title;
    private readonly TextBlock summary;
    private readonly Button chart;
    private readonly Func<string, string, int?> remembered;
    private readonly int defaultInversion;
    public ChordSheet Sheet { get; private set; } = new([], [], []);
    internal Func<ChordChartWindow, bool?> chartDialog = window => window.ShowDialog();
    private readonly Button score;
    private readonly TextBlock keyLabel;
    private int shifted;
    /// <summary>Creates the MuseScore score from the measures and title (set by the main window; null hides the button).</summary>
    internal Func<IReadOnlyList<SheetMeasure>, string, Task<bool>>? createScore;

    public ChordSheetWindow(int defaultInversion, Func<string, string, int?> rememberedInversion)
    {
        Title = "Cifra em texto"; Width = 760; Height = 640; MinWidth = 560; MinHeight = 440; DialogUi.Theme(this);
        this.defaultInversion = defaultInversion; remembered = rememberedInversion;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Cifra em texto", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Cole a cifra (acordes sobre a letra, como nos sites de cifras). O app encontra os acordes e mostra cada um com o diagrama de teclado, nas posições que você já escolheu.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
        var titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; top.Children.Add(titleRow);
        titleRow.Children.Add(new TextBlock { Text = "Música", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) }; DockPanel.SetDock(tools, Dock.Right); titleRow.Children.Add(tools);
        keyLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), MinWidth = 70 };
        tools.Children.Add(keyLabel);
        Button Tool(string text, string tip, string name, Action action)
        {
            var button = DialogUi.Button(text, (_, _) => action()); button.ToolTip = tip; button.Name = name; button.Padding = new Thickness(10, 4, 10, 4); button.MinHeight = 30;
            tools.Children.Add(button); return button;
        }
        Tool("½ ↓", "Descer meio tom: reescreve os acordes sobre a letra", "SheetDownButton", () => Shift(-1));
        Tool("½ ↑", "Subir meio tom: reescreve os acordes sobre a letra", "SheetUpButton", () => Shift(1));
        Tool("Simplificar", "Troca os acordes por tríades (Am7 → Am, G7 → G), para iniciantes", "SheetSimplifyButton", Simplify);
        title = new TextBox { Text = "Cifra" }; AutomationProperties.SetName(title, "Nome da música"); titleRow.Children.Add(title);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        score = DialogUi.Button("Criar partitura", async (_, _) => await CreateScoreAsync()); score.IsEnabled = false; score.Name = "ChordSheetScoreButton";
        score.ToolTip = "Gera uma partitura MuseScore: um compasso por acorde, com o diagrama e a letra de cada trecho embaixo, uma linha por linha da cifra.";
        buttons.Children.Add(score);
        chart = DialogUi.Button("Ver acordes com diagramas", (_, _) => ShowChart(), true); chart.IsEnabled = false; buttons.Children.Add(chart);
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; buttons.Children.Add(close);
        summary = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; footer.Children.Add(summary);
        text = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13 };
        AutomationProperties.SetName(text, "Texto da cifra");
        text.TextChanged += (_, _) => Analyze();
        root.Children.Add(text);
        Analyze();
    }

    internal void SetText(string value) => text.Text = value;
    internal string Text => text.Text;

    /// <summary>Transposes the pasted sheet by semitones, keeping each chord over its words.</summary>
    internal void Shift(int semitones)
    {
        if (Sheet.Distinct.Count == 0) return;
        text.Text = ChordSheetParser.Transform(text.Text, semitones, false);
        shifted += semitones;
        keyLabel.Text = shifted == 0 ? "" : $"{shifted:+0;-0} semitons";
    }

    internal void Simplify()
    {
        if (Sheet.Distinct.Count == 0) return;
        text.Text = ChordSheetParser.Transform(text.Text, 0, true);
    }

    private void Analyze()
    {
        Sheet = ChordSheetParser.Parse(text.Text);
        chart.IsEnabled = Sheet.Distinct.Count > 0;
        if (score is not null) score.IsEnabled = Sheet.Distinct.Count > 0 && createScore is not null;
        summary.Text = Sheet.Sequence.Count == 0 ? "Nenhum acorde encontrado ainda."
            : $"{Sheet.Sequence.Count} acordes, {Sheet.Distinct.Count} diferentes: {string.Join("  ", Sheet.Distinct.Select(c => c.Symbol))}" +
              (Sheet.Unknown.Count > 0 ? $" · não reconhecidos: {string.Join(", ", Sheet.Unknown)}" : "");
    }

    internal void RefreshActions() => Analyze();

    /// <summary>The chords in order, one measure each, with the remembered diagram positions.</summary>
    internal IReadOnlyList<SheetMeasure> ScoreMeasures() => ChordSheetParser.Measures(text.Text);

    private async Task CreateScoreAsync()
    {
        var measures = ScoreMeasures();
        if (measures.Count == 0 || createScore is null) return;
        score.IsEnabled = false; summary.Text = "Criando a partitura no MuseScore…";
        try
        {
            if (await createScore(measures, title.Text.Trim().Length > 0 ? title.Text.Trim() : "Cifra")) Close();
            else Analyze();
        }
        finally { if (IsLoaded) score.IsEnabled = Sheet.Distinct.Count > 0; }
    }

    /// <summary>The distinct chords as a card, so the existing chord chart draws, saves and prints them.</summary>
    internal MidiRow ChartRow()
    {
        var row = new MidiRow { FullPath = "cifra-em-texto", DefaultDiagramInversion = defaultInversion, ScoreTitle = title.Text.Trim() };
        double quarter = 0;
        foreach (var chord in Sheet.Distinct)
            row.Chords.Add(new ChordRow { Quarter = quarter++, Root = chord.Root, Quality = chord.Quality, Bass = chord.Bass, DiagramInversion = remembered(chord.Root, chord.Quality) });
        return row;
    }

    private void ShowChart()
    {
        if (Sheet.Distinct.Count == 0) return;
        var window = new ChordChartWindow(ChartRow()) { Owner = this, Title = "Acordes · " + (title.Text.Trim().Length > 0 ? title.Text.Trim() : "Cifra") };
        chartDialog(window);
    }
}
