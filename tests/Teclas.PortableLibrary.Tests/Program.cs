using System.Text.Json;
using Teclas.App;
using Teclas.Core;

string root=Path.GetFullPath(args.FirstOrDefault()??Path.Combine(Path.GetTempPath(),"teclas-portable-"+Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
string midi=Path.Combine(root,"música.mid"),score=Path.Combine(root,"melodia.mscz"),page1=Path.Combine(root,"pagina-1.png"),page2=Path.Combine(root,"pagina-2.png");
foreach(string path in new[]{midi,score,page1,page2})File.WriteAllText(path,Path.GetFileName(path));
string missing=Path.Combine(root,"sem-origem.mid");
var row=new SavedMidi{FullPath=midi,Sha256="SOURCE",ScorePath=score,PreviewPath=page1,PageCount=2,HasMusicalEdits=true,ArrangementSummary="Iniciante · pauta 1 · voz 1",
    ScoreTitle="Música",ScoreArtist="Artista",ScoreComposer="Autora",IsFavorite=true,Collection="Estudo",Genre="MPB",TransposeSemitones=2,DefaultDiagramInversion=1,
    MelodySources=[new(){Track=3,Channel=4,Program=0}],MelodySourcePriority=[new(){Track=3,Channel=4,Program=0}],
    MelodySections=[new(){FirstMeasure=1,LastMeasure=8,Source=new(){Track=3,Channel=4,Program=0}}],
    AnalysisPath=Path.Combine(root,"analysis.json"),Workspace=root,
    Chords=[new(){Quarter=4,Root="C",Quality="m",DiagramInversion=2}],
    Versions=[new(){ScorePath=score,PreviewPath=page1,PageCount=2,Title="Música",HasMusicalEdits=true,ArrangementSummary="Intermediário",Workspace=root,AnalysisPath="stale.json"}]};
var absent=new SavedMidi{FullPath=missing,ScorePath=score,PreviewPath=page1,HasMusicalEdits=true,ScoreTitle="Origem ausente"};
string before=JsonSerializer.Serialize(row);
var plan=LibraryBackupSnapshot.Capture([row,absent],midi);
Check(JsonSerializer.Serialize(row)==before,"Export does not mutate nested live session or history");
Check(plan.Sources.Count==4,"All score pages included once even when versions share them");
Check(plan.MissingFiles.SequenceEqual(new[]{"sem-origem.mid"}),"Missing origin identified before export");
Check(!plan.LibraryJson.Contains(root,StringComparison.OrdinalIgnoreCase),"Snapshot contains no old absolute folder references");
string zip=Path.Combine(root,"portable.teclasbackup");await LibraryBackupService.CreateAsync(zip,plan.LibraryJson,plan.Sources,plan.MissingFiles,null,default);
var inspection=await LibraryBackupService.InspectAsync(zip,null,default);
var restored=await LibraryBackupService.RestoreAsync(inspection,Path.Combine(root,"another-computer"),null,default);
var data=LibraryBackupSnapshot.Resolve(restored.Manifest,restored.Directory);var imported=data.Library[0];
Check(data.Library.Count==2&&data.SelectedPath==imported.FullPath,"Library membership and selection rebound to restored paths");
Check(File.Exists(imported.FullPath)&&File.Exists(imported.ScorePath)&&ScorePageFiles.Discover(imported.PreviewPath).Count==2,"Restored MIDI, score and every preview page are accessible");
Check(imported.ScoreTitle=="Música"&&imported.ScoreArtist=="Artista"&&imported.ScoreComposer=="Autora"&&imported.IsFavorite&&imported.Collection=="Estudo"&&imported.Genre=="MPB","Credits and library organization preserved");
Check(imported.Chords[0].DiagramInversion==2&&imported.DefaultDiagramInversion==1&&imported.TransposeSemitones==2&&imported.MelodySourcePriority[0].Track==3,"Harmony, voicings, transposition and source priority preserved");
Check(imported.HasMusicalEdits&&imported.ArrangementSummary==row.ArrangementSummary&&imported.Versions[0].ArrangementSummary=="Intermediário","Edited-score workflow and arrangement history preserved");
Check(imported.MelodySections.Count==1&&imported.MelodySections[0].FirstMeasure==1&&imported.MelodySections[0].LastMeasure==8&&imported.MelodySections[0].Source.Track==3,"Melody source ranges survive portable restore");
Check(imported.AnalysisPath is null&&imported.Workspace is null&&imported.Versions[0].AnalysisPath is null&&imported.Versions[0].Workspace is null,"Machine-specific temporary analyses are invalidated");
Check(!File.Exists(data.Library[1].FullPath)&&File.Exists(data.Library[1].ScorePath),"Missing origin stays explicit while its saved score remains available");
Check(imported.Versions[0].ScorePath==imported.ScorePath&&imported.Versions[0].PreviewPath==imported.PreviewPath,"Shared references preserved after relocation");
var unsafeData=JsonSerializer.Deserialize<PortableLibraryData>(plan.LibraryJson)!;unsafeData.Library[0].FullPath=midi;
Check(Reject(()=>LibraryBackupSnapshot.Resolve(inspection.Manifest with{LibraryJson=JsonSerializer.Serialize(unsafeData)},restored.Directory)),"Absolute source paths inside untrusted snapshot rejected");
unsafeData.Library[0].FullPath="asset:files/../escape.mid";
Check(Reject(()=>LibraryBackupSnapshot.Resolve(inspection.Manifest with{LibraryJson=JsonSerializer.Serialize(unsafeData)},restored.Directory)),"Escaping snapshot references rejected");
unsafeData.Library[0].FullPath="asset:files/unknown.mid";
Check(Reject(()=>LibraryBackupSnapshot.Resolve(inspection.Manifest with{LibraryJson=JsonSerializer.Serialize(unsafeData)},restored.Directory)),"Reference absent from inventory rejected");
unsafeData.Library[0].FullPath="asset:"+inspection.Manifest.Files.First(f=>f.RelativePath.EndsWith(".png")).RelativePath;
Check(Reject(()=>LibraryBackupSnapshot.Resolve(inspection.Manifest with{LibraryJson=JsonSerializer.Serialize(unsafeData)},restored.Directory)),"Preview cannot masquerade as a MIDI source");
await File.WriteAllTextAsync(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS: {checks.Count} portable library checks. {root}");
