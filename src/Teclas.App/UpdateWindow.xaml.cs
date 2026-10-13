using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

public partial class UpdateWindow : Window
{
    private readonly string pythonPath;
    private readonly string museScorePath;
    private readonly CancellationTokenSource lifetime = new();
    private ReleaseUpdate? availableRelease;
    private bool installing;
    private readonly Version installedVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new(0, 0, 0);

    public UpdateWindow(string pythonPath, string museScorePath)
    {
        this.pythonPath = pythonPath;
        this.museScorePath = museScorePath;
        InitializeComponent();
        InstalledVersionText.Text = $"Versão instalada: {installedVersion.ToString(3)}";
        SourceInitialized += (_, _) => ApplyNativeTheme();
        Closed += (_, _) => lifetime.Cancel();
        Closing += (_, e) => { if (installing) e.Cancel = true; };
    }

    private async void CheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Consultando as versões publicadas…";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            availableRelease = await new ReleaseUpdateService(client).CheckAsync(lifetime.Token);
            UpdateStatusText.Text = availableRelease is null
                ? "Não foi possível acessar uma versão pública. O repositório pode ser privado ou ainda não ter releases. Abra o GitHub para conferir."
                : availableRelease.Version > new Version(installedVersion.Major, installedVersion.Minor, installedVersion.Build)
                    ? $"Nova versão disponível: {availableRelease.Version}. Baixe o pacote na página da versão."
                    : $"Você está atualizado. Última versão estável publicada: {availableRelease.Version}.";
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) UpdateStatusText.Text = "A consulta demorou demais. Tente novamente ou abra o GitHub."; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        { UpdateStatusText.Text = "Não foi possível consultar as versões. Verifique sua conexão ou abra o GitHub para consultar a publicação."; }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private void OpenReleasesClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(availableRelease?.PageUrl ?? ReleaseUpdateService.ReleasesUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { UpdateStatusText.Text = "Não foi possível abrir o navegador. Endereço: " + ReleaseUpdateService.ReleasesUrl; }
    }

    private async void DiagnosticsClick(object sender, RoutedEventArgs e)
    {
        DiagnosticsButton.IsEnabled = false;
        DiagnosticsText.Text = "Conferindo as dependências…";
        try { DiagnosticsText.Text = await DependencyDiagnostics.CheckAsync(pythonPath, museScorePath, lifetime.Token); }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) DiagnosticsText.Text = "O diagnóstico excedeu o tempo de espera. Confira os caminhos em Configurações."; }
        finally { DiagnosticsButton.IsEnabled = true; }
    }

    private async void InstallUpdateClick(object sender, RoutedEventArgs e)
    {
        var zipDialog = new OpenFileDialog { Title = "Selecione o ZIP do TeclaScore baixado do GitHub", Filter = "Pacote TeclaScore (*.zip)|*.zip", CheckFileExists = true };
        if (zipDialog.ShowDialog(this) != true) return;
        string expectedHash;
        try
        {
            if (availableRelease?.PackageName == Path.GetFileName(zipDialog.FileName) && availableRelease.PackageSha256 is not null)
                expectedHash = availableRelease.PackageSha256;
            else
            {
                var hashDialog = new OpenFileDialog { Title = "Selecione SHA256SUMS baixado da mesma release", Filter = "SHA256SUMS|SHA256SUMS;SHA256SUMS.txt|Todos os arquivos|*.*", CheckFileExists = true, InitialDirectory = Path.GetDirectoryName(zipDialog.FileName) };
                if (hashDialog.ShowDialog(this) != true) return;
                expectedHash = ReleaseUpdateService.ReadChecksum(hashDialog.FileName, Path.GetFileName(zipDialog.FileName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { UpdateStatusText.Text = ex.Message; return; }
        if (MessageBox.Show(this, "Instalar este pacote do TeclaScore para seu usuário? A versão anterior e suas configurações serão preservadas.", "Instalar atualização", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        installing = true;
        InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = CloseUpdateButton.IsEnabled = false;
        var extraction = Path.Combine(Path.GetTempPath(), "TeclaScoreInstall-" + Guid.NewGuid().ToString("N"));
        try
        {
            UpdateStatusText.Text = "Validando o SHA-256 e o conteúdo do pacote…";
            await ReleaseUpdateService.VerifyPackageAsync(zipDialog.FileName, expectedHash, lifetime.Token);
            await Task.Run(() => ZipFile.ExtractToDirectory(zipDialog.FileName, extraction), lifetime.Token);
            UpdateStatusText.Text = "Instalando para seu usuário…";
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = extraction };
            foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(extraction, "Install.ps1"), "-SourceDirectory", extraction }) start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o instalador.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await output;
            var errorText = await errors;
            if (process.ExitCode != 0) throw new IOException("O instalador não concluiu. A versão anterior foi preservada. " + errorText[..Math.Min(700, errorText.Length)]);
            UpdateStatusText.Text = "Instalação concluída. Abra a nova versão pelo atalho TeclaScore na área de trabalho ou no menu Iniciar.";
            if (MessageBox.Show(this, "A nova versão foi instalada. Deseja fechar o aplicativo atual? Depois abra o TeclaScore pelo novo atalho.", "Instalação concluída", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            { installing = false; Application.Current.Shutdown(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException or InvalidOperationException or ArgumentException)
        { UpdateStatusText.Text = ex.Message; }
        finally
        {
            installing = false;
            InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = CloseUpdateButton.IsEnabled = true;
            try { if (Directory.Exists(extraction)) Directory.Delete(extraction, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void ApplyNativeTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || TryFindResource("Background") is not SolidColorBrush brush) return;
        var dark = (brush.Color.R + brush.Color.G + brush.Color.B) < 384 ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var color = brush.Color.R | brush.Color.G << 8 | brush.Color.B << 16;
        DwmSetWindowAttribute(handle, 35, ref color, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
