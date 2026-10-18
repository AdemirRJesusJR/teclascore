using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Saved repertoires, PDF of a repertoire, half-page turns and the MIDI keyboard pedal.</summary>
public sealed partial class PresentationWindow
{
    private readonly PresentationCatalog? catalog;
    /// <summary>(songs, title, target file, tablet) → status text.</summary>
    private readonly Func<IReadOnlyList<PresentationEntry>, string, string, bool, Task<string>>? exportPdf;
    private ComboBox? repertoires;
    private CheckBox? halfPage, midiPedal, tabletPdf, phoneRemote, lyricsScreen, nightStage;
    private TextBlock? durationInfo;
    private readonly Dictionary<Guid, double?> durations = [];
    /// <summary>Length of a song in seconds (null when unknown); set by the main window and tests.</summary>
    internal Func<PresentationEntry, double?>? DurationOf { get; init; }
    /// <summary>Night mode as saved in the settings, and where to save a change.</summary>
    internal bool NightInitial { get; init; }
    internal Action<bool>? SaveNight { get; init; }
    private RemoteControlServer? remote;
    private TextBlock? remoteInfo;
    private Image? remoteQr;
    private FrameworkElement? remotePanel;
    private LyricsScreenWindow? lyricsWindow;
    private CancellationTokenSource? lyricsLoading;
    private readonly Dictionary<Guid, IReadOnlyList<string>> lyricsByEntry = [];
    /// <summary>Lyrics of each page of a song (MuseScore page layout), for the lyrics screen; set by the main window.</summary>
    internal Func<PresentationEntry, CancellationToken, Task<IReadOnlyList<string>>>? PageLyrics { get; init; }
    internal LyricsScreenWindow? LyricsWindow => lyricsWindow;
    /// <summary>(songs, title, target file) → status text: the text chord sheets of the list as one PDF.</summary>
    internal Func<IReadOnlyList<PresentationEntry>, string, string, Task<string>>? ExportSongbook { get; init; }
    /// <summary>Opening tempo of a song (null when unknown) and the MIDI output for the count-in; set by the main window and tests.</summary>
    internal Func<PresentationEntry, ScoreTempo?>? TempoOf { get; init; }
    internal Func<IMidiOutputTransport?> countInOutput = () => new WinMmMidiOutputTransport();
    private readonly Dictionary<Guid, ScoreTempo?> tempos = [];
    private CancellationTokenSource? counting;
    internal int CountIns { get; private set; }

    /// <summary>Tempo and meter of the current song, read once per song.</summary>
    internal ScoreTempo? CurrentTempo()
    {
        if (Current is not { } item || TempoOf is null) return null;
        if (!tempos.TryGetValue(item.Id, out var tempo))
        {
            try { tempo = TempoOf(item); }
            catch (Exception error) when (IsStorageFailure(error) || error is InvalidDataException or System.Xml.XmlException or InvalidOperationException) { tempo = null; }
            tempos[item.Id] = tempo;
        }
        return tempo;
    }

    /// <summary>One measure of clicks at the song's tempo (C, the Contagem button, or the phone).</summary>
    internal async Task CountIn()
    {
        if (CurrentTempo() is not { } tempo) { pageStatus.Text = "Andamento desta música indisponível para a contagem."; return; }
        counting?.Cancel();
        using var operation = new CancellationTokenSource(); counting = operation;
        IMidiOutputTransport? output = null;
        try
        {
            output = countInOutput();
            if (output is null) { pageStatus.Text = "Nenhuma saída de som MIDI disponível para a contagem."; return; }
            CountIns++; pageStatus.Text = $"Contagem: {tempo.Beats} tempos a {tempo.Label}";
            await StageCountIn.PlayAsync(output, tempo, operation.Token);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException error) { pageStatus.Text = "Contagem indisponível: " + error.Message; }
        finally { output?.Dispose(); if (ReferenceEquals(counting, operation)) counting = null; }
    }
    internal RemoteControlServer? Remote => remote;
    private MidiKeyboardInput? pedalInput;
    private readonly Dictionary<int, int> pedalValues = [];
    private bool switching;
    /// <summary>Opens the MIDI input used by the pedal; replaced in smoke tests.</summary>
    internal Func<MidiKeyboardInput?> pedalDevice = () => MidiKeyboardInput.Devices().Count > 0 ? new MidiKeyboardInput(0) : null;
    internal Func<string, string?>? pdfTarget = null;

