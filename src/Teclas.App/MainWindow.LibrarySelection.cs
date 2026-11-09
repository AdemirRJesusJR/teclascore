using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Library selection mode: right-click a card › Selecionar (or Ctrl+click) shows check boxes on every card and a floating bar
/// that acts on all marked songs at once — PDFs, repertoire, songbook, generation, collection, favorites, titles and removal.
/// </summary>
public partial class MainWindow
{
    private bool librarySelectionMode;
    private MidiRow? librarySelectionAnchor, libraryContextRow;

    public bool LibrarySelectionMode
    {
        get => librarySelectionMode;
        private set
        {
            if (librarySelectionMode == value) return;
            librarySelectionMode = value;
            if (!value) { foreach (var row in Files) row.IsChecked = false; librarySelectionAnchor = null; }
            Notify(); Notify(nameof(LibrarySelectionVisibility)); Notify(nameof(LibraryActionBarVisibility));
            NotifyLibrarySelection();
        }
    }

    public Visibility LibrarySelectionVisibility => librarySelectionMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LibraryActionBarVisibility => librarySelectionMode ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>Marked songs in library order, including ones hidden by the current search or filter.</summary>
    internal IReadOnlyList<MidiRow> CheckedRows => Files.Where(row => row.IsChecked).ToArray();
    private IReadOnlyList<MidiRow> VisibleRows => LibraryView.Cast<MidiRow>().ToArray();
    public string LibrarySelectionText => CheckedRows.Count switch { 0 => "Nenhuma marcada", 1 => "1 selecionada", var n => $"{n} selecionadas" };
    public string LibrarySelectAllText => VisibleRows is { Count: > 0 } visible && visible.All(row => row.IsChecked) ? "Limpar seleção" : "Selecionar todas";
    public string LibraryFavoriteActionText => CheckedRows is { Count: > 0 } rows && rows.All(row => row.IsFavorite) ? "Desfavoritar" : "Favoritar";

    private void NotifyLibrarySelection()
    {
        Notify(nameof(LibrarySelectionText)); Notify(nameof(LibrarySelectAllText)); Notify(nameof(LibraryFavoriteActionText));
        RefreshGroupChecks();
    }

    internal void EnterLibrarySelection(MidiRow? first)
    {
        LibrarySelectionMode = true;
        if (first is not null && Files.Contains(first)) { first.IsChecked = true; librarySelectionAnchor = first; }
        NotifyLibrarySelection();
        Status = "Seleção: clique nos cards para marcar (Shift+clique marca um intervalo, Ctrl+A marca todas). Esc sai.";
    }

    internal void ExitLibrarySelection() => LibrarySelectionMode = false;

