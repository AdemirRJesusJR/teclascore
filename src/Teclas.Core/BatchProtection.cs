using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teclas.Core;

/// <summary>One song in the preview of a batch: which group it falls in and why; only some groups can be unticked.</summary>
public sealed record BatchPreviewItem(string Id, string Title, string Group, string Detail, bool Selectable)
{
    public string Line => Detail.Length == 0 ? Title : Title + " — " + Detail;
}

/// <summary>
/// "O que vai acontecer": the songs of a batch operation grouped by what will happen to them, with the count of each
/// group and the time the generation should take. Approved scores are never touched by a batch: they are listed as
/// "pulada · aprovada" so the user sees them and knows why.
/// </summary>
public sealed record BatchPreview(string Operation, IReadOnlyList<BatchPreviewItem> Items, double? EstimatedSeconds, int EstimateSamples)
{
    public const string Generate = "Vai gerar", Regenerate = "Vai refazer", Redraw = "Vai redesenhar", Remove = "Vai remover",
        SkipApproved = "Pula · aprovada", NeedsYou = "Precisam de você", AlreadyGenerated = "Já geradas", Skip = "Fica de fora";
    /// <summary>Display order of the groups.</summary>
    public static readonly IReadOnlyList<string> Groups = [Generate, Regenerate, Redraw, Remove, SkipApproved, NeedsYou, AlreadyGenerated, Skip];
    public static readonly IReadOnlyList<string> ActiveGroups = [Generate, Regenerate, Redraw, Remove];

    public int Count(string group) => Items.Count(item => item.Group == group);
    public int ActiveCount => Items.Count(item => ActiveGroups.Contains(item.Group));
    public IReadOnlyList<BatchPreviewItem> Of(string group) => Items.Where(item => item.Group == group).ToArray();
    public bool HasWork => ActiveCount > 0;

    /// <summary>"Vai gerar 120 · pula 12 aprovadas · 5 precisam de você · 31 já geradas".</summary>
    public string Headline()
    {
        var parts = new List<string>();
        foreach (string group in Groups)
        {
            int count = Count(group);
            if (count == 0) continue;
            parts.Add(group switch
            {
                SkipApproved => $"pula {count} aprovada{(count == 1 ? "" : "s")}",
                NeedsYou => $"{count} precisa{(count == 1 ? "" : "m")} de você",
                AlreadyGenerated => $"{count} já gerada{(count == 1 ? "" : "s")}",
                Skip => $"{count} fica{(count == 1 ? "" : "m")} de fora",
                _ => $"{group} {count}"
            });
        }
        return parts.Count == 0 ? "Nada a fazer." : string.Join(" · ", parts);
    }

    public string EstimateText() => EstimatedSeconds is { } seconds && ActiveCount > 0
        ? $"Tempo estimado: {BatchEstimate.Format(seconds)} (mediana de {EstimateSamples} {(EstimateSamples == 1 ? "geração anterior" : "gerações anteriores")})"
        : ActiveCount > 0 && EstimateSamples == 0 ? "Tempo estimado: sem medições anteriores para calcular." : "";

    /// <summary>The preview as plain text (the command line's --simular prints this).</summary>
    public string ToText()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"O que vai acontecer · {Operation} · {Items.Count} música{(Items.Count == 1 ? "" : "s")}");
        text.AppendLine(Headline());
        if (EstimateText() is { Length: > 0 } estimate) text.AppendLine(estimate);
        foreach (string group in Groups)
        {
            var items = Of(group);
            if (items.Count == 0) continue;
            text.AppendLine(); text.AppendLine($"{group} ({items.Count})");
            foreach (var item in items) text.AppendLine("  • " + item.Line);
        }
        return text.ToString().TrimEnd();
    }
}

/// <summary>How long a batch should take, from the durations the app already records about generations.</summary>
public static class BatchEstimate
{
    /// <summary>Median seconds of one generation: the engine's own generate runs, or the app's single-song generations.</summary>
    public static (double? Median, int Samples) MedianGenerationSeconds(IEnumerable<PerformanceRecord> records)
    {
        var list = records.Where(r => r.Outcome == PerformanceOutcome.Success && r.DurationMilliseconds > 0).ToList();
        var engine = list.Where(r => r.Operation == PerformanceOperation.EngineGenerate).Select(r => r.DurationMilliseconds / 1000).ToList();
        var samples = engine.Count > 0 ? engine : list.Where(r => r.Operation == PerformanceOperation.AppGenerate).Select(r => r.DurationMilliseconds / 1000).ToList();
        if (samples.Count == 0) return (null, 0);
        samples.Sort();
        double median = samples.Count % 2 == 1 ? samples[samples.Count / 2] : (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]) / 2;
        return (median, samples.Count);
    }

    /// <summary>Wall-clock seconds for <paramref name="count"/> songs with <paramref name="parallel"/> at a time.</summary>
    public static double? Seconds(double? medianSeconds, int count, int parallel)
    {
        if (medianSeconds is not { } median || count <= 0) return null;
        return median * Math.Ceiling(count / (double)Math.Max(1, parallel));
    }

    public static string Format(double seconds)
    {
        if (seconds < 60) return "menos de 1 min";
        if (seconds < 3600) return $"cerca de {Math.Round(seconds / 60, MidpointRounding.AwayFromZero)} min";
        double hours = Math.Floor(seconds / 3600); int minutes = (int)Math.Round(seconds % 3600 / 60, MidpointRounding.AwayFromZero);
        return minutes == 0 ? $"cerca de {hours:0} h" : $"cerca de {hours:0} h {minutes} min";
    }
}

