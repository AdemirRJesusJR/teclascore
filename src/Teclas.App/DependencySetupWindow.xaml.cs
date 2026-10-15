using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

public partial class DependencySetupWindow : Window
{
    private readonly DependencyPreparation preparation;
    private CancellationTokenSource? operation;
    private PythonDependencyCheck? python;
    private MuseScoreDependencyCheck? museScore;
    private bool settingPaths, closeAfterCancellation;
    public string SelectedPythonPath { get; private set; } = "";
    public string SelectedMuseScorePath { get; private set; } = "";

    public DependencySetupWindow(string pythonPath, string museScorePath, DependencyPreparation? preparation = null)
    {
        this.preparation = preparation ?? new DependencyPreparation(Path.Combine(Settings.DataDirectory, "Dependencies"), Path.Combine(Settings.DataDirectory, "DependencyChecks"));
        InitializeComponent();
        SetPaths(pythonPath, museScorePath);
        RuntimeStatus.Text = $"Pronto: .NET Desktop {Environment.Version}. O aplicativo está em execução.";
        SourceInitialized += (_, _) => ApplyNativeTheme();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CancelSetupClick(this, new RoutedEventArgs()); } };
        Closing += (_, e) =>
        {
            if (operation is null) return;
            e.Cancel = true; closeAfterCancellation = true; operation.Cancel(); SetupStatus.Text = "Cancelando e aguardando o processo terminar…";
        };
    }

    private void PathsChanged(object sender, TextChangedEventArgs e)
    {
        if (settingPaths || ApplyDependenciesButton is null) return;
        // Also reject programmatic/binding changes while a check is in flight.
        operation?.Cancel();
        python = null; museScore = null;
        PythonStatus.Text = MuseScoreStatus.Text = "Caminho alterado. Clique em Detectar e verificar.";
        UpdateButtons();
    }
    private void SetPaths(string pythonPath, string museScorePath)
    {
        settingPaths = true;
        try { PythonPathBox.Text = pythonPath; MuseScorePathBox.Text = museScorePath; }
        finally { settingPaths = false; }
    }
    private void UpdateButtons()
    {
        bool idle = operation is null;
        SetupFields.IsEnabled = CheckDependenciesButton.IsEnabled = idle;
        PreparePythonButton.IsEnabled = idle && python?.Supported == true && SamePath(python.Path, PythonPathBox.Text);
        ApplyDependenciesButton.IsEnabled = idle && python?.Ready == true && museScore?.Ready == true
            && SamePath(python.Path, PythonPathBox.Text) && SamePath(museScore.Path, MuseScorePathBox.Text);
        CancelSetupButton.Content = idle ? "Fechar" : "Cancelar";
        SetupProgress.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
    }
    private static bool SamePath(string first, string second) => string.Equals(first, second.Trim(), StringComparison.OrdinalIgnoreCase);
    private async Task OperateAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation; UpdateButtons();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { SetupStatus.Text = "Operação cancelada. As configurações anteriores foram preservadas."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or ArgumentException or TimeoutException)
        { SetupStatus.Text = ex.Message; }
        finally
        {
            operation = null; UpdateButtons();
            if (closeAfterCancellation) { closeAfterCancellation = false; DialogResult = false; }
        }
    }
    private IProgress<DependencyProgress> Progress()
    {
        var owner = operation;
        return new Progress<DependencyProgress>(value =>
        { if (ReferenceEquals(operation, owner) && owner is { IsCancellationRequested: false }) SetupStatus.Text = value.Message; });
    }

    private async void CheckDependenciesClick(object sender, RoutedEventArgs e) => await OperateAsync(async token =>
    {
        string currentPython = PythonPathBox.Text.Trim(), currentMuse = MuseScorePathBox.Text.Trim();
        python = null; museScore = null;
        SetupStatus.Text = "Procurando instalações neste computador…";
        var candidates = await Task.Run(() => preparation.Discover(currentPython, currentMuse), token);
        PythonDependencyCheck? bestPython = null;
        foreach (string path in candidates.Python)
        {
            token.ThrowIfCancellationRequested(); SetupStatus.Text = "Conferindo Python e Pillow…";
            var result = await preparation.CheckPythonAsync(path, token);
            if (bestPython is null || (!bestPython.Supported && result.Supported)) bestPython = result;
            if (result.Ready) { bestPython = result; break; }
        }
        bestPython ??= await preparation.CheckPythonAsync(currentPython, token);
        MuseScoreDependencyCheck? bestMuse = null;
        foreach (string path in candidates.MuseScore)
        {
            var result = await preparation.CheckMuseScoreAsync(path, Progress(), token);
            bestMuse ??= result;
            if (result.Ready) { bestMuse = result; break; }
        }
        bestMuse ??= await preparation.CheckMuseScoreAsync(currentMuse, Progress(), token);
        token.ThrowIfCancellationRequested();
        python = bestPython; museScore = bestMuse;
        SetPaths(bestPython.Path, bestMuse.Path);
        PythonStatus.Text = bestPython.Message; MuseScoreStatus.Text = bestMuse.Message;
        SetupStatus.Text = bestPython.Ready && bestMuse.Ready ? "Tudo conferido. Clique em Usar estas configurações para salvar os caminhos." : "Confira os itens acima. Após instalar ou preparar o que falta, execute o teste novamente.";
    });

    private async void PreparePythonClick(object sender, RoutedEventArgs e)
    {
        if (operation is not null || python?.Supported != true || !SamePath(python.Path, PythonPathBox.Text)) return;
        // The button explains the download and private environment; this is the explicit installation action.
        string original = python.Path;
        await OperateAsync(async token =>
        {
            var prepared = await preparation.PreparePythonAsync(original, Progress(), token);
            token.ThrowIfCancellationRequested(); python = prepared;
            SetPaths(prepared.Path, MuseScorePathBox.Text);
            PythonStatus.Text = prepared.Message;
            SetupStatus.Text = "Python do TeclaScore preparado em uma pasta própria. Use estas configurações para ativá-lo; o Python anterior foi preservado.";
        });
    }
    private void ApplyDependenciesClick(object sender, RoutedEventArgs e)
    {
        if (operation is not null || python?.Ready != true || museScore?.Ready != true || !SamePath(python.Path, PythonPathBox.Text) || !SamePath(museScore.Path, MuseScorePathBox.Text)) return;
        if (!File.Exists(python.Path) || !File.Exists(museScore.Path)) { SetupStatus.Text = "Um executável não está mais disponível. Execute Detectar e verificar novamente."; python = null; museScore = null; UpdateButtons(); return; }
        SelectedPythonPath = python.Path; SelectedMuseScorePath = museScore.Path; DialogResult = true;
    }
    private void CancelSetupClick(object sender, RoutedEventArgs e)
    {
        if (operation is not null) { operation.Cancel(); SetupStatus.Text = "Cancelando e aguardando o processo terminar…"; }
        else DialogResult = false;
    }
    private void BrowsePythonClick(object sender, RoutedEventArgs e) => Browse(PythonPathBox, "Localizar python.exe");
    private void BrowseMuseScoreClick(object sender, RoutedEventArgs e) => Browse(MuseScorePathBox, "Localizar MuseScore4.exe");
    private void Browse(TextBox target, string title)
    {
        if (operation is not null) return;
        var dialog = new OpenFileDialog { Title = title, Filter = "Executável (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName;
    }
    private void DownloadPythonClick(object sender, RoutedEventArgs e) => OpenOfficial(DependencyPreparation.PythonDownloadUrl);
    private void DownloadMuseScoreClick(object sender, RoutedEventArgs e) => OpenOfficial(DependencyPreparation.MuseScoreDownloadUrl);
    private void OpenOfficial(string url)
    {
        if (operation is not null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); SetupStatus.Text = "Site oficial aberto. Conclua a instalação e volte para Detectar e verificar."; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { SetupStatus.Text = "Não foi possível abrir o navegador. Acesse " + url; }
    }
    private void ApplyNativeTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || TryFindResource("Background") is not SolidColorBrush brush) return;
        int dark = (brush.Color.R + brush.Color.G + brush.Color.B) < 384 ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        int color = brush.Color.R | brush.Color.G << 8 | brush.Color.B << 16;
        DwmSetWindowAttribute(handle, 35, ref color, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
