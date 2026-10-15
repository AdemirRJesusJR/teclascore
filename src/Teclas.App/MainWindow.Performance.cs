using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private LocalPerformanceRecorder? performanceRecorder, previousPerformanceRecorder;

    private void InitializePerformance(bool smoke, string? settingsPath)
    {
        if (smoke && settingsPath is null) return;
        string directory = settingsPath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        performanceRecorder = new(Path.Combine(directory, "performance"));
        previousPerformanceRecorder = PerformanceDiagnostics.Recorder;
        PerformanceDiagnostics.Recorder = performanceRecorder;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(PerformanceDiagnostics.Recorder, performanceRecorder))
                PerformanceDiagnostics.Recorder = previousPerformanceRecorder;
        };
    }

    private void PerformanceClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || performanceRecorder is null) return;
        new PerformanceWindow(performanceRecorder) { Owner = this }.ShowDialog();
    }

    // Only fixed enum values go to disk, never UI text or musical file names.
    private static PerformanceOperation PerformanceOperationFor(string title) => title switch
    {
        "Importando para a biblioteca" => PerformanceOperation.AppImport,
        "Analisando notas e acordes" or "Diagnosticando a partitura" or "Lendo compassos da música" => PerformanceOperation.AppAnalyze,
        "Gerando partitura" => PerformanceOperation.AppGenerate,
        "Preparando áudio da partitura" or "Preparando pista para ouvir" or "Preparando acompanhamento da partitura" => PerformanceOperation.AppPlayback,
        "Exportando partituras em PDF" or "Salvando partitura em PDF" => PerformanceOperation.AppPdfBatch,
        "Preparando backup da biblioteca" or "Salvando backup da biblioteca" or "Conferindo backup da biblioteca" or "Restaurando biblioteca" => PerformanceOperation.AppBackup,
        "Atualizando partitura" or "Revisando a apresentação" => PerformanceOperation.AppScoreUpdate,
        _ => PerformanceOperation.AppOther
    };
}
