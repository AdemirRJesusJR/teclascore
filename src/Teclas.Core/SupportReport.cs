using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Teclas.Core;

/// <summary>What goes into a problem report: a summary written by the app, recent messages and engine error diagnostics.</summary>
public sealed record SupportReportInput(string AppVersion, IReadOnlyList<(string Label, string Value)> Facts,
    IReadOnlyList<(DateTime When, string Message)> RecentMessages, IReadOnlyList<string> SearchFolders, bool IncludeDiagnostics);

/// <summary>
/// A zip to send when something goes wrong. Personal folder names are replaced (%USERPROFILE%, the user name), the library
/// is reported only as counts, and engine diagnostics (which may name music files) are included only when asked.
/// </summary>
public static partial class SupportReport
{
    public const int MaximumDiagnostics = 10;
    public const long MaximumDiagnosticBytes = 1024 * 1024;

    public static string Anonymize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase).Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        string user = Environment.UserName;
        if (user.Length >= 3) text = Regex.Replace(text, Regex.Escape(user), "<usuario>", RegexOptions.IgnoreCase);
        return EmailPattern().Replace(text, "<email>");
    }

    /// <summary>Engine error diagnostics of the last 14 days, newest first.</summary>
    public static IReadOnlyList<string> FindDiagnostics(IEnumerable<string> folders) => folders.Where(Directory.Exists)
        .SelectMany(folder =>
        {
            try { return Directory.EnumerateFiles(folder, "*-error-*.json", SearchOption.AllDirectories); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        })
        .Select(path => new FileInfo(path)).Where(file => file.Length <= MaximumDiagnosticBytes && file.LastWriteTime > DateTime.Now.AddDays(-14))
        .OrderByDescending(file => file.LastWriteTime).Take(MaximumDiagnostics).Select(file => file.FullName).ToArray();

    public static void Create(string destination, SupportReportInput input)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"TeclaScore {input.AppVersion} · relatório gerado em {DateTime.Now:dd/MM/yyyy HH:mm}");
        summary.AppendLine($"Windows {Environment.OSVersion.Version} · {(Environment.Is64BitOperatingSystem ? "64" : "32")} bits · .NET {Environment.Version}");
        foreach (var (label, value) in input.Facts) summary.AppendLine($"{label}: {Anonymize(value)}");
        summary.AppendLine().AppendLine("Mensagens recentes:");
        foreach (var (when, message) in input.RecentMessages) summary.AppendLine($"{when:dd/MM HH:mm:ss}  {Anonymize(message)}");
        var diagnostics = input.IncludeDiagnostics ? FindDiagnostics(input.SearchFolders) : [];
        summary.AppendLine().AppendLine(input.IncludeDiagnostics ? $"Diagnósticos de conversões com falha incluídos: {diagnostics.Count}" : "Diagnósticos de conversões não incluídos.");
        string temporary = destination + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("resumo.txt").Open(), new UTF8Encoding(true))) writer.Write(summary.ToString());
                int index = 0;
                foreach (string path in diagnostics)
                {
                    string text;
                    try { text = File.ReadAllText(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                    using var writer = new StreamWriter(zip.CreateEntry($"diagnosticos/{++index:00}-{Path.GetFileName(path)}").Open(), new UTF8Encoding(false));
                    writer.Write(Anonymize(text));
                }
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}
