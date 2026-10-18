using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public sealed class ConversionQueueWindow : Window
{
    private sealed class QueueRow(ConversionQueueItem item) : Observable
    {
        public ConversionQueueItem Item { get; private set; } = item;
        public string Name => Item.DisplayName;
        public string Source => Item.OriginalSourcePath;
        public string Status => Item.Status switch
        {
            ConversionQueueStatus.Pending => "Aguardando", ConversionQueueStatus.Running => "Convertendo",
            ConversionQueueStatus.Paused => "Pausado", ConversionQueueStatus.Failed => "Falhou",
            ConversionQueueStatus.Completed => "Concluído", _ => Item.Status.ToString()
        };
        public int Attempts => Item.Attempts;
        public string Stage => Item.Stage;
        public string Error => Item.Error ?? "";
        public void Update(ConversionQueueItem value)
        {
            Item = value;
            foreach (string name in new[] { nameof(Name), nameof(Source), nameof(Status), nameof(Attempts), nameof(Stage), nameof(Error) }) Notify(name);
        }
    }

    private readonly ConversionQueueService service;
    private readonly Action reconcileCompleted;
    private readonly ObservableCollection<QueueRow> rows = [];
    private readonly HashSet<string> reconciled = new(StringComparer.Ordinal);
    private readonly DataGrid grid;
    private readonly TextBlock counts, message, activity, details;
    private readonly StackPanel progressPanel;
    private readonly Button start, pause, resumeSelected, resumeAll, remove, openFolder;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch elapsed = new();
    private CancellationTokenSource? operation;
    private bool runningQueue, closed, pendingClose;
    private string stage = "";

    /// <summary>Song chosen in the summary to open in the editor; the queue window closes after it.</summary>
    public string? OpenSource { get; private set; }
    internal Func<QueueSummaryWindow, bool?> summaryDialog = window => window.ShowDialog();
    private DateTimeOffset runStarted;

    public ConversionQueueWindow(ConversionQueueService service, Action reconcileCompleted)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.reconcileCompleted = reconcileCompleted ?? throw new ArgumentNullException(nameof(reconcileCompleted));
        Title = "Fila de conversões · TeclaScore"; Width = 1150; Height = 730; MinWidth = 900; MinHeight = 600;
        DialogUi.Theme(this);
        var root = new Grid { Margin = new(24) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new(0, 0, 0, 14) }; root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Fila de conversões", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "As partituras são preparadas uma de cada vez. A fila guarda as escolhas de cada item e permite tentar novamente após uma falha.",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 10) });
        if (!string.IsNullOrWhiteSpace(service.RecoveryMessage))
            header.Children.Add(new TextBlock { Text = service.RecoveryMessage, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        counts = new TextBlock { Name = "QueueCounts", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        header.Children.Add(counts);
        grid = new DataGrid { Name = "ConversionQueueItems", ItemsSource = rows, AutoGenerateColumns = false,
            IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = double.NaN, MinRowHeight = 54 };
        var wrap = new Style(typeof(TextBlock));
        wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        wrap.Setters.Add(new Setter(MarginProperty, new Thickness(6)));
        wrap.Setters.Add(new Setter(MaxHeightProperty, 96d));
        grid.Columns.Add(new DataGridTextColumn { Header = "Arquivo", Binding = new Binding(nameof(QueueRow.Name)), Width = 245, ElementStyle = wrap });
        grid.Columns.Add(new DataGridTextColumn { Header = "Situação", Binding = new Binding(nameof(QueueRow.Status)), Width = 110, ElementStyle = wrap });
        grid.Columns.Add(new DataGridTextColumn { Header = "Tentativas", Binding = new Binding(nameof(QueueRow.Attempts)), Width = 80, ElementStyle = wrap });
        grid.Columns.Add(new DataGridTextColumn { Header = "Etapa", Binding = new Binding(nameof(QueueRow.Stage)), Width = new(1, DataGridLengthUnitType.Star), MinWidth = 160, ElementStyle = wrap });
        grid.Columns.Add(new DataGridTextColumn { Header = "Erro", Binding = new Binding(nameof(QueueRow.Error)), Width = 235, ElementStyle = wrap });
        Grid.SetRow(grid, 1); root.Children.Add(grid);
        var footer = new StackPanel { Margin = new(0, 12, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        details = new TextBlock { Name = "QueueItemDetails", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
        footer.Children.Add(new ScrollViewer { Content = details, MaxHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        progressPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new(0, 6, 0, 8) }; footer.Children.Add(progressPanel);
        progressPanel.Children.Add(new ProgressBar { Name = "QueueProgress", IsIndeterminate = true, Height = 5 });
        activity = new TextBlock { Name = "QueueActivity", TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) }; progressPanel.Children.Add(activity);
        message = new TextBlock { Name = "QueueMessage", Text = "Clique em Iniciar pendentes quando estiver pronto.", TextWrapping = TextWrapping.Wrap, MinHeight = 30 };
        footer.Children.Add(message);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        start = Button("Iniciar pendentes", "QueueStart", async (_, _) => await Start(), true); actions.Children.Add(start);
        pause = Button("Pausar", "QueuePause", (_, _) => CancelOperation()); actions.Children.Add(pause);
        resumeSelected = Button("Retomar selecionado", "QueueResumeSelected", async (_, _) => await Resume(false)); actions.Children.Add(resumeSelected);
        resumeAll = Button("Retomar falhas e pausados", "QueueResumeAll", async (_, _) => await Resume(true)); actions.Children.Add(resumeAll);
        var other = new WrapPanel { Margin = new(0, 4, 0, 0) }; footer.Children.Add(other);
        remove = Button("Remover da fila", "QueueRemove", async (_, _) => await Remove()); remove.ToolTip = "Remove somente o registro da fila. Os arquivos continuam salvos."; other.Children.Add(remove);
        openFolder = Button("Abrir pasta do resultado", "QueueOpenFolder", (_, _) => OpenFolder()); other.Children.Add(openFolder);
        var summary = Button("Resumo", "QueueSummary", (_, _) => ShowSummary(service.Items)); summary.ToolTip = "Prontas, para conferir e com falha"; other.Children.Add(summary);
        other.Children.Add(Button("Fechar", "QueueClose", (_, _) => Close()));
        grid.SelectionChanged += (_, _) => RefreshControls();
        timer.Tick += (_, _) => UpdateActivity();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            closed = true; service.Changed -= ServiceChanged; timer.Stop(); operation?.Cancel();
        };
        service.Changed += ServiceChanged;
        RefreshItems(); ReconcileCompleted();
    }

    internal void ShowSummary(IEnumerable<ConversionQueueItem> items)
    {
        var outcomes = ConversionQueueSummary.Build(items);
        if (outcomes.Count == 0) { message.Text = "Ainda não há conversões concluídas ou com falha para resumir."; return; }
        var window = new QueueSummaryWindow(outcomes) { Owner = this };
        if (summaryDialog(window) == true && window.OpenSource is { } source) { OpenSource = source; Close(); }
    }

    private static Button Button(string text, string name, RoutedEventHandler action, bool primary = false)
    { var button = DialogUi.Button(text, action, primary); button.Name = name; return button; }

    private void ServiceChanged(object? sender, EventArgs e)
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (closed) return;
                RefreshItems();
                ReconcileCompleted();
            });
        }
        catch (InvalidOperationException) { /* The UI dispatcher is shutting down. */ }
    }

    private void RefreshItems()
    {
        var snapshots = service.Items;
        var ids = snapshots.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Where(row => !ids.Contains(row.Item.Id)).ToArray()) rows.Remove(row);
        foreach (var item in snapshots)
        {
            var row = rows.FirstOrDefault(row => row.Item.Id == item.Id);
            if (row is null) rows.Add(new(item)); else row.Update(item);
        }
        if (grid.SelectedItem is null && rows.Count > 0) grid.SelectedIndex = 0;
        counts.Text = $"{snapshots.Count} itens · {snapshots.Count(item => item.Status == ConversionQueueStatus.Pending)} aguardando · " +
            $"{snapshots.Count(item => item.Status == ConversionQueueStatus.Running)} em execução · " +
            $"{snapshots.Count(item => item.Status == ConversionQueueStatus.Completed)} concluídos · " +
            $"{snapshots.Count(item => item.Status == ConversionQueueStatus.Paused)} pausados · " +
            $"{snapshots.Count(item => item.Status == ConversionQueueStatus.Failed)} falhas";
        RefreshControls();
    }

    private void ReconcileCompleted()
    {
        var completed = service.Items.Where(item => item.Status == ConversionQueueStatus.Completed && item.Result is not null &&
            !reconciled.Contains(item.Id)).Select(item => item.Id).ToArray();
        if (completed.Length == 0) return;
        try
        {
            reconcileCompleted();
            foreach (string id in completed) reconciled.Add(id);
        }
        catch (Exception error)
        {
            message.Text = "A conversão foi salva, mas a biblioteca ainda precisa ser atualizada: " + error.Message;
        }
    }

    private void RefreshControls()
    {
        bool idle = operation is null && !service.IsRunning;
        var selected = (grid.SelectedItem as QueueRow)?.Item;
        start.IsEnabled = idle && rows.Any(row => row.Item.Status == ConversionQueueStatus.Pending);
        pause.IsEnabled = runningQueue && operation is { IsCancellationRequested: false };
        resumeSelected.IsEnabled = idle && selected?.Status is ConversionQueueStatus.Paused or ConversionQueueStatus.Failed;
        resumeAll.IsEnabled = idle && rows.Any(row => row.Item.Status is ConversionQueueStatus.Paused or ConversionQueueStatus.Failed);
        remove.IsEnabled = idle && selected is not null && selected.Status != ConversionQueueStatus.Running;
        openFolder.IsEnabled = selected?.Status == ConversionQueueStatus.Completed && ResultDirectory(selected) is not null;
        details.Text = selected is null ? "A fila está vazia. Adicione uma partitura pela biblioteca." :
            "Origem: " + selected.OriginalSourcePath + (string.IsNullOrWhiteSpace(selected.Error) ? "" : "\n" + selected.Error);
        progressPanel.Visibility = operation is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        if (closed || operation is null) return;
        activity.Text = $"{stage} · tempo decorrido {elapsed.Elapsed:hh\\:mm\\:ss}";
    }

    private async Task Start()
    {
        if (!start.IsEnabled) return;
        runStarted = DateTimeOffset.Now;
        await Run("Preparando a fila", async token =>
        {
            var request = operation;
            var progress = new Progress<ConversionQueueProgress>(value =>
            {
                if (closed || operation != request || request is null || request.IsCancellationRequested) return;
                var item = rows.FirstOrDefault(row => row.Item.Id == value.ItemId)?.Item;
                stage = (item is null ? "" : item.DisplayName + " · ") + value.Stage;
                UpdateActivity();
            });
            await service.RunAsync(progress, token);
            message.Text = token.IsCancellationRequested ? "Fila pausada. Retome os itens pausados quando desejar." :
                "Execução concluída. Confira os resultados e os itens que precisam de uma nova tentativa.";
        }, true);
        // At the end of a run, show what this run produced.
        var finished = service.Items.Where(item => item.UpdatedAt >= runStarted && item.Status is ConversionQueueStatus.Completed or ConversionQueueStatus.Failed).ToArray();
        if (!closed && finished.Length > 0) ShowSummary(finished);
    }

    private Task Resume(bool all)
    {
        var selected = (grid.SelectedItem as QueueRow)?.Item;
        var ids = service.Items.Where(item => item.Status is ConversionQueueStatus.Paused or ConversionQueueStatus.Failed)
            .Where(item => all || item.Id == selected?.Id).Select(item => item.Id).ToArray();
        if (ids.Length == 0 || operation is not null || service.IsRunning) return Task.CompletedTask;
        return Run("Preparando nova tentativa", async token =>
        {
            foreach (string id in ids) { token.ThrowIfCancellationRequested(); await service.ResumeAsync(id, token); }
            message.Text = $"{ids.Length} item(ns) prontos para tentar novamente. Clique em Iniciar pendentes.";
        });
    }

    private Task Remove()
    {
        if (!remove.IsEnabled || grid.SelectedItem is not QueueRow selected) return Task.CompletedTask;
        string id = selected.Item.Id;
        return Run("Atualizando a fila", async token =>
        {
            await service.RemoveAsync(id, token);
            message.Text = "Item removido da fila. Os arquivos originais e os resultados continuam salvos.";
        });
    }

    private async Task Run(string title, Func<CancellationToken, Task> action, bool conversion = false)
    {
        if (closed || operation is not null || service.IsRunning) return;
        var request = new CancellationTokenSource();
        operation = request; runningQueue = conversion; stage = title;
        message.Text = conversion ? "Conversões em andamento. Use Pausar para interromper a fila com segurança." : title + "…";
        elapsed.Restart(); timer.Start(); RefreshControls();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            request.Token.ThrowIfCancellationRequested();
            await action(request.Token);
        }
        catch (OperationCanceledException) { if (!closed) message.Text = conversion ? "Fila pausada. As conversões concluídas continuam salvas." : "Atualização cancelada."; }
        catch (Exception error) { if (!closed) message.Text = "Não foi possível concluir esta operação: " + error.Message; }
        finally
        {
            if (operation == request) operation = null;
            runningQueue = false; request.Dispose(); timer.Stop(); elapsed.Stop();
            if (!closed)
            {
                RefreshItems(); ReconcileCompleted();
                if (pendingClose) { pendingClose = false; Close(); }
            }
        }
    }

    private void CancelOperation()
    {
        if (operation is null) return;
        operation.Cancel(); stage = "Pausando com segurança…";
        message.Text = "Aguardando o processo atual parar. As conversões já concluídas ficam salvas.";
        RefreshControls();
    }

    private static string? ResultDirectory(ConversionQueueItem item)
    {
        if (item.Result is not { } result) return null;
        if (Directory.Exists(result.ReviewDirectory)) return result.ReviewDirectory;
        string? folder = Path.GetDirectoryName(result.ScorePath);
        return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? folder : null;
    }

    private void OpenFolder()
    {
        if (grid.SelectedItem is not QueueRow row || ResultDirectory(row.Item) is not { } path) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { message.Text = "Não foi possível abrir a pasta: " + error.Message; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (operation is null) return;
        e.Cancel = true; pendingClose = true; CancelOperation();
    }
}
