using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

/// <summary>"Digitar a cifra": the whole chord chart as text, one measure between bars, applied measure by measure.</summary>
public sealed class TypedChordsWindow : Window
{
    private readonly TextBox text;
    private readonly TextBlock status;
    private readonly double measure, origin;
    public IReadOnlyList<ChartChord> Result { get; private set; } = [];
    public string Text { get => text.Text; set => text.Text = value; }
    public string StatusText => status.Text;

    public TypedChordsWindow(string title, string chart, double measureQuarters, double origin)
    {
        measure = measureQuarters; this.origin = origin;
        Title = "Digitar a cifra · " + title; Width = 760; Height = 560; MinWidth = 520; MinHeight = 360; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Digitar a cifra", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock
        {
            Text = "Um compasso entre barras: | C | G/B | Am F | % |. Dois acordes no compasso ficam nos tempos 1 e 3; use ponto para segurar uma posição (C . . G = tempos 1 e 4). % repete o compasso anterior; um compasso vazio mantém o acorde que está soando. Vale qualquer escrita: F7M, Bm7(b5), C°, G/B.",
            TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 6, 0, 10)
        });
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        buttons.Children.Add(DialogUi.Button("Aplicar e gerar", (_, _) => Apply(), true));
        status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Firebrick };
        footer.Children.Add(status);
        text = new TextBox { Text = chart, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 16,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(text, "Cifra por compasso");
        root.Children.Add(text);
        Loaded += (_, _) => text.Focus();
    }

    internal bool Apply()
    {
        var (chords, errors) = ChordChartText.Parse(text.Text, measure, origin);
        if (errors.Count > 0) { status.Text = string.Join(" ", errors.Take(3)) + (errors.Count > 3 ? $" (+{errors.Count - 3})" : ""); return false; }
        if (chords.Count == 0) { status.Text = "Digite pelo menos um acorde."; return false; }
        Result = chords; DialogResult = true;
        return true;
    }
}
