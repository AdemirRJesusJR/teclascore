using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

public sealed class BatchPdfSelectionRow : Observable
{
    private bool selected;
    public BatchPdfSource Source { get; }
    public string Title => Source.Title;
    public string FileName => Source.ScorePath is null ? "Sem arquivo MSCZ" : Path.GetFileName(Source.ScorePath);
    public string Explanation => CanSelect ? "Pronta · todas as páginas em um PDF" : Source.BlockedReason;
    public bool CanSelect => string.IsNullOrWhiteSpace(Source.BlockedReason);
    public bool Selected { get => selected; set => Set(ref selected, value && CanSelect); }
    public BatchPdfSelectionRow(BatchPdfSource source) { Source = source; selected = CanSelect; }
}

public sealed class BatchPdfSelectionWindow : Window
{
    private readonly TextBlock summary;
    private readonly Button confirm;
    private bool changingAll;
    public IReadOnlyList<BatchPdfSelectionRow> Rows { get; }
    public IReadOnlyList<BatchPdfSource> SelectedSources => Rows.Where(r => r.Selected && r.CanSelect).Select(r => r.Source).ToArray();
    /// <summary>Set on confirm when the window was opened for a repertoire (one PDF with every song).</summary>
    public RepertoireOptions? Repertoire { get; private set; }
    private readonly TextBox? repertoireTitle;
    private readonly CheckBox? repertoireIndex, repertoireChords, repertoireTablet, repertoireAlphabetical;

    public BatchPdfSelectionWindow(IReadOnlyList<BatchPdfSource> sources) : this(sources, 0) { }