    /// <summary>Marks or unmarks a card; with <paramref name="range"/> marks every visible card from the last one clicked.</summary>
    internal void ToggleLibraryCheck(MidiRow row, bool range = false)
    {
        var visible = VisibleRows.ToList();
        int from = librarySelectionAnchor is { } anchor ? visible.IndexOf(anchor) : -1, to = visible.IndexOf(row);
        if (range && from >= 0 && to >= 0)
            for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) visible[i].IsChecked = true;
        else row.IsChecked = !row.IsChecked;
        librarySelectionAnchor = row;
        NotifyLibrarySelection();
    }

    internal void SetAllLibraryChecks(bool value)
    {
        foreach (var row in VisibleRows) row.IsChecked = value;
        NotifyLibrarySelection();
    }

    private void ExitLibrarySelectionClick(object sender, RoutedEventArgs e) => ExitLibrarySelection();

    private void ToggleSelectAllClick(object sender, RoutedEventArgs e) => SetAllLibraryChecks(LibrarySelectAllText == "Selecionar todas");

    private void LibraryPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        // The check box on a section header marks or clears the whole section.
        if (librarySelectionMode && Ancestor<CheckBox>(source) is { Tag: CollectionViewGroup group })
        {
            var rows = group.Items.OfType<MidiRow>().ToArray();
            bool all = rows.All(row => row.IsChecked);
            foreach (var row in rows) row.IsChecked = !all;
            NotifyLibrarySelection(); e.Handled = true; return;
        }
        if (Ancestor<ListBoxItem>(source) is not { DataContext: MidiRow card }) return;
        if (!librarySelectionMode)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && IsIdle) { EnterLibrarySelection(card); e.Handled = true; }
            return;
        }
        // The card's own MIDI player and trash buttons keep working; the rest of the card marks it instead of opening it.
        if (Ancestor<ButtonBase>(source) is { } button && button is not CheckBox) return;
        ToggleLibraryCheck(card, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private void LibraryPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsIdle || e.OriginalSource is not DependencyObject source || Ancestor<ListBoxItem>(source) is not { DataContext: MidiRow row }) return;
        OpenLibraryCardMenu(row);
        e.Handled = true;
    }

    internal void OpenLibraryCardMenu(MidiRow row)
    {
        libraryContextRow = row;
        CardMenuSelectButton.Visibility = librarySelectionMode ? Visibility.Collapsed : Visibility.Visible;
        CardMenuPlayButton.IsEnabled = !row.IsNativeScore && row.Document is not null;
        CardMenuPdfButton.IsEnabled = row.ScorePath is { } score && File.Exists(score) && !row.HasPendingScoreProperties;
        CardMenuFavoriteText.Text = row.IsFavorite ? "Remover dos favoritos" : "Favoritar";
        LibraryCardMenu.PlacementTarget = Library;
        LibraryCardMenu.IsOpen = true;
    }

    private void LibrarySelectionKeyDown(object sender, KeyEventArgs e)
    {
        if (!librarySelectionMode) return;
        if (e.Key == Key.Escape) { ExitLibrarySelection(); Library.Focus(); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { SetAllLibraryChecks(true); e.Handled = true; }
        else if (e.Key == Key.Space && sender == Library && Keyboard.FocusedElement is ListBoxItem { DataContext: MidiRow row })
        { ToggleLibraryCheck(row, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
    }

    private void LibraryGroupCheckLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box) UpdateGroupCheck(box);
    }

    private void RefreshGroupChecks()
    {
        if (Library is null) return;
        foreach (var box in Descendants<CheckBox>(Library).Where(box => box.Tag is CollectionViewGroup)) UpdateGroupCheck(box);
    }

    private static void UpdateGroupCheck(CheckBox box)
    {
        if (box.Tag is not CollectionViewGroup group) return;
        var rows = group.Items.OfType<MidiRow>().ToArray();
        box.IsChecked = rows.Length > 0 && rows.All(row => row.IsChecked) ? true : rows.Any(row => row.IsChecked) ? null : false;
    }

    // ---- Context menu of one card ----

    private void CardMenuSelectClick(object sender, RoutedEventArgs e) { if (IsIdle) EnterLibrarySelection(libraryContextRow); }

    private void CardMenuOpenClick(object sender, RoutedEventArgs e)
    {
        if (libraryContextRow is not { } row || !Files.Contains(row) || !IsIdle) return;
        ExitLibrarySelection();
        Library.SelectedItem = row; Library.ScrollIntoView(row);
    }

    private void CardMenuPlayClick(object sender, RoutedEventArgs e)
    {
        if (libraryContextRow is { } row) PlayLibraryMidiClick(new Button { Tag = row }, new RoutedEventArgs());
    }

    private void CardMenuPdfClick(object sender, RoutedEventArgs e)
    {
        if (libraryContextRow is not { } row || !Files.Contains(row) || !IsIdle) return;
        Library.SelectedItem = row;
        if (CanExportPdf) ExportPdfClick(this, new RoutedEventArgs());
    }

    private void CardMenuFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (libraryContextRow is not { } row || !Files.Contains(row) || !IsIdle) return;
        row.IsFavorite = !row.IsFavorite;
        Status = row.IsFavorite ? $"{row.ScoreTitle} entrou nos favoritos." : $"{row.ScoreTitle} saiu dos favoritos.";
        RefreshLibraryFilters(); NotifyLibrarySelection();
    }

    private void CardMenuOrganizeClick(object sender, RoutedEventArgs e)
    {
        if (libraryContextRow is not { } row || !Files.Contains(row) || !IsIdle) return;
        Library.SelectedItem = row;
        OrganizeScoreClick(this, new RoutedEventArgs());
    }

    private void CardMenuRemoveClick(object sender, RoutedEventArgs e)
    {
        if (libraryContextRow is not { } row || !Files.Contains(row) || !IsIdle || !CommitEdits()) return;
        RemoveLibraryItem(row); NotifyLibrarySelection();
    }

    // ---- Actions of the floating bar ----

    private bool TryMarked(out IReadOnlyList<MidiRow> rows)
    {
        rows = CheckedRows;
        if (!IsIdle || !CommitEdits()) return false;
        if (rows.Count == 0) { Status = "Marque ao menos uma música."; return false; }
        return true;
    }

    private void SelectedPdfsClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var rows)) return;
        batchScope = rows.ToArray(); ExportBatchPdfClick(sender, e);
    }

    private void SelectedRepertoireClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var rows)) return;
        batchScope = rows.ToArray(); RepertoirePdfClick(sender, e);
    }

    private void SelectedSongbookClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var rows)) return;
        batchScope = rows.ToArray(); SongbookClick(sender, e);
    }

    private void SelectedFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var rows)) return;
        bool value = !rows.All(row => row.IsFavorite);
        foreach (var row in rows) row.IsFavorite = value;
        Status = value ? $"{rows.Count} música(s) nos favoritos." : $"{rows.Count} música(s) fora dos favoritos.";
        RefreshLibraryFilters(); NotifyLibrarySelection();
    }

    internal Func<LibraryCollectionWindow, bool?> libraryCollectionDialog = window => window.ShowDialog();

    private void SelectedCollectionClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var rows)) return;
        var shared = rows.Select(row => row.Collection).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? rows[0].Collection : "";
        var dialog = new LibraryCollectionWindow(rows.Count, shared, LibraryCollections.Where(name => name.Length > 0).ToArray()) { Owner = this };
        if (libraryCollectionDialog(dialog) != true) return;
        ApplyCollection(rows, dialog.Collection);
    }

    internal void ApplyCollection(IReadOnlyList<MidiRow> rows, string collection)
    {
        foreach (var row in rows) row.Collection = collection;
        Status = collection.Length == 0 ? $"{rows.Count} música(s) saíram da coleção." : $"{rows.Count} música(s) na coleção {collection}.";
        Notify(nameof(LibraryCollections)); RefreshLibraryFilters(); NotifyLibrarySelection();
    }

    private void SelectedOrganizeClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var rows)) return;
        var window = new MetadataSheetWindow(rows) { Owner = this };
        if (metadataSheetDialog(window) == true) ApplyMetadataSheet(window.Rows);
        NotifyLibrarySelection();
    }

    private async void SelectedRemoveClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var rows)) return;
        // "O que vai acontecer": approved scores stay; the others can still be unticked here.
        var removable = rows.Where(row => !IsApprovalLocked(row)).ToList();
        var preview = BuildBatchPreview("Remover as marcadas", rows.Select(row => IsApprovalLocked(row)
            ? new BatchPreviewItem(row.FullPath, SongTitle(row), BatchPreview.SkipApproved, "retire a aprovação na própria música para removê-la", false)
            : new BatchPreviewItem(row.FullPath, SongTitle(row), BatchPreview.Remove, row.ScorePath is null ? "sem partitura" : "a partitura continua na pasta", true)), estimate: false);
        if (ConfirmBatch(preview, removable, "Remover") is not { } chosen) return;
        await RemoveMarkedLibraryItemsAsync(chosen, rows.Count - removable.Count);
    }

    /// <summary>Removes several cards at once (approved scores are never removed in bulk); Desfazer brings all of them back in their places.</summary>
    internal void RemoveMarkedLibraryItems(IReadOnlyList<MidiRow> rows) => _ = RemoveMarkedLibraryItemsAsync(rows.Where(row => !IsApprovalLocked(row)).ToList(), rows.Count(IsApprovalLocked), restorePoint: false);

    internal async Task RemoveMarkedLibraryItemsAsync(IReadOnlyList<MidiRow> rows, int skippedApproved, bool restorePoint = true)
    {
        var removing = rows.Where(Files.Contains).Where(row => !IsApprovalLocked(row)).OrderBy(Files.IndexOf).ToArray();
        if (removing.Length == 0) { if (skippedApproved > 0) Status = $"Nada removido: {skippedApproved} música(s) marcada(s) estão aprovadas. Retire a aprovação na própria música para removê-las."; return; }
        var started = DateTimeOffset.Now;
        var point = restorePoint && removing.Length > 1 ? await CreateRestorePointAsync($"Remover {removing.Length} músicas", removing) : null;
        bool openRemoved = selectedFile is { } open && removing.Contains(open);
        lastRemovedLibraryItems.Clear();
        lastRemovedLibraryItems.AddRange(removing.Select(row => (row, Files.IndexOf(row))));
        removedLibrarySelection = openRemoved ? selectedFile : removing[0];
        foreach (var row in removing) { row.IsChecked = false; Files.Remove(row); }
        if (openRemoved) Library.SelectedItem = null;
        RefreshSelection();
        ExitLibrarySelection();
        var items = removing.Select(row => LogItem(row, "removida", "card fora da biblioteca; arquivos mantidos")).ToList();
        var entry = RecordBatch("Remover as marcadas", started, items,
            [$"{removing.Length} card(s) saíram da biblioteca: {string.Join(", ", removing.Take(8).Select(SongTitle))}{(removing.Length > 8 ? " e outros" : "")} (os arquivos continuam nas pastas; Desfazer traz os cards de volta)"], point);
        Status = (removing.Length == 1 ? $"{removing[0].Name} removido da biblioteca." : $"{removing.Length} músicas removidas da biblioteca.")
            + " Os arquivos foram mantidos nas suas pastas. Você pode desfazer esta remoção."
            + (skippedApproved > 0 ? $" {skippedApproved} pulada(s) · aprovada(s)." : "")
            + (point is not null ? $" Ponto de restauração: {point.Label}." : "");
        _ = entry;
        PersistPreferences();
    }

    private async void SelectedGenerateClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var marked)) return;
        // Songs still without a melody get the same proposal as Melodias em lote; nothing is chosen without the confirmation.
        var proposals = MelodyBatchWindow.Build(marked.Where(row => !IsApprovalLocked(row)).ToList());
        if (proposals.Count > 0)
        {
            var dialog = new MelodyBatchWindow(proposals) { Owner = this };
            if (melodyBatchDialog(dialog) == true)
            {
                foreach (var item in dialog.SelectedRows) { item.Track!.IsMelody = true; item.Row.AutoMelody = true; item.Row.AutoMelodyConfident = item.Proposal.Confident; }
                PersistPreferences();
            }
        }
        var ready = marked.Where(row => !IsApprovalLocked(row) && !row.UsesNativeScoreWorkflow && row.Document is not null && (row.UseReviewedProfile || row.HasMelodyChoice)).ToArray();
        int approved = marked.Count(IsApprovalLocked);
        int left = marked.Count - ready.Length - approved;
        if (ready.Length == 0)
        {
            Status = approved > 0 && left == 0 ? $"Nenhuma música vai para a fila: {approved} marcada(s) estão aprovadas. Retire a aprovação na própria música para refazê-las."
                : "Nenhuma das músicas marcadas pode ir para a fila: escolha a melodia primeiro. As importadas do MuseScore se melhoram uma a uma, em Gerar.";
            return;
        }
        // "O que vai acontecer": what will be generated or redone, what is skipped because it is approved, what still needs a melody.
        var preview = BuildBatchPreview("Gerar as marcadas", marked.Select(row =>
            IsApprovalLocked(row) ? new BatchPreviewItem(row.FullPath, SongTitle(row), BatchPreview.SkipApproved, "retire a aprovação na própria música para refazê-la", false)
            : ready.Contains(row) ? new BatchPreviewItem(row.FullPath, SongTitle(row), row.ScorePath is null ? BatchPreview.Generate : BatchPreview.Regenerate, row.ScorePath is null ? "" : "a versão atual fica no Histórico", true)
            : row.UsesNativeScoreWorkflow ? new BatchPreviewItem(row.FullPath, SongTitle(row), BatchPreview.Skip, "partitura importada ou editada: melhore-a uma a uma, em Gerar", false)
            : new BatchPreviewItem(row.FullPath, SongTitle(row), BatchPreview.NeedsYou, row.Document is null ? "o MIDI não pôde ser lido" : "escolha a melodia primeiro", false)));
        if (ConfirmBatch(preview, ready, "Gerar") is not { } chosen || chosen.Count == 0) return;
        ready = chosen.ToArray();
        var started = DateTimeOffset.Now;
        var point = await CreateRestorePointAsync("Gerar as marcadas", ready);
        int added = 0; var failed = new List<string>();
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            // One save of the queue for all the marked songs; the hook (smokes) keeps the one-by-one path.
            if (melodyBatchEnqueue is null) added = await EnqueueRowsAsync(ready, token, failed);
            else foreach (var row in ready)
            {
                token.ThrowIfCancellationRequested();
                SetOperationItems(added + failed.Count, ready.Length, row.ScoreTitle);
                try { await (melodyBatchEnqueue?.Invoke(row, token) ?? EnqueueConversion(row, token)); added++; }
                catch (Exception error) when (error is not OperationCanceledException) { failed.Add(row.ScoreTitle + ": " + error.Message); }
            }
            Status = $"{added} música(s) marcada(s) foram para a Fila de conversões."
                + (approved > 0 ? $" {approved} pulada(s) · aprovada(s)." : "")
                + (left > 0 ? $" {left} ficaram de fora porque ainda não têm melodia escolhida ou são partituras importadas." : "")
                + (failed.Count > 0 ? $" {failed.Count} não entraram: " + string.Join(" · ", failed) : "");
        }, operationTitle: "Enfileirando as músicas marcadas");
        var items = marked.Select(row => IsApprovalLocked(row) ? LogItem(row, "pulada · aprovada") : ready.Contains(row) ? LogItem(row, "enfileirada", row.ScorePath is null ? null : "refazer; versão anterior no Histórico") : LogItem(row, "fica de fora", "sem melodia escolhida ou partitura importada")).ToList();
        var entry = RecordBatch("Gerar as marcadas", started, items, [], point);
        Status += " " + entry.ChangesText();
        if (added > 0 && melodyBatchEnqueue is null) { StartBackgroundQueue(); Status += " A fila está gerando em segundo plano."; }
    }

    internal MidiRow? LibraryContextRowForSmoke => libraryContextRow;
    internal void RepertoirePdfClickForSmoke() => RepertoirePdfClick(this, new RoutedEventArgs());
    internal void UndoLibraryRemovalForSmoke() => UndoRemoveLibraryItemClick(this, new RoutedEventArgs());

    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}

