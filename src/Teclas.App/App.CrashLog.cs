using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Unexpected errors are written, anonymized, to falhas\ in the app data folder (the last 20 are kept), so the problem
/// report already carries them. Nothing is sent anywhere.
/// </summary>
public partial class App
{
    public const int KeptCrashLogs = 20;
    public static string CrashFolder => Path.Combine(Settings.DataDirectory, "falhas");

    private void InstallCrashLog()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            string? file = WriteCrashLog(CrashFolder, "interface", e.Exception);
            MessageBox.Show($"O TeclaScore encontrou um erro inesperado e vai tentar continuar.\n\n{e.Exception.Message}\n\n" +
                (file is null ? "" : "Os detalhes foram guardados e entram no Relatório de problema (Configurações)."),
                "TeclaScore", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception error) WriteCrashLog(CrashFolder, "aplicativo", error); };
        TaskScheduler.UnobservedTaskException += (_, e) => { WriteCrashLog(CrashFolder, "tarefa", e.Exception); e.SetObserved(); };
    }

    /// <summary>Writes one anonymized crash file and trims the folder; returns its path, or null when it could not be written.</summary>
    public static string? WriteCrashLog(string folder, string where, Exception error)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"falha-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            var text = new StringBuilder()
                .AppendLine($"TeclaScore {typeof(App).Assembly.GetName().Version?.ToString(3)} · {DateTime.Now:dd/MM/yyyy HH:mm:ss} · {where}")
                .AppendLine($"Windows {Environment.OSVersion.Version} · .NET {Environment.Version}")
                .AppendLine().AppendLine(error.ToString());
            File.WriteAllText(path, SupportReport.Anonymize(text.ToString()), new UTF8Encoding(true));
            foreach (var old in new DirectoryInfo(folder).GetFiles("falha-*.txt").OrderByDescending(f => f.Name).Skip(KeptCrashLogs)) old.Delete();
            return path;
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { return null; }
    }
}
