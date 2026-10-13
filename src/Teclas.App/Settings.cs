using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teclas.App;

public sealed class Settings
{
    public int SchemaVersion { get; set; } = 2;
    public string PythonPath { get; set; } = "";
    public string MuseScorePath { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string ThemeId { get; set; } = "classic";
    public bool IsSidebarCollapsed { get; set; }
    public double TrackAuditionSpeed { get; set; } = 1.0;
    public double ScorePlaybackSpeed { get; set; } = 1.0;
    public bool FavoritesOnly { get; set; }
    public string CollectionFilter { get; set; } = "";
    public int DefaultDiagramInversion { get; set; } = 2;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public string CurrentPage { get; set; } = "dashboard";
    public string CurrentFilter { get; set; } = "all";
    public string SearchText { get; set; } = "";
    public string? SelectedMidiPath { get; set; }
    public int EditorTab { get; set; }
    public List<SavedMidi> Library { get; set; } = [];
    [JsonIgnore] public string LoadWarning { get; private set; } = "";
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeclasPartituras");
    public static string DefaultPath => Path.Combine(DataDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load(string? filePath = null)
    {
        var path = filePath ?? DefaultPath;
        Settings? settings = null;
        var warning = "";
        if (File.Exists(path))
        {
            settings = ReadValid(path);
            if (settings is null) warning = "O arquivo de configurações não pôde ser lido.";
        }
        if (settings is null && File.Exists(path + ".bak"))
        {
            settings = ReadValid(path + ".bak");
            if (settings is not null) warning = "As configurações foram recuperadas da cópia de segurança.";
            else if (string.IsNullOrEmpty(warning)) warning = "A cópia de segurança das configurações não pôde ser lida.";
        }
        settings ??= new();
        settings.LoadWarning = warning;
        settings.Normalize();
        settings.FindDefaultPaths();
        return settings;
    }

    private static Settings? ReadValid(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            return JsonSerializer.Deserialize<Settings>(input, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { return null; }
    }

    private void Normalize()
    {
        TrackAuditionSpeed = NormalizeTrackAuditionSpeed(TrackAuditionSpeed);
        ScorePlaybackSpeed = NormalizeTrackAuditionSpeed(ScorePlaybackSpeed);
        CollectionFilter ??= "";
        DefaultDiagramInversion = NormalizeDiagramInversion(DefaultDiagramInversion);
        ThemeId = ThemeId is "classic" or "ocean" or "indigo" or "graphite" or "light" ? ThemeId : "classic";
        CurrentPage = CurrentPage is "library" or "production" or "settings" or "about" ? CurrentPage : "dashboard";
        CurrentFilter = CurrentFilter is "ready" or "review" or "generated" or "error" ? CurrentFilter : "all";
        SearchText ??= "";
        PythonPath ??= ""; MuseScorePath ??= ""; OutputDirectory ??= "";
        EditorTab = Math.Clamp(EditorTab, 0, 2);
        WindowLeft = Finite(WindowLeft); WindowTop = Finite(WindowTop);
        WindowWidth = Positive(WindowWidth); WindowHeight = Positive(WindowHeight);
        Library ??= [];
        Library = Library.Where(row => row is not null && !string.IsNullOrWhiteSpace(row.FullPath))
            .GroupBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        foreach (var row in Library)
        {
            row.Collection ??= ""; row.Genre ??= ""; row.Versions ??= [];
            row.TransposeSemitones = Math.Clamp(row.TransposeSemitones,-12,12);
            row.Versions = row.Versions.Where(version => version is not null && !string.IsNullOrWhiteSpace(version.ScorePath)).ToList();
            foreach(var version in row.Versions)
            {
                version.MelodySources ??= []; version.Chords ??= [];
                version.MelodySources=version.MelodySources.Where(source=>source is not null).ToList();
                version.Chords=version.Chords.Where(chord=>chord is not null && double.IsFinite(chord.Quarter) && double.IsFinite(chord.Confidence)).ToList();
                version.Title ??= "";version.Composer ??= "";version.Artist ??= "";version.MetadataOrganizationNotice ??= "";
                version.DefaultInversion=NormalizeDiagramInversion(version.DefaultInversion);
                if(version.Inversion is <0 or >2)version.Inversion=null;
                version.TransposeSemitones=Math.Clamp(version.TransposeSemitones,-12,12);
            }
            row.DefaultDiagramInversion = NormalizeDiagramInversion(row.DefaultDiagramInversion);
            row.MetadataOrganizationNotice ??= "";
            if (row.DiagramInversion is < 0 or > 2) row.DiagramInversion = null;
            row.MelodySources ??= []; row.Chords ??= [];
            row.MelodySources = row.MelodySources.Where(source => source is not null).ToList();
            row.Chords = row.Chords.Where(chord => chord is not null && double.IsFinite(chord.Quarter)
                && double.IsFinite(chord.Confidence)).ToList();
            foreach (var chord in row.Chords) { chord.Root ??= "C"; chord.Quality ??= ""; if(chord.DiagramInversion is <0 or >2)chord.DiagramInversion=null; }
            foreach(var chord in row.Versions.SelectMany(version=>version.Chords))
                if(chord.DiagramInversion is <0 or >2)chord.DiagramInversion=null;
        }
    }

    public static double NormalizeTrackAuditionSpeed(double value) => value is 0.25 or 0.5 or 0.75 or 1.0 or 1.25 or 1.5 ? value : 1.0;
    public static int NormalizeDiagramInversion(int value) => value is >= 0 and <= 2 ? value : 2;
    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
    private static double? Positive(double? value) => Finite(value) is > 0 ? value : null;

    private void FindDefaultPaths()
    {
        // A disconnected drive or temporarily missing tool must not erase a user's choice.
        if (string.IsNullOrWhiteSpace(PythonPath))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "runtime", "python.exe"),
                Path.Combine(home, @".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe") };
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
            try { if (Directory.Exists(local)) candidates.AddRange(Directory.GetDirectories(local).Select(directory => Path.Combine(directory, "python.exe"))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            PythonPath = candidates.FirstOrDefault(File.Exists) ?? "";
        }
        if (string.IsNullOrWhiteSpace(MuseScorePath)) MuseScorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"MuseScore 4\bin\MuseScore4.exe");
        if (string.IsNullOrWhiteSpace(OutputDirectory)) OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Partituras geradas");
    }

    public void Save(string? filePath = null)
    {
        var path = Path.GetFullPath(filePath ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            SchemaVersion = 2;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, this, JsonOptions);
                output.Flush(true);
            }
            if (File.Exists(path) && ReadValid(path) is not null)
                File.Replace(temporary, path, path + ".bak");
            else
            {
                // Keep the damaged original for recovery; never replace the usable backup with it.
                if (File.Exists(path)) File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"));
                File.Move(temporary, path, true);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class SavedMidi
{
    public bool IsFavorite { get; set; }
    public string Collection { get; set; } = "";
    public string Genre { get; set; } = "";
    public int TransposeSemitones { get; set; }
    public List<SavedScoreVersion> Versions { get; set; } = [];
    public string? ScoreTitle { get; set; }
    public string? ScoreSubtitle { get; set; }
    public string? ScoreComposer { get; set; }
    public string? ScoreArtist { get; set; }
    public string MetadataOrganizationNotice { get; set; } = "";
    public int DefaultDiagramInversion { get; set; } = 2;
    public int? DiagramInversion { get; set; }
    public bool HasPendingScoreProperties { get; set; }
    public string FullPath { get; set; } = "";
    public string? Sha256 { get; set; }
    public bool UseReviewedProfile { get; set; }
    public List<SavedSource> MelodySources { get; set; } = [];
    public List<SavedChord> Chords { get; set; } = [];
    public int KeyFifths { get; set; }
    public bool KeyMinor { get; set; }
    public string? AnalysisPath { get; set; }
    public string? Workspace { get; set; }
    public string? ScorePath { get; set; }
    public string? PreviewPath { get; set; }
    public int PageCount { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    public bool HasOperationError { get; set; }
    public string Status { get; set; } = "";
}

public sealed class SavedScoreVersion
{
    public string? SourceSha256 { get; set; }
    public bool UseReviewedProfile { get; set; }
    public List<SavedSource> MelodySources { get; set; } = [];
    public string? AnalysisPath { get; set; }
    public string? Workspace { get; set; }
    public string ScorePath { get; set; } = "";
    public string? PreviewPath { get; set; }
    public int PageCount { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string Composer { get; set; } = "";
    public string Artist { get; set; } = "";
    public string MetadataOrganizationNotice { get; set; } = "";
    public int DefaultInversion { get; set; } = 2;
    public int? Inversion { get; set; }
    public int TransposeSemitones { get; set; }
    public int KeyFifths { get; set; }
    public bool KeyMinor { get; set; }
    public List<SavedChord> Chords { get; set; } = [];
    [JsonIgnore] public string Label => $"{CreatedAt?.ToLocalTime():dd/MM/yyyy HH:mm} · {Title} · {PageCount} pág.";
    [JsonIgnore] public bool IsAvailable => File.Exists(ScorePath);
}

public sealed class SavedSource
{
    public int Track { get; set; }
    public int Channel { get; set; }
    public int Program { get; set; }
}

public sealed class SavedChord
{
    public int? DiagramInversion { get; set; }
    public double Quarter { get; set; }
    public string Root { get; set; } = "C";
    public string Quality { get; set; } = "";
    public double Confidence { get; set; }
}
