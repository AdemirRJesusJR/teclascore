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
    public string Name=>Path.GetFileName(FullPath);
    public MidiDocument? Document{get;init;}
    public JsonObject? Profile{get;init;}
    public bool HasReviewedProfile=>Profile?["config"]?["status"]?.GetValue<string>()=="ready";
    private bool useReviewedProfile;
    public bool UseReviewedProfile{get=>useReviewedProfile;set{if(Set(ref useReviewedProfile,value))NotifyCategory();}}
    private string status="";public string Status{get=>status;set=>Set(ref status,value);}
    public string ProfileReason=>Profile?["config"]?["reason"]?.GetValue<string>()??"MIDI novo: escolha a pista de melodia e confira as cifras sugeridas.";
    public ObservableCollection<TrackRow> Tracks{get;}=[];
    public ObservableCollection<ChordRow> Chords{get;}=[];
    public string? AnalysisPath{get;set;}
    public string? Workspace{get;set;}
    private string? scorePath;
    public string? ScorePath{get=>scorePath;set{if(Set(ref scorePath,value))NotifyCategory();}}
    public string? PreviewPath{get;set;}
    private int pageCount;
    public int PageCount{get=>pageCount;set{if(Set(ref pageCount,value))Notify(nameof(PageCountText));}}
    private DateTimeOffset? generatedAt;
    public DateTimeOffset? GeneratedAt{get=>generatedAt;set{if(Set(ref generatedAt,value))Notify(nameof(GeneratedAtText));}}
    private bool hasOperationError;
    public bool HasOperationError{get=>hasOperationError;set{if(Set(ref hasOperationError,value))NotifyCategory();}}
    public DashboardItem DashboardFacts=>new(Document is not null,HasReviewedProfile,UseReviewedProfile,
        ScorePath is not null && File.Exists(ScorePath),HasOperationError,PageCount);
    public DashboardCategory Category=>DashboardFacts.Category;
    public string CategoryCode=>Category switch{DashboardCategory.Ready=>"ready",DashboardCategory.Generated=>"generated",DashboardCategory.Error=>"error",_=>"review"};
    public string CategoryLabel=>Category switch
    {
        DashboardCategory.Ready=>"Pronto para o lote",
        DashboardCategory.Generated=>"Partitura gerada",
        DashboardCategory.Error=>Document is null?"Falha na leitura":"Falha na conversão",
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
    public string Summary=>Document is null?"Não foi possível ler o arquivo":$"{Document.Notes.Count:N0} notas · {Document.Sources.Count} fontes · MIDI {Document.Format}";
}
public sealed class TrackRow : Observable
{
    private bool melody;
    public bool IsMelody{get=>melody;set{if(Set(ref melody,value))Changed?.Invoke();}}
    public Action? Changed{get;set;}
    public required MidiSource Source{get;init;}
    public string InstrumentName{get;init;}="";
    public string Instrument=>string.IsNullOrEmpty(InstrumentName)?$"Instrumento GM {Source.Key.Program+1}":InstrumentName;
    public int Track=>Source.Key.Track+1;
    public int Channel=>Source.Key.Channel;
    public int Count=>Source.Count;
    public string Range=>$"{MidiReader.NoteName(Source.Minimum)}–{MidiReader.NoteName(Source.Maximum)}";
    public string Hint=>Source.Hint;
}
public sealed class ChordRow : Observable
{
    private double quarter;public double Quarter{get=>quarter;set=>Set(ref quarter,value);}
    private string root="C";public string Root{get=>root;set=>Set(ref root,value);}
    private string quality="";public string Quality{get=>quality;set=>Set(ref quality,value);}
    public double Confidence{get;init;}
}

