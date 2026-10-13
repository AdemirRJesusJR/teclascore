using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanTransposeScore=>IsIdle && selectedFile?.Document is not null;
    private async void TransposeScoreClick(object sender,RoutedEventArgs e)
    {
        if(!CanTransposeScore || selectedFile is not {} row || !CommitEdits())return;
        var dialog=new TransposeWindow(row.TransposeSemitones){Owner=this};
        if(dialog.ShowDialog()!=true || dialog.Semitones==row.TransposeSemitones)return;
        RememberVersion(row);
        row.TransposeSemitones=dialog.Semitones;row.HasPendingScoreProperties=true;
        ResetPlayback();RefreshSelection();PersistPreferences();
        if(!row.UseReviewedProfile && !row.Tracks.Any(t=>t.IsMelody)){Status="Transposição salva. Escolha a melodia e analise os acordes para gerar.";return;}
        await RunOperation(async()=>
        {
            if(row.AnalysisPath is null || !File.Exists(row.AnalysisPath))
            {
                var chords=row.Chords.ToArray();int fifths=row.KeyFifths;bool minor=row.KeyMinor;
                await Analyze(row);
                if(chords.Length>0){row.Chords.Clear();foreach(var chord in chords)row.Chords.Add(chord);row.KeyFifths=fifths;row.KeyMinor=minor;}
            }
            await Generate(row);EditorTabs.SelectedIndex=2;
        },row);
    }
}

public sealed class TransposeWindow:Window
{
    private readonly ComboBox steps;
    public int Semitones=>(int)(steps.SelectedValue??0);
    public TransposeWindow(int current)
    {
        Title="Transpor partitura";Width=510;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;DialogUi.Theme(this);
        var panel=new StackPanel { Margin=new(24) };Content=panel;
        panel.Children.Add(new TextBlock { Text="Mude o tom para tocar com conforto",FontSize=20,TextWrapping=TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text="Melodia, armadura, cifras e diagramas serão atualizados juntos. O intervalo é sempre relativo ao MIDI original; 0 retorna ao tom original.",TextWrapping=TextWrapping.Wrap,Margin=new(0,14,0,18) });
        steps=new ComboBox { ItemsSource=Enumerable.Range(-12,25).Select(n=>new {Value=n,Label=n==0?"0 · tom original":$"{n:+0;-0} semitons"}).ToArray(),DisplayMemberPath="Label",SelectedValuePath="Value",SelectedValue=current };
        panel.Children.Add(steps);
        panel.Children.Add(new TextBlock { Text="A tabela de cifras permanece no tom de origem para permitir novas transposições sem acumular alterações. A nova versão fica no Histórico.",TextWrapping=TextWrapping.Wrap,Margin=new(0,14,0,18) });
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        var cancel=DialogUi.Button("Cancelar",(_,_)=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
        buttons.Children.Add(DialogUi.Button("Aplicar e atualizar",(_,_)=>DialogResult=true,true));panel.Children.Add(buttons);
    }
}
