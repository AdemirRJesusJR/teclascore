using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private string LocalJobsDirectory=>Path.Combine(persistencePath is null?Settings.DataDirectory:Path.GetDirectoryName(Path.GetFullPath(persistencePath))!,"native-scores");
    public string GenerateButtonLabel=>selectedFile?.IsNativeScore==true?"Melhorar apresentação →":"Gerar partitura →";
    private MidiRow LoadNativeScore(string path)
    {
        path=Path.GetFullPath(path);
        var metadata=NativeScoreDocument.Inspect(path);
        using var stream=File.OpenRead(path);
        return new MidiRow{FullPath=path,NativeSourceSha256=Convert.ToHexString(SHA256.HashData(stream)),ScorePath=path,
            ScoreTitle=metadata.Title,ScoreSubtitle=metadata.Subtitle,ScoreComposer=metadata.Composer,ScoreArtist=metadata.Artist,
            DefaultDiagramInversion=settings.DefaultDiagramInversion,Status="Partitura MuseScore importada",GeneratedAt=File.GetLastWriteTime(path)};
    }
    private async Task PrepareNativePreview(MidiRow row)
    {
        var service=new ScoreEditingService(MuseScorePath,LocalJobsDirectory);
        var result=await service.ImportAsync(row.FullPath,LocalJobsDirectory,CreateOperationProgress(),cancellation!.Token);
        row.ScorePath=result.Path;row.PreviewPath=result.PreviewPages.FirstOrDefault();row.PageCount=result.PreviewPages.Count;
        row.HasOperationError=false;row.Status="Importada · "+row.PageCountText;RememberVersion(row);
    }
    private async void ImproveNativeScoreClick(object sender,RoutedEventArgs e)
    {
        if(!IsIdle || selectedFile is not{IsNativeScore:true,ScorePath:{} score} row)return;
        NativeLayoutWindow dialog;
        try{dialog=new NativeLayoutWindow(NativeScoreDocument.Inspect(score)){Owner=this};}
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException){Status=error.Message;return;}
        if(dialog.ShowDialog()!=true)return;
        RememberVersion(row);
        await RunOperation(async()=>
        {
            if(!Path.IsPathFullyQualified(OutputDirectory))throw new InvalidOperationException("Escolha uma pasta de saída nas Configurações.");
            ResetPlayback();
            var result=await new ScoreEditingService(MuseScorePath,LocalJobsDirectory).ImproveAsync(score,OutputDirectory,dialog.Options,CreateOperationProgress(),cancellation!.Token);
            row.ScorePath=result.Path;row.PreviewPath=result.PreviewPages.FirstOrDefault();row.PageCount=result.PreviewPages.Count;row.GeneratedAt=DateTimeOffset.Now;
            row.ScoreTitle=result.Metadata.Title;row.ScoreSubtitle=result.Metadata.Subtitle;row.ScoreComposer=result.Metadata.Composer;row.ScoreArtist=result.Metadata.Artist;
            row.HasPendingScoreProperties=false;row.HasOperationError=false;row.Status="Apresentação atualizada · "+row.PageCountText;
            RememberVersion(row);EditorTabs.SelectedIndex=2;Status="Nova cópia salva. Abra o Histórico para comparar com a original.";
        },row,"Melhorando apresentação da partitura");
        PersistPreferences();
    }
    private void UpdatesClick(object sender,RoutedEventArgs e)=>new UpdateWindow(PythonPath,MuseScorePath){Owner=this}.ShowDialog();
    private async void ImportMuseScoreClick(object sender,RoutedEventArgs e)
    {
        if(!IsIdle || !CommitEdits())return;
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="Abrir partituras MuseScore para melhorar",Filter="Partitura MuseScore 4|*.mscz",Multiselect=true};
        if(dialog.ShowDialog(this)==true)await ImportAndOpenLibraryAsync(dialog.FileNames);
    }
}
