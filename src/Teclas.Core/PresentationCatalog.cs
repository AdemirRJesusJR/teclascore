using System.Text.Json;

namespace Teclas.Core;

public sealed record PresentationListInfo(string Path, string Title, int Songs, bool IsDefault)
{
    public string Label => $"{Title} ({Songs})";
}

/// <summary>
/// Several saved repertoires (setlists). The first one keeps the original presentation.json, so existing lists
/// open unchanged; the others live in a "repertorios" folder next to it. Each file is handled by its own
/// <see cref="PresentationStore"/>. Deleting moves the file aside instead of erasing it.
/// </summary>
public sealed class PresentationCatalog(string defaultPath)
{
    public string DefaultPath { get; } = System.IO.Path.GetFullPath(defaultPath);
    public string Folder => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(DefaultPath)!, "repertorios");

    public IReadOnlyList<PresentationListInfo> List()
    {
        var items = new List<PresentationListInfo> { Describe(DefaultPath, true) };
        if (Directory.Exists(Folder))
            items.AddRange(Directory.EnumerateFiles(Folder, "*.json").Select(path => Describe(path, false)).OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase));
        return items;
    }

    /// <summary>Creates an empty repertoire with this title and returns its file.</summary>
    public string Create(string title)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Novo repertório" : title.Trim();
        if (title.Length > 240) title = title[..240];
        Directory.CreateDirectory(Folder);
        string path = System.IO.Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".json");
        var store = new PresentationStore(path); store.Load();
        store.Save(PresentationDocument.Empty with { Title = title });
        return path;
    }

    /// <summary>Removes a repertoire from the list (never the first one); the file is kept with a ".excluido" suffix.</summary>
    public void Delete(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (string.Equals(full, DefaultPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("O primeiro repertório não pode ser excluído; esvazie a lista se quiser.");
        if (!string.Equals(System.IO.Path.GetDirectoryName(full), System.IO.Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Repertório fora da pasta de repertórios.", nameof(path));
        if (File.Exists(full)) File.Move(full, full + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".excluido");
    }

    private static PresentationListInfo Describe(string path, bool isDefault)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= 2 * 1024 * 1024)
            {
                using var stream = File.OpenRead(path);
                var document = JsonSerializer.Deserialize<PresentationDocument>(stream);
                if (document is not null) return new(path, string.IsNullOrWhiteSpace(document.Title) ? "Sem nome" : document.Title, document.Entries?.Count ?? 0, isDefault);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }
        return new(path, isDefault ? PresentationDocument.Empty.Title : "Repertório ilegível", 0, isDefault);
    }
}
