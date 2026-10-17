using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    private CheckBox? halfPage, midiPedal, tabletPdf;
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
            tabletPdf = new CheckBox { Content = "formato tablet", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 6), ToolTip = "Margens estreitas e página 3:4; o layout das partituras não muda." };
            row.Children.Add(tabletPdf);
        }
        halfPage = new CheckBox { Name = "PresentationHalfPage", Content = "Virar meia página", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 6),
            ToolTip = "Na apresentação, a página ocupa a largura e cada toque desce meia tela antes de passar à próxima página: a próxima linha aparece antes de você precisar dela." };
        row.Children.Add(halfPage);
        midiPedal = new CheckBox { Name = "PresentationMidiPedal", Content = "Pedal do teclado MIDI vira a página", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 0, 6),
            ToolTip = "Durante a apresentação: pedal de sustentação avança, pedal suave (una corda) volta. Use um teclado MIDI conectado por USB." };
        row.Children.Add(midiPedal);
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
