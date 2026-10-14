using System.IO;
using System.Text.Json;
using Teclas.Core;

namespace Teclas.App;

public sealed record PortableLibraryData(List<SavedMidi> Library, string? SelectedPath);
public sealed record PortableLibraryPlan(string LibraryJson, IReadOnlyList<LibraryBackupSource> Sources,
    IReadOnlyList<string> MissingFiles, int ItemCount);

public static class LibraryBackupSnapshot
{
    public static PortableLibraryPlan Capture(IEnumerable<SavedMidi> rows, string? selectedPath)
    {
        // Clone every nested choice, including future section/diagram fields; never rewrite the live session.
        var copied = JsonSerializer.Deserialize<List<SavedMidi>>(JsonSerializer.Serialize(rows))!;
        var files = new Dictionary<string, LibraryBackupSource>(StringComparer.OrdinalIgnoreCase);
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        string? Map(string? path, bool required = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (required) throw new InvalidDataException("Um item da biblioteca não tem arquivo de origem.");
                return null;
            }
            string full = Path.GetFullPath(path);
            if (references.TryGetValue(full, out var cached)) return cached;
            string parent = Path.GetDirectoryName(full)!;
            if (!folders.TryGetValue(parent, out string? folder)) folders[parent] = folder = $"g{folders.Count + 1:D5}";
            string relative = "files/" + folder + "/" + Path.GetFileName(full);
            // Reuse the extraction validator even on exports, so a backup always remains portable.
            _ = LibraryBackupService.SafePath(Path.GetTempPath(), relative);
            bool exists = File.Exists(full);
            if (exists) files.Add(relative, new(relative, full));
            else missing.Add(Path.GetFileName(full));
            return references[full] = (exists ? "asset:" : "missing:") + relative;
        }
        string? Preview(string? first)
        {
            string? result = Map(first);
            foreach (string page in ScorePageFiles.Discover(first)) Map(page);
            return result;
        }
        foreach (var row in copied)
        {
            row.FullPath = Map(row.FullPath, true)!;
            row.ScorePath = Map(row.ScorePath); row.PreviewPath = Preview(row.PreviewPath);
            row.AnalysisPath = null; row.Workspace = null; row.HasOperationError = false;
            foreach (var version in row.Versions)
            {
                version.ScorePath = Map(version.ScorePath, true)!; version.PreviewPath = Preview(version.PreviewPath);
                version.AnalysisPath = null; version.Workspace = null;
            }
        }
        string? selected = selectedPath is not null && references.TryGetValue(Path.GetFullPath(selectedPath), out var reference) ? reference : null;
        return new(JsonSerializer.Serialize(new PortableLibraryData(copied, selected)), files.Values.ToArray(), missing, copied.Count);
    }

    public static PortableLibraryData Resolve(LibraryBackupManifest manifest, string root)
    {
        var data = JsonSerializer.Deserialize<PortableLibraryData>(manifest.LibraryJson)
            ?? throw new InvalidDataException("A biblioteca do backup está vazia.");
        if (data.Library is null || data.Library.Count > 10000 || data.Library.Any(row => row is null))
            throw new InvalidDataException("A lista de músicas do backup é inválida.");
        var assets = manifest.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? ResolvePath(string? reference, string[] extensions, bool required = false)
        {
            if (reference is null)
            {
                if (required) throw new InvalidDataException("Uma referência obrigatória está ausente do backup.");
                return null;
            }
            bool missing = reference.StartsWith("missing:", StringComparison.Ordinal);
            if (!missing && !reference.StartsWith("asset:", StringComparison.Ordinal))
                throw new InvalidDataException("O backup contém um caminho que não é portátil.");
            string relative = reference[(missing ? 8 : 6)..];
            if (!extensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase) ||
                (!missing && !assets.Contains(relative)) || (missing && assets.Contains(relative)))
                throw new InvalidDataException("Uma referência do backup não corresponde ao inventário.");
            return LibraryBackupService.SafePath(root, relative);
        }
        string[] sources = [".mid", ".midi", ".kar", ".mscz"], scores = [".mscz"], pages = [".png"];
        foreach (var row in data.Library)
        {
            row.FullPath = ResolvePath(row.FullPath, sources, true)!;
            row.ScorePath = ResolvePath(row.ScorePath, scores); row.PreviewPath = ResolvePath(row.PreviewPath, pages);
            row.AnalysisPath = null; row.Workspace = null; row.HasOperationError = false;
            row.Versions ??= [];
            if (row.Versions.Count > 10000 || row.Versions.Any(v => v is null)) throw new InvalidDataException("Histórico inválido no backup.");
            foreach (var version in row.Versions)
            {
                version.ScorePath = ResolvePath(version.ScorePath, scores, true)!;
                version.PreviewPath = ResolvePath(version.PreviewPath, pages);
                version.AnalysisPath = null; version.Workspace = null;
            }
            row.Status = "Restaurada do backup · escolhas musicais e Histórico preservados";
        }
        string? selected = ResolvePath(data.SelectedPath, sources);
        // Use the same migration/default validation as normal sessions, in memory and without local executable paths.
        string temporary = Path.Combine(Path.GetTempPath(), "teclas-backup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings { Library = data.Library, SelectedMidiPath = selected }));
            var normalized = Settings.Load(temporary);
            if (normalized.LoadWarning.Length > 0) throw new InvalidDataException("As escolhas musicais do backup não puderam ser lidas.");
            return new(normalized.Library, selected);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
