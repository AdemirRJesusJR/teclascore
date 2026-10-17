using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    internal Func<DiagramPositionsWindow, bool?> diagramPositionsDialog = window => window.ShowDialog();
    internal Func<MidiRow, Task>? diagramGeneration = null;
    public bool CanCustomizeDiagrams=>IsIdle && selectedFile is { UsesNativeScoreWorkflow:false } row && row.Chords.Count>0;
    private void ChordChartClick(object sender,RoutedEventArgs e)
    {
        if(!CanCustomizeDiagrams || selectedFile is not {} row || !CommitEdits())return;
        new ChordChartWindow(row){Owner=this}.ShowDialog();
    }
    private async void DiagramPositionsClick(object sender,RoutedEventArgs e)
    {
        if(!CanCustomizeDiagrams || selectedFile is not {} row || !CommitEdits())return;
        int others=Files.Count(other=>IsPropagationTarget(other,row) &&
            DiagramPositionPropagation.SharesAnyShape(Entries(row),row.TransposeSemitones,Entries(other),other.TransposeSemitones));
        var dialog=new DiagramPositionsWindow(row,settings.ComfortableDiagramMaximumSpan,others,settings.PropagateDiagramPositions,PreferredInversions(row)){Owner=this};
        if(diagramPositionsDialog(dialog)!=true)return;
        settings.ComfortableDiagramMaximumSpan=dialog.ComfortableMaximumSpan;
        if(others>0)settings.PropagateDiagramPositions=dialog.ApplyToLibrary;
        PersistPreferences();
        await ApplyDiagramPositions(row,dialog.Inversions,others>0 && dialog.ApplyToLibrary);
    }
    private async Task ApplyDiagramPositions(MidiRow row,IReadOnlyList<int?> values,bool applyToLibrary=false)
    {
        RequireMidiWorkflow(row);
        if(values.Count!=row.Chords.Count || values.Any(value=>value is <0 or >2))
            throw new ArgumentException("Escolha uma posição válida para cada acorde.");
        var changes=DiagramPositionPropagation.Changes(Entries(row),values,row.TransposeSemitones);
        bool changed=!row.Chords.Select(c=>c.DiagramInversion).SequenceEqual(values) || row.HasPendingScoreProperties;
        if(changed)
        {
            RememberVersion(row);
            for(int index=0;index<values.Count;index++)row.Chords[index].DiagramInversion=values[index];
            row.HasPendingScoreProperties=true;
        }
        // Other songs receive only the chords changed here; their remaining chords and defaults stay as they were.
        var updated=applyToLibrary && changes.Count>0 ? PropagateDiagramPositions(row,changes) : [];
        // Every chosen position is remembered and used automatically for chords analyzed later.
        if(changes.Count>0)RememberChordPreferences(changes);
        if(!changed && updated.Count==0)return;
        ResetPlayback();RefreshSelection();PersistPreferences();
        var regenerate=new List<MidiRow>();
        if(changed && CanRegenerate(row))regenerate.Add(row);
        regenerate.AddRange(updated.Where(other=>other.ScorePath is not null && CanRegenerate(other)));
        string library=updated.Count==0?"":updated.Count==1?" Também aplicadas a 1 outra música.":$" Também aplicadas a {updated.Count} outras músicas.";
        if(regenerate.Count==0)
        {
            Status=(row.Document is null?"Posições salvas. Reconecte o MIDI original para atualizar os diagramas.":
                changed?"Posições salvas. Escolha a melodia e analise os acordes para gerar.":"Posições salvas.")+library;
            return;
        }
        int done=0,failed=0;
        await RunOperation(async()=>
        {
            foreach(var target in regenerate)
            {
                cancellation!.Token.ThrowIfCancellationRequested();
                SetOperationItems(done,regenerate.Count,target.Name);
                try{await (diagramGeneration?.Invoke(target) ?? Generate(target));}
                catch(OperationCanceledException){throw;}
                catch(Exception error) when(!ReferenceEquals(target,row))
                {
                    // One song failing must not block the others; it keeps the new positions and stays pending.
                    failed++;target.HasOperationError=true;target.Status="Posições salvas · atualização falhou: "+error.Message;
                }
                SetOperationItems(++done,regenerate.Count);
            }
            if(changed && ReferenceEquals(regenerate[0],row))EditorTabs.SelectedIndex=2;
            Status=$"Diagramas atualizados em {done-failed} partitura(s)."+library+(failed>0?$" {failed} não puderam ser atualizadas; confira o status de cada card.":"");
        },row,regenerate.Count==1?"Atualizando diagramas de acordes":$"Atualizando diagramas em {regenerate.Count} partituras");
        PersistPreferences();
    }

    private List<MidiRow> PropagateDiagramPositions(MidiRow source,IReadOnlyList<ChordPositionChange> changes)
    {
        var updated=new List<MidiRow>();
        foreach(var other in Files.Where(other=>IsPropagationTarget(other,source)).ToArray())
        {
            var plan=DiagramPositionPropagation.Plan(Entries(other),other.TransposeSemitones,changes);
            if(plan.Count==0)continue;
            RememberVersion(other);
            foreach(var (index,inversion) in plan)other.Chords[index].DiagramInversion=inversion;
            if(other.ScorePath is not null)other.HasPendingScoreProperties=true;
            updated.Add(other);
        }
        return updated;
    }

    private static bool IsPropagationTarget(MidiRow other,MidiRow source)=>
        !ReferenceEquals(other,source) && !other.UsesNativeScoreWorkflow && other.Chords.Count>0;
    private static ChordPositionEntry[] Entries(MidiRow row)=>
        row.Chords.Select(chord=>new ChordPositionEntry(chord.Root,chord.Quality,chord.DiagramInversion)).ToArray();
    private static bool CanRegenerate(MidiRow row)=>
        row.Document is not null && (row.AnalysisPath is not null || row.UseReviewedProfile || row.Tracks.Any(t=>t.IsMelody));
}