    /// <param name="hiddenWithoutScore">Library cards left out because they have no generated score yet.</param>
    public BatchPdfSelectionWindow(IReadOnlyList<BatchPdfSource> sources, int hiddenWithoutScore, bool repertoire = false, bool songbook = false)
    {
        Title = songbook ? "Caderno de cifras" : repertoire ? "Repertório em PDF" : "Exportar partituras em PDF"; Width = 960; Height = 630; MinWidth = 740; MinHeight = 480;
        ShowInTaskbar = false; UseLayoutRounding = true; DialogUi.Theme(this); NameScope.SetNameScope(this, new NameScope());
        Rows = sources.Select(source => new BatchPdfSelectionRow(source)).ToArray();
        var root = BatchPdfUi.Root(this);
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 14) }; root.Children.Add(top);
        top.Children.Add(BatchPdfUi.Heading(songbook ? "Monte o caderno de cifras" : repertoire ? "Monte o repertório" : "Escolha as partituras"));
        top.Children.Add(BatchPdfUi.Text(songbook
            ? "As músicas marcadas viram um PDF com a cifra em texto de cada uma (acordes sobre a letra), uma música por página, com índice e marcadores. Mais leve que as partituras para imprimir e levar ao ensaio."
            : repertoire
            ? "As músicas marcadas viram um único PDF: índice com links, marcadores para pular de música no leitor do tablet e, se quiser, os acordes de cada música logo depois da partitura."
            : "Cada música será salva em um PDF separado, com todas as páginas, direto na pasta que você escolher na próxima etapa. Arquivos que já estão na pasta nunca são substituídos.", 8));
        if (repertoire)
        {
            var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; top.Children.Add(options);
            options.Children.Add(new TextBlock { Text = "Título", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
            repertoireTitle = new TextBox { Text = "Repertório", Width = 220, Margin = new Thickness(0, 0, 18, 6), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(repertoireTitle, "Título do repertório"); Register(repertoireTitle, "RepertoireTitleBox"); options.Children.Add(repertoireTitle);
            CheckBox Option(string text, bool value, string tip, string name)
            {
                var box = new CheckBox { Content = text, IsChecked = value, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 6) };
                Register(box, name); options.Children.Add(box); return box;
            }
            repertoireIndex = Option("Índice", true, "Primeira página com a lista das músicas e o número da página; cada linha é um link.", "RepertoireIndexCheckBox");
            repertoireChords = Option("Acordes de cada música", false, "Acrescenta, depois de cada partitura, a página com os diagramas dos acordes usados.", "RepertoireChordsCheckBox");
            repertoireTablet = Option("Formato tablet", false, "Margens estreitas e página 3:4 que ocupa a tela do tablet; a distribuição dos compassos não muda.", "RepertoireTabletCheckBox");
            repertoireAlphabetical = Option("Ordem alfabética", false, "Desmarcado, segue a ordem da biblioteca.", "RepertoireAlphabeticalCheckBox");
            if (songbook) { repertoireTitle.Text = "Caderno de cifras"; repertoireChords.Visibility = repertoireTablet.Visibility = Visibility.Collapsed; repertoireChords.IsChecked = repertoireTablet.IsChecked = false; }
        }
        if (hiddenWithoutScore > 0)
            top.Children.Add(BatchPdfUi.Text(hiddenWithoutScore == 1 ? "1 música ainda sem partitura gerada não aparece na lista."
                : $"{hiddenWithoutScore} músicas ainda sem partitura gerada não aparecem na lista.", 4));
        var actions = new WrapPanel { Margin = new Thickness(-4, 10, 0, 0) }; top.Children.Add(actions);
        var all = DialogUi.Button("Selecionar todas disponíveis", (_, _) => SelectAll(true));
        Register(all, "SelectAllBatchPdfButton"); all.IsEnabled = Rows.Any(r => r.CanSelect); actions.Children.Add(all);
        var clear = DialogUi.Button("Limpar seleção", (_, _) => SelectAll(false)); Register(clear, "ClearBatchPdfButton"); actions.Children.Add(clear);
        var table = BatchPdfUi.Table(); Register(table, "BatchPdfSelectionTable"); table.ItemsSource = Rows;
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(BatchPdfSelectionRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetBinding(IsEnabledProperty, new Binding(nameof(BatchPdfSelectionRow.CanSelect)));
        check.SetBinding(ToolTipProperty, new Binding(nameof(BatchPdfSelectionRow.Explanation)));
        check.SetValue(ToolTipService.ShowOnDisabledProperty, true);
        check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(AutomationProperties.NameProperty, "Incluir esta partitura na exportação");
        table.Columns.Add(new DataGridTemplateColumn { Header = "Exportar", Width = 75, MinWidth = 75, CellTemplate = new DataTemplate { VisualTree = check } });
        BatchPdfUi.Column(table, "Partitura", nameof(BatchPdfSelectionRow.Title), 2, 180);
        BatchPdfUi.Column(table, "Arquivo salvo", nameof(BatchPdfSelectionRow.FileName), 2, 170);
        BatchPdfUi.Column(table, "Disponibilidade", nameof(BatchPdfSelectionRow.Explanation), 3, 210);
        Grid.SetRow(table, 1); root.Children.Add(table);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        summary = BatchPdfUi.Text("", 0); Register(summary, "BatchPdfSelectionSummary"); footer.Children.Add(summary);
        footer.Children.Add(BatchPdfUi.Text("Partituras com alterações pendentes precisam ser atualizadas antes da exportação. Os arquivos originais serão preservados.", 7));
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, -4, 0) }; footer.Children.Add(buttons);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        confirm = DialogUi.Button(repertoire ? "Escolher arquivo e montar" : "Escolher pasta e salvar", (_, _) => { if (Confirm()) DialogResult = true; }, true);
        confirm.IsDefault = true; Register(confirm, "ConfirmBatchPdfButton"); buttons.Children.Add(confirm);
        foreach (var row in Rows) row.PropertyChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }
    /// <summary>Accepts the selection; for a repertoire, also reads the options.</summary>
    internal bool Confirm()
    {
        if (SelectedSources.Count == 0) return false;
        if (repertoireTitle is not null)
            Repertoire = new(string.IsNullOrWhiteSpace(repertoireTitle.Text) ? "Repertório" : repertoireTitle.Text.Trim(),
                repertoireIndex!.IsChecked == true, repertoireChords!.IsChecked == true, repertoireTablet!.IsChecked == true, repertoireAlphabetical!.IsChecked == true);
        return true;
    }
    private void Register(FrameworkElement element, string name) { element.Name = name; RegisterName(name, element); }
    private void SelectAll(bool selected)
    { changingAll = true; try { foreach (var row in Rows) row.Selected = selected; } finally { changingAll = false; } UpdateSummary(); }
    private void UpdateSummary()
    {
        if (changingAll) return;
        int count = SelectedSources.Count, unavailable = Rows.Count(r => !r.CanSelect);
        summary.Text = $"{count} selecionada(s) de {Rows.Count} · {unavailable} indisponível(is).";
        confirm.IsEnabled = count > 0;
    }
}

