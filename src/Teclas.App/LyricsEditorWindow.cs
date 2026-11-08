using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Editar › Letra…: the melody notes with their syllables, verse by verse. Correct a syllable, move it to the
/// neighbouring note, join or split syllables, link words with a hyphen, or paste a whole lyric. Saving makes a new
/// version of the score (the music itself never changes) and the previous one stays in the History.
/// </summary>
public sealed class LyricsEditorWindow : Window
{
    private readonly ListBox notes;
    private readonly TextBox syllable, pasted;
    private readonly ItemsControl verses;
    private readonly TextBlock status;
    private readonly Button save;
    private bool refreshing;
    public LyricsEditorDocument Document { get; }

    public LyricsEditorWindow(LyricsEditorDocument document, string title)
    {
        Document = document;
        Title = "Letra · " + title; Width = 900; Height = 660; MinWidth = 640; MinHeight = 440; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;

        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Letra", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10),
            Text = "Cada linha é uma nota da melodia. Escreva a sílaba (termine com - quando a palavra continua), mova para a nota vizinha ou cole uma letra inteira. As notas e os ritmos não mudam; a versão anterior fica no Histórico." });

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        save = DialogUi.Button("Salvar nova versão", (_, _) => { Commit(); if (Document.HasChanges) DialogResult = true; else status.Text = "A letra não mudou."; }, true); save.Name = "SaveLyricsButton";
        buttons.Children.Add(save);
        footer.Children.Add(status);

        var grid = new Grid(); root.Children.Add(grid);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: the notes and the selected syllable's actions.
        var left = new DockPanel(); grid.Children.Add(left);
        var edit = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(edit, Dock.Bottom); left.Children.Add(edit);
        var line = new DockPanel();
        line.Children.Add(new TextBlock { Text = "Sílaba", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        syllable = new TextBox { MaxLength = 65, Name = "LyricSyllableBox" }; AutomationProperties.SetName(syllable, "Sílaba da nota escolhida");
        syllable.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Step(1); e.Handled = true; } };
        syllable.LostKeyboardFocus += (_, _) => Commit();
        line.Children.Add(syllable); edit.Children.Add(line);
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; edit.Children.Add(actions);
        actions.Children.Add(Small("◀ Mover", "Passa a sílaba para a nota anterior", () => Act(i => Document.Move(i, -1), -1)));
        actions.Children.Add(Small("Mover ▶", "Passa a sílaba para a próxima nota; as seguintes andam junto até a primeira nota livre", () => Act(i => Document.Move(i, 1), 1)));
        actions.Children.Add(Small("Juntar", "Junta com a próxima sílaba nesta nota", () => Act(Document.JoinWithNext, 0)));
        actions.Children.Add(Small("Dividir", "Divide a sílaba onde está o cursor; a outra parte vai para a próxima nota", () => Act(i => Document.Split(i, syllable.CaretIndex), 0)));
        actions.Children.Add(Small("Hífen", "Liga à próxima sílaba como a mesma palavra, ou separa as palavras", () => Act(Document.ToggleHyphen, 0)));
        actions.Children.Add(Small("Apagar", "Tira a sílaba desta nota", () => Act(i => Document.SetText(i, ""), 0)));
        notes = new ListBox { FontFamily = new System.Windows.Media.FontFamily("Consolas"), Name = "LyricNotesList" };
        AutomationProperties.SetName(notes, "Notas da melodia e suas sílabas");
        VirtualizingPanel.SetIsVirtualizing(notes, true);
        var compact = new Style(typeof(ListBoxItem), TryFindResource(typeof(ListBoxItem)) as Style);
        compact.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2))); compact.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        compact.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        notes.ItemContainerStyle = compact;
        notes.SelectionChanged += (_, _) => { if (!refreshing) ShowSelected(); };
        left.Children.Add(notes);

        // Right: the verses as they read, and pasting a whole lyric.
        var right = new DockPanel(); Grid.SetColumn(right, 2); grid.Children.Add(right);
        var paste = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(paste, Dock.Bottom); right.Children.Add(paste);
        paste.Children.Add(new TextBlock { Text = "Colar uma letra (linha = verso, linha em branco = estrofe)", TextWrapping = TextWrapping.Wrap });
        pasted = new TextBox { AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top, Height = 120, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 4), Name = "LyricPasteBox" };
        AutomationProperties.SetName(pasted, "Letra para distribuir nas notas");
        paste.Children.Add(pasted);
        var distribute = DialogUi.Button("Distribuir a partir da nota escolhida", (_, _) => Paste()); distribute.HorizontalAlignment = HorizontalAlignment.Left; distribute.Margin = new Thickness(0, 2, 0, 0);
        distribute.ToolTip = "Divide as palavras em sílabas e põe uma por nota, substituindo a letra daí em diante. Confira depois: a divisão automática não conhece todas as exceções.";
        paste.Children.Add(distribute);
        var heading = new TextBlock { Text = "Versos", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) }; DockPanel.SetDock(heading, Dock.Top); right.Children.Add(heading);
        verses = new ItemsControl { Name = "LyricVersesList" };
        right.Children.Add(new ScrollViewer { Content = verses, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Refresh(0);
        if (Document.Slots.Count == 0) status.Text = "A pauta de cima não tem notas para receber a letra.";
        Loaded += (_, _) => syllable.Focus();
    }

    private Button Small(string text, string tip, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4), ToolTip = tip };
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    internal int Selected => notes.SelectedIndex;
    internal void Select(int index) { notes.SelectedIndex = Math.Clamp(index, 0, Math.Max(0, Document.Slots.Count - 1)); ShowSelected(); }
    internal string SyllableText { get => syllable.Text; set => syllable.Text = value; }
    internal string PastedText { get => pasted.Text; set => pasted.Text = value; }
    internal string StatusText => status.Text;
    internal IReadOnlyList<string> VerseTexts => verses.Items.Cast<TextBlock>().Select(t => t.Text).ToArray();

    private void Step(int delta) { if (Document.Slots.Count > 0) Select(Selected + delta); syllable.SelectAll(); }

    private static string Show(LyricSlot slot) => slot.Text.Length == 0 ? "" : slot.Text + (slot.Continues ? "-" : "");

    private void ShowSelected()
    {
        if (Selected < 0) { syllable.Text = ""; return; }
        syllable.Text = Show(Document.Slots[Selected]);
    }

    /// <summary>Writes the syllable box into the selected note.</summary>
    internal void Commit()
    {
        if (refreshing || Selected < 0 || Selected >= Document.Slots.Count) return;
        if (syllable.Text.Trim() == Show(Document.Slots[Selected])) return;
        int index = Selected;
        Document.SetText(index, syllable.Text);
        Refresh(index);
    }

    /// <summary>Runs an action on the selected note and keeps the selection on the moved syllable.</summary>
    internal void Act(Action<int> action, int follow)
    {
        Commit();
        if (Selected < 0) { status.Text = "Escolha uma nota da lista."; return; }
        int index = Selected;
        try { action(index); status.Text = ""; Refresh(index + follow); }
        catch (InvalidOperationException error) { status.Text = error.Message; }
    }

    internal void Paste()
    {
        Commit();
        int from = Math.Max(0, Selected);
        try
        {
            int left = Document.Paste(pasted.Text, from);
            Refresh(from);
            status.Text = left > 0 ? $"{left} sílaba(s) ficaram sem nota e não foram usadas; nenhuma nota é criada." : "Letra distribuída. Confira as divisões e use Mover quando uma sílaba cair na nota errada.";
        }
        catch (InvalidOperationException error) { status.Text = error.Message; }
    }

    private void Refresh(int select)
    {
        refreshing = true;
        try
        {
            notes.Items.Clear();
            int verse = 0;
            var starts = Document.Verses().Select(v => v.First).ToHashSet();
            for (int i = 0; i < Document.Slots.Count; i++)
            {
                var slot = Document.Slots[i];
                if (starts.Contains(i)) verse++;
                notes.Items.Add($"{(starts.Contains(i) ? $"v{verse,-3}" : "    ")} c.{slot.Measure,-4} {slot.NoteName,-4} {Show(slot)}");
            }
            verses.Items.Clear();
            foreach (var item in Document.Verses())
                verses.Items.Add(new TextBlock { Text = item.Text.Length == 0 ? "(sem letra)" : item.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            if (Document.Slots.Count > 0) notes.SelectedIndex = Math.Clamp(select, 0, Document.Slots.Count - 1);
            if (notes.SelectedItem is { } item2) notes.ScrollIntoView(item2);
        }
        finally { refreshing = false; }
        ShowSelected();
    }
}
