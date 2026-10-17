using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public sealed record PresentationLibraryItem(string SourcePath, string ScorePath, string Title, string? PreviewPath);

/// <summary>Repertoire editing and performance are separate modes so pedal keys never consume annotations.</summary>
public sealed partial class PresentationWindow : Window
{
    private PresentationStore? store;
    private readonly Func<PresentationEntry, IProgress<string>, CancellationToken, Task<string>> preparePages;
    private readonly ObservableCollection<PresentationEntry> entries = [];
    private readonly ListBox list;
    private readonly ComboBox library;
    private readonly TextBox repertoireTitle, notes;
    private readonly TextBlock currentTitle, nextTitle, performedNotes, saveStatus, pageStatus;
    private readonly FrameworkElement manager, heading;
    private readonly ColumnDefinition managerColumn;
    private readonly ScoreViewer viewer;
    private readonly Button startButton, upButton, downButton, removeButton, addButton, rebuildButton;
    private readonly StackPanel performanceBar, progressPanel;
    private readonly WrapPanel actionsPanel;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer elapsedTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly System.Diagnostics.Stopwatch elapsed = new();
    private CancellationTokenSource? preparation;
    private Guid? selectedId;
    private bool loading, dirty, closed, presenting, persistenceBlocked;
    private WindowState previousState;
    private string operationStage = "";