/// <summary>One song of a finished batch, for the batch log.</summary>
public sealed record BatchLogItem(string Titulo, string Arquivo, string Situacao, string? Motivo = null, string? Partitura = null);

/// <summary>
/// What a batch did, saved in <c>%LOCALAPPDATA%\TeclasPartituras\lotes\&lt;data&gt;.json</c> so a later report can read
/// it. <c>NadaApagado</c> is true when no file and no card left the library; otherwise <c>Mudancas</c> lists what changed.
/// </summary>
public sealed record BatchLogEntry(string Versao, string Operacao, DateTimeOffset Inicio, DateTimeOffset Fim, int Total,
    IReadOnlyDictionary<string, int> Resumo, IReadOnlyList<BatchLogItem> Itens, IReadOnlyList<string> Mudancas, bool NadaApagado,
    string? PontoDeRestauracao, bool Cancelado = false)
{
    public static BatchLogEntry Create(string version, string operation, DateTimeOffset start, IReadOnlyList<BatchLogItem> items,
        IReadOnlyList<string> changes, string? restorePoint, bool cancelled = false) =>
        new(version, operation, start, DateTimeOffset.Now, items.Count,
            items.GroupBy(i => i.Situacao).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            items, changes, changes.Count == 0, restorePoint, cancelled);

    /// <summary>The summary line of the result window: "Nada foi apagado." or the list of what changed.</summary>
    public string ChangesText() => NadaApagado ? "Nada foi apagado." : "O que mudou: " + string.Join("; ", Mudancas) + ".";
}

/// <summary>Writes one JSON file per batch and reads them back, newest first; the folder is bounded to the newest 200.</summary>
public sealed class BatchLogStore(string directory)
{
    public const int Kept = 200;
    public string Directory { get; } = Path.GetFullPath(directory);
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeclasPartituras", "lotes");
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never, PropertyNameCaseInsensitive = true
    };

    /// <summary>Saves the entry as <c>yyyy-MM-dd-HHmmss.json</c> by the clock of the batch itself (its own offset, so a reader in another time zone, like the CI runner, sees the same name; a suffix keeps two batches in the same second apart) and returns the path.</summary>
    public string Write(BatchLogEntry entry)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string stem = entry.Inicio.DateTime.ToString("yyyy-MM-dd-HHmmss"), path = Path.Combine(Directory, stem + ".json");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(Directory, $"{stem}-{n}.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entry, Json), new System.Text.UTF8Encoding(false)); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        foreach (var old in System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderByDescending(Path.GetFileNameWithoutExtension, StringComparer.Ordinal).Skip(Kept))
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return path;
    }

    /// <summary>The newest entries (unreadable files are skipped, never thrown).</summary>
    public IReadOnlyList<BatchLogEntry> ReadRecent(int count = 20)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var entries = new List<BatchLogEntry>();
        // Without the extension, "…-140200-2" sorts after "…-140200": the later batch of the same second comes first.
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderByDescending(Path.GetFileNameWithoutExtension, StringComparer.Ordinal).Take(count))
        {
            try { if (JsonSerializer.Deserialize<BatchLogEntry>(File.ReadAllText(path), Json) is { } entry) entries.Add(entry); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return entries;
    }
}

/// <summary>A library snapshot taken before a batch; the package is a .teclasbackup kept locally (not portable).</summary>
public sealed record RestorePointInfo(string Id, string Operation, DateTimeOffset CreatedAt, int SongCount, int CopiedFiles, string Package)
{
    /// <summary>"Antes de: Piloto automático · 09/11 14:02 · 168 músicas".</summary>
    [JsonIgnore] public string Label => $"Antes de: {Operation} · {CreatedAt.LocalDateTime:dd/MM HH:mm} · {SongCount} música{(SongCount == 1 ? "" : "s")}";
    [JsonIgnore] public bool IsAvailable => File.Exists(Package);
}

