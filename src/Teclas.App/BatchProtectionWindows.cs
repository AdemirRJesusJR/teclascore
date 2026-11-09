using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// "O que vai acontecer": the preview shown before the Piloto automático or a batch action runs. Each group lists its
/// songs with the reason; the songs that will be worked on can be unticked right here. The header tells how many go in
/// each group, the estimated time and that a restore point is saved first.
/// </summary>
public sealed class BatchPreviewWindow : Window
{
    private readonly Dictionary<string, CheckBox> checks = new(StringComparer.Ordinal);
    private readonly TextBlock headline;
    private readonly Button start;
    private readonly string startLabel;
    public BatchPreview Preview { get; }
    /// <summary>Ids of the songs the user kept ticked (only the active groups have check boxes).</summary>
    public IReadOnlyList<string> SelectedIds => Preview.Items.Where(i => i.Selectable && checks.TryGetValue(i.Id, out var box) && box.IsChecked == true).Select(i => i.Id).ToArray();
    internal IReadOnlyDictionary<string, CheckBox> Checks => checks;
    internal TextBlock Headline => headline;

    public BatchPreviewWindow(BatchPreview preview, string startLabel = "Começar", bool restorePoint = true, params UIElement[] extras)
    {
        Preview = preview; this.startLabel = startLabel;
        Title = "O que vai acontecer"; Width = 680; Height = 620; MinWidth = 520; MinHeight = 420; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "O que vai acontecer", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = $"{preview.Operation} · {preview.Items.Count} música{(preview.Items.Count == 1 ? "" : "s")}", Opacity = .85, Margin = new Thickness(0, 4, 0, 0) });
        headline = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0), Name = "BatchPreviewHeadline" };
        AutomationProperties.SetName(headline, "Resumo do lote");
        top.Children.Add(headline);
        var estimate = new TextBlock { Text = preview.EstimateText(), TextWrapping = TextWrapping.Wrap, Opacity = .85, Margin = new Thickness(0, 4, 0, 0) };
        if (estimate.Text.Length > 0) top.Children.Add(estimate);
        if (restorePoint) top.Children.Add(new TextBlock { Text = "Um ponto de restauração da biblioteca é guardado antes de começar (Histórico › Pontos de restauração). Partituras aprovadas não são tocadas.",
            TextWrapping = TextWrapping.Wrap, Opacity = .75, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        foreach (var extra in extras) bottom.Children.Add(extra);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        start = DialogUi.Button(startLabel, (_, _) => DialogResult = true, true); start.IsDefault = true; start.Name = "BatchPreviewStart";
        AutomationProperties.SetName(start, startLabel);
        buttons.Children.Add(start); bottom.Children.Add(buttons);
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (string group in BatchPreview.Groups)
        {
            var items = preview.Of(group);
            if (items.Count == 0) continue;
            bool active = BatchPreview.ActiveGroups.Contains(group);
            var header = new DockPanel { Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 4) };
            header.Children.Add(new TextBlock { Text = $"{group} ({items.Count})", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (active)
            {
                var toggle = new Button { Content = "Desmarcar todas", Padding = new Thickness(8, 1, 8, 1), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
                toggle.Click += (_, _) =>
                {
                    bool any = items.Any(i => checks[i.Id].IsChecked == true);
                    foreach (var item in items) checks[item.Id].IsChecked = !any;
                    toggle.Content = any ? "Marcar todas" : "Desmarcar todas";
                };
                DockPanel.SetDock(toggle, Dock.Right); header.Children.Add(toggle);
            }
            list.Children.Add(header);
            var panel = new StackPanel(); list.Children.Add(panel);
            // Long lists stay fast: the first songs are shown and the rest summarized; every selectable one still has a box.
            foreach (var item in items)
            {
                if (active && item.Selectable)
                {
                    var box = new CheckBox { IsChecked = true, Margin = new Thickness(8, 1, 0, 1), Content = new TextBlock { Text = item.Line, TextWrapping = TextWrapping.Wrap } };
                    AutomationProperties.SetName(box, item.Title);
                    box.Checked += (_, _) => Refresh(); box.Unchecked += (_, _) => Refresh();
                    checks[item.Id] = box; panel.Children.Add(box);
                }
                else panel.Children.Add(new TextBlock { Text = "• " + item.Line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 1, 0, 1), Opacity = group == BatchPreview.SkipApproved ? .8 : 1 });
            }
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Refresh();
    }

    private void Refresh()
    {
        int kept = SelectedIds.Count, total = Preview.Items.Count(i => i.Selectable);
        var parts = new List<string>();
        if (total > 0) parts.Add(kept == total ? Preview.Headline() : Preview.Headline().Replace($"{Preview.Items.First(i => i.Selectable).Group} {total}", $"{Preview.Items.First(i => i.Selectable).Group} {kept} de {total}"));
        else parts.Add(Preview.Headline());
        headline.Text = string.Join(" · ", parts);
        start.IsEnabled = total == 0 ? Preview.HasWork : kept > 0;
        start.Content = total == 0 || kept == total ? startLabel : $"{startLabel} ({kept})";
    }
}

/// <summary>The automatic restore points, newest first, with "Voltar a este ponto".</summary>
public sealed class RestorePointsWindow : Window
{
    private readonly ListBox list;
    public RestorePointInfo? Selected => list.SelectedItem as RestorePointInfo;
    internal ListBox List => list;

    public RestorePointsWindow(IReadOnlyList<RestorePointInfo> points, string folder)
    {
        Title = "Pontos de restauração"; Width = 640; Height = 440; MinWidth = 480; MinHeight = 320; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Pontos de restauração", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
            Text = $"Antes de cada operação em lote o TeclaScore guarda como a biblioteca estava; os {RestorePointStore.Kept} mais recentes ficam aqui. " +
                   "Voltar a um ponto deixa a biblioteca como naquele momento, sem apagar as partituras feitas depois (elas continuam nas pastas), e guarda um novo ponto antes, para desfazer a volta." });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var open = DialogUi.Button("Abrir pasta", (_, _) => { try { if (Directory.Exists(folder)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true }); } catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { } });
        open.HorizontalAlignment = HorizontalAlignment.Left; buttons.Children.Add(open);
        var close = DialogUi.Button("Fechar", (_, _) => DialogResult = false); close.IsCancel = true; buttons.Children.Add(close);
        var restore = DialogUi.Button("Voltar a este ponto", (_, _) => DialogResult = true, true); restore.IsEnabled = false; restore.Name = "ReturnToRestorePoint";
        AutomationProperties.SetName(restore, "Voltar a este ponto"); buttons.Children.Add(restore);
        list = new ListBox { ItemsSource = points, DisplayMemberPath = nameof(RestorePointInfo.Label) };
        AutomationProperties.SetName(list, "Pontos de restauração");
        list.SelectionChanged += (_, _) => restore.IsEnabled = Selected is { IsAvailable: true };
        if (points.Count == 0) root.Children.Add(new TextBlock { Text = "Nenhum ponto ainda. O primeiro é guardado antes da próxima operação em lote.", Opacity = .8, TextWrapping = TextWrapping.Wrap });
        else { root.Children.Add(list); list.SelectedIndex = 0; }
    }
}