    public PresentationWindow(PresentationStore? store, IReadOnlyList<PresentationLibraryItem> available,
        Func<PresentationEntry, IProgress<string>, CancellationToken, Task<string>> preparePages,
        PresentationCatalog? catalog = null, Func<IReadOnlyList<PresentationEntry>, string, string, bool, Task<string>>? exportPdf = null)
    {
        this.catalog = catalog; this.exportPdf = exportPdf;
        this.store = store; this.preparePages = preparePages;
        Title = "Repertório e apresentação · TeclaScore"; Width = 1260; Height = 850; MinWidth = 950; MinHeight = 650;
        DialogUi.Theme(this);
        var root = new Grid { Margin = new(16) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var titlePanel = new StackPanel { Margin = new(0, 0, 0, 12) }; heading = titlePanel; root.Children.Add(titlePanel);
        titlePanel.Children.Add(new TextBlock { Text = "Repertório e apresentação", FontSize = 24, FontWeight = FontWeights.SemiBold });
        titlePanel.Children.Add(new TextBlock { Text = "Organize as músicas e as anotações. Durante a apresentação, use as teclas, um pedal USB configurado como teclado ou o pedal do teclado MIDI.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0) });
        BuildStageControls(titlePanel);
        var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
        managerColumn = new() { Width = new(320) }; body.ColumnDefinitions.Add(managerColumn); body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var left = new Grid { Margin = new(0, 0, 14, 0) }; manager = left; body.Children.Add(left);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, new GridLength(.55, GridUnitType.Star) })
            left.RowDefinitions.Add(new() { Height = height });
        repertoireTitle = new TextBox { Name = "PresentationTitle", MaxLength = 240, Margin = new(0, 0, 0, 10), ToolTip = "Nome do repertório" }; left.Children.Add(repertoireTitle);
        library = new ComboBox { Name = "PresentationLibrary", ItemsSource = available, DisplayMemberPath = nameof(PresentationLibraryItem.Title), MinHeight = 36, Margin = new(0, 0, 0, 4), ToolTip = "Partituras da biblioteca" };
        Grid.SetRow(library, 1); left.Children.Add(library); if (available.Count > 0) library.SelectedIndex = 0;
        addButton = Button("Adicionar ao repertório", "PresentationAdd", (_, _) => AddSelected()); Grid.SetRow(addButton, 2); left.Children.Add(addButton);
        list = new ListBox { Name = "PresentationEntries", ItemsSource = entries, DisplayMemberPath = nameof(PresentationEntry.Title), MinHeight = 80, Margin = new(0, 5, 0, 5) };
        Grid.SetRow(list, 3); left.Children.Add(list);
        var ordering = new WrapPanel(); Grid.SetRow(ordering, 4); left.Children.Add(ordering);
        upButton = Button("↑", "PresentationMoveUp", (_, _) => MoveEntry(-1)); upButton.ToolTip = "Mover música para cima";
        downButton = Button("↓", "PresentationMoveDown", (_, _) => MoveEntry(1)); downButton.ToolTip = "Mover música para baixo";
        removeButton = Button("Retirar da lista", "PresentationRemove", (_, _) => RemoveSelected()); removeButton.ToolTip = "Remove apenas deste repertório; preserva a biblioteca e os arquivos";
        ordering.Children.Add(upButton); ordering.Children.Add(downButton); ordering.Children.Add(removeButton);
        var notesLabel = new TextBlock { Text = "Anotações de ensaio · salvas automaticamente", Margin = new(0, 8, 0, 6), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(notesLabel, 5); left.Children.Add(notesLabel);
        notes = new TextBox { Name = "PresentationNotes", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 16000, MinHeight = 60 };
        Grid.SetRow(notes, 6); left.Children.Add(notes);
        var right = new Grid(); Grid.SetColumn(right, 1); body.Children.Add(right);
        right.RowDefinitions.Add(new() { Height = GridLength.Auto }); right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var scoreHeader = new StackPanel { Margin = new(0, 0, 0, 8) }; right.Children.Add(scoreHeader);
        currentTitle = new TextBlock { Name = "PresentationCurrent", FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }; scoreHeader.Children.Add(currentTitle);
        nextTitle = new TextBlock { Name = "PresentationNext", Margin = new(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap }; scoreHeader.Children.Add(nextTitle);
        progressPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new(0, 0, 0, 8) }; Grid.SetRow(progressPanel, 1); right.Children.Add(progressPanel);
        progressPanel.Children.Add(new ProgressBar { Height = 5, IsIndeterminate = true });
        progressPanel.Children.Add(Button("Cancelar preparação", "PresentationCancel", (_, _) => preparation?.Cancel()));
        viewer = new ScoreViewer { Name = "PresentationViewer" }; Grid.SetRow(viewer, 2); right.Children.Add(viewer);
        // This host handles F11 itself, retaining song navigation and rehearsal notes in full screen.
        if (viewer.FindName("FullscreenButton") is Button fullscreen) fullscreen.Visibility = Visibility.Collapsed;
        performedNotes = new TextBlock { Name = "PresentationPerformedNotes", FontSize = 17, TextWrapping = TextWrapping.Wrap, Margin = new(0, 9, 0, 0) };
        var noteScroll = new ScrollViewer { Content = performedNotes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 110 }; Grid.SetRow(noteScroll, 3); right.Children.Add(noteScroll);
        var footer = new StackPanel { Margin = new(0, 9, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        pageStatus = new TextBlock { Name = "PresentationPageStatus", TextWrapping = TextWrapping.Wrap }; footer.Children.Add(pageStatus);
        saveStatus = new TextBlock { Name = "PresentationSaveStatus", TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 5) }; footer.Children.Add(saveStatus);
        performanceBar = new StackPanel { Visibility = Visibility.Collapsed }; footer.Children.Add(performanceBar);
        performanceBar.Children.Add(new TextBlock { Text = "Página: ← / →, ↑ / ↓, Page Up / Page Down ou Espaço · Música: Ctrl + ← / → · Esc: voltar · Pedal USB deve enviar essas teclas", TextWrapping = TextWrapping.Wrap });
        var navigation = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center }; performanceBar.Children.Add(navigation);
        navigation.Children.Add(Button("‹ Música", "PresentationPreviousSong", (_, _) => ChangeSong(-1)));
        navigation.Children.Add(Button("‹ Página", "PresentationPreviousPage", (_, _) => ChangePage(-1)));
        navigation.Children.Add(Button("Página ›", "PresentationNextPage", (_, _) => ChangePage(1)));
        navigation.Children.Add(Button("Música ›", "PresentationNextSong", (_, _) => ChangeSong(1)));
        navigation.Children.Add(Button("Voltar (Esc)", "PresentationExitPerformance", (_, _) => SetPresentation(false)));
        var actions = actionsPanel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        rebuildButton = Button("Atualizar páginas", "PresentationRebuild", async (_, _) => await LoadSelectedPages(true)); actions.Children.Add(rebuildButton);
        startButton = Button("Iniciar apresentação", "PresentationStart", (_, _) => SetPresentation(!presenting), true); actions.Children.Add(startButton);
        actions.Children.Add(Button("Fechar", "PresentationClose", (_, _) => Close()));
        list.SelectionChanged += async (_, _) => await SelectionChanged();
        repertoireTitle.TextChanged += (_, _) => { if (!loading) Changed(); };
        notes.TextChanged += (_, _) => { if (!loading && Current is { } current) { UpdateEntry(current with { Notes = notes.Text }); performedNotes.Text = notes.Text; Changed(); } };
        saveTimer.Tick += (_, _) => SaveChanges();
        elapsedTimer.Tick += (_, _) => pageStatus.Text = operationStage + $" · {elapsed.Elapsed:mm\\:ss}";
        PreviewKeyDown += HandleKey;
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; preparation?.Cancel(); saveTimer.Stop(); elapsedTimer.Stop(); SetMidiPedal(false); };
        Loaded += (_, _) => { if (viewer.FindName("FitPageButton") is Button fit) fit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); };
        DependencyPropertyDescriptor.FromProperty(ScoreViewer.CurrentPageProperty, typeof(ScoreViewer)).AddValueChanged(viewer, ViewerPageChanged);
        Closed += (_, _) => DependencyPropertyDescriptor.FromProperty(ScoreViewer.CurrentPageProperty, typeof(ScoreViewer)).RemoveValueChanged(viewer, ViewerPageChanged);
        LoadDocument();
    }

