using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // CI entry point: no personal MIDI, default settings, audio device or MuseScore.
    // Reports contain only evidence; synthetic music and settings live in sibling work/.
    private async void VerifyV018Ui(string reportPath)
    {
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        string report=Path.GetFullPath(reportPath);
        string parent=Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(parent);
        string folder=Path.Combine(Directory.GetParent(parent)!.FullName,"work","ui-"+Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var checks=new Dictionary<string,object>();
        var windows=new List<Window>();
        using var bindingOutput=new StringWriter(CultureInfo.InvariantCulture);
        using var listener=new TextWriterTraceListener(bindingOutput);
        var trace=PresentationTraceSources.DataBindingSource;
        var previousLevel=trace.Switch.Level;trace.Switch.Level=SourceLevels.Warning;trace.Listeners.Add(listener);
        var timeout=new DispatcherTimer { Interval=TimeSpan.FromSeconds(90) };
        timeout.Tick+=(_,_)=>
        {
            File.WriteAllText(report,JsonSerializer.Serialize(new {status="failed",checks,error="UI verification exceeded 90 seconds."}));
            Shutdown(1);
        };
        timeout.Start();
        void Check(bool value,string name)
        {if(!value)throw new InvalidOperationException("v0.18 UI regression: "+name);checks[name]=true;}
        void Flush()=>Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        static object? Invoke(object target,string name,params object?[] arguments)=>target.GetType()
            .GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,arguments);
        static Button Button(Window window,string name)=>(Button)(window.FindName(name)
            ??throw new InvalidOperationException("Missing button: "+name));
        static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        MainWindow Open(string path)
        {
            var window=new MainWindow(true,path){Opacity=0,ShowInTaskbar=false,ShowActivated=false,Width=1360,Height=920};
            windows.Add(window);window.Show();Flush();window.UpdateLayout();
            Check((string?)typeof(MainWindow).GetField("persistencePath",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)==path,
                "explicit-isolated-session-path");
            return window;
        }
        void Select(MainWindow window,MidiRow row)
        {
            window.SearchText="";window.CurrentFilter="all";window.SelectPageForSmoke("library",2);
            ((Selector)window.FindName("Library")).SelectedItem=row;Flush();window.UpdateLayout();
        }
        void Close(Window window)
        {
            bool closed=false;window.Closed+=(_,_)=>closed=true;
            window.Close();Flush();Check(closed,"window-closes-with-isolated-settings");windows.Remove(window);
        }
        void Capture(Window window,string path,int width,int height)
        {
            window.Width=width;window.Height=height;Flush();window.UpdateLayout();
            var content=(FrameworkElement)window.Content;
            int clientWidth=(int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right);
            int clientHeight=(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom);
            var drawing=new DrawingVisual();using(var context=drawing.RenderOpen())
            {
                context.DrawRectangle(window.Background,null,new Rect(0,0,clientWidth,clientHeight));
                context.DrawRectangle(new VisualBrush(content){ViewboxUnits=BrushMappingMode.Absolute,
                    Viewbox=new Rect(content.Margin.Left,content.Margin.Top,content.ActualWidth,content.ActualHeight),Stretch=Stretch.Fill},null,
                    new Rect(content.Margin.Left,content.Margin.Top,content.ActualWidth,content.ActualHeight));
            }
            var bitmap=new RenderTargetBitmap(clientWidth,clientHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(path);encoder.Save(stream);
        }
        void Properties(MainWindow owner,Action<ScorePropertiesWindow> edit,bool save)
        {
            Exception? failure=null;bool visited=false;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>
            {
                var dialog=Windows.OfType<ScorePropertiesWindow>().SingleOrDefault(window=>window.Owner==owner);
                try
                {
                    if(dialog is null)throw new InvalidOperationException("Properties modal did not open.");
                    dialog.Opacity=0;dialog.ShowInTaskbar=false;edit(dialog);visited=true;
                    var button=Button(dialog,save?"SavePropertiesButton":"CancelPropertiesButton");
                    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                }
                catch(Exception ex){failure=ex;dialog?.Close();}
            }));
            Click(Button(owner,"ScorePropertiesButton"));Flush();
            if(failure is not null)throw new InvalidOperationException("Properties modal failed.",failure);
            Check(visited,"properties-modal-exercised");
        }
        try
        {
            string settingsPath=Path.Combine(folder,"settings.json"),unused=Path.Combine(folder,"unused-tool.exe");
            File.WriteAllText(unused,"This synthetic fixture must never execute.");
            new Settings{ThemeId="graphite",PythonPath=unused,MuseScorePath=unused,OutputDirectory=Path.Combine(folder,"output")}.Save(settingsPath);
            string[] sources=[Path.Combine(folder,"Gusttavo Lima - A gente fez amor.mid"),
                Path.Combine(folder,"50 Reais - Naiara Azevedo.mid"),Path.Combine(folder,"Lua Azul - Mar Aberto.mid")];
            for(int index=0;index<sources.Length;index++)File.WriteAllBytes(sources[index],V018Midi((byte)(60+index)));
            var hashes=sources.ToDictionary(path=>path,Hash);
            var window=Open(settingsPath);
            foreach(string path in sources)window.ImportForSmoke(path);
            var first=window.Files[0];var second=window.Files[1];var ambiguous=window.Files[2];
            Check(first.ScoreTitle=="A gente fez amor" && first.ScoreArtist=="Gusttavo Lima"
                && second.ScoreTitle=="50 Reais" && second.ScoreArtist=="Naiara Azevedo","new-import-recognizes-both-filename-orders");
            foreach(var row in window.Files)
            {row.UseReviewedProfile=false;foreach(var track in row.Tracks)track.IsMelody=false;}
            first.ScoreTitle="Gusttavo Lima";first.ScoreArtist="A gente fez amor";first.ScoreComposer="Compositora informada";
            Select(window,first);
            Properties(window,dialog=>
            {
                Check(((Border)dialog.FindName("MetadataSuggestionPanel")).Visibility==Visibility.Visible,"existing-inverted-header-offers-correction");
                Click(Button(dialog,"ApplyMetadataSuggestionButton"));
                Check(dialog.TitleValue=="A gente fez amor" && dialog.ArtistValue=="Gusttavo Lima"
                    && dialog.ComposerValue=="Compositora informada","metadata-suggestion-preserves-manual-composer");
                Click(Button(dialog,"SwapTitleArtistButton"));
                Check(dialog.TitleValue=="Gusttavo Lima" && dialog.ArtistValue=="A gente fez amor","swap-title-artist-forward");
                Click(Button(dialog,"SwapTitleArtistButton"));
                Check(dialog.TitleValue=="A gente fez amor" && dialog.ArtistValue=="Gusttavo Lima"
                    && dialog.ComposerValue=="Compositora informada","swap-title-artist-backward-preserves-composer");
                Capture(dialog,Path.Combine(parent,"v018-properties.png"),660,800);
            },save:false);
            Check(first.ScoreTitle=="Gusttavo Lima" && first.ScoreArtist=="A gente fez amor"
                && first.ScoreComposer=="Compositora informada" && !first.HasPendingScoreProperties,"cancel-properties-preserves-row");
            Properties(window,dialog=>Click(Button(dialog,"ApplyMetadataSuggestionButton")),save:true);
            Check(first.ScoreTitle=="A gente fez amor" && first.ScoreArtist=="Gusttavo Lima" && first.ScoreComposer=="Compositora informada"
                && first.HasPendingScoreProperties && first.ScorePath is null && window.IsIdle,"save-corrected-properties-without-musical-conversion");
            ambiguous.ScoreArtist="Intérprete antigo indevido";ambiguous.ScoreComposer="Autoria manual";Select(window,ambiguous);
            Properties(window,dialog=>
            {
                Click(Button(dialog,"ApplyMetadataSuggestionButton"));
                Check(dialog.ArtistValue=="" && dialog.TitleValue=="Lua Azul - Mar Aberto" && dialog.ComposerValue=="Autoria manual",
                    "ambiguous-suggestion-clears-incorrect-artist-and-keeps-composer");
            },save:true);

            // Production rows use synthetic local score files and pages; no renderer runs.
            string preview=Path.Combine(folder,"synthetic-preview.png");V018Preview(preview);
            foreach(var (row,index) in new[]{(first,1),(second,2)})
            {
                string score=Path.Combine(folder,$"synthetic-{index}.mscz");V018Score(score);
                row.ScorePath=score;row.PreviewPath=preview;row.PageCount=index;row.GeneratedAt=DateTimeOffset.UtcNow;
                row.HasPendingScoreProperties=false;row.Status="Gerada · prévia sintética para verificação";
                row.Chords.Add(new ChordRow{Quarter=0,Root="C",Quality="",Confidence=1});
                hashes[score]=Hash(score);
            }
            hashes[preview]=Hash(preview);Invoke(window,"RefreshSelection");Invoke(window,"PersistPreferences");
            Check(window.GeneratedCount==2,"two-separate-production-scores");
            checks["navigation"]=VerifyNavigation(window);checks["sidebar"]=VerifySidebar(window);
            checks["themes"]=VerifyThemes(window,Path.Combine(folder,"themes.png"));window.SetThemeForSmoke("graphite");
            Select(window,second);window.SelectPageForSmoke("production");Flush();window.UpdateLayout();
            Capture(window,Path.Combine(parent,"v018-production.png"),1360,920);
            Button RemoveButton(MidiRow row)=>Descendants((DependencyObject)window.Content).OfType<Button>()
                .Single(button=>ReferenceEquals(button.Tag,row) && button.Content?.ToString()=="Remover");
            var remove=RemoveButton(first);
            Check(remove.IsEnabled && ReferenceEquals(window.SelectedFile,second),"production-removal-target-differs-from-editor-selection");
            Invoke(window,"SetBusy",true);Flush();
            Check(!remove.IsEnabled,"production-remove-button-disabled-while-busy");
            Click(remove);Check(window.Files.Count==3 && window.Files.Contains(first),"production-removal-guard-blocks-busy-handler");
            Invoke(window,"SetBusy",false);Flush();Click(remove);Flush();
            Check(window.Files.Count==2 && !window.Files.Contains(first) && window.Files.Contains(second)
                && ReferenceEquals(window.SelectedFile,second) && window.GeneratedCount==1,"production-removes-clicked-row-not-selected-row");
            Check(Settings.Load(settingsPath).Library.All(row=>row.FullPath!=first.FullPath),"production-removal-is-persisted");
            Check(hashes.All(pair=>File.Exists(pair.Key)&&Hash(pair.Key)==pair.Value),"production-removal-preserves-all-source-and-score-files");
            Check(window.CanUndoLibraryRemoval,"production-removal-exposes-undo");
            Click(Button(window,"UndoLibraryRemovalButton"));Flush();
            Check(window.Files.Count==3 && ReferenceEquals(window.Files[0],first) && ReferenceEquals(window.SelectedFile,first)
                && first.ScoreComposer=="Compositora informada" && first.Chords.Count==1 && first.ScorePath is not null
                && window.GeneratedCount==2 && !window.CanUndoLibraryRemoval,"undo-restores-position-metadata-musical-edits-and-output");

            window.FavoritesOnly=true;window.CollectionFilter="Outra coleção";window.SearchText="Nada encontrado";
            window.CurrentFilter="review";window.SelectPageForSmoke("production");Flush();window.UpdateLayout();
            Click(RemoveButton(first));Flush();
            Check(!window.Files.Contains(first) && window.CanUndoLibraryRemoval,"production-removes-row-hidden-by-library-filters");
            Click(Button(window,"UndoLibraryRemovalButton"));Flush();
            Check(!window.FavoritesOnly && window.CollectionFilter=="" && window.SearchText=="" && window.CurrentFilter=="all"
                && window.LibraryView.Cast<MidiRow>().Contains(first) && ReferenceEquals(window.SelectedFile,first)
                && window.CurrentPage=="library","undo-clears-blocking-filters-and-selects-visible-restored-row");

            // Bulk removal includes hidden rows, has an explicit cancel path and preserves files.
            void ClearLibrary(bool confirm)
            {
                Exception? failure=null;
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>
                {
                    var dialog=Windows.OfType<ClearLibraryWindow>().SingleOrDefault();
                    try
                    {
                        if(dialog is null)throw new InvalidOperationException("Missing bulk confirmation");
                        dialog.DialogResult=confirm;
                    }
                    catch(Exception ex){failure=ex;dialog?.Close();}
                }));
                Click(Button(window,"ClearLibraryButton"));Flush();
                if(failure is not null)throw failure;
            }
            ClearLibrary(false);
            Check(window.Files.Count==3 && !window.CanUndoLibraryRemoval,"bulk-cancel-leaves-library-and-undo-intact");
            window.SearchText="Nada encontrado";Flush();
            Invoke(window,"SetBusy",true);
            Invoke(window,"ClearLibrary");Check(window.Files.Count==3 && !window.CanClearLibrary,"bulk-clear-blocked-during-operation");
            Invoke(window,"SetBusy",false);
            ClearLibrary(true);
            Check(window.Files.Count==0 && window.SelectedFile is null && window.GeneratedCount==0 && !window.CanClearLibrary
                && !window.CanExportPdf && window.CanUndoLibraryRemoval && Settings.Load(settingsPath).Library.Count==0,
                "bulk-clear-removes-hidden-and-visible-cards-and-persists-empty-library");
            Check(hashes.All(pair=>File.Exists(pair.Key)&&Hash(pair.Key)==pair.Value),"bulk-clear-preserves-midi-score-and-preview-files");
            Click(Button(window,"UndoLibraryRemovalButton"));Flush();
            Check(window.Files.SequenceEqual(new[]{first,second,ambiguous}) && window.SearchText=="" && window.GeneratedCount==2
                && !window.CanUndoLibraryRemoval && Settings.Load(settingsPath).Library.Count==3,"bulk-undo-restores-order-edits-and-persistent-library");
            Select(window,first);window.Width=1100;window.Height=740;Flush();window.UpdateLayout();
            var pdf=Button(window,"PdfExportButton");var header=(FrameworkElement)window.FindName("WorkspaceHeader");
            var editorScroll=(ScrollViewer)window.FindName("ScoreEditorScrollViewer");
            foreach(bool compact in new[]{false,true})
            {
                window.IsPreviewControlsCompact=compact;Flush();editorScroll.ScrollToEnd();Flush();window.UpdateLayout();
                var bounds=pdf.TransformToAncestor(header).TransformBounds(new Rect(pdf.RenderSize));
                Check(pdf.IsVisible && pdf.IsEnabled && bounds.Left>=0 && bounds.Right<=header.ActualWidth+1
                    && bounds.Top>=0 && bounds.Bottom<=header.ActualHeight+1,"pdf-fixed-header-visible-after-scroll-compact-"+compact);
                var importScore=Descendants(header).OfType<Button>().Single(button=>button.Content?.ToString()=="Importar MuseScore");
                var importBounds=importScore.TransformToAncestor(header).TransformBounds(new Rect(importScore.RenderSize));
                Check(Math.Abs(bounds.Top-importBounds.Top)<1 && Math.Abs(bounds.Height-importBounds.Height)<1
                    && Math.Abs(bounds.Width-importBounds.Width)<1 && Math.Abs(importBounds.Left-bounds.Right-8)<1,
                    "pdf-standard-button-size-aligned-next-to-musescore-compact-"+compact);
                Capture(window,Path.Combine(parent,"v0205-fixed-pdf-"+compact+".png"),1100,740);
            }
            window.IsPreviewControlsCompact=false;
            Check(window.CanShiftOctave,"octave-actions-enabled-for-generated-score");
            first.HasPendingScoreProperties=true;Invoke(window,"RefreshSelection");Flush();
            Check(!window.CanShiftOctave && !Button(window,"RaiseOctaveButton").IsEnabled,"octave-disabled-for-pending-edits");
            first.HasPendingScoreProperties=false;Invoke(window,"RefreshSelection");
            Invoke(window,"SetBusy",true);Check(!window.CanShiftOctave,"octave-disabled-during-operation");Invoke(window,"SetBusy",false);
            await (Task)Invoke(window,"ShiftOctave",0)!;Check(hashes.All(pair=>Hash(pair.Key)==pair.Value),"invalid-octave-action-does-not-alter-originals");

            // Measure real app operation wrappers with synthetic success/failure outcomes.
            async Task Operation(Func<Task> action,string name)=>await (Task)Invoke(window,"RunOperation",action,null,name,null)!;
            await Operation(()=>Task.CompletedTask,"Analisando notas e acordes");
            await Operation(()=>Task.FromException(new IOException("PRIVATE_ERROR_DO_NOT_RECORD")),"Gerando partitura");
            await Operation(()=>Task.FromCanceled(new CancellationToken(true)),"Preparando áudio da partitura");
            await Operation(()=>Task.FromException(new TimeoutException("PRIVATE_ERROR_DO_NOT_RECORD")),"PRIVATE_TITLE_DO_NOT_RECORD");
            var recorder=(LocalPerformanceRecorder)typeof(MainWindow).GetField("performanceRecorder",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            var performance=recorder.ReadReport();
            Check(performance.Records.Select(item=>item.Outcome).ToHashSet().IsSupersetOf(Enum.GetValues<PerformanceOutcome>()),
                "app-operation-records-success-failure-cancel-and-timeout");
            Check(window.IsIdle,"all-performance-test-operations-release-busy-state");
            window.SelectPageForSmoke("about");Flush();Check(Button(window,"PerformanceButton").IsEnabled,"about-exposes-performance-window");
            var performanceWindow=new PerformanceWindow(recorder){Owner=window,Opacity=0,ShowInTaskbar=false,ShowActivated=false};
            windows.Add(performanceWindow);await performanceWindow.RefreshReportAsync();performanceWindow.Show();Flush();
            await V018Wait(()=>((Panel)performanceWindow.FindName("Actions")).IsEnabled);
            Check(((DataGrid)performanceWindow.FindName("PerformanceRows")).Items.Count==performance.Records.Count
                && ((TextBlock)performanceWindow.FindName("SummaryText")).Text.Contains(performance.Records.Count.ToString(CultureInfo.InvariantCulture)),
                "performance-ui-displays-recorded-results");
            Capture(performanceWindow,Path.Combine(parent,"v018-performance.png"),1080,730);
            foreach(string theme in new[]{"graphite","light"})
            {
                window.SetThemeForSmoke(theme);
                Capture(performanceWindow,Path.Combine(parent,$"v018-performance-compact-{theme}.png"),760,640);
                var content=(FrameworkElement)performanceWindow.Content;
                var actions=(FrameworkElement)performanceWindow.FindName("Actions");
                var actionBounds=actions.TransformToAncestor(content).TransformBounds(new Rect(actions.RenderSize));
                Check(actionBounds.Right<=content.ActualWidth+1 && actionBounds.Bottom<=content.ActualHeight+1
                    && ((DataGrid)performanceWindow.FindName("PerformanceRows")).ActualHeight>=120,
                    "performance-minimum-window-visible-actions-and-results-"+theme);
            }
            window.SetThemeForSmoke("graphite");
            string exported=Path.Combine(folder,"performance-export.json");
            await performanceWindow.ExportReportToAsync(exported);
            Check(File.Exists(exported) && ((TextBlock)performanceWindow.FindName("ReportStatus")).Text.Contains("salvo",StringComparison.OrdinalIgnoreCase),"performance-report-export-controller");
            string exportedText=File.ReadAllText(exported);
            Check(!exportedText.Contains(folder,StringComparison.OrdinalIgnoreCase) && !exportedText.Contains("PRIVATE_",StringComparison.Ordinal)
                && !exportedText.Contains("Gusttavo",StringComparison.OrdinalIgnoreCase),"performance-export-excludes-paths-titles-and-error-messages");
            var clear=Descendants((DependencyObject)performanceWindow.Content).OfType<Button>().Single(button=>button.Content?.ToString()=="Limpar registros");
            Click(clear);await V018Wait(()=>((Panel)performanceWindow.FindName("Actions")).IsEnabled && recorder.ReadReport().Records.Count==0);
            Check(((DataGrid)performanceWindow.FindName("PerformanceRows")).Items.Count==0,"performance-clear-button-refreshes-empty-state");
            Check(hashes.All(pair=>Hash(pair.Key)==pair.Value),"performance-clear-preserves-music-files");Close(performanceWindow);

            Close(window);var restored=Open(settingsPath);
            Check(restored.Files.Count==3 && restored.GeneratedCount==2,"reopening-restores-undo-result-and-production");
            var restoredFirst=restored.Files.Single(row=>row.FullPath==first.FullPath);
            Check(restoredFirst.ScoreTitle=="A gente fez amor" && restoredFirst.ScoreArtist=="Gusttavo Lima" && restoredFirst.ScoreComposer=="Compositora informada",
                "reopening-preserves-corrected-header-and-manual-composer");
            Select(restored,restored.Files.Single(row=>row.FullPath==second.FullPath));restored.SelectPageForSmoke("production");Flush();restored.UpdateLayout();
            var finalRemove=Descendants((DependencyObject)restored.Content).OfType<Button>().Single(button=>ReferenceEquals(button.Tag,restoredFirst)&&button.Content?.ToString()=="Remover");
            Click(finalRemove);Close(restored);var removedReopen=Open(settingsPath);
            Check(removedReopen.Files.Count==2 && removedReopen.Files.All(row=>row.FullPath!=first.FullPath) && removedReopen.GeneratedCount==1,
                "reopening-preserves-production-removal");Close(removedReopen);
            Check(hashes.All(pair=>Hash(pair.Key)==pair.Value),"all-synthetic-originals-unchanged");
            listener.Flush();Check(string.IsNullOrWhiteSpace(bindingOutput.ToString()),"no-wpf-binding-errors");
            File.WriteAllText(report,JsonSerializer.Serialize(new {status="passed",folder,checks},new JsonSerializerOptions{WriteIndented=true}));
            timeout.Stop();Shutdown(0);
        }
        catch(Exception ex)
        {
            File.WriteAllText(report,JsonSerializer.Serialize(new {status="failed",folder,checks,error=ex.ToString(),bindingDiagnostics=bindingOutput.ToString()},new JsonSerializerOptions{WriteIndented=true}));
            timeout.Stop();foreach(var window in windows.ToArray())try{window.Close();}catch{ }
            Shutdown(1);
        }
        finally{timeout.Stop();trace.Listeners.Remove(listener);trace.Switch.Level=previousLevel;}
    }

    private static async Task V018Wait(Func<bool> condition)
    {
        var watch=Stopwatch.StartNew();
        while(!condition())
        {if(watch.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("UI action did not finish.");await Task.Delay(10);}
    }

    private static byte[] V018Midi(byte pitch)
    {
        byte[] track=[0,0xc0,0,0,0x90,pitch,90,0x83,0x60,0x80,pitch,0,0,0xff,0x2f,0];
        return [..Encoding.ASCII.GetBytes("MThd"),0,0,0,6,0,0,0,1,1,224,..Encoding.ASCII.GetBytes("MTrk"),0,0,0,(byte)track.Length,..track];
    }

    private static void V018Score(string path)
    {
        using var archive=ZipFile.Open(path,ZipArchiveMode.Create);
        using var writer=new StreamWriter(archive.CreateEntry("score.mscx").Open(),Encoding.UTF8);
        writer.Write("<museScore version=\"4.0\"><Score><Division>480</Division><Part><Staff id=\"1\"/><Instrument><trackName>Piano</trackName><Channel><program value=\"0\"/></Channel></Instrument></Part><Staff id=\"1\"><Measure><voice><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig><Chord><durationType>whole</durationType><Note><pitch>60</pitch><tpc>14</tpc></Note></Chord></voice></Measure></Staff></Score></museScore>");
    }

    private static void V018Preview(string path)
    {
        var visual=new DrawingVisual();using(var drawing=visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White,null,new Rect(0,0,400,600));
            drawing.DrawText(new FormattedText("Prévia sintética de teste",CultureInfo.GetCultureInfo("pt-BR"),FlowDirection.LeftToRight,new Typeface("Segoe UI"),18,Brushes.Black,1),new Point(48,40));
            for(int line=0;line<5;line++)drawing.DrawLine(new Pen(Brushes.Black,1),new Point(30,120+line*12),new Point(370,120+line*12));
            drawing.DrawEllipse(Brushes.Black,null,new Point(100,156),7,5);
        }
        var bitmap=new RenderTargetBitmap(400,600,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
}
