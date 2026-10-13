using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Teclas.App;

public partial class MainWindow
{
    private readonly Stopwatch operationWatch = new();
    private readonly DispatcherTimer operationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long operationId;
    private bool operationCancelRequested, operationsDisposed;
    private int operationCompleted, operationTotal;
    private string operationTitle = "Processando", operationItem = "";
    private IInputElement? operationPreviousFocus;

    public Visibility OperationVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public string OperationTitle => operationTitle;
    public string OperationItem => operationItem;
    public string OperationElapsedText => $"Tempo decorrido · {FormatTime(operationWatch.Elapsed.TotalSeconds)}";
    public bool OperationProgressIndeterminate => operationTotal <= 1;
    public double OperationProgressValue => operationTotal > 0 ? 100.0 * operationCompleted / operationTotal : 0;
    public string OperationCountText => operationTotal > 1 ? $"{operationCompleted} de {operationTotal} arquivos processados" : "Em andamento";
    public bool CanCancelOperation => IsBusy && !operationCancelRequested;
    public string OperationCancelLabel => operationCancelRequested ? "Cancelando…" : "Cancelar operação";

    private void InitializeOperations()
    {
        operationTimer.Tick += (_, _) => Notify(nameof(OperationElapsedText));
        Closed += (_, _) =>
        {
            operationsDisposed = true;
            operationTimer.Stop(); operationWatch.Stop();
            cancellation?.Cancel();
        };
    }

    private void BeginOperation(string title, string? item)
    {
        operationId++;
        operationPreviousFocus = Keyboard.FocusedElement;
        operationTitle = title; operationItem = item ?? "";
        operationCompleted = operationTotal = 0; operationCancelRequested = false;
        operationWatch.Restart(); operationTimer.Start();
        Status = "Preparando…";
        NotifyOperation();
    }

    private void EndOperation()
    {
        // Reject queued progress from this job, including callbacks posted just before completion.
        operationId++;
        operationTimer.Stop(); operationWatch.Stop();
        NotifyOperation();
        if (!operationsDisposed && operationPreviousFocus is UIElement { IsVisible: true, IsEnabled: true } target)
            target.Focus();
        operationPreviousFocus = null;
    }

    private void NotifyOperation()
    {
        foreach (string name in new[] { nameof(OperationVisibility), nameof(OperationTitle), nameof(OperationItem),
            nameof(OperationElapsedText), nameof(OperationProgressIndeterminate), nameof(OperationProgressValue),
            nameof(OperationCountText), nameof(CanCancelOperation), nameof(OperationCancelLabel) }) Notify(name);
    }

    private void SetOperationItems(int completed, int total, string? item = null)
    {
        operationTotal = Math.Max(0, total);
        operationCompleted = Math.Clamp(completed, 0, operationTotal);
        if (item is not null) operationItem = item;
        NotifyOperation();
    }

    private IProgress<string> CreateOperationProgress()
    {
        var id = operationId;
        return new Progress<string>(message =>
        {
            if (IsBusy && !operationsDisposed && !operationCancelRequested && id == operationId && !string.IsNullOrWhiteSpace(message))
                Status = message;
        });
    }

    private void RequestOperationCancellation()
    {
        if (!CanCancelOperation) return;
        operationCancelRequested = true;
        Status = "Cancelando a operação. Aguarde a liberação dos arquivos…";
        NotifyOperation();
        cancellation?.Cancel();
    }
}
