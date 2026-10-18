using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private string LocalJobsDirectory=>Path.Combine(persistencePath is null?Settings.DataDirectory:Path.GetDirectoryName(Path.GetFullPath(persistencePath))!,"native-scores");
    public string GenerateButtonLabel=>selectedFile?.UsesNativeScoreWorkflow==true?"Melhorar apresentação →":"Gerar partitura →";
    private MidiRow LoadNativeScore(string path)
    {
        path=Path.GetFullPath(path);
        var metadata=NativeScoreDocument.Inspect(path);
        using var stream=File.OpenRead(path);
        return new MidiRow{FullPath=path,NativeSourceSha256=Convert.ToHexString(SHA256.HashData(stream)),ScorePath=path,
            ScoreTitle=metadata.Title,ScoreSubtitle=metadata.Subtitle,ScoreComposer=metadata.Composer,ScoreArtist=metadata.Artist,ArrangementSummary=metadata.ArrangementSummary,
            DefaultDiagramInversion=settings.DefaultDiagramInversion,Status="Partitura MuseScore importada",GeneratedAt=File.GetLastWriteTime(path)};
    }
    private async Task PrepareNativePreview(MidiRow row)
    {
        var service=new ScoreEditingService(MuseScorePath,LocalJobsDirectory);
        NativeScoreResult result;
        try
        {
            result=await service.ImportAsync(row.FullPath,LocalJobsDirectory,CreateOperationProgress(),cancellation!.Token);
        }
        catch(OperationCanceledException){throw;}
        catch
        {
            // A missing embedded image can prevent the preview while the original remains
            // readable. Keep that useful report without replacing the primary import error.
            try{await DiagnoseSavedScore(row);}
            catch(OperationCanceledException){throw;}
            catch(Exception){/* The preview failure remains the actionable error. */}
            throw;
        }
        row.ScorePath=result.Path;row.PreviewPath=result.PreviewPages.FirstOrDefault();row.PageCount=result.PreviewPages.Count;
        row.HasOperationError=false;row.Status="Importada · "+row.PageCountText;RememberVersion(row);
        PersistPreferences();
        await DiagnosePublishedNativeScore(row,row.Status,"Partitura importada. O diagnóstico está disponível junto à prévia.");
    }
    private async Task DiagnosePublishedNativeScore(MidiRow row,string completionStatus,string completionMessage)
    {
        // The copy and its version history have already been committed. A secondary read-only
        // diagnostic must never turn that successful conversion into a failure or discard it.
        row.DiagnosticReport=null;
        try
        {
            await DiagnoseSavedScore(row);
            if(row.DiagnosticReport is null)throw new IOException("A cópia salva não está disponível para leitura.");
            Status=completionMessage;
        }
        catch(OperationCanceledException)
        {
            row.Status=completionStatus+" · diagnóstico cancelado";
            Status="A partitura foi salva; somente o diagnóstico foi cancelado. Você pode executá-lo novamente pela prévia.";
        }
        catch(Exception error)
        {
            row.Status=completionStatus+" · diagnóstico pendente";
            Status="A partitura foi salva, mas o diagnóstico não terminou: "+error.Message;
        }
    }
    private async void ImproveNativeScoreClick(object sender,RoutedEventArgs e)
    {
        if(!IsIdle || selectedFile is not{UsesNativeScoreWorkflow:true,ScorePath:{} score} row)return;
        NativeLayoutWindow dialog;
        try{dialog=new NativeLayoutWindow(NativeScoreDocument.Inspect(score)){Owner=this};}
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException){Status=error.Message;return;}
        if(dialog.ShowDialog()!=true)return;
        RememberVersion(row);
        await RunOperation(async()=>
        {
            string outputDirectory = await PrepareOutputDirectoryAsync(cancellation!.Token);
            ResetPlayback();
            var result=await new ScoreEditingService(MuseScorePath,LocalJobsDirectory).ImproveAsync(score,outputDirectory,dialog.Options,CreateOperationProgress(),cancellation!.Token);
            row.ScorePath=result.Path;row.PreviewPath=result.PreviewPages.FirstOrDefault();row.PageCount=result.PreviewPages.Count;row.GeneratedAt=DateTimeOffset.Now;
            row.ScoreTitle=result.Metadata.Title;row.ScoreSubtitle=result.Metadata.Subtitle;row.ScoreComposer=result.Metadata.Composer;row.ScoreArtist=result.Metadata.Artist;row.ArrangementSummary=result.Metadata.ArrangementSummary;
            row.HasPendingScoreProperties=false;row.HasOperationError=false;row.Status="Apresentação atualizada · "+row.PageCountText;
            RememberVersion(row);EditorTabs.SelectedIndex=2;PersistPreferences();
            await DiagnosePublishedNativeScore(row,row.Status,"Nova cópia salva e diagnosticada. Abra o Histórico para comparar com a original.");
        },row,"Melhorando apresentação da partitura");
        PersistPreferences();
    }
    private void UpdatesClick(object sender,RoutedEventArgs e)
    {
        if(!IsIdle)return;
        new UpdateWindow(PythonPath,MuseScorePath,ApplyDependencyPaths){Owner=this}.ShowDialog();
    }

    private void DependencySetupClick(object sender,RoutedEventArgs e)
    {
        if(!IsIdle)return;
        var dialog=new DependencySetupWindow(PythonPath,MuseScorePath){Owner=this};
        if(dialog.ShowDialog()==true)ApplyDependencyPaths(dialog.SelectedPythonPath,dialog.SelectedMuseScorePath);
    }

    private void ApplyDependencyPaths(string python,string museScore)
    {
        PythonPath=python;MuseScorePath=museScore;PersistPreferences();
        Status="Ferramentas verificadas e caminhos salvos. O computador está preparado para gerar e reproduzir partituras.";
    }
    private async void ImportMuseScoreClick(object sender,RoutedEventArgs e)
    {
        if(!IsIdle || !CommitEdits())return;
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="Abrir partituras MuseScore ou MusicXML",Filter="Partituras (MuseScore e MusicXML)|*.mscz;*.musicxml;*.mxl;*.xml|Partitura MuseScore 4|*.mscz|MusicXML|*.musicxml;*.mxl;*.xml",Multiselect=true};
        if(dialog.ShowDialog(this)!=true)return;
        var files=await ConvertMusicXmlAsync(dialog.FileNames);
        if(files.Count>0)await ImportAndOpenLibraryAsync(files);
    }

    internal Func<string,string,CancellationToken,Task>? musicXmlConverter=null;

    /// <summary>
    /// MusicXML from other editors is converted by MuseScore into a new .mscz in the output folder (the original is kept),
    /// then imported like any MuseScore score. Keyboard diagrams are added when the score is improved or regenerated.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ConvertMusicXmlAsync(IReadOnlyList<string> paths)
    {
        var result=paths.Where(path=>Path.GetExtension(path).Equals(".mscz",StringComparison.OrdinalIgnoreCase)).ToList();
        var xml=paths.Except(result).ToArray();
        if(xml.Length==0)return result;
        await RunOperation(async()=>
        {
            string folder=!string.IsNullOrWhiteSpace(OutputDirectory)?OutputDirectory:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Partituras geradas");
            Directory.CreateDirectory(folder);
            var taken=new HashSet<string>(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(),StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<xml.Length;i++)
            {
                SetOperationItems(i,xml.Length,Path.GetFileName(xml[i]));Status=$"Convertendo {Path.GetFileName(xml[i])} para MuseScore…";
                string target=Path.Combine(folder,ScoreFileName.Unique(ScoreFileName.Stem(null,Path.GetFileNameWithoutExtension(xml[i])),".mscz",taken));
                try
                {
                    if(musicXmlConverter is {} convert)await convert(xml[i],target,cancellation!.Token);
                    else await new ScoreExporter(MuseScorePath,PlaybackCacheDirectory).ExportNativeAsync(xml[i],target,cancellation!.Token);
                    result.Add(target);
                }
                catch(Exception error) when (error is InvalidOperationException or IOException or InvalidDataException or System.Xml.XmlException)
                { Status=$"Não foi possível converter {Path.GetFileName(xml[i])}: {error.Message}"; }
            }
        },operationTitle:"Convertendo MusicXML");
        return result;
    }
}