    private PresentationEntry? Current => selectedId is { } id ? entries.FirstOrDefault(entry => entry.Id == id) : null;
    private static Button Button(string text, string name, RoutedEventHandler handler, bool primary = false)
    { var result = DialogUi.Button(text, handler, primary); result.Name = name; return result; }

    private void LoadDocument()
    {
        loading = true;
        try
        {
            var loaded = store?.Load() ?? new(PresentationDocument.Empty, "Repertório temporário: este modo de teste não salva configurações pessoais.");
            repertoireTitle.Text = loaded.Document.Title;
            foreach (var entry in loaded.Document.Entries) entries.Add(entry);
            saveStatus.Text = loaded.Warning ?? "Repertório salvo automaticamente neste computador. Os arquivos da biblioteca são preservados.";
            list.SelectedItem = entries.FirstOrDefault(e => e.Id == loaded.Document.SelectedId) ?? entries.FirstOrDefault();
        }
        catch (Exception error) when (IsStorageFailure(error))
        { persistenceBlocked = true; saveStatus.Text = error.Message; }
        finally { loading = false; Refresh(); }
        _ = SelectionChanged();
    }

    private async Task SelectionChanged()
    {
        if (loading || closed) return;
        selectedId = (list.SelectedItem as PresentationEntry)?.Id;
        loading = true; notes.Text = Current?.Notes ?? ""; loading = false;
        performedNotes.Text = notes.Text; Refresh(); Changed();
        await LoadSelectedPages(false);
    }

    private void UpdateEntry(PresentationEntry replacement)
    {
        int index = entries.ToList().FindIndex(item => item.Id == replacement.Id); if (index < 0) return;
        bool wasLoading = loading; loading = true;
        entries[index] = replacement; if (selectedId == replacement.Id) list.SelectedItem = replacement;
        loading = wasLoading;
    }