    public bool HalfPage => halfPage?.IsChecked == true;
    public IReadOnlyList<PresentationListInfo> Repertoires => repertoires?.ItemsSource as IReadOnlyList<PresentationListInfo> ?? [];
    public string RepertoireTitle => repertoireTitle.Text;
    public IReadOnlyList<PresentationEntry> Entries => entries;

    private void BuildStageControls(Panel heading)
    {
        var row = new WrapPanel { Margin = new(0, 10, 0, 0) }; heading.Children.Add(row);
        if (catalog is not null)
        {
            row.Children.Add(new TextBlock { Text = "Repertório", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 6) });
            repertoires = new ComboBox { Name = "PresentationRepertoires", Width = 260, DisplayMemberPath = nameof(PresentationListInfo.Label), Margin = new(0, 0, 6, 6) };
            AutomationProperties.SetName(repertoires, "Repertório salvo");
            repertoires.SelectionChanged += (_, _) => { if (!switching && repertoires.SelectedItem is PresentationListInfo info) SwitchTo(info.Path); };
            row.Children.Add(repertoires);
            row.Children.Add(Button("Novo", "PresentationNewRepertoire", (_, _) => CreateRepertoire()));
            row.Children.Add(Button("Excluir", "PresentationDeleteRepertoire", (_, _) => DeleteRepertoire()));
            ReloadCatalog(store?.Path ?? catalog.DefaultPath);
        }
        if (exportPdf is not null)
        {
            row.Children.Add(Button("PDF do repertório", "PresentationPdf", async (_, _) => await ExportPdf()));
            var songbook = Button("Cifras do repertório", "PresentationSongbook", async (_, _) => await ExportSongbookPdf());
            songbook.ToolTip = "Um PDF com a cifra em texto de cada música da lista, na ordem do repertório"; row.Children.Add(songbook);
            tabletPdf = new CheckBox { Content = "formato tablet", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 6), ToolTip = "Margens estreitas e página 3:4; o layout das partituras não muda." };
            row.Children.Add(tabletPdf);
        }
        halfPage = new CheckBox { Name = "PresentationHalfPage", Content = "Virar meia página", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 6),
            ToolTip = "Na apresentação, a página ocupa a largura e cada toque desce meia tela antes de passar à próxima página: a próxima linha aparece antes de você precisar dela." };
        row.Children.Add(halfPage);
        midiPedal = new CheckBox { Name = "PresentationMidiPedal", Content = "Pedal do teclado MIDI vira a página", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 0, 6),
            ToolTip = "Durante a apresentação: pedal de sustentação avança, pedal suave (una corda) volta. Use um teclado MIDI conectado por USB." };
        row.Children.Add(midiPedal);
        phoneRemote = new CheckBox { Name = "PresentationPhoneRemote", Content = "Celular como controle", VerticalAlignment = VerticalAlignment.Center, Margin = new(16, 0, 0, 6),
            ToolTip = "Abre um endereço para o celular na mesma rede Wi-Fi virar as páginas. Protegido por um código de seis dígitos; desligue para encerrar. O Windows pode pedir para permitir o TeclaScore na rede privada." };
        phoneRemote.Click += (_, _) => SetPhoneRemote(phoneRemote.IsChecked == true);
        row.Children.Add(phoneRemote);
        lyricsScreen = new CheckBox { Name = "PresentationLyricsScreen", Content = "Letra em outra tela", VerticalAlignment = VerticalAlignment.Center, Margin = new(16, 0, 0, 6),
            ToolTip = "Abre uma janela com a letra da página atual em letras grandes, para um segundo monitor ou projetor (vai sozinha para a segunda tela, se houver). Acompanha as viradas de página." };
        lyricsScreen.Click += async (_, _) => await SetLyricsScreen(lyricsScreen.IsChecked == true);
        row.Children.Add(lyricsScreen);
        nightStage = new CheckBox { Name = "PresentationNight", Content = "Modo noturno", VerticalAlignment = VerticalAlignment.Center, Margin = new(16, 0, 0, 6),
            ToolTip = "Inverte as cores das páginas (notas claras em fundo escuro) para não ofuscar em palco ou igreja com pouca luz. Os arquivos não mudam." };
        nightStage.Click += (_, _) => SetNight(nightStage.IsChecked == true, save: true);
        row.Children.Add(nightStage);
        durationInfo = new TextBlock { Name = "PresentationDuration", TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
        heading.Children.Add(durationInfo);
        Loaded += (_, _) => { if (NightInitial) { nightStage.IsChecked = true; SetNight(true, save: false); } Refresh(); };
        var remoteRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 6, 0, 0), Visibility = Visibility.Collapsed }; remotePanel = remoteRow;
        remoteQr = new Image { Width = 112, Height = 112, Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        RenderOptions.SetBitmapScalingMode(remoteQr, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(remoteQr, "Código QR do controle pelo celular");
        remoteRow.Children.Add(remoteQr);
        remoteInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center };
        remoteRow.Children.Add(remoteInfo);
        heading.Children.Add(remoteRow);
        Closed += (_, _) => { SetPhoneRemote(false); lyricsLoading?.Cancel(); lyricsWindow?.Close(); counting?.Cancel(); };
    }

    /// <summary>Starts or stops the phone remote; commands arrive on the network thread and run here.</summary>
    internal void SetPhoneRemote(bool enabled)
    {
        remote?.Dispose(); remote = null;
        if (remotePanel is not null) remotePanel.Visibility = Visibility.Collapsed;
        if (!enabled) return;
        try
        {
            remote = new RemoteControlServer(action => Dispatcher.InvokeAsync(() => RemoteCommand(action)));
            var addresses = remote.Addresses();
            remoteInfo!.Text = addresses.Count == 0 ? "Sem rede ativa: conecte o computador ao Wi-Fi para usar o celular."
                : "Aponte a câmera do celular (mesma rede Wi-Fi) para o código, ou abra: " + string.Join("  ou  ", addresses.Take(2)) +
                  "\nO celular mostra a música e a página atuais; num tablet, toque em Ver a partitura aqui para acompanhar as páginas.";
            remoteQr!.Source = addresses.Count == 0 ? null : QrImage(QrCode.Encode(addresses[0]));
            remoteQr.Visibility = addresses.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            remotePanel!.Visibility = Visibility.Visible;
            PublishState();
        }
        catch (System.Net.Sockets.SocketException error)
        {
            if (phoneRemote is not null) phoneRemote.IsChecked = false;
            saveStatus.Text = "Não foi possível abrir o controle pelo celular: " + error.Message;
        }
    }

    /// <summary>A QR code as a crisp image: 4 pixels per module and the standard quiet zone of 4 modules.</summary>
    internal static BitmapSource QrImage(bool[,] modules)
    {
        int n = modules.GetLength(0), scale = 4, quiet = 4, size = (n + 2 * quiet) * scale;
        var pixels = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int my = y / scale - quiet, mx = x / scale - quiet;
                pixels[y * size + x] = (byte)(my >= 0 && my < n && mx >= 0 && mx < n && modules[my, mx] ? 0 : 255);
            }
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size); image.Freeze();
        return image;
    }

    internal void SetNight(bool enabled, bool save)
    {
        viewer.NightMode = enabled;
        if (nightStage is not null) nightStage.IsChecked = enabled;
        if (save) SaveNight?.Invoke(enabled);
    }

    /// <summary>Length of a song, read once; null when unknown.</summary>
    internal double? Duration(PresentationEntry entry)
    {
        if (DurationOf is null) return null;
        if (!durations.TryGetValue(entry.Id, out var seconds))
        {
            try { seconds = DurationOf(entry); }
            catch (Exception error) when (IsStorageFailure(error) || error is InvalidDataException or InvalidOperationException) { seconds = null; }
            durations[entry.Id] = seconds;
        }
        return seconds;
    }

    /// <summary>"12 músicas · 42:10 · tocadas 3 (10:05) · faltam 32:05" for the open repertoire.</summary>
    internal string DurationSummary()
    {
        if (DurationOf is null || entries.Count == 0) return "";
        int index = Current is { } current ? entries.IndexOf(current) : 0;
        var lengths = entries.Select(Duration).ToArray();
        double total = lengths.Sum(l => l ?? 0), played = lengths.Take(Math.Max(0, index)).Sum(l => l ?? 0);
        int unknown = lengths.Count(l => l is null);
        return $"{entries.Count} música(s) · {MidiDuration.Format(total)} no total" +
            (index > 0 ? $" · tocadas {index} ({MidiDuration.Format(played)}) · faltam {MidiDuration.Format(total - played)}" : "") +
            (unknown > 0 ? $" · {unknown} sem duração conhecida" : "");
    }

    /// <summary>Song and page for the phone and the lyrics screen; called whenever either changes.</summary>
    private void PublishState()
    {
        if (durationInfo is not null) durationInfo.Text = DurationSummary();
        int index = Current is { } current ? entries.IndexOf(current) : -1;
        if (remote is not null)
        {
            remote.State = index < 0 ? null : new RemoteState(entries[index].Title, index + 1, entries.Count, Math.Max(1, viewer.CurrentPage), viewer.PageCount);
            var pagePaths = viewer.PagePaths; int page = viewer.CurrentPage;
            remote.PageImage = index >= 0 && page >= 1 && page <= pagePaths.Count ? pagePaths[page - 1] : null;
        }
        UpdateLyricsScreen();
    }

    private async Task SetLyricsScreen(bool enabled)
    {
        if (!enabled) { lyricsLoading?.Cancel(); lyricsWindow?.Close(); lyricsWindow = null; return; }
        if (lyricsWindow is null)
        {
            lyricsWindow = new LyricsScreenWindow();
            lyricsWindow.Closed += (_, _) => { lyricsWindow = null; if (lyricsScreen is not null) lyricsScreen.IsChecked = false; };
            lyricsWindow.Show(); Activate();
        }
        await LoadLyrics();
    }

    private async Task LoadLyrics()
    {
        if (lyricsWindow is null || Current is not { } item) { UpdateLyricsScreen(); return; }
        if (lyricsByEntry.ContainsKey(item.Id) || PageLyrics is null) { UpdateLyricsScreen(); return; }
        lyricsLoading?.Cancel();
        using var loading = new CancellationTokenSource(); lyricsLoading = loading;
        lyricsWindow.Show(item.Title, "Preparando a letra das páginas…");
        try { lyricsByEntry[item.Id] = await PageLyrics(item, loading.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (IsStorageFailure(error) || error is InvalidOperationException or InvalidDataException or TimeoutException or System.Xml.XmlException)
        { lyricsByEntry[item.Id] = []; pageStatus.Text = "Não foi possível preparar a letra desta música: " + error.Message; }
        finally { if (ReferenceEquals(lyricsLoading, loading)) lyricsLoading = null; }
        UpdateLyricsScreen();
    }

    private void UpdateLyricsScreen()
    {
        if (lyricsWindow is null) return;
        if (Current is not { } item) { lyricsWindow.Show("", ""); return; }
        if (!lyricsByEntry.TryGetValue(item.Id, out var pages)) { _ = LoadLyrics(); return; }
        int page = Math.Max(1, viewer.CurrentPage);
        string text = page <= pages.Count ? pages[page - 1] : "";
        lyricsWindow.Show(item.Title, pages.All(p => p.Length == 0) ? "Esta partitura não tem letra." : text);
    }

    internal void RemoteCommand(string action)
    {
        switch (action)
        {
            case "next": ChangePage(1); break;
            case "prev": ChangePage(-1); break;
            case "nextsong": ChangeSong(1); break;
            case "prevsong": ChangeSong(-1); break;
            case "count": _ = CountIn(); break;
        }
    }

    private void ReloadCatalog(string selectPath)
    {
        if (catalog is null || repertoires is null) return;
        switching = true;
        try
        {
            var items = catalog.List();
            repertoires.ItemsSource = items;
            repertoires.SelectedItem = items.FirstOrDefault(item => string.Equals(item.Path, Path.GetFullPath(selectPath), StringComparison.OrdinalIgnoreCase)) ?? items[0];
        }
        finally { switching = false; }
    }

    /// <summary>Saves the open repertoire and opens another one.</summary>
    internal void SwitchTo(string path)
    {
        if (catalog is null || presenting || string.Equals(store?.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
        if (!SaveChanges()) { ReloadCatalog(store?.Path ?? catalog.DefaultPath); return; }
        preparation?.Cancel();
        store = new PresentationStore(path); persistenceBlocked = false; dirty = false;
        loading = true; entries.Clear(); selectedId = null; loading = false;
        LoadDocument();
        ReloadCatalog(path);
    }

    internal void CreateRepertoire(string? title = null)
    {
        if (catalog is null) return;
        if (title is null)
        {
            var prompt = new TextPromptWindow("Novo repertório", "Nome do repertório (por exemplo: Festa junina, Missa de domingo)", "Novo repertório") { Owner = this };
            title = prompt.ShowDialog() == true ? prompt.Value : null;
        }
        if (string.IsNullOrWhiteSpace(title)) return;
        try { SwitchTo(catalog.Create(title)); saveStatus.Text = $"Repertório \"{title.Trim()}\" criado. Adicione as músicas da biblioteca."; }
        catch (Exception error) when (IsStorageFailure(error) || error is InvalidOperationException) { saveStatus.Text = "Não foi possível criar o repertório: " + error.Message; }
    }

    internal void DeleteRepertoire(bool confirmed = false)
    {
        if (catalog is null || store is null || presenting) return;
        if (string.Equals(store.Path, catalog.DefaultPath, StringComparison.OrdinalIgnoreCase)) { saveStatus.Text = "O primeiro repertório não pode ser excluído; esvazie a lista se quiser."; return; }
        if (!confirmed && MessageBox.Show(this, $"Excluir o repertório \"{repertoireTitle.Text}\"? As partituras não são afetadas.", "Excluir repertório", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SaveChanges();
        string removed = store.Path;
        try { catalog.Delete(removed); }
        catch (Exception error) when (IsStorageFailure(error) || error is InvalidOperationException) { saveStatus.Text = "Não foi possível excluir: " + error.Message; return; }
        store = null; SwitchTo(catalog.DefaultPath);
    }

    internal async Task ExportPdf()
    {
        if (exportPdf is null) return;
        if (entries.Count == 0) { saveStatus.Text = "Adicione músicas ao repertório antes de montar o PDF."; return; }
        SaveChanges();
        string? target = pdfTarget is { } choose ? choose(repertoireTitle.Text) : null;
        if (pdfTarget is null)
        {
            var dialog = new SaveFileDialog { Title = "Salvar o repertório em PDF", Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true,
                FileName = ScoreFileName.Stem(null, repertoireTitle.Text) + (tabletPdf?.IsChecked == true ? " - tablet" : "") + ".pdf" };
            target = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (target is null) return;
        saveStatus.Visibility = Visibility.Visible;
        saveStatus.Text = "Montando o PDF do repertório…";
        saveStatus.Text = await exportPdf(entries.ToArray(), repertoireTitle.Text, target, tabletPdf?.IsChecked == true);
    }

    internal async Task ExportSongbookPdf()
    {
        if (ExportSongbook is null) return;
        if (entries.Count == 0) { saveStatus.Text = "Adicione músicas ao repertório antes de montar o caderno de cifras."; return; }
        SaveChanges();
        string? target = pdfTarget is { } choose ? choose(repertoireTitle.Text + " - cifras") : null;
        if (pdfTarget is null)
        {
            var dialog = new SaveFileDialog { Title = "Salvar as cifras do repertório", Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true,
                FileName = ScoreFileName.Stem(null, repertoireTitle.Text) + " - cifras.pdf" };
            target = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (target is null) return;
        saveStatus.Visibility = Visibility.Visible; saveStatus.Text = "Montando as cifras do repertório…";
        saveStatus.Text = await ExportSongbook(entries.ToArray(), repertoireTitle.Text, target);
    }

    /// <summary>While presenting, the sustain pedal (64) turns forward and the soft pedal (67) back, on the press only.</summary>
    private void SetMidiPedal(bool enabled)
    {
        pedalInput?.Dispose(); pedalInput = null; pedalValues.Clear();
        if (!enabled || midiPedal?.IsChecked != true) return;
        try
        {
            pedalInput = pedalDevice();
            if (pedalInput is null) return;
            pedalInput.ControlChange += (controller, value) => Dispatcher.InvokeAsync(() => Pedal(controller, value));
        }
        catch (InvalidOperationException error) { pageStatus.Text = "Pedal do teclado MIDI indisponível: " + error.Message; }
    }

    internal void Pedal(int controller, int value)
    {
        if (!presenting || controller is not (64 or 67)) return;
        bool wasDown = pedalValues.TryGetValue(controller, out int previous) && previous >= 64;
        pedalValues[controller] = value;
        if (value >= 64 && !wasDown) ChangePage(controller == 64 ? 1 : -1);
    }

    internal void Present(bool enabled) => SetPresentation(enabled);
    internal void AddForSmoke() => AddSelected();
    internal bool IsPresenting => presenting;
    internal bool IsPreparing => preparation is not null;
    internal ScoreViewer Viewer => viewer;
    internal int CurrentPage => viewer.CurrentPage;
}

/// <summary>Small themed prompt for one line of text.</summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox box;
    public string Value => box.Text.Trim();
    public TextPromptWindow(string title, string label, string value)
    {
        Title = title; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; DialogUi.Theme(this);
        var panel = new StackPanel { Margin = new(22) }; Content = panel;
        box = DialogUi.Field(panel, label, value, 240); box.Margin = new(0, 0, 0, 14);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        var ok = DialogUi.Button("Criar", (_, _) => { if (Value.Length > 0) DialogResult = true; }, true); ok.IsDefault = true; buttons.Children.Add(ok);
        Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}

/// <summary>Lyrics of the current page in large type, for a second monitor or a projector. Double-click: full screen.</summary>
public sealed class LyricsScreenWindow : Window
{
    private readonly TextBlock song, lyrics;
    public string Lyrics => lyrics.Text;

    public LyricsScreenWindow()
    {
        Title = "Letra · TeclaScore"; Width = 900; Height = 560; Background = Brushes.Black; ShowInTaskbar = true;
        var root = new DockPanel { Margin = new(40, 28, 40, 28) }; Content = root;
        song = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(160, 168, 180)), FontSize = 22, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 18) };
        DockPanel.SetDock(song, Dock.Top); root.Children.Add(song);
        lyrics = new TextBlock { Name = "LyricsScreenText", Foreground = Brushes.White, FontSize = 46, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, LineHeight = 62 };
        root.Children.Add(new Viewbox { Child = new Border { Width = 1100, Child = lyrics }, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
        // With a second monitor, open on it (to the right or left of the main one) in full screen.
        if (SystemParameters.VirtualScreenWidth > SystemParameters.PrimaryScreenWidth + 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.VirtualScreenLeft < 0 ? SystemParameters.VirtualScreenLeft + 40 : SystemParameters.PrimaryScreenWidth + 40; Top = 40;
            Loaded += (_, _) => FullScreen(true);
        }
        MouseDoubleClick += (_, _) => FullScreen(WindowStyle != WindowStyle.None);
        KeyDown += (_, e) => { if (e.Key == Key.Escape && WindowStyle == WindowStyle.None) FullScreen(false); };
    }

    private void FullScreen(bool enabled)
    {
        WindowState = WindowState.Normal; WindowStyle = enabled ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = enabled ? WindowState.Maximized : WindowState.Normal;
    }

    public void Show(string title, string text) { song.Text = title; lyrics.Text = text; }
}
