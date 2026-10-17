using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Teclas.App;

public partial class MainWindow
{
    private const double ShortcutSeekSeconds = 5;
    internal static readonly IReadOnlyList<(string Keys, string Action)> KeyboardShortcuts =
    [
        ("Espaço", "Reproduzir ou pausar a partitura selecionada (Biblioteca)"),
        ("Esc", "Parar a reprodução ou a contagem"),
        ("Ctrl+← / Ctrl+→", "Voltar ou avançar 5 segundos"),
        ("Ctrl+M", "Ligar ou desligar o metrônomo"),
        ("Ctrl+Shift+M", "Ligar ou desligar a contagem antes de tocar"),
        ("Ctrl+O", "Escolher arquivos avulsos (MIDI ou MuseScore)"),
        ("Ctrl+Shift+O", "Importar MuseScore"),
        ("Ctrl+L", "Pasta de MIDIs: escolher arquivos de uma pasta, sem copiar"),
        ("Ctrl+P", "Salvar a partitura em PDF"),
        ("Ctrl+F", "Buscar na biblioteca"),
        ("Ctrl+1 / Ctrl+2 / Ctrl+3", "Visão geral, Biblioteca, Produção"),
        ("Ctrl+,", "Configurações"),
        ("F11", "Partitura em tela cheia (com a prévia em foco)"),
        ("F1", "Mostrar esta lista"),
    ];

    private void InitializeShortcuts() => PreviewKeyDown += ShortcutKeyDown;

    private void ShortcutKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleShortcut(key, Keyboard.Modifiers, Keyboard.FocusedElement)) e.Handled = true;
    }

    /// <summary>Returns true when the key belongs to an app shortcut, even if the action is unavailable right now.</summary>
    internal bool HandleShortcut(Key key, ModifierKeys modifiers, IInputElement? focus)
    {
        const ModifierKeys ctrl = ModifierKeys.Control, ctrlShift = ModifierKeys.Control | ModifierKeys.Shift;
        switch (modifiers, key)
        {
            case (ModifierKeys.None, Key.F1): ShortcutsClick(this, new RoutedEventArgs()); return true;
            case (ctrl, Key.O): if (IsIdle) ImportClick(this, new RoutedEventArgs()); return true;
            case (ctrl, Key.L): if (IsIdle) OpenMidiFolderClick(this, new RoutedEventArgs()); return true;
            case (ctrlShift, Key.O): if (IsIdle) ImportMuseScoreClick(this, new RoutedEventArgs()); return true;
            case (ctrl, Key.P): if (CanExportPdf) ExportPdfClick(this, new RoutedEventArgs()); return true;
            case (ctrl, Key.F): FocusLibrarySearch(); return true;
            case (ctrl, Key.M): if (IsIdle) MetronomeEnabled = !MetronomeEnabled; return true;
            case (ctrlShift, Key.M): if (IsIdle) CountInEnabled = !CountInEnabled; return true;
            case (ctrl, Key.D1 or Key.NumPad1): NavigateByShortcut("dashboard"); return true;
            case (ctrl, Key.D2 or Key.NumPad2): NavigateByShortcut("library"); return true;
            case (ctrl, Key.D3 or Key.NumPad3): NavigateByShortcut("production"); return true;
            case (ctrl, Key.OemComma): NavigateByShortcut("settings"); return true;
        }
        // Text fields keep Space, Esc and Ctrl+arrows for editing; buttons and check boxes keep Space for activation.
        bool typing = focus is TextBoxBase or PasswordBox || focus is ComboBox { IsEditable: true };
        if (typing) return false;
        if (modifiers == ModifierKeys.None && key == Key.Space && focus is not ButtonBase && currentPage == "library")
        {
            if (CanPauseScore) PauseScoreClick(this, new RoutedEventArgs());
            else if (countingIn) StopPlayback("Contagem cancelada.");
            else if (CanPlayScore) PlayScoreClick(this, new RoutedEventArgs());
            else return false;
            return true;
        }
        if (modifiers == ModifierKeys.None && key == Key.Escape && focus is not ComboBoxItem && (audioPlaying || countingIn))
        { StopPlayback(); return true; }
        if (modifiers == ctrl && key is Key.Left or Key.Right && CanSeekScore && currentPage == "library")
        {
            PlaybackPositionSeconds += key == Key.Right ? ShortcutSeekSeconds : -ShortcutSeekSeconds;
            return true;
        }
        return false;
    }

    private void NavigateByShortcut(string page)
    {
        if (IsIdle && CommitEdits()) SetCurrentPage(page);
    }

    private void FocusLibrarySearch()
    {
        if (!IsIdle || !CommitEdits()) return;
        SetCurrentPage("library");
        Dispatcher.BeginInvoke(() => { LibrarySearchBox.Focus(); LibrarySearchBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShortcutsClick(object sender, RoutedEventArgs e)
    {
        var window = new KeyboardShortcutsWindow(KeyboardShortcuts) { Owner = this };
        window.ShowDialog();
    }
}

public sealed class KeyboardShortcutsWindow : Window
{
    public KeyboardShortcutsWindow(IReadOnlyList<(string Keys, string Action)> shortcuts)
    {
        Title = "Atalhos de teclado"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; DialogUi.Theme(this);
        var panel = new StackPanel { Margin = new(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Atalhos de teclado", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Espaço, Esc e Ctrl+setas não agem enquanto você digita em um campo de texto.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 16) });
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(190) }); grid.ColumnDefinitions.Add(new());
        foreach (var (keys, action) in shortcuts)
        {
            int row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new());
            var keyText = new TextBlock { Text = keys, FontWeight = FontWeights.SemiBold, Margin = new(0, 4, 12, 4) };
            var actionText = new TextBlock { Text = action, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 4) };
            Grid.SetRow(keyText, row); Grid.SetRow(actionText, row); Grid.SetColumn(actionText, 1);
            grid.Children.Add(keyText); grid.Children.Add(actionText);
        }
        panel.Children.Add(grid);
        var close = DialogUi.Button("Fechar", (_, _) => Close(), true);
        close.IsCancel = true; close.IsDefault = true; close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new(0, 18, 0, 0);
        panel.Children.Add(close);
    }
}
