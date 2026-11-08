using System.IO;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    /// <summary>
    /// Reads and analyzes the new MIDIs of an import (files, watched folder, restored backup) on several cores before the
    /// one-by-one loop, which then finds them ready in <c>prefetchedMidis</c>. The caller clears the cache when the loop ends.
    /// </summary>
    private async Task PrefetchForImportAsync(IReadOnlyCollection<string> paths, CancellationToken token)
    {
        var known = Files.Select(row => row.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = paths.Where(path => !string.Equals(Path.GetExtension(path), ".mscz", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath).Where(path => !known.Contains(path) && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // One file gains nothing from the parallel read.
        if (fresh.Length < 2) return;
        await Task.Run(() => PrefetchLibrary(fresh), token);
    }

    internal async Task<IReadOnlyList<QuietImport>> ImportQuietlyAsync(IEnumerable<string> paths)
    {
        var list = paths.Where(MidiFolderScanner.IsMidi).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await PrefetchForImportAsync(list, CancellationToken.None);
        try { return await ImportQuietlyCoreAsync(list); }
        finally { prefetchedMidis = null; }
    }
}
