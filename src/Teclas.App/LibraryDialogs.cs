using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Teclas.App;

internal static class DialogUi
{
    [DllImport("dwmapi.dll",ExactSpelling=true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd,uint attribute,ref uint value,uint size);
    internal static void Theme(Window window)
    {
        window.SetResourceReference(Window.BackgroundProperty,"Background");
        window.SetResourceReference(Window.ForegroundProperty,"Ink");
        window.FontFamily=new("Segoe UI"); window.FontSize=13;
        window.WindowStartupLocation=WindowStartupLocation.CenterOwner;
        window.SourceInitialized+=(_,_)=>
        {
            var handle=new WindowInteropHelper(window).Handle;
            foreach(var (attribute,key) in new[]{(35u,"Background"),(36u,"Ink"),(34u,"Line")})
                if(window.TryFindResource(key) is SolidColorBrush brush){uint color=(uint)(brush.Color.R | brush.Color.G<<8 | brush.Color.B<<16);DwmSetWindowAttribute(handle,attribute,ref color,sizeof(uint));}
        };
    }
    internal static TextBox Field(Panel panel,string label,string value,int maximum=200)
    {
        panel.Children.Add(new TextBlock { Text=label,Margin=new(0,12,0,6) });
        var box=new TextBox { Text=value,MaxLength=maximum };panel.Children.Add(box);return box;
    }
    internal static Button Button(string text,RoutedEventHandler click,bool primary=false)
    {
        var button=new Button { Content=text,Margin=new(4),Padding=new(14,8,14,8) };
        if(primary)button.SetResourceReference(FrameworkElement.StyleProperty,"PrimaryButton");
        button.Click+=click;return button;
    }
}

public sealed class ScoreOrganizationWindow : Window
{
    private readonly CheckBox favorite;
    private readonly TextBox collection,genre,tags;
    public bool Favorite=>favorite.IsChecked==true;
    public string Collection=>collection.Text.Trim();
    public string Genre=>genre.Text.Trim();
    public string Tags=>string.Join(", ",tags.Text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));
    public ScoreOrganizationWindow(MidiRow row)
    {
        Title="Organizar música";Width=470;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;DialogUi.Theme(this);
        var panel=new StackPanel { Margin=new(24) };Content=panel;
        panel.Children.Add(new TextBlock { Text=row.ScoreTitle,FontSize=20,TextWrapping=TextWrapping.Wrap });
        favorite=new CheckBox { Content="Adicionar aos favoritos",IsChecked=row.IsFavorite,Margin=new(0,20,0,6) };panel.Children.Add(favorite);
        collection=DialogUi.Field(panel,"Coleção / repertório",row.Collection,80);
        genre=DialogUi.Field(panel,"Gênero musical",row.Genre,80);
        tags=DialogUi.Field(panel,"Etiquetas, separadas por vírgula (busque com #etiqueta)",row.Tags,200);
        panel.Children.Add(new TextBlock { Text="Use o mesmo nome de coleção para reunir músicas de um show, artista ou projeto.",TextWrapping=TextWrapping.Wrap,Margin=new(0,14,0,14) });
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        var cancel=DialogUi.Button("Cancelar",(_,_)=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
        buttons.Children.Add(DialogUi.Button("Salvar",(_,_)=>DialogResult=true,true));panel.Children.Add(buttons);
    }
}

public sealed class ScoreHistoryWindow : Window
{
    private readonly ListBox list;
    private readonly ScoreViewer previous;
    private readonly Button restore;
    public SavedScoreVersion? SelectedVersion=>list.SelectedItem as SavedScoreVersion;
    public ScoreHistoryWindow(MidiRow row)
    {
        Title="Histórico · "+row.ScoreTitle;Width=1180;Height=800;MinWidth=900;MinHeight=600;DialogUi.Theme(this);
        var root=new DockPanel { Margin=new(20) };Content=root;
        var heading=new TextBlock { Text="Compare com a versão atual. Restaurar muda a versão ativa; os arquivos continuam preservados.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12) };DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        var footer=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(DialogUi.Button("Fechar",(_,_)=>DialogResult=false));
        restore=DialogUi.Button("Restaurar versão selecionada",(_,_)=>DialogResult=true,true);restore.IsEnabled=false;footer.Children.Add(restore);
        list=new ListBox { ItemsSource=row.Versions.OrderByDescending(v=>v.CreatedAt).ToArray(),DisplayMemberPath="Label",Height=112,Margin=new(0,0,0,12) };DockPanel.SetDock(list,Dock.Top);root.Children.Add(list);
        var views=new Grid();views.ColumnDefinitions.Add(new());views.ColumnDefinitions.Add(new());root.Children.Add(views);
        previous=new ScoreViewer { Margin=new(0,0,8,0) };views.Children.Add(previous);
        var current=new ScoreViewer { FirstPagePath=row.PreviewPath,Margin=new(8,0,0,0) };Grid.SetColumn(current,1);views.Children.Add(current);
        list.SelectionChanged+=(_,_)=>{previous.FirstPagePath=SelectedVersion?.PreviewPath;restore.IsEnabled=SelectedVersion?.IsAvailable==true;};
        if(list.Items.Count>0)list.SelectedIndex=0;
    }
}