    private void AddSelected()
    {
        if (persistenceBlocked || library.SelectedItem is not PresentationLibraryItem item) return;
        var entry = new PresentationEntry(Guid.NewGuid(), item.Title.Length > 240 ? item.Title[..240] : item.Title, item.SourcePath, item.ScorePath, item.PreviewPath);
        entries.Add(entry); list.SelectedItem = entry; Changed(); Refresh();
    }
    private void MoveEntry(int direction)
    {
        if (Current is not { } item || persistenceBlocked) return;
        int index = entries.IndexOf(item), destination = index + direction; if (destination < 0 || destination >= entries.Count) return;
        loading = true; entries.Move(index, destination); list.SelectedItem = item; loading = false;
        Changed(); Refresh();
    }
    private void RemoveSelected()
    {
        if (Current is not { } item || persistenceBlocked) return;
        int index = entries.IndexOf(item); entries.Remove(item);
        list.SelectedIndex = entries.Count == 0 ? -1 : Math.Min(index, entries.Count - 1); Changed(); Refresh();
    }
    private void ViewerPageChanged(object? sender, EventArgs e)
    {
        if (loading || preparation is not null || viewer.PageCount == 0 || Current is not { } current) return;
        UpdateEntry(current with { LastPage = Math.Clamp(viewer.CurrentPage, 1, 100000) }); Changed();
    }
    private void Changed()
    {
        if (loading || persistenceBlocked || closed) return;
        dirty = true; saveStatus.Text = store is null ? "Repertório temporário nesta janela." : "Salvando alterações…";
        saveTimer.Stop(); saveTimer.Start();
    }
    private bool SaveChanges()
    {
        saveTimer.Stop(); if (!dirty) return true;
        if (persistenceBlocked) return false;
        try
        {
            store?.Save(new(1, repertoireTitle.Text, selectedId, entries.ToArray())); dirty = false;
            saveStatus.Text = store is null ? "Repertório temporário nesta janela." : "Repertório, ordem, anotações e página atual salvos."; return true;
        }
        catch (Exception error) when (IsStorageFailure(error))
        { saveStatus.Visibility = Visibility.Visible; saveStatus.Text = "Não foi possível salvar: " + error.Message + " Suas alterações continuam nesta janela."; return false; }
    }

