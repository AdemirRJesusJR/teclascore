using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

public sealed class MidiFolderItem(MidiFolderEntry entry, bool inLibrary, bool generated = false, bool newSinceVisit = false) : INotifyPropertyChanged
{
    private bool selected;
    private string listenGlyph = "▶";
    public MidiFolderEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Folder => Entry.RelativeFolder.Length == 0 ? "—" : Entry.RelativeFolder;
    public string Size => Entry.Bytes < 1024 ? $"{Entry.Bytes} B" : $"{Entry.Bytes / 1024d:0.#} KB";
    public string Modified => Entry.ModifiedLocal.ToString("dd/MM/yyyy");
    public bool InLibrary { get; } = inLibrary;
    public bool Generated { get; } = inLibrary && generated;
    public bool NewSinceVisit { get; } = !inLibrary && newSinceVisit;
    public bool CanSelect => !InLibrary;
    public string State => Generated ? "Partitura gerada" : InLibrary ? "Na biblioteca" : NewSinceVisit ? "Novo desde a última visita" : "Fora da biblioteca";
    public bool IsSelected { get => selected; set { if (selected == value || InLibrary) return; selected = value; Changed(); } }
    public string ListenGlyph { get => listenGlyph; set { listenGlyph = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>Browses a MIDI folder in place: nothing is copied, and only the checked files join the library.</summary>
public sealed class MidiFolderWindow : Window
{
    private readonly Dictionary<string, bool> library;
    private readonly DateTime? lastVisit;
    private readonly CheckBox onlyOutside;
    private readonly ObservableCollection<MidiFolderItem> items = [];
    private readonly ICollectionView view;
    private readonly TextBox folderBox, filterBox;
    private readonly CheckBox subfolders;
    private readonly TextBlock summary;
    private readonly Button addButton;
    private readonly Action stopOtherPlayback;
    private readonly Func<IMidiOutputTransport>? outputFactory;
    private MidiTrackPlayer? player;
    private MidiFolderItem? playing;
    private CancellationTokenSource? scan;
    private string? lastScanNote;
    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];
    public bool Accepted { get; private set; }
    public string Folder => folderBox.Text;
    public bool IncludeSubfolders => subfolders.IsChecked == true;
    internal IReadOnlyList<MidiFolderItem> Items => items;
    internal Task? ScanTask { get; private set; }

    public MidiFolderWindow(string folder, bool includeSubfolders, IEnumerable<string> libraryPaths, Action stopOtherPlayback, Func<IMidiOutputTransport>? outputFactory = null)
        : this(folder, includeSubfolders, libraryPaths.ToDictionary(path => path, _ => false), null, stopOtherPlayback, outputFactory) { }

    /// <param name="libraryPaths">Library files and whether each already has a generated score.</param>
    /// <param name="lastVisit">When this folder was last opened here; later files are marked new.</param>
    public MidiFolderWindow(string folder, bool includeSubfolders, IReadOnlyDictionary<string, bool> libraryPaths, DateTime? lastVisit, Action stopOtherPlayback, Func<IMidiOutputTransport>? outputFactory = null)
    {
        library = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, generated) in libraryPaths) library[Path.GetFullPath(path)] = generated;
        this.lastVisit = lastVisit;
        this.stopOtherPlayback = stopOtherPlayback; this.outputFactory = outputFactory;
        Title = "Pasta de MIDIs"; Width = 880; Height = 640; MinWidth = 640; MinHeight = 420; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new(20) }; Content = root;

        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Pasta de MIDIs", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Escolha os MIDIs que entram na biblioteca. Os arquivos continuam na pasta: nada é copiado nem alterado.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 12) });
        var folderRow = new DockPanel { Margin = new(0, 0, 0, 8) };
        var browse = DialogUi.Button("Escolher pasta…", (_, _) => BrowseFolder()); browse.Margin = new(8, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right);
        subfolders = new CheckBox { Content = "Incluir subpastas", IsChecked = includeSubfolders, VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0) };
        subfolders.Click += (_, _) => StartScan(); DockPanel.SetDock(subfolders, Dock.Right);
        folderBox = new TextBox { Text = folder, IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationName(folderBox, "Pasta de MIDIs");
        folderRow.Children.Add(browse); folderRow.Children.Add(subfolders); folderRow.Children.Add(folderBox);
        header.Children.Add(folderRow);
        filterBox = new TextBox { Margin = new(0, 0, 0, 10), ToolTip = "Filtrar por nome ou subpasta (sem diferenciar acentos)" };
        AutomationName(filterBox, "Filtrar MIDIs da pasta");
        filterBox.TextChanged += (_, _) => { view!.Refresh(); UpdateSummary(); };
        onlyOutside = new CheckBox { Content = "Mostrar só os que ainda não estão na biblioteca", Margin = new(0, 0, 0, 10) };
        onlyOutside.Click += (_, _) => { view!.Refresh(); UpdateSummary(); };
        header.Children.Add(new TextBlock { Text = "Filtrar por nome ou subpasta", FontSize = 11, Margin = new(0, 0, 0, 4) });
        header.Children.Add(filterBox);
        header.Children.Add(onlyOutside);

        var footer = new DockPanel { Margin = new(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right);
        var cancel = DialogUi.Button("Cancelar", (_, _) => Close()); cancel.IsCancel = true;
        addButton = DialogUi.Button("Adicionar à biblioteca", (_, _) => Accept(), true);
        actions.Children.Add(cancel); actions.Children.Add(addButton);
        var selection = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var chooseFiles = DialogUi.Button("Escolher arquivos…", (_, _) => ChooseFiles());
        chooseFiles.ToolTip = "Adicionar MIDIs ou partituras MuseScore avulsos, de qualquer pasta (Ctrl+O na janela principal)";
        actions.Children.Insert(0, chooseFiles);
        selection.Children.Add(DialogUi.Button("Marcar visíveis", (_, _) => SetVisibleSelection(true)));
        selection.Children.Add(DialogUi.Button("Desmarcar", (_, _) => SetVisibleSelection(false)));
        summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 10, 0), TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(actions); footer.Children.Add(selection); footer.Children.Add(summary);
        DockPanel.SetDock(selection, Dock.Left);

        view = CollectionViewSource.GetDefaultView(items);
        view.Filter = item => item is MidiFolderItem folderItem && (onlyOutside.IsChecked != true || !folderItem.InLibrary)
            && MidiFolderScanner.Matches(folderItem.Entry, filterBox.Text);
        var grid = new DataGrid
        {
            ItemsSource = view, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended, GridLinesVisibility = DataGridGridLinesVisibility.None, RowHeight = 30, IsReadOnly = false
        };
        AutomationName(grid, "MIDIs da pasta");
        grid.SetResourceReference(BackgroundProperty, "CardBrush"); grid.SetResourceReference(ForegroundProperty, "Ink");
        grid.SetResourceReference(Control.BorderBrushProperty, "Line"); grid.SetResourceReference(DataGrid.RowBackgroundProperty, "CardBrush");
        grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Soft");
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(ToggleButton_IsChecked, new Binding(nameof(MidiFolderItem.IsSelected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetBinding(IsEnabledProperty, new Binding(nameof(MidiFolderItem.CanSelect)));
        check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Adicionar este MIDI");
        grid.Columns.Add(new DataGridTemplateColumn { Header = "✓", Width = 36, CellTemplate = new DataTemplate { VisualTree = check } });
        var listen = new FrameworkElementFactory(typeof(Button));
        listen.SetBinding(ContentControl.ContentProperty, new Binding(nameof(MidiFolderItem.ListenGlyph)));
        listen.SetBinding(TagProperty, new Binding());
        listen.SetValue(PaddingProperty, new Thickness(6, 0, 6, 0)); listen.SetValue(MinHeightProperty, 24d); listen.SetValue(FontSizeProperty, 11d);
        listen.SetValue(ToolTipProperty, "Ouvir ou parar este MIDI (todas as pistas)");
        listen.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Ouvir MIDI");
        listen.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(ListenClick));
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Ouvir", Width = 52, CellTemplate = new DataTemplate { VisualTree = listen } });
        grid.Columns.Add(new DataGridTextColumn { Header = "Nome", Binding = new Binding(nameof(MidiFolderItem.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Subpasta", Binding = new Binding(nameof(MidiFolderItem.Folder)), Width = 150, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Tamanho", Binding = new Binding(nameof(MidiFolderItem.Size)), Width = 76, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Modificado", Binding = new Binding(nameof(MidiFolderItem.Modified)), Width = 90, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Situação", Binding = new Binding(nameof(MidiFolderItem.State)), Width = 170, IsReadOnly = true });
        // Space toggles the checked state of every highlighted row; double click toggles one row.
        grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Space || grid.SelectedItems.Count == 0 || e.OriginalSource is CheckBox or Button) return;
            var rows = grid.SelectedItems.OfType<MidiFolderItem>().Where(i => i.CanSelect).ToArray();
            bool value = rows.Any(i => !i.IsSelected);
            foreach (var row in rows) row.IsSelected = value;
            e.Handled = true;
        };
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is MidiFolderItem { CanSelect: true } item) item.IsSelected = !item.IsSelected; };
        root.Children.Add(grid);

        items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (MidiFolderItem item in e.NewItems) item.PropertyChanged += (_, _) => UpdateSummary();
            UpdateSummary();
        };
        Loaded += (_, _) => { if (Directory.Exists(folder)) StartScan(); else BrowseFolder(); };
        Closed += (_, _) => { scan?.Cancel(); StopListening(); player?.Dispose(); };
        UpdateSummary();
    }

    private static readonly DependencyProperty ToggleButton_IsChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;
    private static void AutomationName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    private void ChooseFiles()
    {
        var dialog = new OpenFileDialog { Title = "Escolher arquivos para a biblioteca", Multiselect = true,
            Filter = "MIDI e MuseScore|*.mid;*.midi;*.kar;*.mscz|MIDI e karaokê|*.mid;*.midi;*.kar|MuseScore 4|*.mscz" };
        if (Directory.Exists(folderBox.Text)) dialog.InitialDirectory = folderBox.Text;
        if (dialog.ShowDialog(this) == true) AcceptFiles(dialog.FileNames);
    }

    /// <summary>Loose files chosen by the user go straight to the library, like the former header import.</summary>
    internal void AcceptFiles(IEnumerable<string> paths)
    {
        SelectedPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (SelectedPaths.Count == 0) return;
        StopListening();
        Accepted = true;
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Escolher pasta de MIDIs" };
        if (Directory.Exists(folderBox.Text)) dialog.InitialDirectory = folderBox.Text;
        if (dialog.ShowDialog(this) != true) return;
        folderBox.Text = dialog.FolderName;
        StartScan();
    }

    internal void StartScan()
    {
        scan?.Cancel(); StopListening();
        string folder = folderBox.Text; bool recursive = IncludeSubfolders;
        items.Clear();
        if (!Directory.Exists(folder)) { lastScanNote = "Escolha uma pasta disponível."; UpdateSummary(); return; }
        var token = (scan = new CancellationTokenSource()).Token;
        lastScanNote = "Lendo a pasta…"; UpdateSummary();
        ScanTask = Run();
        async Task Run()
        {
            try
            {
                var result = await Task.Run(() => MidiFolderScanner.Scan(folder, recursive, token), token);
                if (token.IsCancellationRequested) return;
                foreach (var entry in result.Entries)
                    items.Add(new(entry, library.TryGetValue(entry.FullPath, out bool generated), generated, lastVisit is { } visit && entry.ModifiedLocal > visit));
                lastScanNote = result.Entries.Count == 0 ? "Nenhum MIDI (.mid, .midi, .kar) nesta pasta." : null;
                if (result.Truncated) lastScanNote = $"Mostrando os primeiros {MidiFolderScanner.MaximumEntries} MIDIs; escolha uma subpasta para ver o restante.";
                if (result.SkippedFolders > 0) lastScanNote = (lastScanNote is null ? "" : lastScanNote + " ") + $"{result.SkippedFolders} pasta(s) sem permissão de leitura foram ignoradas.";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { lastScanNote = "Não foi possível ler a pasta: " + ex.Message; }
            UpdateSummary();
        }
    }

    private void SetVisibleSelection(bool value)
    {
        foreach (MidiFolderItem item in view) item.IsSelected = value;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (summary is null || addButton is null) return;
        int selected = items.Count(i => i.IsSelected), inLibrary = items.Count(i => i.InLibrary), visible = view.Cast<object>().Count();
        int generatedCount = items.Count(i => i.Generated), fresh = items.Count(i => i.NewSinceVisit);
        string counts = items.Count == 0 ? "" : $"{items.Count} MIDIs · {visible} visíveis · {inLibrary} na biblioteca ({generatedCount} gerados)"
            + (fresh > 0 ? $" · {fresh} novos desde a última visita" : "") + $" · {selected} marcados.";
        summary.Text = string.Join(" ", new[] { counts, lastScanNote }.Where(t => !string.IsNullOrEmpty(t)));
        addButton.Content = selected == 0 ? "Adicionar à biblioteca" : $"Adicionar {selected} à biblioteca";
        addButton.IsEnabled = selected > 0;
    }

    internal void Accept()
    {
        SelectedPaths = items.Where(i => i.IsSelected).Select(i => i.Entry.FullPath).ToArray();
        if (SelectedPaths.Count == 0) return;
        StopListening();
        Accepted = true;
        // Hosts may show the window without a modal loop (layout checks); DialogResult only exists for ShowDialog.
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }

    private async void ListenClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MidiFolderItem item }) return;
        if (ReferenceEquals(item, playing)) { StopListening(); return; }
        StopListening();
        stopOtherPlayback();
        playing = item; item.ListenGlyph = "…";
        try
        {
            var sequence = await Task.Run(() => MidiAuditionSequence.CreateFull(MidiReader.Read(item.Entry.FullPath), CancellationToken.None));
            if (!ReferenceEquals(item, playing)) return;
            player ??= CreatePlayer();
            player.Play(sequence);
            item.ListenGlyph = "■";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OverflowException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(item, playing)) { playing = null; item.ListenGlyph = "▶"; }
            lastScanNote = "Não foi possível ouvir " + item.Name + ": " + ex.Message; UpdateSummary();
        }
    }

    private MidiTrackPlayer CreatePlayer()
    {
        var created = new MidiTrackPlayer(outputFactory);
        created.StateChanged += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (playing is { } item && !created.IsPlaying && !created.IsPaused) { item.ListenGlyph = "▶"; playing = null; }
        });
        return created;
    }

    private void StopListening()
    {
        if (playing is { } item) item.ListenGlyph = "▶";
        playing = null;
        player?.Stop(releaseOutput: true);
    }
}
