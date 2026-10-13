using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public sealed class MusicEditorWindow : Window
{
    private sealed record RhythmValue(string Duration, int Dots);
    private sealed record HarmonyValue(string Root, string Quality, int Inversion);
    private sealed record DraftState(Dictionary<string,string> Notes, Dictionary<string,RhythmValue> Rhythms, Dictionary<string,HarmonyValue> Harmonies);
    private sealed record Choice(object Value, string Label);
    private readonly NativeMusicModel model;
    private readonly string? originalPreview;
    private readonly Func<NativeMusicEdits,IProgress<string>,CancellationToken,Task<NativeScoreResult>> prepare;
    private readonly Func<NativeScoreResult,IProgress<string>,CancellationToken,Task<NativeScoreResult>> publish;
    private readonly EditorScorePlayback playback;
    private readonly List<MusicNoteRow> notes;
    private readonly List<MusicRhythmRow> rhythms;
    private readonly List<MusicHarmonyRow> harmonies;
    private readonly List<DraftState> undo = [], redo = [];
    private DraftState current = new([], [], []);
    private readonly ComboBox staff, measure, speed;
    private readonly DataGrid noteGrid, rhythmGrid, harmonyGrid;
    private readonly TabControl tabs;
    private readonly ScoreViewer viewer;
    private readonly TextBlock status, previewStatus, operationText, audioClock;
    private readonly StackPanel progressPanel;
    private readonly Button undoButton, redoButton, previewButton, playButton, pauseButton, saveButton;
    private readonly Slider seek;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch elapsed = new();
    private CancellationTokenSource? operation;
    private NativeScoreResult? prepared;
    private int revision, preparedRevision = -1;
    private bool restoring, closed, pendingClose, discardConfirmed, updatingSeek;
    private string operationStage = "";
    public NativeScoreResult? Result { get; private set; }
    public bool HasChanges => current.Notes.Count + current.Rhythms.Count + current.Harmonies.Count > 0;

    public MusicEditorWindow(NativeMusicModel model, string? initialPreview,
        Func<NativeMusicEdits,IProgress<string>,CancellationToken,Task<NativeScoreResult>> prepare,
        Func<NativeScoreResult,IProgress<string>,CancellationToken,Task<NativeScoreResult>> publish,
        EditorScorePlayback playback)
    {
        this.model=model; originalPreview=initialPreview; this.prepare=prepare; this.publish=publish; this.playback=playback;
        notes=model.Measures.SelectMany(m=>m.Events).SelectMany(e=>e.Notes.Select(n=>new MusicNoteRow(n,e,Changed))).ToList();
        rhythms=model.Measures.SelectMany(m=>m.Events).Select(e=>new MusicRhythmRow(e,Changed)).ToList();
        harmonies=model.Measures.SelectMany(m=>m.Harmonies).Select(h=>new MusicHarmonyRow(h,Changed)).ToList();
        Title="Editor musical · TeclaScore"; Width=1300; Height=850; MinWidth=1000; MinHeight=640; DialogUi.Theme(this);
        var root=new Grid { Margin=new(20) }; Content=root;
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header=new StackPanel{Margin=new(0,0,0,12)}; root.Children.Add(header);
        header.Children.Add(new TextBlock{Text="Editor musical",FontSize=24,FontWeight=FontWeights.SemiBold});
        header.Children.Add(new TextBlock{Text=model.Metadata.Title,TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,6)});
        header.Children.Add(new TextBlock{Text="Edite por compasso, atualize a prévia e ouça. Salvar cria uma nova versão no Histórico.",TextWrapping=TextWrapping.Wrap});
        var selectors=new WrapPanel{Margin=new(0,12,0,0)};header.Children.Add(selectors);
        selectors.Children.Add(new TextBlock{Text="Pauta",VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,8,8)});
        staff=new ComboBox{Name="MusicEditorStaff",Width=72,ItemsSource=model.Measures.Select(m=>m.Staff).Distinct().ToArray(),Margin=new(0,0,16,8)};
        selectors.Children.Add(staff);
        selectors.Children.Add(new TextBlock{Text="Compasso",VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,8,8)});
        measure=new ComboBox{Name="MusicEditorMeasure",Width=210,DisplayMemberPath=nameof(MusicMeasure.Label),Margin=new(0,0,16,8)};selectors.Children.Add(measure);
        undoButton=Button("Desfazer","MusicEditorUndo",(_,_)=>Undo());selectors.Children.Add(undoButton);
        redoButton=Button("Refazer","MusicEditorRedo",(_,_)=>Redo());selectors.Children.Add(redoButton);
        var body=new Grid();Grid.SetRow(body,1);root.Children.Add(body);
        body.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star),MinWidth=430});
        body.ColumnDefinitions.Add(new(){Width=new(12)});
        body.ColumnDefinitions.Add(new(){Width=new(1.15,GridUnitType.Star),MinWidth=410});
        var editing=new DockPanel();body.Children.Add(editing);
        var limitations=new Expander{Header="Orientações e limites desta partitura",Margin=new(0,8,0,0),
            Content=new ScrollViewer{MaxHeight=80,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new TextBlock{
                Text="Notas: use C4, F#4 ou Bb3. C4 é o dó central. Ritmo: mantenha a soma da voz no compasso e as posições das cifras. A reprodução inclui a partitura inteira.\n"+string.Join("\n",model.Limitations),
                TextWrapping=TextWrapping.Wrap,Margin=new(8)}}};
        DockPanel.SetDock(limitations,Dock.Bottom);editing.Children.Add(limitations);
        tabs=new TabControl();editing.Children.Add(tabs);
        noteGrid=MakeGrid("MusicEditorNotes");
        noteGrid.Columns.Add(Text("Posição",nameof(MusicNoteRow.Position),130));
        noteGrid.Columns.Add(Text("Original",nameof(MusicNoteRow.Original),68));
        noteGrid.Columns.Add(Input("Nota",nameof(MusicNoteRow.Value),nameof(MusicNoteRow.CanEdit),110));
        noteGrid.Columns.Add(Text("Observação",nameof(MusicNoteRow.Restriction),190));
        tabs.Items.Add(new TabItem{Header="Notas",Content=noteGrid});
        rhythmGrid=MakeGrid("MusicEditorRhythm");
        rhythmGrid.Columns.Add(Text("Posição",nameof(MusicRhythmRow.Position),130));
        rhythmGrid.Columns.Add(Text("Notas / pausa",nameof(MusicRhythmRow.Notes),100));
        rhythmGrid.Columns.Add(Options("Duração",nameof(MusicRhythmRow.Duration),nameof(MusicRhythmRow.CanEdit),
            new[]{new Choice("whole","Semibreve"),new("half","Mínima"),new("quarter","Semínima"),new("eighth","Colcheia"),new("16th","Semicolcheia"),new("32nd","Fusa"),new("64th","Semifusa"),new("128th","1/128"),new("256th","1/256"),new("breve","Breve"),new("long","Longa"),new("measure","Compasso")},145));
        rhythmGrid.Columns.Add(Options("Pontos",nameof(MusicRhythmRow.Dots),nameof(MusicRhythmRow.CanEdit),Enumerable.Range(0,4).Select(i=>new Choice(i,i.ToString())).ToArray(),65));
        rhythmGrid.Columns.Add(Text("Observação",nameof(MusicRhythmRow.Restriction),190));
        tabs.Items.Add(new TabItem{Header="Ritmo",Content=rhythmGrid});
        harmonyGrid=MakeGrid("MusicEditorHarmony");
        harmonyGrid.Columns.Add(Text("Posição",nameof(MusicHarmonyRow.Position),130));
        harmonyGrid.Columns.Add(Text("Original",nameof(MusicHarmonyRow.Original),74));
        harmonyGrid.Columns.Add(Options("Raiz",nameof(MusicHarmonyRow.Root),nameof(MusicHarmonyRow.CanEdit),
            new[]{"C","C#","Db","D","D#","Eb","E","F","F#","Gb","G","G#","Ab","A","A#","Bb","B","B#","Cb","E#","Fb","C##","Cbb","D##","Dbb","E##","Ebb","F##","Fbb","G##","Gbb","A##","Abb","B##","Bbb"}.Select(s=>new Choice(s,s)).ToArray(),80));
        harmonyGrid.Columns.Add(Options("Tipo",nameof(MusicHarmonyRow.Quality),nameof(MusicHarmonyRow.CanEdit),
            new[]{"","m","7","maj7","m7","dim","dim7","m7b5","aug","sus2","sus4","6","m6","9","maj9","m9","add9","madd9","mMaj7","7sus4"}.Select(s=>new Choice(s,s==""?"Maior":s)).ToArray(),100));
        harmonyGrid.Columns.Add(Options("Diagrama",nameof(MusicHarmonyRow.Inversion),nameof(MusicHarmonyRow.CanEditDiagram),
            new[]{new Choice(0,"Fundamental"),new(1,"1ª inversão"),new(2,"2ª inversão")},155));
        harmonyGrid.Columns.Add(Text("Observação",nameof(MusicHarmonyRow.Restriction),190));
        tabs.Items.Add(new TabItem{Header="Cifras",Content=harmonyGrid});
        var preview=new DockPanel();Grid.SetColumn(preview,2);body.Children.Add(preview);
        previewStatus=new TextBlock{Text="Partitura salva · selecione o compasso no editor à esquerda.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,8)};
        DockPanel.SetDock(previewStatus,Dock.Top);preview.Children.Add(previewStatus);
        viewer=new ScoreViewer{FirstPagePath=initialPreview};preview.Children.Add(viewer);
        var footer=new StackPanel{Margin=new(0,12,0,0)};Grid.SetRow(footer,2);root.Children.Add(footer);
        progressPanel=new StackPanel{Visibility=Visibility.Collapsed};footer.Children.Add(progressPanel);
        progressPanel.Children.Add(new ProgressBar{Name="MusicEditorProgress",IsIndeterminate=true,Height=5});
        var progressLine=new DockPanel{Margin=new(0,6,0,6)};progressPanel.Children.Add(progressLine);
        var cancel=Button("Cancelar preparação","MusicEditorCancel",(_,_)=>{operation?.Cancel();playback.Stop();});
        DockPanel.SetDock(cancel,Dock.Right);progressLine.Children.Add(cancel);
        operationText=new TextBlock{Name="MusicEditorOperation",TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};progressLine.Children.Add(operationText);
        status=new TextBlock{Name="MusicEditorStatus",Text="As alterações ficam em rascunho até salvar.",TextWrapping=TextWrapping.Wrap,MinHeight=34};footer.Children.Add(status);
        var audio=new WrapPanel();footer.Children.Add(audio);
        playButton=Button("▶ Ouvir partitura","MusicEditorPlay",async(_,_)=>await Play());audio.Children.Add(playButton);
        pauseButton=Button("Pausar","MusicEditorPause",(_,_)=>{if(playback.IsPaused)playback.Resume();else playback.Pause();Refresh();});audio.Children.Add(pauseButton);
        audio.Children.Add(Button("■ Parar","MusicEditorStop",(_,_)=>{operation?.Cancel();playback.Stop();status.Text="Reprodução parada.";}));
        speed=new ComboBox{Name="MusicEditorSpeed",Width=95,ItemsSource=new[]{.5,.75,1.0},SelectedItem=1.0,ItemStringFormat="{0:P0}",Margin=new(0,0,8,8)};audio.Children.Add(speed);
        seek=new Slider{Name="MusicEditorSeek",Width=220,Minimum=0,Maximum=1,Margin=new(0,4,8,8)};audio.Children.Add(seek);
        audioClock=new TextBlock{Text="0:00 / 0:00",VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,0,8)};audio.Children.Add(audioClock);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};footer.Children.Add(actions);
        previewButton=Button("Atualizar prévia","MusicEditorPreview",async(_,_)=>await Prepare());actions.Children.Add(previewButton);
        actions.Children.Add(Button("Fechar","MusicEditorClose",(_,_)=>Close()));
        saveButton=Button("Salvar nova versão","MusicEditorSave",async(_,_)=>await Save(),true);actions.Children.Add(saveButton);
        staff.SelectionChanged+=(_,_)=>{if(staff.SelectedItem is int s){measure.ItemsSource=model.Measures.Where(m=>m.Staff==s).ToArray();measure.SelectedIndex=0;}};
        measure.SelectionChanged+=(_,_)=>SelectMeasure();
        speed.SelectionChanged+=(_,_)=>{playback.Stop();status.Text="Clique em Ouvir partitura para usar a velocidade selecionada.";};
        seek.ValueChanged+=(_,_)=>{if(!updatingSeek&&playback.HasAudio)playback.Seek(seek.Value);};
        playback.StateChanged+=PlaybackChanged;
        playback.PlaybackFailed+=PlaybackFailed;
        timer.Tick+=(_,_)=>Tick();timer.Start();
        Closing+=OnClosing;
        Closed+=(_,_)=>{closed=true;operation?.Cancel();timer.Stop();playback.StateChanged-=PlaybackChanged;playback.PlaybackFailed-=PlaybackFailed;playback.Dispose();};
        staff.SelectedIndex=0;Refresh();
    }

    private static Button Button(string text,string name,RoutedEventHandler action,bool primary=false)
    { var button=DialogUi.Button(text,action,primary);button.Name=name;return button; }
    private static DataGrid MakeGrid(string name)=>new(){Name=name,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,
        HeadersVisibility=DataGridHeadersVisibility.Column,SelectionMode=DataGridSelectionMode.Single,RowHeight=double.NaN,MinRowHeight=44,IsReadOnly=true};
    private static DataGridTextColumn Text(string label,string path,int width)=>new(){Header=label,Binding=new Binding(path),Width=width};
    private static DataGridTemplateColumn Input(string label,string path,string enabled,int width)
    {
        var factory=new FrameworkElementFactory(typeof(TextBox));
        factory.SetBinding(TextBox.TextProperty,new Binding(path){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
        factory.SetBinding(IsEnabledProperty,new Binding(enabled));factory.SetBinding(ToolTipProperty,new Binding("Restriction"));
        factory.SetValue(TextBox.MaxLengthProperty,12);factory.SetValue(MarginProperty,new Thickness(3));
        return new(){Header=label,Width=width,CellTemplate=new DataTemplate{VisualTree=factory}};
    }
    private static DataGridTemplateColumn Options(string label,string path,string enabled,Choice[] choices,int width)
    {
        var factory=new FrameworkElementFactory(typeof(ComboBox));
        factory.SetValue(ItemsControl.ItemsSourceProperty,choices);factory.SetValue(ItemsControl.DisplayMemberPathProperty,nameof(Choice.Label));
        factory.SetValue(System.Windows.Controls.Primitives.Selector.SelectedValuePathProperty,nameof(Choice.Value));
        factory.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,new Binding(path){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
        factory.SetBinding(IsEnabledProperty,new Binding(enabled));factory.SetBinding(ToolTipProperty,new Binding("Restriction"));factory.SetValue(MarginProperty,new Thickness(3));
        return new(){Header=label,Width=width,CellTemplate=new DataTemplate{VisualTree=factory}};
    }
    private void SelectMeasure()
    {
        playback.Stop();
        if(measure.SelectedItem is not MusicMeasure m)return;
        noteGrid.ItemsSource=notes.Where(n=>n.Event.Staff==m.Staff&&n.Event.Measure==m.Number).ToArray();
        rhythmGrid.ItemsSource=rhythms.Where(n=>n.Event.Staff==m.Staff&&n.Event.Measure==m.Number).ToArray();
        harmonyGrid.ItemsSource=harmonies.Where(n=>n.Harmony.Staff==m.Staff&&n.Harmony.Measure==m.Number).ToArray();
    }
    private DraftState Capture()=>new(
        notes.Where(n=>n.Value!=n.Note.Name).ToDictionary(n=>n.Note.Id,n=>n.Value),
        rhythms.Where(r=>r.Duration!=r.Event.DurationType||r.Dots!=r.Event.Dots).ToDictionary(r=>r.Event.Id,r=>new RhythmValue(r.Duration,r.Dots)),
        harmonies.Where(h=>h.Root!=h.Harmony.Root||h.Quality!=h.Harmony.Quality||(h.Harmony.HasDiagram&&h.Inversion!=h.Harmony.DiagramInversion))
            .ToDictionary(h=>h.Harmony.Id,h=>new HarmonyValue(h.Root,h.Quality,h.Inversion)));
    private void Changed()
    {
        if(restoring||closed)return;
        undo.Add(current);if(undo.Count>100)undo.RemoveAt(0);redo.Clear();current=Capture();Invalidate();
    }
    private void Invalidate()
    {
        revision++;prepared=null;preparedRevision=-1;playback.Stop();
        if(!HasChanges)viewer.FirstPagePath=originalPreview;
        previewStatus.Text=HasChanges?"Prévia anterior · atualize para conferir o rascunho.":"Partitura salva · nenhuma alteração no rascunho.";
        status.Text=HasChanges?"Rascunho alterado. Atualize a prévia antes de salvar.":"Rascunho igual à partitura salva.";Refresh();
    }
    private void Restore(DraftState state)
    {
        restoring=true;
        try
        {
            foreach(var row in notes)row.Value=state.Notes.GetValueOrDefault(row.Note.Id,row.Note.Name);
            foreach(var row in rhythms){var value=state.Rhythms.GetValueOrDefault(row.Event.Id,new(row.Event.DurationType,row.Event.Dots));row.Duration=value.Duration;row.Dots=value.Dots;}
            foreach(var row in harmonies){var value=state.Harmonies.GetValueOrDefault(row.Harmony.Id,new(row.Harmony.Root,row.Harmony.Quality,row.Harmony.DiagramInversion??2));row.Root=value.Root;row.Quality=value.Quality;row.Inversion=value.Inversion;}
        }
        finally{restoring=false;}
        current=Capture();Invalidate();
    }
    private void Undo(){if(operation is not null||undo.Count==0)return;redo.Add(current);var state=undo[^1];undo.RemoveAt(undo.Count-1);Restore(state);}
    private void Redo(){if(operation is not null||redo.Count==0)return;undo.Add(current);var state=redo[^1];redo.RemoveAt(redo.Count-1);Restore(state);}
    private NativeMusicEdits BuildEdits()
    {
        var changes=new List<MusicNoteChange>();
        foreach(var row in notes.Where(n=>current.Notes.ContainsKey(n.Note.Id)))
        {
            if(!MusicRowLabels.ParsePitch(row.Value,out int pitch,out int tpc))throw new InvalidOperationException($"Pauta {row.Event.Staff}, compasso {row.Event.Measure}: use uma nota como C4, F#4 ou Bb3.");
            if(pitch!=row.Note.Pitch||tpc!=row.Note.Tpc)changes.Add(new(row.Note.Id,pitch,tpc));
        }
        return new(changes,current.Rhythms.Select(p=>new MusicDurationChange(p.Key,p.Value.Duration,p.Value.Dots)).ToArray(),
            harmonies.Where(h=>current.Harmonies.ContainsKey(h.Harmony.Id)).Select(h=>new MusicHarmonyChange(h.Harmony.Id,h.Root,h.Quality,h.Harmony.HasDiagram?h.Inversion:null)).ToArray());
    }
    private IProgress<string> Progress()
    {
        var request=operation;
        return new Progress<string>(message=>{if(!closed&&operation==request&&request is not null&&!request.IsCancellationRequested){operationStage=message;Tick();}});
    }
    private async Task PrepareCurrent(CancellationToken token)
    {
        if(prepared is not null&&preparedRevision==revision)return;
        var edits=BuildEdits();
        var errors=await Task.Run(()=>NativeMusicEditor.Validate(model,edits),token);token.ThrowIfCancellationRequested();
        if(errors.Count>0)throw new InvalidOperationException(string.Join("\n",errors.Take(5)));
        int expected=revision;
        var result=await prepare(edits,Progress(),token);token.ThrowIfCancellationRequested();
        if(closed||expected!=revision)throw new OperationCanceledException(token);
        prepared=result;preparedRevision=revision;viewer.FirstPagePath=result.PreviewPages.FirstOrDefault();
        previewStatus.Text="Prévia atual do rascunho · confira todas as páginas antes de salvar.";
        status.Text="Prévia atualizada. Você pode ouvir o rascunho ou salvar uma nova versão.";
    }
    private Task Prepare()=>Run("Atualizando a prévia",PrepareCurrent);
    private Task Play()=>Run("Preparando reprodução do rascunho",async token=>
    {
        if(HasChanges)await PrepareCurrent(token);
        else
        {
            await using var stream=File.OpenRead(model.SourcePath);
            if(Convert.ToHexString(await SHA256.HashDataAsync(stream,token))!=model.SourceSha256)
                throw new IOException("A partitura mudou desde a abertura do editor. Reabra a versão atual antes de ouvir.");
        }
        await playback.PlayAsync(prepared is not null&&preparedRevision==revision?prepared.Path:model.SourcePath,
            speed.SelectedItem is double rate?rate:1.0,Progress(),token);
        token.ThrowIfCancellationRequested();status.Text="Reproduzindo a partitura inteira. O seletor de compasso controla a edição.";
    });
    private Task Save()=>Run("Salvando nova versão",async token=>
    {
        if(!HasChanges||prepared is null||preparedRevision!=revision)throw new InvalidOperationException("Atualize a prévia antes de salvar.");
        playback.Stop();Result=await publish(prepared,Progress(),token);
        discardConfirmed=true;DialogResult=true;
    });
    private async Task Run(string title,Func<CancellationToken,Task> action)
    {
        if(operation is not null||closed)return;
        var request=new CancellationTokenSource();operation=request;operationStage=title;elapsed.Restart();Refresh();
        try{await Dispatcher.Yield(DispatcherPriority.Background);request.Token.ThrowIfCancellationRequested();await action(request.Token);}
        catch(OperationCanceledException){if(!closed){playback.Stop();status.Text="Operação cancelada. Seu rascunho continua disponível.";}}
        catch(Exception error){if(!closed){playback.Stop();status.Text=error.Message;}}
        finally{if(operation==request)operation=null;request.Dispose();elapsed.Stop();if(!closed){Refresh();if(pendingClose){pendingClose=false;Close();}}}
    }
    private void PlaybackChanged(object? sender,EventArgs e){if(!closed){Refresh();Tick();}}
    private void PlaybackFailed(object? sender,string message){if(!closed)status.Text="Não foi possível ouvir a partitura. "+message;}
    private void Refresh()
    {
        bool idle=operation is null;
        staff.IsEnabled=measure.IsEnabled=tabs.IsEnabled=speed.IsEnabled=idle;
        undoButton.IsEnabled=idle&&undo.Count>0;redoButton.IsEnabled=idle&&redo.Count>0;
        previewButton.IsEnabled=idle&&HasChanges;playButton.IsEnabled=idle;
        pauseButton.IsEnabled=idle&&playback.HasAudio;pauseButton.Content=playback.IsPaused?"Continuar":"Pausar";
        saveButton.IsEnabled=idle&&HasChanges&&prepared is not null&&preparedRevision==revision;
        progressPanel.Visibility=idle?Visibility.Collapsed:Visibility.Visible;
        seek.IsEnabled=idle&&playback.HasAudio;
    }
    private void Tick()
    {
        if(closed)return;
        if(operation is not null)operationText.Text=$"{operationStage} · {elapsed.Elapsed:mm\\:ss}";
        updatingSeek=true;try{seek.Maximum=Math.Max(1,playback.DurationSeconds);seek.Value=playback.PositionSeconds;}finally{updatingSeek=false;}
        audioClock.Text=$"{TimeSpan.FromSeconds(playback.PositionSeconds):m\\:ss} / {TimeSpan.FromSeconds(playback.DurationSeconds):m\\:ss}";
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(discardConfirmed||Result is not null)return;
        if(operation is not null){e.Cancel=true;pendingClose=true;operation.Cancel();playback.Stop();status.Text="Cancelando antes de fechar…";return;}
        if(HasChanges&&MessageBox.Show(this,"Descartar as alterações do rascunho e fechar?","Editor musical",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)e.Cancel=true;
    }
}
