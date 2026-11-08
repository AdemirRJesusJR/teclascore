using System.IO;
using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// The conversion queue as a background service. The Piloto automático, the batch, "Gerar em segundo plano", the
/// redo of the review in sequence, the watched folder and <c>--gerar</c> put songs in the durable queue, which runs
/// outside the busy state: the library, the search and the review stay usable, and only the cards being generated
/// are locked (marked on the card, editing disabled). Each finished song comes back to its card at once, so the
/// ready ones can be reviewed and approved while the others are still being generated. The footer shows a discreet
/// progress and a "Pausar fila" button; paused items stay in the queue to be resumed.
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource? backgroundQueueCancellation;
    private Task? backgroundQueueTask;
    private bool backgroundQueueRefreshPosted, backgroundQueueSubscribed;
    private readonly Dictionary<string, TaskCompletionSource<ConversionQueueSummaryItem?>> queueWaiters = new(StringComparer.Ordinal);
    /// <summary>Items of the current background run (for "Fila 3 de 10").</summary>
    private readonly HashSet<string> backgroundQueueRun = new(StringComparer.Ordinal);
    private readonly HashSet<string> reconciledQueueItems = new(StringComparer.Ordinal);
    private IReadOnlyList<ConversionQueueSummaryItem> queueSnapshot = [];
    /// <summary>The engine's cause of each failure in the last queue attempt (null = unknown), for the retry by cause.</summary>
    internal readonly Dictionary<MidiRow, string?> generationFailureKinds = [];
    /// <summary>Test hook: the engine used by the queue (a synthetic one in the smokes).</summary>
    internal IConversionQueueWorker? conversionQueueWorker;
    internal void SelectSongForSmoke(MidiRow row) => SelectSong(row);
    internal string QueueDebugText => string.Join(" | ", conversionQueue?.Summaries.Select(i => $"{i.DisplayName}:{i.Status}:{i.Stage}:{i.Error}") ?? []);

    public bool IsBackgroundQueueRunning => backgroundQueueTask is { IsCompleted: false };
    public Visibility BackgroundQueueVisibility => IsBackgroundQueueRunning ? Visibility.Visible : Visibility.Collapsed;
    public bool CanPauseBackgroundQueue => IsBackgroundQueueRunning && backgroundQueueCancellation is { IsCancellationRequested: false };

    public string BackgroundQueueText
    {
        get
        {
            var run = queueSnapshot.Where(item => backgroundQueueRun.Contains(item.Id)).ToArray();
            int done = run.Count(item => item.Status is ConversionQueueStatus.Completed or ConversionQueueStatus.Failed);
            return backgroundQueueCancellation is { IsCancellationRequested: true } ? "Pausando a fila…" : $"Fila {done} de {Math.Max(run.Length, done)}";
        }
    }

    public string BackgroundQueueToolTip
    {
        get
        {
            var running = queueSnapshot.Where(item => item.Status == ConversionQueueStatus.Running).Select(item => item.DisplayName).ToArray();
            int failed = queueSnapshot.Count(item => backgroundQueueRun.Contains(item.Id) && item.Status == ConversionQueueStatus.Failed);
            return "Fila de conversões em segundo plano" + (running.Length > 0 ? " · gerando " + string.Join(", ", running) : "") +
                (failed > 0 ? $" · {failed} falha(s)" : "") + ". Clique para abrir a fila; a biblioteca continua disponível.";
        }
    }

    /// <summary>Starts the queue in the background (if it is not already running) with the chosen number of simultaneous conversions.</summary>
    internal void StartBackgroundQueue()
    {
        var queue = GetConversionQueue();
        SubscribeBackgroundQueue(queue);
        foreach (var item in queue.Summaries.Where(item => item.Status is ConversionQueueStatus.Pending or ConversionQueueStatus.Running)) backgroundQueueRun.Add(item.Id);
        if (IsBackgroundQueueRunning) { RefreshBackgroundQueue(); return; }
        queue.MaxParallel = ConversionParallelism.Resolve(settings.ConversionParallelism);
        var cancellation = backgroundQueueCancellation = new CancellationTokenSource();
        backgroundQueueTask = RunBackgroundQueueAsync(queue, cancellation);
        RefreshBackgroundQueue();
    }

    private async Task RunBackgroundQueueAsync(ConversionQueueService queue, CancellationTokenSource cancellation)
    {
        var measurement = performanceRecorder?.Begin(PerformanceOperationFor("Fila de conversões"));
        try
        {
            await Task.Yield();
            // Songs added while the lanes were finishing are taken by another round.
            while (!cancellation.IsCancellationRequested && queue.Summaries.Any(item => item.Status == ConversionQueueStatus.Pending))
                await Task.Run(() => queue.RunAsync(null, cancellation.Token));
            measurement?.Success();
        }
        catch (OperationCanceledException) { measurement?.Cancelled(); }
        catch (InvalidOperationException error) when (queue.IsRunning)
        {
            // The queue window is running it: wait for that run and keep the cards in step.
            measurement?.Cancelled();
            while (queue.IsRunning && !cancellation.IsCancellationRequested) await Task.Delay(500);
            _ = error;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        { measurement?.Failure(); if (!operationsDisposed) Status = "A fila de conversões parou: " + error.Message; }
        finally
        {
            if (measurement is not null) await Task.Run(measurement.Dispose);
            if (ReferenceEquals(backgroundQueueCancellation, cancellation)) backgroundQueueCancellation = null;
            cancellation.Dispose();
            if (!operationsDisposed)
            {
                RefreshBackgroundQueue(finished: true);
                if (!IsBackgroundQueueRunning) backgroundQueueRun.Clear();
            }
        }
    }

    /// <summary>Pauses the background queue safely: the conversions in progress stop and wait in the queue as paused.</summary>
    internal void PauseBackgroundQueue()
    {
        autopilotCancellation?.Cancel();
        if (backgroundQueueCancellation is not { IsCancellationRequested: false } cancellation) return;
        cancellation.Cancel();
        Status = "Pausando a fila. As partituras concluídas continuam salvas; retome as pausadas na Fila de conversões.";
        NotifyBackgroundQueue();
    }

    private void PauseBackgroundQueueClick(object sender, RoutedEventArgs e) => PauseBackgroundQueue();

    private void BackgroundQueueClick(object sender, RoutedEventArgs e)
    {
        if (IsIdle && CommitEdits()) ShowConversionQueue();
    }

    private void SubscribeBackgroundQueue(ConversionQueueService queue)
    {
        if (backgroundQueueSubscribed) return;
        backgroundQueueSubscribed = true;
        queue.Changed += (_, _) =>
        {
            // Many notifications become one refresh on the UI thread.
            if (operationsDisposed || Dispatcher.HasShutdownStarted) return;
            lock (queueWaiters) { if (backgroundQueueRefreshPosted) return; backgroundQueueRefreshPosted = true; }
            try { Dispatcher.BeginInvoke(() => { lock (queueWaiters) backgroundQueueRefreshPosted = false; RefreshBackgroundQueue(); }, DispatcherPriority.Background); }
            catch (InvalidOperationException) { }
        };
    }

    /// <summary>Brings finished songs to their cards, locks the cards being generated and resolves who waits for an item.</summary>
    private void RefreshBackgroundQueue(bool finished = false)
    {
        if (conversionQueue is null || operationsDisposed) return;
        queueSnapshot = conversionQueue.Summaries;
        ReconcileQueueResults();
        bool active = IsBackgroundQueueRunning;
        var locked = queueSnapshot.Where(item => item.Status == ConversionQueueStatus.Running || active && item.Status == ConversionQueueStatus.Pending)
            .GroupBy(item => item.OriginalSourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Any(item => item.Status == ConversionQueueStatus.Running), StringComparer.OrdinalIgnoreCase);
        bool selectionChanged = false;
        foreach (var row in Files)
        {
            var state = locked.TryGetValue(row.FullPath, out bool running) ? running ? QueueCardState.Generating : QueueCardState.Waiting : QueueCardState.None;
            if (row.QueueState == state) continue;
            row.QueueState = state;
            if (ReferenceEquals(row, selectedFile)) selectionChanged = true;
        }
        ResolveQueueWaiters(finished || !active);
        if (selectionChanged) RefreshSelection();
        NotifyBackgroundQueue();
    }

    private void NotifyBackgroundQueue()
    {
        foreach (string name in new[] { nameof(IsBackgroundQueueRunning), nameof(BackgroundQueueVisibility), nameof(CanPauseBackgroundQueue), nameof(BackgroundQueueText), nameof(BackgroundQueueToolTip) })
            Notify(name);
    }

    /// <summary>Completes when the item finishes (completed, failed or paused), leaves the queue, or the queue stops before it.</summary>
    internal Task<ConversionQueueSummaryItem?> WaitForQueueItemAsync(string id)
    {
        TaskCompletionSource<ConversionQueueSummaryItem?> waiter;
        lock (queueWaiters)
        {
            if (!queueWaiters.TryGetValue(id, out waiter!)) queueWaiters[id] = waiter = new(TaskCreationOptions.None);
        }
        ResolveQueueWaiters(stopped: !IsBackgroundQueueRunning);
        return waiter.Task;
    }

    private void ResolveQueueWaiters(bool stopped)
    {
        KeyValuePair<string, TaskCompletionSource<ConversionQueueSummaryItem?>>[] waiting;
        lock (queueWaiters) waiting = [.. queueWaiters];
        if (waiting.Length == 0) return;
        var items = (conversionQueue?.Summaries ?? []).ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var (id, waiter) in waiting)
        {
            items.TryGetValue(id, out var item);
            bool done = item is null || item.Status is ConversionQueueStatus.Completed or ConversionQueueStatus.Failed or ConversionQueueStatus.Paused
                || stopped && item.Status == ConversionQueueStatus.Pending;
            if (!done) continue;
            lock (queueWaiters) queueWaiters.Remove(id);
            waiter.TrySetResult(item);
        }
    }

    /// <summary>
    /// Freezes the rows as queue recipes with one save of the queue file. Rows that cannot be added are returned in
    /// <paramref name="failures"/> with the reason; the others with their queue item id.
    /// </summary>
    internal async Task<IReadOnlyList<(MidiRow Row, string Id)>> EnqueueConversionsAsync(IReadOnlyList<MidiRow> rows, CancellationToken token, IDictionary<MidiRow, string> failures)
    {
        var added = new List<(MidiRow, string)>();
        if (rows.Count == 0) return added;
        string outputDirectory;
        try { ValidateSettings(true); outputDirectory = await PrepareOutputDirectoryAsync(token); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        { foreach (var row in rows) failures[row] = error.Message; return added; }
        var recipes = new List<ConversionQueueRecipe>(); var owners = new List<MidiRow>();
        foreach (var row in rows)
        {
            try { recipes.Add(BuildQueueRecipe(row, outputDirectory)); owners.Add(row); }
            catch (Exception error) when (error is InvalidOperationException or IOException) { failures[row] = error.Message; }
        }
        if (recipes.Count == 0) return added;
        var batch = await GetConversionQueue().EnqueueManyAsync(recipes, token);
        for (int index = 0; index < owners.Count; index++)
        {
            if (batch.Items[index] is { } item) { added.Add((owners[index], item.Id)); backgroundQueueRun.Add(item.Id); }
            else failures[owners[index]] = batch.Errors[index]?.Message ?? "não foi possível adicionar à fila";
        }
        return added;
    }

    /// <summary>Enqueues the rows, runs them in the background queue and waits for them (not for the whole queue).</summary>
    /// <param name="completed">Called on the UI thread for each song as soon as its score is back on the card.</param>
    private async Task<IReadOnlyDictionary<MidiRow, string>> GenerateInBackgroundAsync(IReadOnlyList<MidiRow> rows, CancellationToken token, Action<MidiRow>? completed)
    {
        var failures = new Dictionary<MidiRow, string>();
        foreach (var row in rows) generationFailureKinds.Remove(row);
        var added = await EnqueueConversionsAsync(rows, token, failures);
        if (added.Count == 0) return failures;
        StartBackgroundQueue();
        using var registration = token.Register(() => Dispatcher.BeginInvoke(PauseBackgroundQueue));
        var results = await Task.WhenAll(added.Select(async pair =>
        {
            var item = await WaitForQueueItemAsync(pair.Id);
            if (item?.Status == ConversionQueueStatus.Completed)
            {
                ReconcileQueueResults();
                if (pair.Row.ScorePath is not null)
                    try { completed?.Invoke(pair.Row); }
                    catch (Exception error) when (error is InvalidOperationException or IOException) { Status = error.Message; }
            }
            return (pair.Row, Item: item);
        }));
        ReconcileQueueResults();
        foreach (var (row, item) in results)
        {
            if (item is null) failures[row] = "o item saiu da fila antes de ser gerado";
            else if (item.Status == ConversionQueueStatus.Failed)
            { failures[row] = item.Error ?? "falha na conversão"; generationFailureKinds[row] = item.ErrorKind; row.HasOperationError = true; }
            else if (item.Status != ConversionQueueStatus.Completed) failures[row] = "a fila foi pausada antes de gerar esta música";
        }
        return failures;
    }

    // ---- Conversões simultâneas (Configurações) ----

    public IReadOnlyList<object> ConversionParallelismOptions { get; } =
        new[] { (object)new { Value = ConversionParallelism.Automatic, Label = "Automático" } }
            .Concat(Enumerable.Range(1, ConversionParallelism.Maximum).Select(n => (object)new { Value = n, Label = n == 1 ? "1 de cada vez" : $"{n} ao mesmo tempo" })).ToArray();

    public int ConversionParallelismSetting
    {
        get => ConversionParallelism.Normalize(settings.ConversionParallelism);
        set
        {
            value = ConversionParallelism.Normalize(value);
            if (settings.ConversionParallelism == value) return;
            settings.ConversionParallelism = value;
            // A queue already running keeps its lanes; the next start uses the new number.
            if (conversionQueue is { IsRunning: false } queue) queue.MaxParallel = ConversionParallelism.Resolve(value);
            Notify(); Notify(nameof(ConversionParallelismHint)); PersistPreferences();
        }
    }

    public string ConversionParallelismHint => ConversionParallelism.Describe(settings.ConversionParallelism) +
        (IsBackgroundQueueRunning ? " · vale a partir da próxima fila" : "") + ". Cada geração usa cerca de " + ConversionParallelism.MeasuredPeakText + " de memória.";

    // ---- Gerar em segundo plano e abrir a próxima ----

    public bool CanGenerateInBackground => CanEnqueueNow && !IsAutopilotRunning;

    /// <summary>The next song still to prepare after the selected one, in the library order (wrapping around).</summary>
    internal MidiRow? NextPendingSong(MidiRow? after)
    {
        var order = LibraryView?.Cast<MidiRow>().ToList() ?? Files.ToList();
        int start = after is null ? -1 : order.IndexOf(after);
        return order.Skip(start + 1).Concat(order.Take(Math.Max(0, start)))
            .FirstOrDefault(row => !ReferenceEquals(row, after) && row is { UsesNativeScoreWorkflow: false, Document: not null, ScorePath: null, QueueState: QueueCardState.None });
    }

    private async void GenerateInBackgroundClick(object sender, RoutedEventArgs e) => await GenerateInBackgroundAndOpenNextAsync();

    /// <summary>Puts the selected song in the queue, starts the queue and opens the next pending song on the Melodia step.</summary>
    internal async Task<MidiRow?> GenerateInBackgroundAndOpenNextAsync()
    {
        if (!CanGenerateInBackground || selectedFile is not { } row || !CommitEdits()) { if (!CanGenerateInBackground) Status = "Conclua as etapas antes de gerar: veja o que falta em Para continuar."; return null; }
        var failures = new Dictionary<MidiRow, string>();
        IReadOnlyList<(MidiRow Row, string Id)> added;
        try { added = await EnqueueConversionsAsync([row], CancellationToken.None, failures); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException) { Status = "Não foi possível adicionar à fila: " + error.Message; return null; }
        if (added.Count == 0) { Status = "Não foi possível adicionar à fila: " + failures.GetValueOrDefault(row, ""); return null; }
        StartBackgroundQueue();
        PersistPreferences();
        var next = NextPendingSong(row);
        if (next is null) { Status = $"{row.Name} está sendo gerada em segundo plano. Não há outra música pendente na lista."; return null; }
        SetCurrentPage("library");
        Library.SelectedItem = next; Library.ScrollIntoView(next);
        EditorTabs.SelectedIndex = TabOfStep(ScoreFlowStep.Melody);
        Status = $"{row.Name} está sendo gerada em segundo plano. Agora: {next.Name} · escolha a melodia.";
        return next;
    }
}