    private async Task LoadSelectedPages(bool force)
    {
        preparation?.Cancel();
        if (Current is not { } item) { viewer.FirstPagePath = null; pageStatus.Text = "Adicione uma partitura da biblioteca para montar o repertório."; return; }
        using var operation = new CancellationTokenSource(); preparation = operation; var token = operation.Token;
        progressPanel.Visibility = Visibility.Visible; operationStage = "Preparando as páginas de " + item.Title; elapsed.Restart(); elapsedTimer.Start(); Refresh();
        try
        {
            string? preview = force ? null : item.PreviewPath;
            if (preview is null || !File.Exists(preview))
            {
                if (!File.Exists(item.ScorePath)) throw new FileNotFoundException("A partitura não está disponível neste caminho. Escolha outra música ou recoloque o arquivo original na pasta.");
                preview = await preparePages(item, new Progress<string>(message => { if (ReferenceEquals(preparation, operation) && !closed) operationStage = message; }), token);
            }
            token.ThrowIfCancellationRequested(); if (closed || selectedId != item.Id || !ReferenceEquals(preparation, operation)) return;
            viewer.FirstPagePath = preview; viewer.CurrentPage = item.LastPage;
            await viewer.ReloadAsync(); token.ThrowIfCancellationRequested();
            if (closed || selectedId != item.Id || !ReferenceEquals(preparation, operation)) return;
            if (Current is { } latest) UpdateEntry(latest with { PreviewPath = preview, LastPage = Math.Max(1, viewer.CurrentPage) });
            Changed();
            pageStatus.Text = File.Exists(item.ScorePath) ? "Páginas prontas. A partitura original permanece intacta." : "Partitura original ausente. Exibindo a prévia já salva; a atualização depende do arquivo MSCZ.";
        }
        catch (OperationCanceledException)
        { if (!closed && ReferenceEquals(preparation, operation)) { viewer.FirstPagePath = null; pageStatus.Text = "Preparação cancelada. Selecione outra música ou use Atualizar páginas."; } }
        catch (Exception error) when (IsStorageFailure(error) || error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!closed && ReferenceEquals(preparation, operation)) { viewer.FirstPagePath = null; pageStatus.Text = "Não foi possível preparar esta música: " + error.Message; } }
        finally
        {
            if (ReferenceEquals(preparation, operation))
            { preparation = null; elapsedTimer.Stop(); elapsed.Stop(); progressPanel.Visibility = Visibility.Collapsed; Refresh(); }
        }
    }

    private void Refresh()
    {
        int index = Current is { } current ? entries.IndexOf(current) : -1;
        currentTitle.Text = index < 0 ? "Seu repertório" : $"{index + 1} / {entries.Count} · {entries[index].Title}";
        nextTitle.Text = index >= 0 && index + 1 < entries.Count ? "Próxima: " + entries[index + 1].Title : index >= 0 ? "Última música do repertório" : "Nenhuma música selecionada";
        upButton.IsEnabled = !persistenceBlocked && index > 0; downButton.IsEnabled = !persistenceBlocked && index >= 0 && index + 1 < entries.Count;
        addButton.IsEnabled = !persistenceBlocked && library.Items.Count > 0; removeButton.IsEnabled = !persistenceBlocked && index >= 0;
        notes.IsEnabled = !persistenceBlocked && index >= 0; repertoireTitle.IsEnabled = !persistenceBlocked;
        startButton.IsEnabled = index >= 0 && preparation is null; startButton.Content = presenting ? "Voltar ao repertório (Esc)" : "Iniciar apresentação";
        rebuildButton.IsEnabled = index >= 0 && preparation is null;
    }
    private void SetPresentation(bool enabled)
    {
        if (enabled == presenting || (enabled && (Current is null || preparation is not null || !SaveChanges()))) return;
        presenting = enabled;
        if (enabled) { previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; WindowState = previousState; }
        manager.Visibility = heading.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        managerColumn.Width = new(enabled ? 0 : 320); performanceBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        actionsPanel.Visibility = saveStatus.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (viewer.FindName("ThumbnailsButton") is Button thumbnails && viewer.FindName("ThumbnailList") is ListBox thumbList && ((enabled && thumbList.Visibility == Visibility.Visible) || (!enabled && thumbList.Visibility != Visibility.Visible)))
            thumbnails.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (viewer.FindName(enabled && HalfPage ? "FitWidthButton" : "FitPageButton") is Button fit) fit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        SetMidiPedal(enabled);
        Refresh(); viewer.Focus();
    }
    private void ChangePage(int direction)
    {
        if (preparation is not null || viewer.PageCount == 0) return;
        if (HalfPage && presenting && viewer.ScrollHalf(direction)) { pageStatus.Text = $"Página {viewer.CurrentPage} / {viewer.PageCount} · {(direction > 0 ? "metade de baixo" : "metade de cima")}"; return; }
        int next = Math.Clamp(viewer.CurrentPage + direction, 1, viewer.PageCount);
        if (next == viewer.CurrentPage) pageStatus.Text = direction > 0 ? "Última página. Use Ctrl + → para passar à próxima música." : "Primeira página desta música.";
        else { viewer.CurrentPage = next; pageStatus.Text = $"Página {next} / {viewer.PageCount}"; }
    }
    private void ChangeSong(int direction)
    {
        if (Current is not { } item) return;
        int next = entries.IndexOf(item) + direction;
        if (next < 0 || next >= entries.Count) { pageStatus.Text = direction > 0 ? "Fim do repertório." : "Início do repertório."; return; }
        // A deliberate song change starts on its first page; reopening the repertoire restores the saved page.
        var nextEntry = entries[next] with { LastPage = 1 }; UpdateEntry(nextEntry); list.SelectedItem = nextEntry;
    }
    private void HandleKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && preparation is not null) { preparation.Cancel(); e.Handled = true; return; }
        if (e.Key == Key.Escape && presenting) { SetPresentation(false); e.Handled = true; return; }
        if (e.Key == Key.F11) { SetPresentation(!presenting); e.Handled = true; return; }
        if (!presenting) return;
        if (e.IsRepeat && e.Key is Key.Right or Key.Left or Key.Up or Key.Down or Key.PageDown or Key.PageUp or Key.Space)
        { e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Right or Key.Left)
        { ChangeSong(e.Key == Key.Right ? 1 : -1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Right or Key.Down or Key.PageDown or Key.Space)
        { ChangePage(1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Left or Key.Up or Key.PageUp)
        { ChangePage(-1); e.Handled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!SaveChanges())
        {
            e.Cancel = MessageBox.Show(this, "As últimas alterações do repertório ainda não foram salvas.\n\n" + saveStatus.Text + "\n\nFechar mesmo assim e perder somente essas alterações?", "Repertório não salvo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
        }
        if (!e.Cancel) preparation?.Cancel();
    }
    private static bool IsStorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException or ArgumentException or System.Security.SecurityException;
}
