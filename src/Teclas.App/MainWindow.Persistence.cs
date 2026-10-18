using System.ComponentModel;
using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private bool persistenceInitialized, persistenceEnabled, restoringSession;
    private string? persistencePath;
    private string persistenceError = "";
    private readonly Dictionary<string, SavedMidi> unavailableSessionRows = new(StringComparer.OrdinalIgnoreCase);
    public string PersistenceError { get => persistenceError; private set { persistenceError = value; Notify(); } }

    private void InitializePersistence(bool smoke, string? settingsPath = null)
    {
        // Automated previews may only read/write a session when given an explicit isolated path.
        persistenceEnabled = !smoke || settingsPath is not null;
        persistencePath = settingsPath;
        if (persistenceEnabled) RestoreSession();
        persistenceInitialized = true;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) settings.WindowMaximized = WindowState == WindowState.Maximized;
        };
        Closing += PersistOnClosing;
    }

    private void RestoreSession()
    {
        restoringSession = true;
        loading = true;
        int unavailable = 0, changed = 0;
        try
        {
            RestoreWindowBounds();
            PrefetchLibrary(settings.Library.Select(saved => saved.FullPath).Where(File.Exists));
            foreach (var saved in settings.Library)
            {
                MidiRow row;
                bool preserveFreshNativeHeader=false;
                try
                {
                    row = LoadMidi(saved.FullPath);
                    if (!string.IsNullOrEmpty(saved.Sha256) && string.Equals(row.SourceSha256, saved.Sha256, StringComparison.OrdinalIgnoreCase))
                        ApplySavedMidi(row, saved);
                    else if (saved.HasMusicalEdits)
                    {
                        if (!string.IsNullOrEmpty(saved.Sha256)) changed++;
                        // The edited score is now the musical source, independently of the MIDI.
                        row.ScorePath = saved.ScorePath; row.PreviewPath = saved.PreviewPath;
                        row.PageCount = saved.PageCount; row.GeneratedAt = saved.GeneratedAt;
                        row.KeyFifths = saved.KeyFifths; row.KeyMinor = saved.KeyMinor;
                        foreach (var chord in saved.Chords) row.Chords.Add(new ChordRow
                        { Quarter = chord.Quarter, Root = chord.Root, Quality = chord.Quality, Confidence = chord.Confidence, DiagramInversion = chord.DiagramInversion, Recommendation = chord.Recommendation, Alternatives = chord.Alternatives });
                        row.HasOperationError = saved.HasOperationError;
                        row.Status = "Partitura editada preservada · arquivo de origem alterado ou sem identificação anterior";
                    }
                    else if (!string.IsNullOrEmpty(saved.Sha256))
                    {
                        changed++;
                        if(row.IsNativeScore)
                        {
                            if(File.Exists(saved.ScorePath) && !string.Equals(saved.ScorePath,saved.FullPath,StringComparison.OrdinalIgnoreCase))
                            {
                                row.ScorePath=saved.ScorePath;row.PreviewPath=saved.PreviewPath;row.PageCount=saved.PageCount;row.GeneratedAt=saved.GeneratedAt;
                            }
                            else preserveFreshNativeHeader=true;
                            row.Status="Origem MuseScore alterada · cópias anteriores preservadas no Histórico";
                        }
                        else row.Status = "MIDI alterado desde a última sessão · analise os acordes novamente";
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException or ArgumentException or NotSupportedException)
                {
                    unavailable++;
                    unavailableSessionRows[saved.FullPath] = saved;
                    row = new MidiRow { FullPath = saved.FullPath, Status = "Arquivo indisponível · sua sessão foi preservada: " + ex.Message };
                    // Existing output remains accessible even if the input is on a disconnected drive.
                    row.ScorePath = saved.ScorePath; row.PreviewPath = saved.PreviewPath;
                    row.PageCount = saved.PageCount; row.GeneratedAt = saved.GeneratedAt;
                    row.KeyFifths = saved.KeyFifths; row.KeyMinor = saved.KeyMinor;
                    foreach (var chord in saved.Chords) row.Chords.Add(new ChordRow
                    { Quarter = chord.Quarter, Root = chord.Root, Quality = chord.Quality, Confidence = chord.Confidence, DiagramInversion = chord.DiagramInversion, Recommendation = chord.Recommendation, Alternatives = chord.Alternatives });
                }
                var freshHeader=(row.ScoreTitle,row.ScoreSubtitle,row.ScoreComposer,row.ScoreArtist,row.ArrangementSummary);
                ApplySavedScoreProperties(row, saved);
                if(preserveFreshNativeHeader){row.ScoreTitle=freshHeader.ScoreTitle;row.ScoreSubtitle=freshHeader.ScoreSubtitle;row.ScoreComposer=freshHeader.ScoreComposer;row.ScoreArtist=freshHeader.ScoreArtist;row.ArrangementSummary=freshHeader.ArrangementSummary;row.HasPendingScoreProperties=false;}
                RememberVersion(row);
                Files.Add(row);
            }
            SearchText = settings.SearchText;
            CurrentFilter = settings.CurrentFilter;
            // Restore the working session, but always greet the user on Visão geral.
            // Navigation during the session (including import) still uses SetCurrentPage normally.
            SetCurrentPage("dashboard");
            var selected = Files.FirstOrDefault(row => string.Equals(row.FullPath, settings.SelectedMidiPath, StringComparison.OrdinalIgnoreCase)
                && MatchesLibraryFilter(row));
            if (selected is not null) Library.SelectedItem = selected;
            EditorTabs.SelectedIndex = settings.EditorTab;
            RefreshSelection();
            var notices = new List<string>();
            if (!string.IsNullOrEmpty(settings.LoadWarning)) notices.Add(settings.LoadWarning);
            if (settings.Library.Count > 0) notices.Add($"Sessão restaurada: {settings.Library.Count} MIDIs.");
            if (unavailable > 0) notices.Add($"{unavailable} arquivos indisponíveis continuam lembrados na biblioteca.");
            if (changed > 0) notices.Add($"{changed} MIDIs foram alterados; a análise anterior não foi reaplicada.");
            if (notices.Count > 0) Status = string.Join(" ", notices);
        }
        finally { loading = false; restoringSession = false; prefetchedMidis = null; }
    }

    private void ApplySavedMidi(MidiRow row, SavedMidi saved)
    {
        row.UseReviewedProfile = saved.UseReviewedProfile && row.HasReviewedProfile;
        var sources = saved.MelodySources.Select(source => new SourceKey(source.Track, source.Channel, source.Program)).ToHashSet();
        foreach (var track in row.Tracks) track.IsMelody = sources.Contains(track.Source.Key);
        row.KeyFifths = saved.KeyFifths; row.KeyMinor = saved.KeyMinor;
        foreach (var chord in saved.Chords) row.Chords.Add(new ChordRow
        { Quarter = chord.Quarter, Root = chord.Root, Quality = chord.Quality, Confidence = chord.Confidence, DiagramInversion = chord.DiagramInversion, Recommendation = chord.Recommendation, Alternatives = chord.Alternatives });
        row.Workspace = saved.Workspace;
        row.AnalysisPath = File.Exists(saved.AnalysisPath) && Directory.Exists(saved.Workspace) ? saved.AnalysisPath : null;
        RestoreReviewPoints(row);
        row.ScorePath = saved.ScorePath; row.PreviewPath = saved.PreviewPath;
        row.PageCount = saved.PageCount; row.GeneratedAt = saved.GeneratedAt;
        row.HasOperationError = saved.HasOperationError;
        if (!string.IsNullOrEmpty(saved.Status)) row.Status = saved.Status;
        if (!saved.HasMusicalEdits && saved.AnalysisPath is not null && row.AnalysisPath is null)
            row.Status = "Cifras restauradas · análise temporária indisponível; analise novamente antes de gerar";
    }

    private static void ApplySavedScoreProperties(MidiRow row, SavedMidi saved)
    {
        row.HasMusicalEdits = saved.HasMusicalEdits;
        row.ArrangementSummary = saved.ArrangementSummary;
        // Preserve disconnected origins, but never apply a stale ordering to changed MIDI content.
        row.MelodySourcePriority = row.SourceSha256 is null || saved.HasMusicalEdits ||
            string.Equals(row.SourceSha256, saved.Sha256, StringComparison.OrdinalIgnoreCase)
            ? RestoreMelodyPriority(saved.MelodySourcePriority) : [];
        row.MelodySections = row.SourceSha256 is null || saved.HasMusicalEdits ||
            string.Equals(row.SourceSha256, saved.Sha256, StringComparison.OrdinalIgnoreCase)
            ? Settings.CloneMelodySections(saved.MelodySections) : [];
        row.IsFavorite = saved.IsFavorite; row.Collection = saved.Collection; row.Genre = saved.Genre;
        row.TransposeSemitones = saved.TransposeSemitones; row.SimplifiedChords = saved.SimplifiedChords; row.FoldRepeats = saved.FoldRepeats; row.Versions = saved.Versions;
        row.ScoreTitle = saved.ScoreTitle ?? ScoreMetadataDefaults.Title(row.FullPath);
        row.ScoreSubtitle = saved.ScoreSubtitle;
        row.ScoreComposer = saved.ScoreComposer ?? "";
        row.ScoreArtist = saved.ScoreArtist ?? "";
        row.MetadataOrganizationNotice = saved.MetadataOrganizationNotice;
        row.DefaultDiagramInversion = saved.DefaultDiagramInversion;
        row.DiagramInversion = saved.DiagramInversion;
        row.HasPendingScoreProperties = saved.HasPendingScoreProperties;
    }

    private void RestoreWindowBounds()
    {
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (settings.WindowWidth is { } width) Width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, desktop.Width));
        if (settings.WindowHeight is { } height) Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, desktop.Height));
        if (settings.WindowLeft is { } left && settings.WindowTop is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Clamp(left, desktop.Left, Math.Max(desktop.Left, desktop.Right - Width));
            Top = Math.Clamp(top, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - Height));
        }
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void CaptureSession()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) && bounds.Width > 0 && bounds.Height > 0)
        {
            settings.WindowLeft = bounds.Left; settings.WindowTop = bounds.Top;
            settings.WindowWidth = bounds.Width; settings.WindowHeight = bounds.Height;
        }
        if (WindowState != WindowState.Minimized) settings.WindowMaximized = WindowState == WindowState.Maximized;
        settings.CurrentPage = CurrentPage; settings.CurrentFilter = CurrentFilter; settings.SearchText = SearchText;
        settings.SelectedMidiPath = selectedFile?.FullPath;
        settings.EditorTab = Math.Max(0, EditorTabs.SelectedIndex);
        settings.Library = Files.Select(CaptureMidi).ToList();
    }

    private SavedMidi CaptureMidi(MidiRow row)
    {
        if (row.Document is null && unavailableSessionRows.TryGetValue(row.FullPath, out var remembered))
        {
            remembered.HasMusicalEdits = row.HasMusicalEdits;
            remembered.ArrangementSummary = row.ArrangementSummary;
            remembered.MelodySourcePriority = SaveMelodyPriority(row.MelodySourcePriority);
            remembered.MelodySections = Settings.CloneMelodySections(row.MelodySections);
            remembered.ScoreTitle = row.ScoreTitle; remembered.ScoreSubtitle = row.ScoreSubtitle;
            remembered.ScoreComposer = row.ScoreComposer; remembered.DefaultDiagramInversion = row.DefaultDiagramInversion;
            remembered.ScoreArtist = row.ScoreArtist; remembered.MetadataOrganizationNotice = row.MetadataOrganizationNotice;
            remembered.DiagramInversion = row.DiagramInversion; remembered.HasPendingScoreProperties = row.HasPendingScoreProperties;
            remembered.IsFavorite = row.IsFavorite; remembered.Collection = row.Collection; remembered.Genre = row.Genre;
            remembered.TransposeSemitones = row.TransposeSemitones; remembered.SimplifiedChords = row.SimplifiedChords; remembered.FoldRepeats = row.FoldRepeats; remembered.Versions = row.Versions;
            remembered.ScorePath = row.ScorePath; remembered.PreviewPath = row.PreviewPath;
            remembered.PageCount = row.PageCount; remembered.GeneratedAt = row.GeneratedAt;
            remembered.Status = row.Status; remembered.HasOperationError = row.HasOperationError;
            remembered.KeyFifths = row.KeyFifths; remembered.KeyMinor = row.KeyMinor;
            remembered.Chords = row.Chords.Select(chord => new SavedChord
                { Quarter = chord.Quarter, Root = chord.Root, Quality = chord.Quality, Confidence = chord.Confidence, DiagramInversion = chord.DiagramInversion, Recommendation = chord.Recommendation, Alternatives = chord.Alternatives }).ToList();
            return remembered;
        }
        return new SavedMidi
        {
            HasMusicalEdits = row.HasMusicalEdits, ArrangementSummary = row.ArrangementSummary,
            MelodySourcePriority = SaveMelodyPriority(row.MelodySourcePriority),
            MelodySections = Settings.CloneMelodySections(row.MelodySections),
            FullPath = row.FullPath, Sha256 = row.SourceSha256, UseReviewedProfile = row.UseReviewedProfile,
            IsFavorite = row.IsFavorite, Collection = row.Collection, Genre = row.Genre,
            TransposeSemitones = row.TransposeSemitones, SimplifiedChords = row.SimplifiedChords, FoldRepeats = row.FoldRepeats, Versions = row.Versions,
            ScoreTitle = row.ScoreTitle, ScoreSubtitle = row.ScoreSubtitle, ScoreComposer = row.ScoreComposer,
            ScoreArtist = row.ScoreArtist, MetadataOrganizationNotice = row.MetadataOrganizationNotice,
            DefaultDiagramInversion = row.DefaultDiagramInversion, DiagramInversion = row.DiagramInversion,
            HasPendingScoreProperties = row.HasPendingScoreProperties,
            MelodySources = SaveMelodyPriority(GetOrderedMelodySources(row)),
            Chords = row.Chords.Select(chord => new SavedChord
                { Quarter = chord.Quarter, Root = chord.Root, Quality = chord.Quality, Confidence = chord.Confidence, DiagramInversion = chord.DiagramInversion, Recommendation = chord.Recommendation, Alternatives = chord.Alternatives }).ToList(),
            KeyFifths = row.KeyFifths, KeyMinor = row.KeyMinor, AnalysisPath = row.AnalysisPath, Workspace = row.Workspace,
            ScorePath = row.ScorePath, PreviewPath = row.PreviewPath, PageCount = row.PageCount, GeneratedAt = row.GeneratedAt,
            HasOperationError = row.HasOperationError, Status = row.Status
        };
    }

    // Theme and sidebar changes can use this hook; closing always takes a fresh complete snapshot.
    private void PersistPreferences()
    {
        if (persistenceInitialized && persistenceEnabled && !restoringSession && !busy) SaveCurrentSession();
    }

    private bool SaveCurrentSession()
    {
        if (!persistenceEnabled) return true;
        if (!CommitEdits())
        {
            PersistenceError = "Corrija os campos destacados para salvar suas configurações e fechar o aplicativo.";
            Status = PersistenceError;
            return false;
        }
        try
        {
            CaptureSession();
            settings.Save(persistencePath);
            PersistenceError = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            PersistenceError = "Não foi possível salvar suas configurações. O aplicativo permanecerá aberto: " + ex.Message;
            Status = PersistenceError;
            return false;
        }
    }

    private void PersistOnClosing(object? sender, CancelEventArgs e)
    {
        if (!persistenceEnabled) return;
        if (busy)
        {
            RequestOperationCancellation(); e.Cancel = true;
            Status = "Cancelando. Aguarde o término antes de fechar e salvar sua sessão.";
            return;
        }
        if (!SaveCurrentSession()) { e.Cancel = true; return; }
        DeferCloseForSync(e);
    }

    public bool SaveSessionForSmoke() => SaveCurrentSession();
}
