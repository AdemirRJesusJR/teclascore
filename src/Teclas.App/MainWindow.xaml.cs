using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Teclas.Core;
namespace Teclas.App;
public partial class MainWindow : Window,INotifyPropertyChanged
{
    private readonly Settings settings;
    private readonly JsonObject profiles;
    private readonly string[] instruments;
    private readonly string engineDirectory=Path.Combine(AppContext.BaseDirectory,"engine");
    private CancellationTokenSource? cancellation;
    private bool busy,loading;
    private MidiRow? selectedFile;
    private string status="Importe seus MIDIs para começar. Os arquivos originais serão preservados.";
    private ImageSource? preview;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
    public ObservableCollection<MidiRow> Files{get;}=[];
    public MidiRow? SelectedFile=>selectedFile;
    private string? CurrentScorePath=>selectedFile?.ScorePath;
    public bool IsBusy=>busy;
    public bool IsIdle=>!busy;
    public bool HasReviewedProfile=>selectedFile is { UsesNativeScoreWorkflow:false, HasReviewedProfile:true };
    public bool CanEditMidi=>IsIdle && selectedFile is { UsesNativeScoreWorkflow:false, Document:not null };
    public bool CanSelectTracks=>CanEditMidi && !UseReviewedProfile;
    public bool CanGenerate=>IsIdle && (selectedFile is { UsesNativeScoreWorkflow:true, ScorePath:{} score } && File.Exists(score)
        || selectedFile is { UsesNativeScoreWorkflow:false, Document:not null } row && (row.UseReviewedProfile || row.Tracks.Any(track=>track.IsMelody)));
    public string? PreviewSourcePath=>selectedFile?.PreviewPath;
    public bool UseReviewedProfile
    {
        get=>selectedFile?.UseReviewedProfile==true;
        set
        {
            if(selectedFile is not { UsesNativeScoreWorkflow:false } row || loading)return;
            bool enabled=value && row.HasReviewedProfile;
            if(row.UseReviewedProfile==enabled)return;
            CurrentFilter="all";
            row.UseReviewedProfile=enabled;InvalidateAnalysis(row);RefreshSelection();
        }
    }
    public string PythonPath{get=>settings.PythonPath;set{settings.PythonPath=value;Notify();}}
    public string MuseScorePath{get=>settings.MuseScorePath;set{settings.MuseScorePath=value;Notify();}}
    public string OutputDirectory{get=>settings.OutputDirectory;set{settings.OutputDirectory=value;Notify();}}
    public string SelectedName=>selectedFile?.Name??"Do MIDI à partitura";
    public string SelectionMessage=>selectedFile is null?"Escolha um MIDI na biblioteca para preparar a melodia, conferir os acordes e gerar sua partitura.":selectedFile is { HasMusicalEdits:true, IsNativeScore:false }?"Partitura com edições musicais: continue no Editor musical. Para voltar à geração pelo MIDI, restaure uma versão anterior às edições no Histórico.":selectedFile.IsNativeScore?"Partitura MuseScore: use o Editor musical ou melhore a apresentação em uma nova cópia.":selectedFile.Document is null?"Este arquivo não pôde ser lido. Substitua-o por uma cópia completa do MIDI.":selectedFile.HasReviewedProfile?"Este MIDI tem uma revisão pronta de melodia, solos e acordes. Você pode aplicá-la ou escolher as pistas manualmente.":selectedFile.Profile?["config"]?["status"]?.GetValue<string>() switch{"no_melody"=>"Este arquivo foi identificado como acompanhamento, sem a melodia completa. Para transcrever a canção inteira, use um MIDI que contenha a voz principal.","partial_melody"=>"Este MIDI contém apenas parte da melodia ou solos. A seleção manual permite preparar esses trechos; a canção completa precisa de outra fonte.",_=>"MIDI novo: selecione uma ou mais pistas para reunir introdução e melodia; confira a prioridade e os acordes antes de gerar."};
    public string FileCountText=>$"{Files.Count} arquivos · {Files.Count(f=>f.ScorePath is not null)} partituras geradas";
    public string Status{get=>status;private set{status=value;Notify();}}
    public ImageSource? Preview{get=>preview;private set{preview=value;Notify();}}
    public string PreviewMessage=>selectedFile?.HasPendingScoreProperties == true?selectedFile.UsesNativeScoreWorkflow?"Alterações pendentes · atualize a partitura pelo Editor musical ou pelas Propriedades.":"Alterações pendentes · gere novamente para atualizar esta prévia.":selectedFile?.ScorePath is null?"A prévia aparecerá depois da geração.":"Todas as páginas · use as miniaturas, o zoom ou a tela cheia para estudar.";
    public string GenerationHint=>selectedFile is { HasMusicalEdits:true, IsNativeScore:false }?"Continue as alterações no Editor musical. Para gerar a partir do MIDI, restaure uma versão anterior às edições no Histórico.":selectedFile?.IsNativeScore==true?"Melhore a apresentação em uma nova cópia e compare as versões no Histórico.":selectedFile?.AnalysisPath is null?selectedFile is { Document:not null } row && (row.UseReviewedProfile || row.Tracks.Any(track=>track.IsMelody))?"Ao gerar, os acordes serão analisados para a melodia selecionada. Confira as cifras sugeridas.":"Escolha a pista de melodia para gerar a partitura.":UseReviewedProfile?"Perfil revisado carregado. Ajustes feitos na tabela de cifras serão aplicados.":"Confira a fonte de melodia e as cifras: uma análise automática precisa de revisão musical.";
    public MainWindow(bool smoke=false,string? settingsPath=null,Func<IMidiOutputTransport>? midiOutputFactory=null)
    {
        settings=Settings.Load(settingsPath);
        ThemeManager.Apply(settings.ThemeId);
        InitializeComponent();
        InitializeOperations();
        InitializePlayback();
        Closed+=(_,_)=>DisposePlayback();
        InitializeTrackAudition(midiOutputFactory);
        profiles=JsonNode.Parse(File.ReadAllText(Path.Combine(engineDirectory,"profiles.json")))!.AsObject();
        instruments=JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(engineDirectory,"gm.json")))!;
        InitDashboard();
        DataContext=this;
        InitializeAppearance();
        InitializeWindowTheme();
        InitializePersistence(smoke,settingsPath);
        Closed+=(_,_)=>conversionQueue?.Dispose();
    }
    private void RefreshSelection()
    {
        NotifyMelodyPriority();Notify(nameof(CanEnqueueConversion));
        NotifyDiagnostics();
        NotifyLibraryActions();
        Notify(nameof(CanCustomizeDiagrams));
        foreach(var name in new[]{nameof(PreviewSourcePath),nameof(CanShowHistory),nameof(GenerateButtonLabel),nameof(LibraryCollections),nameof(CanEditMidi),nameof(CanReviewScore),nameof(CanTransposeScore)})Notify(name);
        Notify(nameof(CanOpenMusicEditor));Notify(nameof(CanArrangeScore));Notify(nameof(CanReviewVisualScore)); Notify(nameof(CanEditScoreProperties)); Notify(nameof(ScorePropertiesSummary));
        OnPlaybackSelectionChanged();
        OnTrackAuditionSelectionChanged();
        foreach(var name in new[]{nameof(SelectedFile),nameof(SelectedName),nameof(SelectionMessage),nameof(HasReviewedProfile),nameof(UseReviewedProfile),nameof(CanSelectTracks),nameof(CanGenerate),nameof(GenerationHint),nameof(PreviewMessage),nameof(FileCountText)})Notify(name);
        RefreshDashboard();
        Preview=null;
        if(selectedFile?.PreviewPath is { } path && File.Exists(path))
        {
            try
            {
                var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(path);image.EndInit();image.Freeze();Preview=image;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
            {Status="Não foi possível abrir a prévia. Sua partitura e suas edições continuam salvas. "+ex.Message;}
        }
    }
    private void SetBusy(bool value){busy=value;Notify(nameof(CanSelectTracks));NotifyMelodyPriority();Notify(nameof(CanEnqueueConversion));Notify(nameof(CanOpenMusicEditor));Notify(nameof(CanArrangeScore));Notify(nameof(CanReviewVisualScore));NotifyDiagnostics();if(value)StopTrackAudition();Notify(nameof(IsBusy));Notify(nameof(IsIdle));Notify(nameof(CanGenerate));Notify(nameof(CanBatch));NotifyPlaybackState();NotifyLibraryActions();NotifyTrackAudition();Notify(nameof(CanEditScoreProperties));Notify(nameof(CanEditMidi));Notify(nameof(CanReviewScore));Notify(nameof(CanTransposeScore));Notify(nameof(CanShowHistory));Notify(nameof(CanCustomizeDiagrams));}
    private void InvalidateAnalysis(MidiRow row)
    {
        if(row.UsesNativeScoreWorkflow)return;
        RememberVersion(row);row.AnalysisPath=null;row.Chords.Clear();row.ReviewPoints.Clear();row.MelodyMergeSummary="";Notify(nameof(CanReviewScore));
        if(row.ScorePath is not null)row.HasPendingScoreProperties=true;
        if(row==selectedFile){ResetPlayback();NotifyPlaybackState();}
        if(row.Document is not null)row.Status=row.UseReviewedProfile?"Perfil revisado · pronto para gerar":"Seleção alterada · gere para atualizar a partitura";
        NotifyMelodyPriority();Notify(nameof(CanEnqueueConversion));Notify(nameof(CanGenerate));Notify(nameof(GenerationHint));Notify(nameof(PreviewMessage));Notify(nameof(ScorePropertiesSummary));
    }
    private MidiRow LoadMidi(string path)
    {
        if(string.Equals(Path.GetExtension(path),".mscz",StringComparison.OrdinalIgnoreCase))return LoadNativeScore(path);
        MidiDocument document=MidiReader.Read(path);
        var profile=profiles[document.Sha256]?.AsObject();
        var row=new MidiRow{FullPath=Path.GetFullPath(path),DefaultDiagramInversion=settings.DefaultDiagramInversion,Document=document,Profile=profile,UseReviewedProfile=profile?["config"]?["status"]?.GetValue<string>()=="ready"};
        var header=ScoreMetadataOrganizer.Suggest(row.FullPath,document.TextEvents);
        row.ScoreTitle=header.Title;row.ScoreArtist=header.Artist;row.ScoreComposer=header.Composer;
        row.MetadataOrganizationNotice=header.Explanation;
        row.Status=row.HasReviewedProfile?"Perfil musical revisado":profile?["config"]?["status"]?.GetValue<string>() switch{"no_melody"=>"Sem melodia completa","partial_melody"=>"Somente melodia parcial",_=>"Escolha a pista de melodia"};
        var keys=profile?["config"]?["melody_sources"]?.AsArray().Select(s=>new SourceKey(s!["track"]!.GetValue<int>(),s["channel"]!.GetValue<int>(),s["program"]!.GetValue<int>())).ToHashSet()??[];
        var suggestions=MelodySourceAdvisor.Analyze(document).ToDictionary(s=>s.Key);
        foreach(var source in document.Sources)
        {
            var track=new TrackRow{Source=source,InstrumentName=source.Key.Channel==10?"Percussão":instruments[source.Key.Program],IsMelody=keys.Contains(source.Key),MelodySuggestion=suggestions.GetValueOrDefault(source.Key)};
            track.Changed=()=>{if(!loading)InvalidateAnalysis(row);};row.Tracks.Add(track);
        }
        return row;
    }
    public void ImportForSmoke(string path)
    {
        MidiRow row;
        try{row=LoadMidi(path);}
        catch(Exception ex) when(ex is IOException or InvalidDataException or OverflowException){row=new(){FullPath=Path.GetFullPath(path),Status="Não foi possível ler: "+ex.Message};}
        Files.Add(row);if(Library.SelectedItem is null)Library.SelectedItem=row;RefreshSelection();
    }
    private async Task<MidiRow?> ImportAsync(IEnumerable<string> paths)
    {
        var list=paths.Where(p=>new[]{".mid",".midi",".kar",".mscz"}.Contains(Path.GetExtension(p).ToLowerInvariant())).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int imported=0;
        int processed=0;
        SetOperationItems(0,list.Length);
        MidiRow? firstImported=null,firstExisting=null;
        foreach(var path in list)
        {
            cancellation!.Token.ThrowIfCancellationRequested();
            SetOperationItems(processed,list.Length,Path.GetFileName(path));
            var existing=Files.FirstOrDefault(f=>string.Equals(f.FullPath,Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase));
            if(existing is not null){firstExisting??=existing;SetOperationItems(++processed,list.Length);continue;}
            Status="Lendo "+Path.GetFileName(path)+"…";
            MidiRow row;
            try{row=await Task.Run(()=>LoadMidi(path),cancellation.Token);}
            catch(Exception ex) when(ex is IOException or InvalidDataException or OverflowException){row=new(){FullPath=Path.GetFullPath(path),Status="Não foi possível ler: "+ex.Message};}
            cancellation.Token.ThrowIfCancellationRequested();
            Files.Add(row);firstImported??=row;
            if(row.IsNativeScore && row.NativeSourceSha256 is not null)
            {
                try{await PrepareNativePreview(row);}
                catch(OperationCanceledException){throw;}
                catch(Exception ex){row.HasOperationError=true;row.Status="Partitura importada · prévia indisponível: "+ex.Message;}
            }
            imported++;
            SetOperationItems(++processed,list.Length);
        }
        Status=imported>0?$"{imported} arquivos importados. Abra a prévia das partituras ou prepare os MIDIs.":firstExisting is not null?"Este arquivo já está na biblioteca.":"Nenhum MIDI ou arquivo MuseScore foi selecionado.";
        Notify(nameof(FileCountText));
        return firstImported??firstExisting;
    }
    private async Task ImportAndOpenLibraryAsync(IEnumerable<string> paths)
    {
        if(busy || !CommitEdits())return;
        MidiRow? importedSelection=null;
        await RunOperation(async()=>{importedSelection=await ImportAsync(paths);}, operationTitle:"Importando para a biblioteca");
        if(importedSelection is null)return;
        // Select after RunOperation releases busy state, so stale filters are refreshed first.
        SearchText="";CurrentFilter="all";FavoritesOnly=false;CollectionFilter="";
        SetCurrentPage("library");
        Library.SelectedItem=importedSelection;
        EditorTabs.SelectedIndex=importedSelection.UsesNativeScoreWorkflow?2:0;
        Library.ScrollIntoView(importedSelection);
    }
    private async Task RunOperation(Func<Task> action, MidiRow? rowOnError=null, string operationTitle="Processando", string? operationItem=null)
    {
        if(busy || operationsDisposed)return;cancellation=new();SetBusy(true);BeginOperation(operationTitle,operationItem??rowOnError?.Name);
        try
        {
            // Let bindings, layout and animation render before any synchronous preparation.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            OperationCancelButton.Focus();
            cancellation.Token.ThrowIfCancellationRequested();
            await action();
        }
        catch(OperationCanceledException){Status="Operação cancelada. Os arquivos originais foram preservados.";if(rowOnError is not null)rowOnError.Status="Operação cancelada · tente novamente quando desejar";}
        catch(Exception ex){Status=ex.Message;if(rowOnError is { } row){row.Status="Precisa de revisão: "+ex.Message;row.HasOperationError=true;}}
        finally
        {
            // Keep the result in the editor when an operation moves it to another category.
            if(rowOnError is not null && selectedFile==rowOnError && !MatchesLibraryFilter(rowOnError))
            {SearchText="";CurrentFilter="all";FavoritesOnly=false;CollectionFilter="";}
            cancellation.Dispose();cancellation=null;SetBusy(false);EndOperation();
            if(!operationsDisposed){RefreshSelection();PersistPreferences();}
        }
    }
    private void ValidateSettings(bool generating)
    {
        if(!File.Exists(PythonPath))throw new InvalidOperationException("Localize o Python com Pillow na aba Configurações.");
        if(generating && !File.Exists(MuseScorePath))throw new InvalidOperationException("Localize o MuseScore Studio 4 na aba Configurações.");
        if(generating && !Path.IsPathFullyQualified(OutputDirectory))throw new InvalidOperationException("Escolha uma pasta de saída com caminho completo.");
    }
    private JsonArray Sources(MidiRow row)=>new(GetOrderedMelodySources(row).Select(key=>(JsonNode)new JsonObject{["track"]=key.Track,["channel"]=key.Channel,["program"]=key.Program}).ToArray());
    private static void RequireMidiWorkflow(MidiRow row)
    {
        if(row.UsesNativeScoreWorkflow)throw new InvalidOperationException(row.HasMusicalEdits && !row.IsNativeScore
            ? "Esta partitura contém edições musicais. Continue no Editor musical ou restaure uma versão anterior às edições no Histórico para gerar a partir do MIDI."
            : "Use o Editor musical ou as Propriedades para atualizar esta partitura MuseScore.");
    }
    private async Task Analyze(MidiRow row)
    {
        RequireMidiWorkflow(row);
        ValidateSettings(false);
        if(row.Document is null)throw new InvalidOperationException("Este arquivo não pôde ser lido como MIDI.");
        if(!row.UseReviewedProfile && !row.Tracks.Any(t=>t.IsMelody))throw new InvalidOperationException("Marque uma pista de melodia ou use o perfil revisado.");
        row.Workspace=Path.Combine(persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!,"jobs",Guid.NewGuid().ToString("N"));
        var request=new JsonObject{["mode"]="analyze",["workspace"]=row.Workspace,["inputMidi"]=row.FullPath,["useReviewedProfile"]=row.UseReviewedProfile,["melodySources"]=Sources(row),["melodySections"]=MelodySections(row),["expectedSourceSha256"]=row.SourceSha256};
        row.Status="Analisando acordes…";
        var client=new EngineClient(PythonPath,engineDirectory);
        var result=await client.RunAsync(request,row.Workspace,CreateOperationProgress(),cancellation!.Token);
        ParseReviewPoints(row,JsonSerializer.SerializeToElement(result));
        row.AnalysisPath=result["analysisPath"]!.GetValue<string>();row.KeyFifths=result["keyFifths"]!.GetValue<int>();row.KeyMinor=result["keyMinor"]!.GetValue<bool>();
        var previousPositions=row.Chords.Where(c=>c.DiagramInversion is not null).ToArray();
        row.Chords.Clear();
        foreach(var c in result["chords"]!.AsArray())row.Chords.Add(new(){Quarter=c!["quarter"]!.GetValue<double>(),Root=c["root"]!.GetValue<string>(),Quality=c["quality"]!.GetValue<string>(),Confidence=c["confidence"]!.GetValue<double>(),Recommendation=c["recommendation"]?.GetValue<string>()??"",Alternatives=c["alternatives"]?.GetValue<string>()??""});
        foreach(var chord in row.Chords)
            chord.DiagramInversion=previousPositions.FirstOrDefault(old=>Math.Abs(old.Quarter-chord.Quarter)<.000001 && old.Root==chord.Root && old.Quality==chord.Quality)?.DiagramInversion;
        row.HasOperationError=false;
        row.Status=$"{row.Chords.Count} mudanças de acorde · confira as cifras";
        var warnings=string.Join(" ",result["warnings"]!.AsArray().Select(x=>x!.GetValue<string>()));
        Status=string.IsNullOrEmpty(warnings)?"Análise concluída. Confira os acordes e gere a partitura.":warnings;
        if(result["rhythmReady"]?.GetValue<bool>()==false)
        {
            row.Status="Revisão rítmica necessária · abra Revisão guiada";
            Status=result["rhythmMessage"]?.GetValue<string>()??"Confira os pontos rítmicos na Revisão guiada antes de gerar.";
        }
        RefreshSelection();
    }
    private async Task Generate(MidiRow row)
    {
        RequireMidiWorkflow(row);
        ValidateSettings(true);
        RememberVersion(row);
        if(row.ScorePath is not null)
        {
            row.HasPendingScoreProperties=true;
            if(row==selectedFile){ResetPlayback();NotifyPlaybackState();Notify(nameof(PreviewMessage));Notify(nameof(ScorePropertiesSummary));}
        }
        await EnsureAnalysisForGeneration(row);
        var chords=new JsonArray(row.Chords.Select(c=>(JsonNode)new JsonObject{["quarter"]=c.Quarter,["root"]=c.Root,["quality"]=c.Quality,["diagramInversion"]=c.DiagramInversion}).ToArray());
        var request=new JsonObject{["mode"]="generate",["workspace"]=row.Workspace,["inputMidi"]=row.FullPath,["analysisPath"]=row.AnalysisPath,["musescorePath"]=MuseScorePath,["outputDirectory"]=OutputDirectory,["keyFifths"]=row.KeyFifths,["keyMinor"]=row.KeyMinor,["chords"]=chords,["scoreTitle"]=row.ScoreTitle,["scoreSubtitle"]=row.ScoreSubtitle,["scoreComposer"]=row.ScoreComposer,["diagramInversion"]=row.EffectiveDiagramInversion};
        row.Status="Gerando e conferindo no MuseScore…";
        request["transposeSemitones"]=row.TransposeSemitones;
        request["scoreArtist"]=row.ScoreArtist;
        var result=await new EngineClient(PythonPath,engineDirectory).RunAsync(request,row.Workspace!,CreateOperationProgress(),cancellation!.Token);
        row.ScorePath=result["scorePath"]!.GetValue<string>();row.PreviewPath=result["previewPath"]!.GetValue<string>();
        row.PageCount=result["pages"]!.GetValue<int>();row.GeneratedAt=DateTimeOffset.Now;row.HasOperationError=false;row.HasPendingScoreProperties=false;
        row.ArrangementSummary="";
        row.Status=$"Gerada · {row.PageCount} páginas";Status=result["message"]!.GetValue<string>();RememberVersion(row);RefreshSelection();
    }
    private async Task EnsureAnalysisForGeneration(MidiRow row)
    {
        RequireMidiWorkflow(row);
        if(HasCurrentMelodyAnalysis(row) && !row.ReviewPoints.Any(point=>point.Blocking))return;
        // Reassess rhythm blockers with the current engine while preserving edits and the old analysis file.
        // A selection change clears the chords, so rebuilding here cannot reuse another melody's edits.
        var chords=row.Chords.ToArray();int fifths=row.KeyFifths;bool minor=row.KeyMinor;
        await Analyze(row);
        if(chords.Length>0)
        {
            row.Chords.Clear();foreach(var chord in chords)row.Chords.Add(chord);
            row.KeyFifths=fifths;row.KeyMinor=minor;
        }
    }
    private async void ImportClick(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Title="Importar para a biblioteca",Filter="MIDI e MuseScore|*.mid;*.midi;*.kar;*.mscz|MuseScore 4|*.mscz|MIDI e karaokê|*.mid;*.midi;*.kar",Multiselect=true};
        if(dialog.ShowDialog(this)==true)await ImportAndOpenLibraryAsync(dialog.FileNames);
    }
    private async void FilesDrop(object sender,DragEventArgs e)
    {
        if(!busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths)await ImportAndOpenLibraryAsync(paths);
    }
    private void SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(Library is null)return;
        if(selectedFile is not null)selectedFile.Chords.CollectionChanged-=SelectedChordsChanged;
        loading=true;selectedFile=Library.SelectedItem as MidiRow;
        if(selectedFile is not null)selectedFile.Chords.CollectionChanged+=SelectedChordsChanged;
        RefreshSelection();loading=false;
    }
    private void SelectedChordsChanged(object? sender,System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {Notify(nameof(CanCustomizeDiagrams));Notify(nameof(ScorePropertiesSummary));}
    private async void AnalyzeClick(object sender,RoutedEventArgs e){if(CanEditMidi && selectedFile is { } row)await RunOperation(async()=>{await Analyze(row);EditorTabs.SelectedIndex=1;},row,"Analisando notas e acordes");}
    private async void GenerateClick(object sender,RoutedEventArgs e)
    {
        if(!CanGenerate)return;
        if(selectedFile?.UsesNativeScoreWorkflow==true){ImproveNativeScoreClick(sender,e);return;}
        if(!CommitEdits())return;
        if(selectedFile is { } row)await RunOperation(async()=>{await Generate(row);EditorTabs.SelectedIndex=2;},row,"Gerando partitura");
    }
    private bool CommitEdits()
    {
        if(!ChordGrid.CommitEdit(DataGridEditingUnit.Cell,true) || !ChordGrid.CommitEdit(DataGridEditingUnit.Row,true) || HasValidationError(this))
        {Status="Corrija os campos destacados para continuar.";return false;}
        return true;
    }
    private static bool HasValidationError(DependencyObject element)
    {
        if(Validation.GetHasError(element))return true;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++)if(HasValidationError(VisualTreeHelper.GetChild(element,i)))return true;
        return false;
    }
    private async void BatchClick(object sender,RoutedEventArgs e)
    {
        if(!IsIdle || !CommitEdits())return;
        var rows=Files.Where(f=>!f.UsesNativeScoreWorkflow && f.Category==DashboardCategory.Ready).ToArray();
        if(rows.Length==0){Status="Não há MIDIs revisados prontos para adicionar à fila.";return;}
        int added=0;
        await RunOperation(async()=>{
            SetOperationItems(0,rows.Length);
            foreach(var row in rows)
            {
                cancellation!.Token.ThrowIfCancellationRequested();
                SetOperationItems(added,rows.Length,row.Name);
                await EnqueueConversion(row,cancellation.Token);added++;
            }
            Status=$"{added} partituras adicionadas à fila. Clique em Iniciar pendentes para converter.";
        },operationTitle:"Adicionando lote à fila");
        if(added>0)ShowConversionQueue();
    }
    private async void ZipClick(object sender,RoutedEventArgs e)
    {
        if(!IsIdle)return;
        var scores=Files.Where(f=>f.ScorePath is not null && File.Exists(f.ScorePath)).ToArray();
        if(scores.Length==0){Status="Gere ao menos uma partitura para criar o ZIP.";return;}
        int pending=scores.Count(row=>row.HasPendingScoreProperties);
        if(pending>0){Status=$"Há {pending} partitura(s) com alterações pendentes. Atualize-as na Biblioteca antes de exportar o ZIP.";return;}
        var dialog=new SaveFileDialog{Title="Salvar pacote",Filter="Pacote ZIP|*.zip",FileName="Partituras de teclado.zip",OverwritePrompt=true};
        if(dialog.ShowDialog(this)!=true)return;
        await RunOperation(async()=>{
            Status="Reunindo as partituras no pacote ZIP…";
            await Task.Run(()=>{
                string temporary=Path.Combine(Path.GetDirectoryName(dialog.FileName)!,".teclas-"+Guid.NewGuid().ToString("N")+".tmp");
                try
                {
                using(var archive=new ZipArchive(File.Create(temporary),ZipArchiveMode.Create))
                {
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var row in scores)
                {
                    cancellation!.Token.ThrowIfCancellationRequested();string name=Path.GetFileName(row.ScorePath!);
                    if(!names.Add(name)){name=Guid.NewGuid().ToString("N")[..6]+" - "+name;names.Add(name);}
                    archive.CreateEntryFromFile(row.ScorePath!,name,CompressionLevel.Optimal);
                }
                using var writer=new StreamWriter(archive.CreateEntry("LEIA-ME.txt").Open());
                writer.Write("Partituras com melodia, cifras e diagramas de teclado.\nA inversão dos diagramas segue as propriedades de cada partitura.\nAbra os arquivos .mscz no MuseScore e confira a música.\nOs diagramas são imagens: alterações nas cifras dentro do MuseScore exigem atualizar o desenho.\n");
                }
                cancellation!.Token.ThrowIfCancellationRequested();File.Move(temporary,dialog.FileName,true);
                }
                finally{if(File.Exists(temporary))File.Delete(temporary);}
            },cancellation!.Token);
            Status=$"ZIP salvo com {scores.Length} partituras.";
        },operationTitle:"Salvando pacote ZIP",operationItem:Path.GetFileName(dialog.FileName));
    }
    private void CancelClick(object sender,RoutedEventArgs e)=>RequestOperationCancellation();
    private void OpenScoreClick(object sender,RoutedEventArgs e)
    {
        if(selectedFile?.ScorePath is not { } path || !File.Exists(path)){Status="Gere a partitura primeiro.";return;}
        try{var start=new ProcessStartInfo(MuseScorePath){UseShellExecute=true};start.ArgumentList.Add(path);Process.Start(start);}catch(Exception ex){Status=ex.Message;}
    }
    private void OpenFolderClick(object sender,RoutedEventArgs e)
    {
        if(!Directory.Exists(OutputDirectory)){Status="A pasta de saída será criada ao gerar a primeira partitura.";return;}
        try{var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};start.ArgumentList.Add(OutputDirectory);Process.Start(start);}catch(Exception ex){Status=ex.Message;}
    }
    private void BrowseMuseScore(object sender,RoutedEventArgs e){var d=new OpenFileDialog{Filter="Executável|*.exe",Title="Localizar MuseScore4.exe"};if(d.ShowDialog(this)==true)MuseScorePath=d.FileName;}
    private void BrowsePython(object sender,RoutedEventArgs e){var d=new OpenFileDialog{Filter="Executável|*.exe",Title="Localizar python.exe"};if(d.ShowDialog(this)==true)PythonPath=d.FileName;}
    private void BrowseOutput(object sender,RoutedEventArgs e){var d=new OpenFolderDialog{Title="Pasta de saída das partituras"};if(d.ShowDialog(this)==true)OutputDirectory=d.FolderName;}
}