/// <summary>One file about to be imported that looks like a song already in the library, and what the user chose to do.</summary>
public sealed class ImportDuplicateChoice(ImportDuplicate duplicate)
{
    public ImportDuplicate Duplicate { get; } = duplicate;
    public ImportDuplicateDecision Decision { get; set; } = ImportDuplicateDecision.Skip;
}

/// <summary>
/// Shown before the import adds its cards: each repeated file with "Manter os dois", "Pular" or "Substituir" (the new
/// file takes the old card's place; the old card leaves the library, its files stay on disk). Nothing is imported until
/// the user confirms.
/// </summary>
public sealed class ImportDuplicatesWindow : Window
{
    private readonly List<(ImportDuplicateChoice Choice, RadioButton Both, RadioButton Skip, RadioButton Replace)> rows = [];
    public IReadOnlyList<ImportDuplicateChoice> Choices => rows.Select(r => r.Choice).ToArray();
    internal IReadOnlyList<(ImportDuplicateChoice Choice, RadioButton Both, RadioButton Skip, RadioButton Replace)> Rows => rows;

    public ImportDuplicatesWindow(IReadOnlyList<ImportDuplicate> duplicates, int newFiles)
    {
        Title = "Músicas repetidas na importação"; Width = 760; Height = 560; MinWidth = 560; MinHeight = 380; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Músicas repetidas na importação", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8),
            Text = $"{duplicates.Count} arquivo(s) parecem músicas que já estão na biblioteca (mesmo conteúdo, ou mesmo título e intérprete)" +
                   (newFiles > 0 ? $"; outros {newFiles} são novos e entram normalmente." : ".") +
                   " Escolha o que fazer com cada um. Substituir troca o card antigo pelo novo arquivo; nenhum arquivo é apagado do computador." });
        var all = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        all.Children.Add(new TextBlock { Text = "Para todos:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Opacity = .85 });
        foreach (var (label, decision) in new[] { ("Manter os dois", ImportDuplicateDecision.KeepBoth), ("Pular", ImportDuplicateDecision.Skip), ("Substituir", ImportDuplicateDecision.Replace) })
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0), FontSize = 12 };
            button.Click += (_, _) => { foreach (var row in rows) Pick(row, decision); };
            all.Children.Add(button);
        }
        top.Children.Add(all);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var cancel = DialogUi.Button("Cancelar importação", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        var ok = DialogUi.Button("Continuar", (_, _) => DialogResult = true, true); ok.IsDefault = true; ok.Name = "ImportDuplicatesContinue"; buttons.Children.Add(ok);
        var list = new StackPanel();
        for (int index = 0; index < duplicates.Count; index++)
        {
            var duplicate = duplicates[index]; var choice = new ImportDuplicateChoice(duplicate);
            string existing = duplicate.Existing.Title + (duplicate.Existing.Artist.Length > 0 ? " · " + duplicate.Existing.Artist : "") + (duplicate.Existing.HasScore ? " · com partitura" : " · sem partitura");
            list.Children.Add(new TextBlock { Text = Path.GetFileName(duplicate.Candidate.Path), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, index == 0 ? 0 : 12, 0, 0), TextWrapping = TextWrapping.Wrap });
            list.Children.Add(new TextBlock { Text = $"{duplicate.Reason} de: {existing}", Opacity = .8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) });
            var options = new WrapPanel(); string group = "dup" + index;
            RadioButton Make(string label, bool @checked) { var radio = new RadioButton { GroupName = group, Content = label, IsChecked = @checked, Margin = new Thickness(0, 0, 16, 0) }; AutomationProperties.SetName(radio, label + " " + Path.GetFileName(duplicate.Candidate.Path)); options.Children.Add(radio); return radio; }
            var both = Make("Manter os dois", false); var skip = Make("Pular", true); var replace = Make("Substituir", false);
            both.Checked += (_, _) => choice.Decision = ImportDuplicateDecision.KeepBoth;
            skip.Checked += (_, _) => choice.Decision = ImportDuplicateDecision.Skip;
            replace.Checked += (_, _) => choice.Decision = ImportDuplicateDecision.Replace;
            rows.Add((choice, both, skip, replace)); list.Children.Add(options);
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private static void Pick((ImportDuplicateChoice Choice, RadioButton Both, RadioButton Skip, RadioButton Replace) row, ImportDuplicateDecision decision) =>
        (decision switch { ImportDuplicateDecision.KeepBoth => row.Both, ImportDuplicateDecision.Replace => row.Replace, _ => row.Skip }).IsChecked = true;
}