/// <summary>
/// Automatic restore points: one before every batch operation, the newest <see cref="Kept"/> kept. The index lists
/// them for the Histórico; a package is created by the app (library JSON plus the affected scores) through
/// <see cref="LibraryBackupService"/> and extracted again on "Voltar a este ponto".
/// </summary>
public sealed class RestorePointStore(string directory)
{
    public const int Kept = 5;
    public string Directory { get; } = Path.GetFullPath(directory);
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeclasPartituras", "pontos-de-restauracao");
    private string IndexPath => Path.Combine(Directory, "index.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string NewId(DateTimeOffset? now = null) => (now ?? DateTimeOffset.Now).LocalDateTime.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    public string PackagePath(string id) => Path.Combine(Directory, Safe(id) + ".teclasbackup");
    /// <summary>Where the package's files are extracted when the user returns to the point.</summary>
    public string RestoreDirectory(string id) => Path.Combine(Directory, Safe(id) + "-restaurado");

    /// <summary>Newest first; entries whose package disappeared are left out.</summary>
    public IReadOnlyList<RestorePointInfo> List()
    {
        var all = ReadIndex();
        return all.Where(p => p.IsAvailable).OrderByDescending(p => p.CreatedAt).ToArray();
    }

    /// <summary>Adds the point and deletes the packages (and extracted folders) beyond the newest <see cref="Kept"/>.</summary>
    public IReadOnlyList<RestorePointInfo> Register(RestorePointInfo point)
    {
        if (!Path.GetFullPath(point.Package).StartsWith(Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("O ponto de restauração precisa ficar na pasta dos pontos.", nameof(point));
        var all = ReadIndex().Where(p => p.Id != point.Id).Append(point).OrderByDescending(p => p.CreatedAt).ToList();
        var removed = all.Skip(Kept).ToList();
        foreach (var old in removed) Delete(old);
        all = all.Take(Kept).ToList();
        WriteIndex(all);
        return removed;
    }

    public void Remove(string id)
    {
        var all = ReadIndex();
        foreach (var old in all.Where(p => p.Id == id)) Delete(old);
        WriteIndex(all.Where(p => p.Id != id).ToList());
    }

    private void Delete(RestorePointInfo point)
    {
        try { if (File.Exists(point.Package)) File.Delete(point.Package); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { string extracted = RestoreDirectory(point.Id); if (System.IO.Directory.Exists(extracted)) System.IO.Directory.Delete(extracted, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private List<RestorePointInfo> ReadIndex()
    {
        if (!File.Exists(IndexPath)) return [];
        try { return JsonSerializer.Deserialize<List<RestorePointInfo>>(File.ReadAllText(IndexPath), Json)?.Where(p => p is { Id.Length: > 0, Package.Length: > 0 }).ToList() ?? []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private void WriteIndex(List<RestorePointInfo> points)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string temporary = IndexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(points, Json), new System.Text.UTF8Encoding(false)); File.Move(temporary, IndexPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Safe(string id)
    {
        if (id.Length == 0 || id.Any(c => !char.IsLetterOrDigit(c) && c != '-')) throw new ArgumentException("Identificador de ponto inválido.", nameof(id));
        return id;
    }
}

/// <summary>A file about to be imported and a card already in the library, compared by content and by title + artist.</summary>
public sealed record ImportCandidate(string Path, string? Sha256, string Title, string Artist);
public sealed record ImportLibraryEntry(string Id, string Path, string? Sha256, string Title, string Artist, bool HasScore);
public sealed record ImportDuplicate(ImportCandidate Candidate, ImportLibraryEntry Existing, string Reason);
public enum ImportDuplicateDecision { KeepBoth, Skip, Replace }

public static class ImportDuplicateDetector
{
    public const string SameContent = "mesmo conteúdo", SameTitleArtist = "mesmo título e intérprete";

    /// <summary>One conflict per candidate (content first, then title + artist); candidates already in the library by path are not conflicts.</summary>
    public static IReadOnlyList<ImportDuplicate> Find(IReadOnlyList<ImportCandidate> candidates, IReadOnlyList<ImportLibraryEntry> library)
    {
        var byHash = library.Where(e => !string.IsNullOrEmpty(e.Sha256)).GroupBy(e => e.Sha256!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.HasScore).First(), StringComparer.OrdinalIgnoreCase);
        var byName = library.GroupBy(e => Key(e.Title, e.Artist)).Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.HasScore).First(), StringComparer.Ordinal);
        var paths = library.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<ImportDuplicate>();
        foreach (var candidate in candidates)
        {
            if (paths.Contains(candidate.Path)) continue;
            if (!string.IsNullOrEmpty(candidate.Sha256) && byHash.TryGetValue(candidate.Sha256, out var same)) { result.Add(new(candidate, same, SameContent)); continue; }
            string key = Key(candidate.Title, candidate.Artist);
            if (key.Length > 0 && byName.TryGetValue(key, out var named)) result.Add(new(candidate, named, SameTitleArtist));
        }
        return result;
    }

    /// <summary>Normalized "title|artist"; a song without a title never matches by name.</summary>
    public static string Key(string title, string artist)
    {
        string t = DuplicateFinder.Normalize(title);
        return t.Length == 0 ? "" : t + "|" + DuplicateFinder.Normalize(artist);
    }
}
