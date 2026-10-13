using System.IO;
using System.Windows;

namespace Teclas.App;

public partial class MainWindow
{
    public bool FavoritesOnly { get=>settings.FavoritesOnly; set { settings.FavoritesOnly=value;Notify();RefreshLibraryFilters(); } }
    public string CollectionFilter { get=>settings.CollectionFilter;set { settings.CollectionFilter=value??"";Notify();RefreshLibraryFilters(); } }
    public IEnumerable<string> LibraryCollections=>new[]{""}.Concat(Files.Select(row=>row.Collection).Where(c=>!string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Order());
    public bool CanShowHistory=>IsIdle && selectedFile is { } row && (row.Versions.Count>0 || row.ScorePath is not null);
    private void RefreshLibraryFilters() { libraryDirty=true;RefreshDashboard();PersistPreferences(); }
    private void OrganizeScoreClick(object sender,RoutedEventArgs e)
    {
        if(!IsIdle || selectedFile is not {} row)return;
        var dialog=new ScoreOrganizationWindow(row){Owner=this};if(dialog.ShowDialog()!=true)return;
        row.IsFavorite=dialog.Favorite;row.Collection=dialog.Collection;row.Genre=dialog.Genre;
        Notify(nameof(LibraryCollections));RefreshLibraryFilters();
    }
    private static void RememberVersion(MidiRow row)
    {
        if(row.ScorePath is not {} path || !File.Exists(path) || row.HasPendingScoreProperties || row.Versions.Any(v=>string.Equals(v.ScorePath,path,StringComparison.OrdinalIgnoreCase)))return;
        row.Versions.Add(new SavedScoreVersion { ScorePath=path,PreviewPath=row.PreviewPath,PageCount=row.PageCount,CreatedAt=row.GeneratedAt??File.GetLastWriteTime(path),
            HasMusicalEdits=row.HasMusicalEdits,
            MelodySourcePriority=SaveMelodyPriority(row.MelodySourcePriority),
            SourceSha256=row.SourceSha256,UseReviewedProfile=row.UseReviewedProfile,AnalysisPath=row.AnalysisPath,Workspace=row.Workspace,
            MelodySources=SaveMelodyPriority(GetOrderedMelodySources(row)),
            Title=row.ScoreTitle,Subtitle=row.ScoreSubtitle,Composer=row.ScoreComposer,DefaultInversion=row.DefaultDiagramInversion,Inversion=row.DiagramInversion,
            Artist=row.ScoreArtist,MetadataOrganizationNotice=row.MetadataOrganizationNotice,
            TransposeSemitones=row.TransposeSemitones,KeyFifths=row.KeyFifths,KeyMinor=row.KeyMinor,
            Chords=row.Chords.Select(c=>new SavedChord { Quarter=c.Quarter,Root=c.Root,Quality=c.Quality,Confidence=c.Confidence,DiagramInversion=c.DiagramInversion,Recommendation=c.Recommendation,Alternatives=c.Alternatives }).ToList() });
    }
    private void ScoreHistoryClick(object sender,RoutedEventArgs e)
    {
        if(!CanShowHistory || selectedFile is not {} row || !CommitEdits())return;
        RememberVersion(row);
        var dialog=new ScoreHistoryWindow(row){Owner=this};
        if(dialog.ShowDialog()!=true || dialog.SelectedVersion is not {} version || !File.Exists(version.ScorePath))return;
        RestoreVersion(row,version);
    }
    private void RestoreVersion(MidiRow row,SavedScoreVersion version)
    {
        if(!File.Exists(version.ScorePath))throw new FileNotFoundException("A cópia desta versão não está disponível.",version.ScorePath);
        ResetPlayback();row.ScorePath=version.ScorePath;row.PreviewPath=version.PreviewPath;row.PageCount=version.PageCount;row.GeneratedAt=version.CreatedAt;
        row.HasMusicalEdits=version.HasMusicalEdits;
        bool sameSource=row.SourceSha256 is not null && string.Equals(row.SourceSha256,version.SourceSha256,StringComparison.OrdinalIgnoreCase);
        loading=true;
        try
        {
            row.UseReviewedProfile=sameSource && version.UseReviewedProfile && row.HasReviewedProfile;
            row.MelodySourcePriority=sameSource || row.SourceSha256 is null || version.HasMusicalEdits
                ? RestoreMelodyPriority(version.MelodySourcePriority) : [];
            foreach(var track in row.Tracks)track.IsMelody=sameSource && version.MelodySources.Any(s=>s.Track==track.Source.Key.Track && s.Channel==track.Source.Key.Channel && s.Program==track.Source.Key.Program);
            row.AnalysisPath=sameSource && File.Exists(version.AnalysisPath)?version.AnalysisPath:null;
            row.Workspace=sameSource?version.Workspace:null;
            RestoreReviewPoints(row);
        }
        finally{loading=false;}
        row.ScoreTitle=version.Title;row.ScoreSubtitle=version.Subtitle;row.ScoreComposer=version.Composer;
        row.ScoreArtist=version.Artist;row.MetadataOrganizationNotice=version.MetadataOrganizationNotice;
        row.DefaultDiagramInversion=version.DefaultInversion;row.DiagramInversion=version.Inversion;row.TransposeSemitones=version.TransposeSemitones;
        row.KeyFifths=version.KeyFifths;row.KeyMinor=version.KeyMinor;row.Chords.Clear();
        foreach(var chord in version.Chords)row.Chords.Add(new ChordRow { Quarter=chord.Quarter,Root=chord.Root,Quality=chord.Quality,Confidence=chord.Confidence,DiagramInversion=chord.DiagramInversion,Recommendation=chord.Recommendation,Alternatives=chord.Alternatives });
        row.HasPendingScoreProperties=false;row.HasOperationError=false;row.Status="Versão restaurada · "+row.PageCountText;
        if(unavailableSessionRows.TryGetValue(row.FullPath,out var remembered))
        {
            remembered.HasMusicalEdits=version.HasMusicalEdits;
            remembered.MelodySourcePriority=SaveMelodyPriority(RestoreMelodyPriority(version.MelodySourcePriority));
            remembered.Sha256=version.SourceSha256;remembered.UseReviewedProfile=version.UseReviewedProfile;
            remembered.MelodySources=version.MelodySources.Select(s=>new SavedSource {Track=s.Track,Channel=s.Channel,Program=s.Program}).ToList();
            remembered.Chords=version.Chords.Select(c=>new SavedChord {Quarter=c.Quarter,Root=c.Root,Quality=c.Quality,Confidence=c.Confidence,DiagramInversion=c.DiagramInversion,Recommendation=c.Recommendation,Alternatives=c.Alternatives}).ToList();
            remembered.KeyFifths=version.KeyFifths;remembered.KeyMinor=version.KeyMinor;
            remembered.AnalysisPath=version.AnalysisPath;remembered.Workspace=version.Workspace;
        }
        RefreshSelection();PersistPreferences();Status="Versão ativa restaurada. Todas as cópias foram preservadas.";
        if(!sameSource && !row.UsesNativeScoreWorkflow)Status="Versão restaurada. A fonte MIDI mudou ou está indisponível; selecione a melodia e analise novamente antes de gerar.";
    }
}
