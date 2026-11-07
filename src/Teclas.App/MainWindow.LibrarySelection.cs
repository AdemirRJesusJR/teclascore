using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

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

    private void SelectedRemoveClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var rows)) return;
        RemoveMarkedLibraryItems(rows);
    }

    /// <summary>Removes several cards at once; Desfazer brings all of them back in their places.</summary>
    internal void RemoveMarkedLibraryItems(IReadOnlyList<MidiRow> rows)
    {
        var removing = rows.Where(Files.Contains).OrderBy(Files.IndexOf).ToArray();
        if (removing.Length == 0) return;
        bool openRemoved = selectedFile is { } open && removing.Contains(open);
        lastRemovedLibraryItems.Clear();
        lastRemovedLibraryItems.AddRange(removing.Select(row => (row, Files.IndexOf(row))));
        removedLibrarySelection = openRemoved ? selectedFile : removing[0];
        foreach (var row in removing) { row.IsChecked = false; Files.Remove(row); }
        if (openRemoved) Library.SelectedItem = null;
        RefreshSelection();
        ExitLibrarySelection();
        Status = removing.Length == 1 ? $"{removing[0].Name} removido da biblioteca. Os arquivos foram mantidos nas suas pastas. Você pode desfazer esta remoção."
            : $"{removing.Length} músicas removidas da biblioteca. Os arquivos foram mantidos nas suas pastas. Você pode desfazer esta remoção.";
        PersistPreferences();
    }

    private async void SelectedGenerateClick(object sender, RoutedEventArgs e)
    {
        if (!TryMarked(out var marked)) return;
        // Songs still without a melody get the same proposal as Melodias em lote; nothing is chosen without the confirmation.
        var proposals = MelodyBatchWindow.Build(marked);
        if (proposals.Count > 0)
        {
            var dialog = new MelodyBatchWindow(proposals) { Owner = this };
            if (melodyBatchDialog(dialog) == true)
            {
                foreach (var item in dialog.SelectedRows) { item.Track!.IsMelody = true; item.Row.AutoMelody = true; item.Row.AutoMelodyConfident = item.Proposal.Confident; }
                PersistPreferences();
            }
        }
        var ready = marked.Where(row => !row.UsesNativeScoreWorkflow && row.Document is not null && (row.UseReviewedProfile || row.HasMelodyChoice)).ToArray();
        int left = marked.Count - ready.Length;
        if (ready.Length == 0)
        {
            Status = "Nenhuma das músicas marcadas pode ir para a fila: escolha a melodia primeiro. As importadas do MuseScore se melhoram uma a uma, em Gerar.";
            return;
        }
        int added = 0; var failed = new List<string>();
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            foreach (var row in ready)
            {
                token.ThrowIfCancellationRequested();
                SetOperationItems(added + failed.Count, ready.Length, row.ScoreTitle);
                try { await (melodyBatchEnqueue?.Invoke(row, token) ?? EnqueueConversion(row, token)); added++; }
                catch (Exception error) when (error is not OperationCanceledException) { failed.Add(row.ScoreTitle + ": " + error.Message); }
            }
            Status = $"{added} música(s) marcada(s) foram para a Fila de conversões."
                + (left > 0 ? $" {left} ficaram de fora porque ainda não têm melodia escolhida ou são partituras importadas." : "")
                + (failed.Count > 0 ? $" {failed.Count} não entraram: " + string.Join(" · ", failed) : "");
        }, operationTitle: "Enfileirando as músicas marcadas");
        if (added > 0 && melodyBatchEnqueue is null) ShowConversionQueue();
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
