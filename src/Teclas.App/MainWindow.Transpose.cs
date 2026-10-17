using System.IO;
using System.Windows;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanTransposeScore=>CanEditMidi || IsIdle && selectedFile?.UsesNativeScoreWorkflow == true && SelectedScoreExists;
    private async void TransposeScoreClick(object sender,RoutedEventArgs e)
    {
        if(!CanTransposeScore || selectedFile is not {} row)return;
        if(row.UsesNativeScoreWorkflow) { await TransposeNativeScore(row); return; }
        if(!CommitEdits())return;
        var dialog=new TransposeWindow(row.TransposeSemitones,row.Chords.Count>0?row.KeyFifths:null,row.KeyMinor){Owner=this};
        if(dialog.ShowDialog()!=true || dialog.Semitones==row.TransposeSemitones)return;
        RememberVersion(row);
        row.TransposeSemitones=dialog.Semitones;row.HasPendingScoreProperties=true;
        ResetPlayback();RefreshSelection();PersistPreferences();
        if(!row.UseReviewedProfile && !row.Tracks.Any(t=>t.IsMelody)){Status="Transposição salva. Escolha a melodia e analise os acordes para gerar.";return;}
        await RunOperation(async()=>
        {
            await Generate(row);EditorTabs.SelectedIndex=2;
        },row,"Transpondo partitura");
    }

    private async Task TransposeNativeScore(MidiRow row)
    {
        if(row.ScorePath is not {} path || !SelectedScoreExists)return;
        if(!File.Exists(MuseScorePath)){Status="Use Preparar este computador em Configurações para localizar o MuseScore Studio 4.";return;}
        StopTrackAudition();ResetPlayback();
        TranspositionPlan? initial=null;
        await RunOperation(async()=>
        {
            var token=cancellation!.Token;
            var opened=await Task.Run(()=>NativeScoreTransposition.CreatePlan(path,new(0),token),token);
            token.ThrowIfCancellationRequested();
            initial=opened;
        },operationTitle:"Conferindo a partitura para transposição",operationItem:row.ScoreTitle);
        if(operationsDisposed || initial is null || selectedFile!=row || row.ScorePath!=path)return;
        string cache=Path.Combine(LocalJobsDirectory,"transposition");
        var service=new NativeScoreTranspositionService(MuseScorePath,cache);
        using var audio=new EditorScorePlayback(MuseScorePath,Path.Combine(cache,"audio"));
        var dialog=new NativeTransposeWindow(initial,row.PreviewPath,
            (options,progress,token)=>Task.Run(()=>NativeScoreTransposition.CreatePlan(path,options,token),token),
            (plan,progress,token)=>service.PrepareAsync(plan,progress,token),
            async (plan,prepared,progress,token)=>await service.PublishAsync(plan,prepared,await PrepareOutputDirectoryAsync(token),progress,token),audio){Owner=this};
        if(dialog.ShowDialog()==true && dialog.Result is {} result)
        {
            ApplyMusicalEditorResult(row,result);
            row.Status="Transposição salva · "+row.PageCountText;PersistPreferences();
            Status="Transposição salva como nova versão. A anterior continua no Histórico.";
        }
    }
}

public sealed class TransposeWindow:Window
{
    private readonly ComboBox steps;
    public int Semitones=>(int)(steps.SelectedValue??0);
    internal ComboBox? TargetKey { get; private set; }
    public TransposeWindow(int current) : this(current, null, false) { }

    /// <summary>With the analyzed key, a target key sets the shortest interval (−6 to +5 semitones).</summary>
    public TransposeWindow(int current, int? keyFifths, bool keyMinor)
    {
        Title="Transpor partitura";Width=510;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;DialogUi.Theme(this);
        var panel=new StackPanel { Margin=new(24) };Content=panel;
        panel.Children.Add(new TextBlock { Text="Mude o tom para tocar com conforto",FontSize=20,TextWrapping=TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text="Melodia, armadura, cifras e diagramas serão atualizados juntos. O intervalo é sempre relativo ao MIDI original; 0 retorna ao tom original.",TextWrapping=TextWrapping.Wrap,Margin=new(0,14,0,18) });
        steps=new ComboBox { ItemsSource=Enumerable.Range(-12,25).Select(n=>new {Value=n,Label=n==0?"0 · tom original":$"{n:+0;-0} semitons"}).ToArray(),DisplayMemberPath="Label",SelectedValuePath="Value",SelectedValue=current };
        if (keyFifths is int fifths and >= -7 and <= 7)
        {
            int original = (((fifths * 7) % 12 + 12) % 12 + (keyMinor ? 9 : 0)) % 12;
            string[] names = ["Dó", "Dó♯/Ré♭", "Ré", "Mi♭", "Mi", "Fá", "Fá♯/Sol♭", "Sol", "Lá♭", "Lá", "Si♭", "Si"];
            string mode = keyMinor ? " menor" : " maior";
            var keys = Enumerable.Range(0, 12).Select(pc => new { Value = pc, Label = names[pc] + mode + (pc == original ? " · original" : "") }).ToArray();
            panel.Children.Add(new TextBlock { Text = "Tom desejado (escolha e confira os semitons abaixo)", Margin = new(0, 0, 0, 6) });
            var target = new ComboBox { Name = "TargetKeyCombo", ItemsSource = keys, DisplayMemberPath = "Label", SelectedValuePath = "Value",
                SelectedValue = ((original + current) % 12 + 12) % 12, Margin = new(0, 0, 0, 12) };
            System.Windows.Automation.AutomationProperties.SetName(target, "Tom desejado");
            target.SelectionChanged += (_, _) =>
            {
                if (target.SelectedValue is not int wanted) return;
                int delta = ((wanted - original) % 12 + 12) % 12;
                steps.SelectedValue = delta > 5 ? delta - 12 : delta;
            };
            panel.Children.Add(target);
            TargetKey = target;
        }
        panel.Children.Add(steps);
        panel.Children.Add(new TextBlock { Text="A tabela de cifras permanece no tom de origem para permitir novas transposições sem acumular alterações. A nova versão fica no Histórico.",TextWrapping=TextWrapping.Wrap,Margin=new(0,14,0,18) });
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        var cancel=DialogUi.Button("Cancelar",(_,_)=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
        buttons.Children.Add(DialogUi.Button("Aplicar e atualizar",(_,_)=>DialogResult=true,true));panel.Children.Add(buttons);
    }
}
