namespace Teclas.Core;

/// <summary>A MIDI found in a folder; the file is referenced in place and never copied.</summary>
public sealed record MidiFolderEntry(string FullPath, string Name, string RelativeFolder, long Bytes, DateTime ModifiedLocal);
public sealed record MidiFolderScan(string Folder, IReadOnlyList<MidiFolderEntry> Entries, bool Truncated, int SkippedFolders);

/// <summary>Lists MIDI and karaoke files without opening or changing them.</summary>
public static class MidiFolderScanner
{
    public const int MaximumEntries = 20_000;
    private static readonly string[] Extensions = [".mid", ".midi", ".kar"];

    public static bool IsMidi(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static MidiFolderScan Scan(string folder, bool includeSubfolders, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("Escolha uma pasta com caminho completo.", nameof(folder));
        string root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("A pasta escolhida não está disponível: " + root);
        int skipped = 0;
        var entries = new List<MidiFolderEntry>();
        var pending = new Stack<string>(); pending.Push(root);
        bool truncated = false;
        while (pending.Count > 0 && !truncated)
        {
            token.ThrowIfCancellationRequested();
            string current = pending.Pop();
            IEnumerable<string> files, folders;
            try
            {
                files = Directory.EnumerateFiles(current).Where(IsMidi).ToArray();
                folders = includeSubfolders ? Directory.EnumerateDirectories(current).ToArray() : [];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { skipped++; continue; }
            foreach (string file in files)
            {
                if (entries.Count >= MaximumEntries) { truncated = true; break; }
                try
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    string relative = Path.GetRelativePath(root, info.DirectoryName!);
                    entries.Add(new(info.FullName, info.Name, relative == "." ? "" : relative, info.Length, info.LastWriteTime));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
            }
            // Reparse points (junctions, links) can loop back to an ancestor; they are listed only when chosen directly.
            foreach (string child in folders.OrderByDescending(f => f, StringComparer.CurrentCultureIgnoreCase))
            {
                try { if ((File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0) pending.Push(child); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { skipped++; }
            }
        }
        var ordered = entries.OrderBy(e => e.RelativeFolder, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new(root, ordered, truncated, skipped);
    }

    /// <summary>Case-insensitive match on every word, ignoring accents, over the name and its folder.</summary>
    public static bool Matches(MidiFolderEntry entry, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string haystack = Fold(entry.RelativeFolder + " " + entry.Name);
        return filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => haystack.Contains(Fold(word), StringComparison.Ordinal));
    }

    private static string Fold(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        return new string(normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
    }
}
