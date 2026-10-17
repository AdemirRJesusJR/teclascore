using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

public partial class PerformanceWindow : Window
{
    private readonly LocalPerformanceRecorder recorder;
    private bool refreshing;
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    public PerformanceWindow(LocalPerformanceRecorder recorder)
    {
        this.recorder = recorder;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshReportAsync();
        SourceInitialized += (_, _) => ApplyNativeTheme();
    }

    internal async Task RefreshReportAsync()
    {
        if (refreshing) return;
        refreshing = true; Actions.IsEnabled = false;
        try
        {
            var report = await Task.Run(recorder.ReadReport);
            PerformanceRows.ItemsSource = report.Records.Reverse().Select(record => new
            {
                When = record.StartedUtc.LocalDateTime.ToString("dd/MM HH:mm:ss", Culture),
                Operation = OperationLabel(record.Operation),
                Process = record.ProcessKind switch { PerformanceProcessKind.Worker => "Python", PerformanceProcessKind.MuseScore => "MuseScore", _ => "Aplicativo" },
                Outcome = record.Outcome switch { PerformanceOutcome.Success => "Concluída", PerformanceOutcome.Cancelled => "Cancelada", PerformanceOutcome.Timeout => "Tempo limite", _ => "Falha" },
                Duration = Seconds(record.DurationMilliseconds),
                Cpu = record.CpuMilliseconds is { } cpu ? Seconds(cpu) : "—",
                Memory = record.PeakWorkingSetBytes is { } memory ? (memory / 1048576d).ToString("N1", Culture) + " MiB" : "—"
            }).ToArray();
            SummaryText.Text = $"{report.Records.Count} registros disponíveis · histórico rotativo de até 512 operações";
            ReportStatus.Text = report.UnreadableFiles > 0 ? $"{report.UnreadableFiles} arquivos de diagnóstico não puderam ser lidos. Os demais registros estão disponíveis."
                : !report.StorageAvailable ? "O histórico local não está disponível. As operações musicais continuam funcionando."
                : report.Records.Count == 0 ? "Importe, analise, reproduza ou gere uma partitura para registrar as primeiras medições." : "Histórico atualizado.";
        }
        finally { refreshing = false; Actions.IsEnabled = true; }
    }

    private static string Seconds(double milliseconds) => (milliseconds / 1000d).ToString("N2", Culture) + " s";
    private static string OperationLabel(PerformanceOperation operation) => operation switch
    {
        PerformanceOperation.AppImport => "Importar biblioteca",
        PerformanceOperation.AppAnalyze => "Analisar música",
        PerformanceOperation.AppGenerate => "Gerar partitura",
        PerformanceOperation.AppPlayback => "Preparar reprodução",
        PerformanceOperation.AppPdfBatch => "Exportar PDF",
        PerformanceOperation.AppScoreUpdate => "Atualizar partitura",
        PerformanceOperation.AppBackup => "Backup da biblioteca",
        PerformanceOperation.EngineAnalyze => "Análise musical",
        PerformanceOperation.EngineGenerate => "Geração musical",
        PerformanceOperation.EngineMeasureMap => "Mapear compassos",
        PerformanceOperation.EngineOther => "Processamento musical",
        PerformanceOperation.ScoreExportNative => "Exportar partitura",
        PerformanceOperation.ScoreExportPreview => "Renderizar prévia",
        PerformanceOperation.ScoreExportAudio => "Preparar áudio",
        PerformanceOperation.ScoreExportMidi => "Preparar metrônomo",
        PerformanceOperation.ScoreExportCopy => "Exportar MusicXML ou imagens",
        PerformanceOperation.ScoreExportPdf => "Renderizar PDF",
        _ => "Operação do aplicativo"
    };

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshReportAsync();

    private async void ClearClick(object sender, RoutedEventArgs e) => await ClearReportAsync();

    internal async Task ClearReportAsync()
    {
        if (refreshing) return;
        refreshing = true; Actions.IsEnabled = false;
        bool cleared;
        try { cleared = await Task.Run(recorder.Clear); }
        finally { refreshing = false; Actions.IsEnabled = true; }
        await RefreshReportAsync();
        ReportStatus.Text = cleared ? "Registros de desempenho removidos. Suas músicas e preferências foram preservadas." : "Não foi possível limpar todos os registros de desempenho.";
    }

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (refreshing) return;
        var dialog = new SaveFileDialog { Title = "Exportar relatório de desempenho", FileName = "TeclaScore-desempenho-" + DateTime.Now.ToString("yyyyMMdd-HHmm"), Filter = "Relatório JSON (*.json)|*.json", DefaultExt = ".json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        await ExportReportToAsync(dialog.FileName);
    }

    internal async Task ExportReportToAsync(string path)
    {
        if (refreshing) return;
        refreshing = true; Actions.IsEnabled = false;
        try
        {
            bool exported = await Task.Run(() => recorder.ExportReport(path));
            ReportStatus.Text = exported ? "Relatório salvo no local escolhido." : "Não foi possível salvar. Escolha uma pasta com permissão de escrita.";
        }
        finally { refreshing = false; Actions.IsEnabled = true; }
    }

    private void ApplyNativeTheme()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            uint dark = ThemeManager.Current.IsDark ? 1u : 0u;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(uint));
            foreach (var (attribute, resource) in new[] { (35u, "Background"), (36u, "Ink"), (34u, "Line") })
            {
                var color = (Color)ColorConverter.ConvertFromString(ThemeManager.ColorHex(resource));
                uint value = (uint)(color.R | color.G << 8 | color.B << 16);
                _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);
}
