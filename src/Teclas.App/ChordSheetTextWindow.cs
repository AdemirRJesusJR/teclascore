using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>The score as a text chord sheet (chords above the words), to copy into a message or save as .txt.</summary>
public sealed class ChordSheetTextWindow : Window
{
    private readonly TextBox text;
    private readonly TextBlock status;
    public string SheetText => text.Text;
    internal Func<string, string?>? saveTarget = null;

    public ChordSheetTextWindow(string sheet, string title)
    {
        Title = "Cifra em texto · " + title; Width = 760; Height = 660; MinWidth = 520; MinHeight = 400; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Cifra em texto", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Os acordes ficam sobre a sílaba em que começam, como nos sites de cifras. Copie para mandar numa mensagem ou salve em .txt. Use uma fonte de largura fixa para manter o alinhamento.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        buttons.Children.Add(DialogUi.Button("Copiar", (_, _) => Copy(), true));
        buttons.Children.Add(DialogUi.Button("Salvar .txt", (_, _) => Save()));
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; buttons.Children.Add(close);
        status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; footer.Children.Add(status);
        text = new TextBox { Text = sheet, AcceptsReturn = true, IsReadOnly = false, TextWrapping = TextWrapping.NoWrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 14,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(text, "Cifra em texto da partitura");
        root.Children.Add(text);
    }

    private void Copy()
    {
        try { Clipboard.SetText(text.Text); status.Text = "Cifra copiada."; }
        catch (System.Runtime.InteropServices.ExternalException) { status.Text = "A área de transferência está ocupada por outro programa. Tente de novo."; }
    }

    internal string? Save()
    {
        string? path = saveTarget is { } choose ? choose(Title) : null;
        if (saveTarget is null)
        {
            var dialog = new SaveFileDialog { Title = "Salvar cifra em texto", Filter = "Texto|*.txt", DefaultExt = ".txt", AddExtension = true,
                FileName = ScoreFileName.Stem(null, Title.Replace("Cifra em texto · ", "")) + " - cifra.txt" };
            path = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (path is null) return null;
        File.WriteAllText(path, text.Text, new System.Text.UTF8Encoding(true));
        status.Text = "Cifra salva: " + path;
        return path;
    }
}
