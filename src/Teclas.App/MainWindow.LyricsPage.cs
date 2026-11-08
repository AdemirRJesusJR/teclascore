using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Lyrics options set at start-up: the engine's extended accent list for titles and the optional "Letra e cifras" PDF
/// page. The page's check box is placed next to the other PDF pages in Configurações.
/// </summary>
public partial class MainWindow
{
    public bool PdfLyricsPage
    {
        get => settings.PdfLyricsPage;
        set { if (settings.PdfLyricsPage == value) return; settings.PdfLyricsPage = value; ScoreExporter.AppendLyricsPage = value; Notify(); PersistPreferences(); }
    }

    private void InitializeLyricsOptions()
    {
        PortugueseAccents.UseEngineList(engineDirectory);
        ScoreExporter.AppendLyricsPage = settings.PdfLyricsPage;
        Loaded += (_, _) => AddLyricsPageCheckBox();
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LyricsTrackHint)) Notify(nameof(LyricsTrackActionVisibility)); };
    }

    private void AddLyricsPageCheckBox()
    {
        if (FindName("PdfLyricsPageCheckBox") is CheckBox || FindName("PdfMusicMapCheckBox") is not CheckBox map || map.Parent is not Panel panel) return;
        var box = new CheckBox
        {
            Name = "PdfLyricsPageCheckBox", Content = "Letra e cifras no PDF", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 6),
            ToolTip = "Acrescenta ao PDF uma página com a letra inteira, por versos e estrofes, e cada acorde sobre a sílaba em que começa. Só aparece em músicas com letra."
        };
        AutomationProperties.SetName(box, "Página de letra e cifras no PDF");
        box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(PdfLyricsPage)) { Source = this, Mode = BindingMode.TwoWay });
        panel.Children.Insert(panel.Children.IndexOf(map) + 1, box);
        RegisterName(box.Name, box);
    }
}
