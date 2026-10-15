using System.Security.Cryptography;
using System.Text.Json;

namespace Teclas.Core;

public sealed record PresentationEntry(Guid Id, string Title, string SourcePath, string ScorePath,
    string? PreviewPath = null, string Notes = "", int LastPage = 1);
public sealed record PresentationDocument(int FormatVersion, string Title, Guid? SelectedId, IReadOnlyList<PresentationEntry> Entries)
{
    public static PresentationDocument Empty => new(1, "Meu repertório", null, []);
}
public sealed record PresentationLoad(PresentationDocument Document, string? Warning);

/// <summary>Owns only the small presentation JSON; score files are never written or removed.</summary>
public sealed class PresentationStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string? expectedHash;
    private bool loaded;
    private bool recovered;
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public PresentationLoad Load()
    {
        loaded = false; recovered = false;
        if (!File.Exists(Path)) { expectedHash = null; loaded = true; return new(PresentationDocument.Empty, null); }
        byte[] bytes = ReadLimited(Path); expectedHash = Hash(bytes);
        try { var document = Parse(bytes); loaded = true; return new(document, null); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            try
            {
                var document = Parse(ReadLimited(Path + ".bak")); loaded = recovered = true;
                return new(document, "O repertório foi recuperado da última cópia válida. Confira as últimas anotações.");
            }
            catch (Exception backupError) when (backupError is IOException or JsonException or InvalidDataException or ArgumentException)
            { throw new InvalidDataException("Não foi possível ler o repertório nem sua cópia anterior. Os arquivos foram preservados; não serão substituídos por uma lista vazia.", error); }
        }
    }

    public void Save(PresentationDocument document)
    {
        if (!loaded) throw new InvalidOperationException("Abra o repertório antes de salvá-lo.");
        Validate(document);
        string? currentHash = File.Exists(Path) ? Hash(ReadLimited(Path)) : null;
        if (currentHash != expectedHash) throw new IOException("O repertório foi alterado em outra janela. Feche e abra novamente antes de continuar; suas alterações ainda estão nesta janela.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("O repertório excedeu o tamanho permitido. Reduza as anotações ou a quantidade de músicas.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            // Check again immediately before publication, including after a slow disk write.
            currentHash = File.Exists(Path) ? Hash(ReadLimited(Path)) : null;
            if (currentHash != expectedHash) throw new IOException("Outra janela alterou o repertório durante o salvamento. A cópia existente foi preservada.");
            if (currentHash is null) File.Move(temporary, Path);
            else if (recovered)
            {
                File.Copy(Path, Path + ".recovered-" + Guid.NewGuid().ToString("N"), false);
                File.Move(temporary, Path, true);
            }
            else File.Replace(temporary, Path, Path + ".bak", true);
            expectedHash = Hash(bytes); recovered = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] ReadLimited(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("O arquivo de repertório é maior que o permitido.");
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static PresentationDocument Parse(byte[] bytes)
    {
        var document = JsonSerializer.Deserialize<PresentationDocument>(bytes, JsonOptions) ?? throw new InvalidDataException("Repertório vazio.");
        if (document.FormatVersion != 1) throw new NotSupportedException("Este repertório foi salvo em um formato diferente. Atualize o TeclaScore antes de editá-lo; o arquivo foi preservado.");
        Validate(document); return document;
    }
    private static void Validate(PresentationDocument document)
    {
        if (document.FormatVersion != 1) throw new InvalidDataException("Este formato de repertório não é compatível com esta versão.");
        if (document.Title is null || document.Title.Length > 240 || document.Entries is null || document.Entries.Count > 1000)
            throw new InvalidDataException("Título ou quantidade de músicas inválidos.");
        var ids = new HashSet<Guid>();
        foreach (var entry in document.Entries)
        {
            if (entry is null || entry.Id == Guid.Empty || !ids.Add(entry.Id) || entry.Title is null || entry.Title.Length > 240 ||
                entry.Notes is null || entry.Notes.Length > 16000 || entry.LastPage is < 1 or > 100000)
                throw new InvalidDataException("O repertório contém uma música ou anotação inválida.");
            if (!ValidPath(entry.ScorePath) || !System.IO.Path.GetExtension(entry.ScorePath).Equals(".mscz", StringComparison.OrdinalIgnoreCase) ||
                !ValidPath(entry.SourcePath) || (entry.PreviewPath is not null && (!ValidPath(entry.PreviewPath) || !System.IO.Path.GetExtension(entry.PreviewPath).Equals(".png", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("O repertório contém um caminho de partitura ou prévia inválido.");
        }
        if (document.SelectedId is { } selected && !ids.Contains(selected)) throw new InvalidDataException("A música selecionada não pertence ao repertório.");
    }
    private static bool ValidPath(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 32767 &&
        System.IO.Path.IsPathFullyQualified(value) && !value.Any(char.IsControl) && value.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0;
}
