using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public sealed class NativeVisualReviewWindow : Window
{
    private readonly NativeVisualReviewReport report;
    private readonly Func<IProgress<string>, CancellationToken, Task<NativeVisualReviewPrepared>> prepare;
    private readonly Func<NativeVisualReviewPrepared, IProgress<string>, CancellationToken, Task<NativeScoreResult>> publish;
    private readonly ScoreViewer viewer;
    private readonly DataGrid issues;
    private readonly Image detail;
    private readonly TextBlock summary, detailLabel, status, operationText, proposalText;
    private readonly Button prepareButton, saveButton;
    private readonly RadioButton originalView, proposalView;
    private readonly StackPanel progressPanel;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch elapsed = new();
    private CancellationTokenSource? operation;
    private NativeVisualReviewPrepared? prepared;
    private string stage = "";
    private bool closed, closeAfterOperation;
    public NativeScoreResult? Result { get; private set; }

    public NativeVisualReviewWindow(NativeVisualReviewReport report,
        Func<IProgress<string>, CancellationToken, Task<NativeVisualReviewPrepared>> prepare,
        Func<NativeVisualReviewPrepared, IProgress<string>, CancellationToken, Task<NativeScoreResult>> publish)
    {
        this.report = report; this.prepare = prepare; this.publish = publish;
        Title = "Revisão visual · TeclaScore"; Width = 1240; Height = 840; MinWidth = 1000; MinHeight = 650;
        DialogUi.Theme(this);
        var root = new Grid { Margin = new(20) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new(0, 0, 0, 12) }; root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Revisão visual", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = report.Metadata.Title, Margin = new(0, 4, 0, 6), TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = "Selecione um problema para localizar o trecho. Compare a proposta antes de salvar uma nova versão.", TextWrapping = TextWrapping.Wrap });
        var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
        body.ColumnDefinitions.Add(new() { Width = new(.85, GridUnitType.Star), MinWidth = 350 });
        body.ColumnDefinitions.Add(new() { Width = new(14) });
        body.ColumnDefinitions.Add(new() { Width = new(1.15, GridUnitType.Star), MinWidth = 440 });
        var left = new Grid(); body.Children.Add(left);
        left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        left.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 90 });
        left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        left.RowDefinitions.Add(new() { Height = new(.55, GridUnitType.Star), MinHeight = 85 });
        summary = new TextBlock { Name = "VisualReviewSummary", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 10),
            Text = report.Issues.Count == 0 ? "Nenhum problema encontrado nos elementos reconhecidos." : $"{report.Issues.Count} achado(s) no original · selecione para conferir" };
        left.Children.Add(summary);
        issues = new DataGrid { Name = "VisualReviewIssues", ItemsSource = report.Issues, AutoGenerateColumns = false,
            IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single, RowHeight = double.NaN, MinRowHeight = 48 };
        var wrap = new Style(typeof(TextBlock)); wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); wrap.Setters.Add(new Setter(MarginProperty, new Thickness(5)));
        issues.Columns.Add(new DataGridTextColumn { Header = "Local", Binding = new Binding(nameof(NativeVisualReviewIssue.Position)), Width = 145, ElementStyle = wrap });
        issues.Columns.Add(new DataGridTextColumn { Header = "O que conferir", Binding = new Binding(nameof(NativeVisualReviewIssue.Message)), Width = new(1, DataGridLengthUnitType.Star), ElementStyle = wrap });
        Grid.SetRow(issues, 1); left.Children.Add(issues);
        var details = new StackPanel { Margin = new(0, 10, 0, 10) }; Grid.SetRow(details, 2); left.Children.Add(details);
        detailLabel = new TextBlock { Text = "Detalhe do trecho selecionado", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) }; details.Children.Add(detailLabel);
        detail = new Image { Name = "VisualReviewDetail", MaxHeight = 115, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        details.Children.Add(detail);
        var limits = new StackPanel();
        limits.Children.Add(new TextBlock { Text = "Alcance da revisão", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 5) });
        limits.Children.Add(new TextBlock { Text = string.Join("\n\n", report.Limitations), TextWrapping = TextWrapping.Wrap });
        var limitsScroll = new ScrollViewer { Content = limits, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(limitsScroll, 3); left.Children.Add(limitsScroll);
        var right = new DockPanel(); Grid.SetColumn(right, 2); body.Children.Add(right);
        var compare = new StackPanel(); DockPanel.SetDock(compare, Dock.Top); right.Children.Add(compare);
        var tabs = new WrapPanel(); compare.Children.Add(tabs);
        originalView = new RadioButton { Name = "VisualReviewOriginal", Content = "Original", GroupName = "VisualReviewPages", IsChecked = true, Margin = new(0, 0, 20, 8) };
        proposalView = new RadioButton { Name = "VisualReviewProposal", Content = "Proposta corrigida", GroupName = "VisualReviewPages", Margin = new(0, 0, 0, 8) };
        tabs.Children.Add(originalView); tabs.Children.Add(proposalView);
        proposalText = new TextBlock { Name = "VisualReviewProposalSummary", Text = "Prévia do arquivo original preservado.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8), MaxHeight = 100 };
        compare.Children.Add(new ScrollViewer { Content = proposalText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 110 });
        viewer = new ScoreViewer { Name = "VisualReviewViewer", FirstPagePath = report.Original.PreviewPages.FirstOrDefault() }; right.Children.Add(viewer);
        var footer = new StackPanel { Margin = new(0, 10, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        progressPanel = new StackPanel { Visibility = Visibility.Collapsed }; footer.Children.Add(progressPanel);
        progressPanel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5 });
        var progressLine = new DockPanel { Margin = new(0, 6, 0, 8) }; progressPanel.Children.Add(progressLine);
        var cancel = MakeButton("Cancelar operação", "VisualReviewCancel", (_, _) => operation?.Cancel()); DockPanel.SetDock(cancel, Dock.Right); progressLine.Children.Add(cancel);
        operationText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; progressLine.Children.Add(operationText);
        status = new TextBlock { Name = "VisualReviewStatus", TextWrapping = TextWrapping.Wrap, MinHeight = 26,
            Text = report.CanPrepare ? "A proposta só altera apresentação; notas, durações, cifras e imagens serão conferidas novamente." : "Não há uma correção automática a preparar. Você pode conferir as páginas ou fechar esta revisão." }; footer.Children.Add(status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        prepareButton = MakeButton("Preparar proposta", "VisualReviewPrepare", async (_, _) => await Prepare()); actions.Children.Add(prepareButton);
        actions.Children.Add(MakeButton("Fechar", "VisualReviewClose", (_, _) => Close()));
        saveButton = MakeButton("Salvar nova versão", "VisualReviewSave", async (_, _) => await Save(), true); actions.Children.Add(saveButton);
        originalView.Checked += (_, _) => SelectPreview(); proposalView.Checked += (_, _) => SelectPreview();
        issues.SelectionChanged += (_, _) => SelectIssue();
        timer.Tick += (_, _) => operationText.Text = stage + $" · {elapsed.Elapsed:mm\\:ss}";
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; operation?.Cancel(); timer.Stop(); };
        Refresh(); if (report.Issues.Count > 0) issues.SelectedIndex = 0;
    }

    private static Button MakeButton(string text, string name, RoutedEventHandler handler, bool primary = false)
    { var button = DialogUi.Button(text, handler, primary); button.Name = name; return button; }
    private void Refresh()
    {
        bool idle = operation is null;
        prepareButton.IsEnabled = idle && report.CanPrepare;
        saveButton.IsEnabled = idle && prepared is not null && proposalView.IsChecked == true;
        proposalView.IsEnabled = idle && prepared is not null;
        originalView.IsEnabled = idle; issues.IsEnabled = idle;
        progressPanel.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
    }
    private void SelectPreview()
    {
        var active = proposalView.IsChecked == true && prepared is not null ? prepared.Result : report.Original;
        viewer.FirstPagePath = active.PreviewPages.FirstOrDefault();
        proposalText.Text = proposalView.IsChecked == true && prepared is not null
            ? "Proposta: nenhum corte ou sobreposição residual detectado no alcance da revisão.\n" + string.Join("\n", prepared.Changes)
            : "Prévia original. Clique em um achado para ir à página correspondente.";
        Refresh();
    }
    private void SelectIssue()
    {
        if (issues.SelectedItem is not NativeVisualReviewIssue issue) return;
        originalView.IsChecked = true; viewer.CurrentPage = issue.Page; detailLabel.Text = issue.Position + " · detalhe do original";
        try
        {
            if (issue.Page < 1 || issue.Page > report.Original.PreviewPages.Count) return;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(report.Original.PreviewPages[issue.Page - 1], UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze();
            var b = issue.Bounds;
            int x = Math.Clamp((int)(b.X * bitmap.PixelWidth)-35, 0, bitmap.PixelWidth-1);
            int y = Math.Clamp((int)(b.Y * bitmap.PixelHeight)-35, 0, bitmap.PixelHeight-1);
            int right = Math.Clamp((int)((b.X+b.Width)*bitmap.PixelWidth)+35, x+1, bitmap.PixelWidth);
            int bottom = Math.Clamp((int)((b.Y+b.Height)*bitmap.PixelHeight)+35, y+1, bitmap.PixelHeight);
            var crop = new CroppedBitmap(bitmap, new Int32Rect(x,y,right-x,bottom-y)); crop.Freeze(); detail.Source = crop;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException) { detail.Source = null; detailLabel.Text = issue.Position; }
    }
    private async Task Prepare()
    {
        if (operation is not null || !report.CanPrepare) return;
        prepared = null; originalView.IsChecked = true;
        await Run(async (progress, token) =>
        {
            var candidate = await prepare(progress, token); token.ThrowIfCancellationRequested();
            prepared = candidate; proposalView.IsChecked = true;
            status.Text = "Proposta preparada. Compare as páginas e confira o resultado antes de salvar.";
        }, "Preparando proposta visual…");
    }
    private async Task Save()
    {
        if (prepared is null || proposalView.IsChecked != true) return;
        await Run(async (progress, token) =>
        {
            var result = await publish(prepared, progress, token);
            Result = result; // Publication already completed atomically; late cancellation cannot erase it.
        }, "Salvando nova versão…");
        if (Result is not null && !closed) { DialogResult = true; Close(); }
    }
    private async Task Run(Func<IProgress<string>,CancellationToken,Task> work, string title)
    {
        if (operation is not null) return;
        operation = new(); var own = operation; stage = title; elapsed.Restart(); timer.Start(); Refresh();
        var progress = new Progress<string>(message => { if (!closed && operation == own && !own.IsCancellationRequested) { stage = message; operationText.Text = message; } });
        try { await work(progress, own.Token); }
        catch (OperationCanceledException) { status.Text = "Operação cancelada. O original permanece preservado."; }
        catch (Exception error) { status.Text = error.GetBaseException().Message; }
        finally { operation = null; own.Dispose(); timer.Stop(); elapsed.Stop(); Refresh(); if (closeAfterOperation && Result is null) Close(); }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (operation is not null) { e.Cancel = true; closeAfterOperation = true; operation.Cancel(); status.Text = "Cancelando a operação antes de fechar…"; }
    }
}