/// <summary>Asks for the collection (new or existing) that the marked songs join; empty takes them out of any collection.</summary>
public sealed class LibraryCollectionWindow : Window
{
    private readonly TextBox name;
    public string Collection => name.Text.Trim();
    internal TextBox NameBox => name;

    public LibraryCollectionWindow(int count, string current, IReadOnlyList<string> existing)
    {
        Title = "Coleção"; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; DialogUi.Theme(this);
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = count == 1 ? "Pôr 1 música numa coleção" : $"Pôr {count} músicas numa coleção", FontSize = 18, TextWrapping = TextWrapping.Wrap });
        name = DialogUi.Field(panel, "Coleção / repertório (vazio: tirar da coleção)", current, 80);
        if (existing.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Ou escolha uma existente:", Margin = new Thickness(0, 12, 0, 4) });
            var chips = new WrapPanel(); panel.Children.Add(chips);
            foreach (var item in existing.Take(24))
            {
                var chip = new Button { Content = item, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 2, 10, 2), MinHeight = 26 };
                chip.Click += (_, _) => { name.Text = item; name.Focus(); name.SelectAll(); };
                chips.Children.Add(chip);
            }
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        var save = DialogUi.Button("Aplicar", (_, _) => DialogResult = true, true); save.IsDefault = true; buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
