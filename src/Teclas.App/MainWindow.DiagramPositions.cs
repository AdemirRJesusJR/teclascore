using System.IO;
using System.Windows;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanCustomizeDiagrams=>IsIdle && selectedFile is { UsesNativeScoreWorkflow:false } row && row.Chords.Count>0;
    private async void DiagramPositionsClick(object sender,RoutedEventArgs e)
    {
        if(!CanCustomizeDiagrams || selectedFile is not {} row || !CommitEdits())return;
        var dialog=new DiagramPositionsWindow(row,settings.ComfortableDiagramMaximumSpan){Owner=this};
        if(dialog.ShowDialog()!=true)return;
        settings.ComfortableDiagramMaximumSpan=dialog.ComfortableMaximumSpan;
        PersistPreferences();
        await ApplyDiagramPositions(row,dialog.Inversions);
    }
    private async Task ApplyDiagramPositions(MidiRow row,IReadOnlyList<int?> values)
    {
        RequireMidiWorkflow(row);
        if(values.Count!=row.Chords.Count || values.Any(value=>value is <0 or >2))
            throw new ArgumentException("Escolha uma posição válida para cada acorde.");
        if(row.Chords.Select(c=>c.DiagramInversion).SequenceEqual(values) && !row.HasPendingScoreProperties)return;
        RememberVersion(row);
        for(int index=0;index<values.Count;index++)row.Chords[index].DiagramInversion=values[index];
        row.HasPendingScoreProperties=true;
        ResetPlayback();RefreshSelection();PersistPreferences();
        if(row.Document is null){Status="Posições salvas. Reconecte o MIDI original para atualizar os diagramas.";return;}
        if(row.AnalysisPath is null && !row.UseReviewedProfile && !row.Tracks.Any(t=>t.IsMelody))
        {Status="Posições salvas. Escolha a melodia e analise os acordes para gerar.";return;}
        await RunOperation(async()=>
        {
            await Generate(row);EditorTabs.SelectedIndex=2;
        },row,"Atualizando diagramas de acordes");
    }
}
