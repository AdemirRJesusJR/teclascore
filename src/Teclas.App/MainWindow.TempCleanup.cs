using System.IO;
using System.Windows;

namespace Teclas.App;

/// <summary>
/// Frees the space used by temporary files: the audio and measure-map caches (rebuilt when needed) and analysis folders no
/// library card uses anymore. MIDIs, scores, versions, drafts, the conversion queue and the study history are never touched.
/// </summary>
public partial class MainWindow
{
    internal Func<string, bool>? confirmTempCleanup = null;
    private string tempFilesStatus = "Calculando o espaço ocupado…";
    public string TempFilesStatus { get => tempFilesStatus; private set { tempFilesStatus = value; Notify(); } }

    private string DataFolder => persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!;

    /// <summary>Folders that can go: the audio cache, and job folders not referenced by any card.</summary>
    internal IReadOnlyList<string> TemporaryFolders()
    {
        var folders = new List<string>();
        if (Directory.Exists(PlaybackCacheDirectory)) folders.Add(PlaybackCacheDirectory);
        string jobs = Path.Combine(DataFolder, "jobs");
        if (Directory.Exists(jobs))
        {
            // A card keeps its workspace and the folder of its saved analysis.
            var used = Files.Select(row => row.Workspace).Concat(Files.Select(row => row.AnalysisPath is { } analysis ? Path.GetDirectoryName(analysis) : null))
                .OfType<string>().Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            folders.AddRange(Directory.EnumerateDirectories(jobs).Where(folder => !used.Contains(Path.GetFullPath(folder))
                && !used.Any(path => path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))));
        }
        return folders;
    }

    internal static long FolderSize(string folder)
    {
        try { return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => { try { return file.Length; } catch (IOException) { return 0; } }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB"
    };

    internal async Task RefreshTempFilesStatus()
    {
        var folders = TemporaryFolders();
        long bytes = await Task.Run(() => folders.Sum(FolderSize));
        TempFilesStatus = bytes < 1 << 20 ? "Quase nada ocupado por arquivos temporários." : $"Arquivos temporários ocupam {Size(bytes)} (áudios de reprodução, mapas de compassos e análises antigas).";
    }

    private async void CleanTempFilesClick(object sender, RoutedEventArgs e) => await CleanTemporaryFiles();

    internal async Task<long> CleanTemporaryFiles()
    {
        if (!IsIdle) return 0;
        var folders = TemporaryFolders();
        long before = await Task.Run(() => folders.Sum(FolderSize));
        string question = $"Liberar {Size(before)}? Os áudios e mapas são refeitos quando você tocar uma partitura de novo. MIDIs, partituras, versões, rascunhos e o histórico não são apagados.";
        bool confirmed = confirmTempCleanup is { } confirm ? confirm(question)
            : MessageBox.Show(this, question, "Liberar espaço", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed) return 0;
        // Nothing may keep using the cache while it goes away.
        StopTrackAudition(); ResetPlayback(); ClearScoreSynchronization(); scoreMapCache.Clear();
        long freed = 0;
        await Task.Run(() =>
        {
            foreach (string folder in folders)
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToArray())
                    try { long length = file.Length; file.Delete(); freed += length; } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                foreach (var directory in new DirectoryInfo(folder).EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length).ToArray())
                    try { directory.Delete(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                if (folder != PlaybackCacheDirectory) try { Directory.Delete(folder); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        });
        Status = $"Espaço liberado: {Size(freed)}." + (freed < before ? " Alguns arquivos em uso ficaram para a próxima limpeza." : "");
        await RefreshTempFilesStatus();
        return freed;
    }
}
