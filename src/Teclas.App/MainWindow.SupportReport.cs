using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Problem report (Configurações): a zip with an anonymized summary, recent messages and, optionally, conversion diagnostics.</summary>
public partial class MainWindow
{
    private readonly Queue<(DateTime When, string Message)> recentMessages = new();
    internal Func<string, string?>? supportReportTarget = null;
    internal IReadOnlyList<(DateTime When, string Message)> RecentMessages => recentMessages.ToArray();

    /// <summary>Keeps the last 60 status messages for the report (nothing leaves the computer unless the user sends the file).</summary>
    private void RememberMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        recentMessages.Enqueue((DateTime.Now, message));
        while (recentMessages.Count > 60) recentMessages.Dequeue();
    }

    private void SupportReportClick(object sender, RoutedEventArgs e)
    {
        bool include = MessageBox.Show(this, "Incluir os diagnósticos das conversões que falharam nos últimos 14 dias?\n\nEles ajudam a achar o problema, mas podem citar nomes de arquivos MIDI. Pastas pessoais e o nome de usuário são trocados por marcadores.",
            "Relatório de problema", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        CreateSupportReport(include);
    }

    internal string? CreateSupportReport(bool includeDiagnostics)
    {
        string? target;
        if (supportReportTarget is { } choose) target = choose("TeclaScore-relatorio.zip");
        else
        {
            var dialog = new SaveFileDialog { Title = "Salvar relatório de problema", Filter = "Arquivo ZIP|*.zip", DefaultExt = ".zip", AddExtension = true,
                FileName = $"TeclaScore-relatorio-{DateTime.Now:yyyy-MM-dd-HHmm}.zip", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
            target = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (target is null) return null;
        var facts = new List<(string, string)>
        {
            ("MuseScore", File.Exists(MuseScorePath) ? MuseScorePath + " (encontrado)" : "não encontrado"),
            ("Python", File.Exists(PythonPath) ? PythonPath + " (encontrado)" : PythonPath.Length == 0 ? "padrão do app" : "não encontrado"),
            ("Biblioteca", $"{Files.Count} músicas · {Files.Count(r => r.ScorePath is not null)} com partitura · {Files.Count(r => r.HasOperationError)} com falha"),
            ("Tema", settings.ThemeId), ("Fonte e tamanho", settings.ScoreMusicFont + " · " + settings.ScoreSize),
            ("Pasta de saída", settings.OutputDirectory), ("Fila de conversões", conversionQueue is null ? "fechada" : $"{conversionQueue.Items.Count} itens")
        };
        string data = persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!;
        try
        {
            SupportReport.Create(target, new SupportReportInput(AppVersion, facts, RecentMessages, [data, Path.Combine(Path.GetTempPath(), "TeclasPartituras")], includeDiagnostics));
            Status = "Relatório salvo: " + target + ". Envie este arquivo junto com a descrição do problema.";
            if (supportReportTarget is null)
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { }
            return target;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { Status = "Não foi possível salvar o relatório: " + error.Message; return null; }
    }
}
