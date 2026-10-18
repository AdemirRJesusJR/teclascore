using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Teclas.Core;
namespace Teclas.App;
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
    protected bool Set<T>(ref T field,T value,[CallerMemberName]string? name=null){if(EqualityComparer<T>.Default.Equals(field,value))return false;field=value;Notify(name);return true;}
}
public sealed partial class MidiRow : Observable
{
    public required string FullPath{get;init;}
    public bool IsNativeScore=>Path.GetExtension(FullPath).Equals(".mscz",StringComparison.OrdinalIgnoreCase);
    private string arrangementSummary="";
    public string ArrangementSummary{get=>arrangementSummary;set=>Set(ref arrangementSummary,Settings.NormalizeArrangementSummary(value));}
    private bool hasMusicalEdits;
    public bool HasMusicalEdits{get=>hasMusicalEdits;set{if(Set(ref hasMusicalEdits,value)){Notify(nameof(UsesNativeScoreWorkflow));NotifyCategory();}}}
    public bool UsesNativeScoreWorkflow=>IsNativeScore||HasMusicalEdits;
    public string? NativeSourceSha256{get;init;}
    public string? SourceSha256=>Document?.Sha256??NativeSourceSha256;
    private bool favorite;
    public bool IsFavorite{get=>favorite;set=>Set(ref favorite,value);}
    private string collection="";
    public string Collection{get=>collection;set=>Set(ref collection,value??"");}
    private string genre="";
    public string Genre{get=>genre;set=>Set(ref genre,value??"");}
    /// <summary>The user looked at the generated score and approved it (review in sequence); cleared when the score changes.</summary>
    private bool approved;public bool Approved{get=>approved;set{if(Set(ref approved,value))NotifyConfidence();}}
    /// <summary>The melody was chosen automatically (autopilot or melody batch), and whether that choice was clear.</summary>
    private bool autoMelody;public bool AutoMelody{get=>autoMelody;set=>Set(ref autoMelody,value);}
    private bool autoMelodyConfident;public bool AutoMelodyConfident{get=>autoMelodyConfident;set=>Set(ref autoMelodyConfident,value);}
    /// <summary>How much the generated score needs review (0–100, higher is better); null without a score.</summary>
    private Teclas.Core.ConfidenceRating? confidence;public Teclas.Core.ConfidenceRating? Confidence{get=>confidence;set{if(Set(ref confidence,value))NotifyConfidence();}}
    public string ConfidenceBadge=>ScorePath is null?"":Approved?"✓ Aprovada":Confidence is {} c?$"{c.Score}% · {c.Label}":"";
    public string ConfidenceToolTip=>Approved?"Você aprovou esta partitura na revisão.":Confidence is {Reasons.Count:>0} c?"Para revisar: "+string.Join("; ",c.Reasons)+".":Confidence is null?"":"Nada chamou atenção na análise.";
    public System.Windows.Visibility ConfidenceVisibility=>ConfidenceBadge.Length==0?System.Windows.Visibility.Collapsed:System.Windows.Visibility.Visible;
    /// <summary>Sort key for "review first": unapproved scores by rating, then approved ones, then songs without a score.</summary>
    public int ReviewOrder=>ScorePath is null?300:Approved?200+(Confidence?.Score??0)/2:Confidence?.Score??0;
    private void NotifyConfidence(){Notify(nameof(ConfidenceBadge));Notify(nameof(ConfidenceToolTip));Notify(nameof(ConfidenceVisibility));Notify(nameof(ReviewOrder));}
    private string tags="";
    /// <summary>Free labels typed by the user, separated by commas ("lentas, missa").</summary>
    public string Tags{get=>tags;set=>Set(ref tags,value??"");}
    public IReadOnlyList<string> TagList=>Tags.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
    private int transposeSemitones;public int TransposeSemitones{get=>transposeSemitones;set=>Set(ref transposeSemitones,value);}
    /// <summary>Generate with basic triads (Am7 → Am); the chord table keeps the analyzed symbols.</summary>
    private bool simplifiedChords;public bool SimplifiedChords{get=>simplifiedChords;set=>Set(ref simplifiedChords,value);}
    private bool foldRepeats;public bool FoldRepeats{get=>foldRepeats;set=>Set(ref foldRepeats,value);}
    /// <summary>Chord chart without melody (slashes, chords and diagrams) for MIDIs without a complete melody.</summary>
    private bool chordsOnly;public bool ChordsOnly{get=>chordsOnly;set=>Set(ref chordsOnly,value);}
    /// <summary>Karaoke lyrics of the MIDI under the melody (on by default).</summary>
    private bool includeLyrics=true;public bool IncludeLyrics{get=>includeLyrics;set=>Set(ref includeLyrics,value);}
    /// <summary>Optional staff below the melody with each chord written as the notes of its keyboard diagram.</summary>
    private bool chordStaff;public bool ChordStaff{get=>chordStaff;set=>Set(ref chordStaff,value);}
    /// <summary>A melody track is marked, or the song is a chord chart that needs none.</summary>
    public bool HasMelodyChoice=>ChordsOnly||Tracks.Any(track=>track.IsMelody);
    public List<SavedScoreVersion> Versions{get;set;}=[];
    public List<ScoreReviewPoint> ReviewPoints{get;set;}=[];
    private string melodyMergeSummary="";
    public string MelodyMergeSummary{get=>melodyMergeSummary;set=>Set(ref melodyMergeSummary,value);}
    private ScoreDiagnosticReport? diagnosticReport;
    public ScoreDiagnosticReport? DiagnosticReport{get=>diagnosticReport;set{if(Set(ref diagnosticReport,value))Notify(nameof(DiagnosticSummary));}}
    public string DiagnosticSummary=>DiagnosticReport is {} report
        ? $"Diagnóstico: {report.Issues.Count(i=>i.Severity==ScoreDiagnosticSeverity.Error)} problemas · {report.Issues.Count(i=>i.Severity==ScoreDiagnosticSeverity.Review)} pontos para conferir · {report.Issues.Count(i=>i.Severity==ScoreDiagnosticSeverity.Information)} observações"
        : "Use Diagnóstico da partitura para conferir estrutura, ritmo e apresentação do arquivo salvo.";
    public string Name=>Path.GetFileName(FullPath);
    private string? scoreTitle;
    public string ScoreTitle{get=>scoreTitle??ScoreMetadataDefaults.Title(FullPath);set=>Set(ref scoreTitle,value);}
    private string? scoreSubtitle;
    public string? ScoreSubtitle{get=>scoreSubtitle;set=>Set(ref scoreSubtitle,value);}
    private string scoreComposer="";
    public string ScoreComposer{get=>scoreComposer;set=>Set(ref scoreComposer,value??"");}
    private string scoreArtist="";
    public string ScoreArtist{get=>scoreArtist;set=>Set(ref scoreArtist,value??"");}
    public string MetadataOrganizationNotice{get;set;}="";
    private int defaultDiagramInversion=2;public int DefaultDiagramInversion{get=>defaultDiagramInversion;set{if(Set(ref defaultDiagramInversion,value))Notify(nameof(EffectiveDiagramInversion));}}
    private int? diagramInversion;
    public int? DiagramInversion{get=>diagramInversion;set{if(Set(ref diagramInversion,value))Notify(nameof(EffectiveDiagramInversion));}}
    public int EffectiveDiagramInversion=>DiagramInversion??DefaultDiagramInversion;
    public bool HasPendingScoreProperties{get;set;}
    public MidiDocument? Document{get;init;}
    public JsonObject? Profile{get;init;}
    /// <summary>Shown on the card before any work: the MIDI lacks a complete melody (catalog) or no track looks like one (analysis).</summary>
    public string MelodyWarning{get;init;}="";
    public System.Windows.Visibility MelodyWarningVisibility=>MelodyWarning.Length==0?System.Windows.Visibility.Collapsed:System.Windows.Visibility.Visible;
    public bool HasReviewedProfile=>Profile?["config"]?["status"]?.GetValue<string>()=="ready";
    private bool useReviewedProfile;
    public bool UseReviewedProfile{get=>useReviewedProfile;set{if(Set(ref useReviewedProfile,value))NotifyCategory();}}
    private string status="";public string Status{get=>status;set=>Set(ref status,value);}
    public string ProfileReason=>Profile?["config"]?["reason"]?.GetValue<string>()??"MIDI novo: escolha a pista de melodia e confira as cifras sugeridas.";
    public ObservableCollection<TrackRow> Tracks{get;}=[];
    public List<SourceKey> MelodySourcePriority{get;set;}=[];
    public List<SavedMelodySection> MelodySections{get;set;}=[];
    public ObservableCollection<ChordRow> Chords{get;}=[];
    public string? AnalysisPath{get;set;}
    public string? Workspace{get;set;}
    private string? scorePath;
    public string? ScorePath{get=>scorePath;set{if(Set(ref scorePath,value)){DiagnosticReport=null;NotifyCategory();}}}
    public string? PreviewPath{get;set;}
    private int pageCount;
    public int PageCount{get=>pageCount;set{if(Set(ref pageCount,value))Notify(nameof(PageCountText));}}
    private DateTimeOffset? generatedAt;
    public DateTimeOffset? GeneratedAt{get=>generatedAt;set{if(Set(ref generatedAt,value))Notify(nameof(GeneratedAtText));}}
    private bool hasOperationError;
    public bool HasOperationError{get=>hasOperationError;set{if(Set(ref hasOperationError,value))NotifyCategory();}}
    public DashboardItem DashboardFacts
    {
        get
        {
            bool available=ScorePath is not null && File.Exists(ScorePath);
            return new(UsesNativeScoreWorkflow?available:SourceSha256 is not null,
                HasReviewedProfile && !UsesNativeScoreWorkflow,UseReviewedProfile && !UsesNativeScoreWorkflow,
                available,HasOperationError,PageCount);
        }
    }
    public DashboardCategory Category=>DashboardFacts.Category;
    public string CategoryCode=>Category switch{DashboardCategory.Ready=>"ready",DashboardCategory.Generated=>"generated",DashboardCategory.Error=>"error",_=>"review"};
    public string CategoryLabel=>Category switch
    {
        DashboardCategory.Ready=>"Pronto para o lote",
        DashboardCategory.Generated=>"Partitura gerada",
        DashboardCategory.Error=>UsesNativeScoreWorkflow?"Partitura indisponível":Document is null?"Falha na leitura":"Falha na conversão",
        _=>Profile?["config"]?["status"]?.GetValue<string>() switch
        {
            "no_melody"=>"Sem melodia completa",
            "partial_melody"=>"Melodia parcial",
            "ready"=>"Seleção manual",
            _=>"MIDI novo · revisar"
        }
    };
    public string CategoryColor=>ThemeManager.ColorHex(Category switch{DashboardCategory.Ready=>"Ready",DashboardCategory.Generated=>"Generated",DashboardCategory.Error=>"Error",_=>"Review"});
    public void RefreshTheme()=>Notify(nameof(CategoryColor));
    public string PageCountText=>PageCount==1?"1 página":$"{PageCount} páginas";
    public string GeneratedAtText=>GeneratedAt?.ToLocalTime().ToString("dd/MM · HH:mm")??"Nesta sessão";
    private void NotifyCategory(){Notify(nameof(Category));Notify(nameof(CategoryCode));Notify(nameof(CategoryLabel));Notify(nameof(CategoryColor));}
    private int keyFifths;public int KeyFifths{get=>keyFifths;set=>Set(ref keyFifths,value);}
    private bool keyMinor;public bool KeyMinor{get=>keyMinor;set=>Set(ref keyMinor,value);}
    public string Summary=>IsNativeScore?"MuseScore · "+PageCountText:Document is null?"Não foi possível ler o arquivo":$"{Document.Notes.Count:N0} notas · {Document.Sources.Count} fontes · MIDI {Document.Format}";
}
public sealed class TrackRow : Observable
{
    private bool isAuditioning;
    public bool IsAuditioning{get=>isAuditioning;set{if(Set(ref isAuditioning,value)){Notify(nameof(AuditionGlyph));Notify(nameof(AuditionToolTip));}}}
    public string AuditionGlyph=>IsAuditioning?"■":"▶";
    public string AuditionToolTip=>$"{(IsAuditioning?"Parar":"Ouvir")} pista {Track}, canal {Channel}: {Instrument}";
    private bool melody;
    public bool IsMelody{get=>melody;set{if(Set(ref melody,value)){if(value)IsSecondVoice=false;Changed?.Invoke();}}}
    private bool secondVoice;
    /// <summary>Counter-melody written on its own staff below the melody (optional, at most one part).</summary>
    public bool IsSecondVoice{get=>secondVoice;set{if(Set(ref secondVoice,value)){if(value)IsMelody=false;SecondVoiceChanged?.Invoke(this);}}}
    public Action<TrackRow>? SecondVoiceChanged{get;set;}
    /// <summary>"🎤 95%" on the part that follows the sung lyrics of a karaoke MIDI; empty on the others.</summary>
    private string lyricsBadge="";public string LyricsBadge{get=>lyricsBadge;set{if(Set(ref lyricsBadge,value))Notify(nameof(LyricsToolTip));}}
    public string LyricsToolTip=>LyricsBadge.Length==0?"":$"Esta pista acompanha o canto: {LyricsBadge.Replace("🎤 ","")} das sílabas da letra caem nas notas dela. Escolha-a como melodia para a letra entrar na partitura.";
    public Action? Changed{get;set;}
    public required MidiSource Source{get;init;}
    public string InstrumentName{get;init;}="";
    public MelodySourceSuggestion? MelodySuggestion{get;init;}
    public string Instrument=>string.IsNullOrEmpty(InstrumentName)?$"Instrumento GM {Source.Key.Program+1}":InstrumentName;
    public int Track=>Source.Key.Track+1;
    public int Channel=>Source.Key.Channel;
    public int Count=>Source.Count;
    public string Range=>$"{MidiReader.NoteName(Source.Minimum)}–{MidiReader.NoteName(Source.Maximum)}";
    public string Hint=>MelodySuggestion?.Label??Source.Hint;
    public string SuggestionReason=>MelodySuggestion?.Reason??Source.Hint;
}
public sealed class ChordRow : Observable
{
    private int? diagramInversion;
    public int? DiagramInversion{get=>diagramInversion;set{if(Set(ref diagramInversion,value))Notify(nameof(DiagramPositionLabel));}}
    public string DiagramPositionLabel=>DiagramInversion is {} value?ScoreMetadataDefaults.InversionLabel(value):"Padrão da partitura";
    private double quarter;public double Quarter{get=>quarter;set{if(Set(ref quarter,value))ClearAdvice();}}
    private string root="C";public string Root{get=>root;set{if(Set(ref root,value))ClearAdvice();}}
    private string quality="";public string Quality{get=>quality;set{if(Set(ref quality,value))ClearAdvice();}}
    /// <summary>Bass of a slash chord read from a chord sheet (G/B → B); null when the bass is the root.</summary>
    public string? Bass{get;init;}
    public double Confidence{get;init;}
    private string recommendation="";
    public string Recommendation{get=>recommendation;set{if(Set(ref recommendation,value))Notify(nameof(Advice));}}
    private string alternatives="";
    public string Alternatives{get=>alternatives;set{if(Set(ref alternatives,value))Notify(nameof(Advice));}}
    public string Advice=>string.Join(" ",new[]{Recommendation,string.IsNullOrWhiteSpace(Alternatives)?"":"Alternativas para conferir: "+Alternatives+"."}.Where(text=>!string.IsNullOrWhiteSpace(text)));
    private void ClearAdvice(){Recommendation="";Alternatives="";}
}
