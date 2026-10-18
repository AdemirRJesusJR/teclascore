using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Teclas.App;

/// <summary>
/// Review in sequence: the generated scores one after another, the riskiest first, with the page large and keyboard
/// shortcuts: A approves, R makes it again with the next melody candidate, E opens it in the editor, arrows move.
/// </summary>
public sealed class ReviewSequenceWindow : Window
{
    private readonly IReadOnlyList<MidiRow> rows;
    private readonly Action<MidiRow> approve;
    private readonly Func<MidiRow, Task<string>> redo;
    private readonly Image page = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock position = new() { Opacity = .7 }, title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
        reasons = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) }, status = new() { TextWrapping = TextWrapping.Wrap, Opacity = .8 };
    private bool busy;
    public int Index { get; private set; }
    public MidiRow? Current => Index < rows.Count ? rows[Index] : null;
    /// <summary>The song to open in the editor after the window closes (E).</summary>
    public MidiRow? EditRequested { get; private set; }
    public int Approved { get; private set; }
    public string StatusText => status.Text;

    public ReviewSequenceWindow(IReadOnlyList<MidiRow> rows, Action<MidiRow> approve, Func<MidiRow, Task<string>> redo)
    {
        this.rows = rows; this.approve = approve; this.redo = redo;
        Title = "Revisão em sequência"; Width = 980; Height = 900; MinWidth = 640; MinHeight = 520; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(position); top.Children.Add(title); top.Children.Add(reasons);
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Polite);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        buttons.Children.Add(DialogUi.Button("◀", (_, _) => Move(-1)));
        buttons.Children.Add(DialogUi.Button("Aprovar (A)", (_, _) => Approve(), true));
        buttons.Children.Add(DialogUi.Button("Refazer com outra melodia (R)", async (_, _) => await RedoAsync()));
        buttons.Children.Add(DialogUi.Button("Editar (E)", (_, _) => Edit()));
        buttons.Children.Add(DialogUi.Button("▶", (_, _) => Move(1)));
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; buttons.Children.Add(close);
        footer.Children.Add(status);
        var frame = new Border { BorderThickness = new Thickness(1), Child = page, Background = Brushes.White };
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        AutomationProperties.SetName(page, "Página da partitura");
        root.Children.Add(frame);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.OriginalSource is TextBox) return;
            switch (e.Key)
            {
                case Key.A: e.Handled = true; Approve(); break;
                case Key.R: e.Handled = true; await RedoAsync(); break;
                case Key.E: e.Handled = true; Edit(); break;
                case Key.Right: case Key.PageDown: e.Handled = true; Move(1); break;
                case Key.Left: case Key.PageUp: e.Handled = true; Move(-1); break;
            }
        };
        Show(0);
    }

    private void Show(int index)
    {
        Index = Math.Clamp(index, 0, Math.Max(0, rows.Count));
        if (Current is not { } row)
        {
            position.Text = $"{rows.Count} de {rows.Count}"; title.Text = "Fim da revisão"; page.Source = null;
            reasons.Text = $"{Approved} partitura(s) aprovada(s) nesta revisão. Feche ou volte com ◀.";
            return;
        }
        position.Text = $"{Index + 1} de {rows.Count}";
        title.Text = string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle;
        reasons.Text = (row.ConfidenceBadge.Length > 0 ? row.ConfidenceBadge + " — " : "") + row.ConfidenceToolTip;
        page.Source = LoadPage(row.PreviewPath);
        if (page.Source is null) status.Text = "Sem prévia desta partitura; abra no editor (E) para conferir.";
    }

    private static ImageSource? LoadPage(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreImageCache; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException) { return null; }
    }

    internal void Move(int step) { if (!busy) { status.Text = ""; Show(Index + step); } }

    internal void Approve()
    {
        if (busy || Current is not { } row) return;
        approve(row); Approved++;
        status.Text = $"Aprovada: {title.Text}.";
        Show(Index + 1);
    }

    internal async Task RedoAsync()
    {
        if (busy || Current is not { } row) return;
        busy = true; status.Text = "Gerando de novo com a próxima melodia candidata…";
        try { string message = await redo(row); Show(Index); status.Text = message; }
        catch (Exception error) when (error is InvalidOperationException or IOException) { status.Text = error.Message; }
        finally { busy = false; }
    }

    internal void Edit()
    {
        if (busy || Current is not { } row) return;
        EditRequested = row; Close();
    }
}
