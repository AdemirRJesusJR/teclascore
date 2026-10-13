using System.Windows;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

public sealed class NativeLayoutWindow : Window
{
    private readonly TextBox title,subtitle,composer;
    private readonly ComboBox preset;
    private readonly CheckBox redistribute;
    private readonly NativeScoreMetadata initial;
    public ScoreLayoutOptions Options=>new()
    {
        Title=title.Text.Trim()==initial.Title?null:title.Text.Trim(),
        Subtitle=subtitle.Text.Trim()==initial.Subtitle?null:subtitle.Text.Trim(),
        Composer=composer.Text.Trim()==initial.Composer?null:composer.Text.Trim(),
        MarginMm=preset.SelectedIndex==2?12:16,
        StaffSpaceMm=preset.SelectedIndex==1?2.0:preset.SelectedIndex==2?1.6:1.75,
        MinimumSystemDistance=preset.SelectedIndex==1?14:12,
        TitleFontSize=20,SubtitleFontSize=12,ComposerFontSize=11,RedistributeSystems=redistribute.IsChecked==true
    };
    public NativeLayoutWindow(NativeScoreMetadata metadata)
    {
        initial=metadata;
        Title="Melhorar apresentação";Width=540;Height=650;MinWidth=450;MinHeight=500;DialogUi.Theme(this);
        var panel=new StackPanel{Margin=new(24)};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        panel.Children.Add(new TextBlock{Text="Uma nova cópia, pronta para revisar",FontSize=22,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        title=DialogUi.Field(panel,"Título",metadata.Title,160);
        subtitle=DialogUi.Field(panel,"Subtítulo",metadata.Subtitle,200);
        composer=DialogUi.Field(panel,"Autor / compositor",metadata.Composer,120);
        panel.Children.Add(new TextBlock{Text="Apresentação",Margin=new(0,16,0,6)});
        preset=new ComboBox{ItemsSource=new[]{"Padrão — margens equilibradas","Leitura confortável — pautas maiores","Compacta — pautas menores"},SelectedIndex=0};panel.Children.Add(preset);
        redistribute=new CheckBox{Content="Redistribuir sistemas e páginas",IsEnabled=metadata.ImageCount==0,Margin=new(0,16,0,12)};panel.Children.Add(redistribute);
        if(metadata.ImageCount>0)panel.Children.Add(new TextBlock{Text="As quebras são preservadas para manter diagramas e imagens dentro da página.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)});
        panel.Children.Add(new TextBlock{Text="Notas, ritmos, vozes, cifras e diagramas existentes serão preservados. Compare a nova cópia no Histórico após concluir.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,16)});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(buttons);
        var cancel=DialogUi.Button("Cancelar",(_,_)=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
        buttons.Children.Add(DialogUi.Button("Aplicar em nova cópia",(_,_)=>DialogResult=true,true));
    }
}