public sealed class BatchPdfResultsWindow : Window
{
    public BatchPdfReport Report { get; }
    public BatchPdfResultsWindow(BatchPdfReport report)
    {
        Report = report; Title = "Resultado da exportação de PDFs"; Width = 980; Height = 640; MinWidth = 740; MinHeight = 480;
        ShowInTaskbar = false; UseLayoutRounding = true; DialogUi.Theme(this); NameScope.SetNameScope(this, new NameScope());
        var root = BatchPdfUi.Root(this);
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 14) }; root.Children.Add(top);
        top.Children.Add(BatchPdfUi.Heading(report.Cancelled ? "Exportação cancelada" : "Exportação concluída"));
        var summary = BatchPdfUi.Text(report.Summary, 8); RegisterName("BatchPdfResultsSummary", summary); top.Children.Add(summary);
        top.Children.Add(BatchPdfUi.Text(report.Cancelled ? "Os PDFs concluídos foram preservados. As músicas canceladas podem ser exportadas em outro lote." : "Confira abaixo o resultado de cada música.", 7));
        if (!string.IsNullOrWhiteSpace(report.Warning)) top.Children.Add(BatchPdfUi.Text(report.Warning, 7));
        var table = BatchPdfUi.Table(); RegisterName("BatchPdfResultsTable", table); table.ItemsSource = report.Items;
        BatchPdfUi.Column(table, "Resultado", nameof(BatchPdfItemResult.StatusText), 1, 110);
        BatchPdfUi.Column(table, "Partitura", nameof(BatchPdfItemResult.Title), 2, 170);
        BatchPdfUi.Column(table, "Detalhes", nameof(BatchPdfItemResult.Message), 3, 240);
        BatchPdfUi.Column(table, "PDF salvo", nameof(BatchPdfItemResult.PdfFileName), 2, 145);
        Grid.SetRow(table, 1); root.Children.Add(table);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        footer.Children.Add(BatchPdfUi.Text(report.Directory is null ? "Nenhum arquivo foi criado." : "Pasta deste lote: " + report.Directory, 0));
        if (report.ReportPath is not null) footer.Children.Add(BatchPdfUi.Text("O relatório também foi salvo nesta pasta.", 6));
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, -4, 0) }; footer.Children.Add(buttons);
        var open = DialogUi.Button("Abrir pasta do lote", (_, _) =>
        {
            if (report.Directory is null) return;
            try { Process.Start(new ProcessStartInfo(report.Directory) { UseShellExecute = true }); }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { MessageBox.Show(this, "Não foi possível abrir a pasta: " + error.Message, "Abrir pasta", MessageBoxButton.OK, MessageBoxImage.Information); }
        });
        open.IsEnabled = report.Directory is not null && Directory.Exists(report.Directory); RegisterName("OpenBatchPdfFolderButton", open); buttons.Children.Add(open);
        var close = DialogUi.Button("Concluir", (_, _) => Close(), true); close.IsDefault = true; close.IsCancel = true; RegisterName("CloseBatchPdfResultsButton", close); buttons.Children.Add(close);
    }
}

internal static class BatchPdfUi
{
    internal static Grid Root(Window window)
    {
        var root = new Grid { Margin = new Thickness(24) }; window.Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); return root;
    }
    internal static TextBlock Heading(string text) => new() { Text = text, FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    internal static TextBlock Text(string text, double top)
    {
        var result = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0), FontSize = 12 };
        result.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return result;
    }
    internal static DataGrid Table() => new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        CanUserResizeRows = false, EnableRowVirtualization = true, RowHeight = double.NaN, MinRowHeight = 48, SelectionMode = DataGridSelectionMode.Single };
    internal static void Column(DataGrid table, string header, string property, double weight, double minimum)
    {
        var wrap = new Style(typeof(TextBlock)); wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        table.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = new DataGridLength(weight, DataGridLengthUnitType.Star), MinWidth = minimum, ElementStyle = wrap });
    }
}
