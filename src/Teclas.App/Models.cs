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
public sealed class MidiRow : Observable
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
    public int TransposeSemitones{get;set;}
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
    public int DefaultDiagramInversion{get;set;}=2;
    private int? diagramInversion;
    public int? DiagramInversion{get=>diagramInversion;set{if(Set(ref diagramInversion,value))Notify(nameof(EffectiveDiagramInversion));}}
    public int EffectiveDiagramInversion=>DiagramInversion??DefaultDiagramInversion;
    public bool HasPendingScoreProperties{get;set;}
    public MidiDocument? Document{get;init;}
    public JsonObject? Profile{get;init;}
    public bool HasReviewedProfile=>Profile?["config"]?["status"]?.GetValue<string>()=="ready";
    private bool useReviewedProfile;
    public bool UseReviewedProfile{get=>useReviewedProfile;set{if(Set(ref useReviewedProfile,value))NotifyCategory();}}
    private string status="";public string Status{get=>status;set=>Set(ref status,value);}
    public string ProfileReason=>Profile?["config"]?["reason"]?.GetValue<string>()??"MIDI novo: escolha a pista de melodia e confira as cifras sugeridas.";
    public ObservableCollection<TrackRow> Tracks{get;}=[];
    public List<SourceKey> MelodySourcePriority{get;set;}=[];
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
    public bool IsMelody{get=>melody;set{if(Set(ref melody,value))Changed?.Invoke();}}
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
    public double Confidence{get;init;}
    private string recommendation="";
    public string Recommendation{get=>recommendation;set{if(Set(ref recommendation,value))Notify(nameof(Advice));}}
    private string alternatives="";
    public string Alternatives{get=>alternatives;set{if(Set(ref alternatives,value))Notify(nameof(Advice));}}
    public string Advice=>string.Join(" ",new[]{Recommendation,string.IsNullOrWhiteSpace(Alternatives)?"":"Alternativas para conferir: "+Alternatives+"."}.Where(text=>!string.IsNullOrWhiteSpace(text)));
    private void ClearAdvice(){Recommendation="";Alternatives="";}
}
