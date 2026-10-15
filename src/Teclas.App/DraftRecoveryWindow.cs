using System.IO;
using System.Windows;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

public sealed class DraftRecoveryWindow : Window
{
    private sealed record Item(EditorDraftCheckpoint Draft)
    {
        public string Label => Draft.Title + " · " + Draft.UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    }
    private readonly EditorDraftRecoveryStore store;
    private readonly ListBox list;
    private readonly TextBlock detail;
    public EditorDraftCheckpoint? SelectedDraft { get; private set; }
    public DraftRecoveryWindow(EditorDraftRecoveryStore store)
    {
        this.store = store; Title = "Recuperar rascunhos · TeclaScore"; Width = 760; Height = 480; MinWidth = 560; MinHeight = 350; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new(24) }; Content = root;
        var header = new TextBlock { Text = "Continue de onde parou", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) }; DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        list = new ListBox { Name = "RecoveryDrafts", DisplayMemberPath = nameof(Item.Label) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        detail = new TextBlock { Name = "RecoveryDetails", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) }; footer.Children.Add(detail);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        actions.Children.Add(DialogUi.Button("Descartar rascunho", (_, _) => Discard()));
        actions.Children.Add(DialogUi.Button("Fechar", (_, _) => Close()));
        var recover = DialogUi.Button("Recuperar", (_, _) => { if (list?.SelectedItem is Item selected) { SelectedDraft = selected.Draft; DialogResult = true; } }, true); recover.Name = "RecoverDraftButton"; actions.Children.Add(recover);
        root.Children.Add(list);
        list.SelectionChanged += (_, _) => { recover.IsEnabled = list.SelectedItem is Item; if (list.SelectedItem is Item selected) detail.Text = "Base preservada de: " + selected.Draft.SourcePath + "\nA recuperação abre uma cópia e mantém a partitura original."; };
        Reload(); recover.IsEnabled = list.SelectedItem is Item;
    }
    private void Reload()
    {
        var index = store.List(); list.ItemsSource = index.Drafts.Select(d => new Item(d)).ToArray(); list.SelectedIndex = index.Drafts.Count > 0 ? 0 : -1;
        if (index.Drafts.Count == 0) detail.Text = "Nenhum rascunho pendente. As edições do Editor musical são protegidas automaticamente.";
        if (index.Warnings.Count > 0) detail.Text += "\n" + string.Join("\n", index.Warnings);
    }
    private void Discard()
    {
        if (list.SelectedItem is not Item selected || MessageBox.Show(this, "Descartar este rascunho? A partitura original será mantida.", "Rascunho", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { store.Discard(selected.Draft); Reload(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { detail.Text = "Não foi possível descartar o rascunho. " + error.Message; }
    }
}
