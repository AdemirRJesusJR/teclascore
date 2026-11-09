using System.IO.Compression;
using System.Text.Json;
using Teclas.Core;

string root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "teclas-backup-tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root); var checks = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
async Task<bool> Reject<T>(Func<Task> action) where T : Exception { try { await action(); return false; } catch (T) { return true; } }
string source = Path.Combine(root,"music.mid"), score = Path.Combine(root,"score.mscz"), backup = Path.Combine(root,"library.teclasbackup");
await File.WriteAllBytesAsync(source,[0x4d,0x54,0x68,0x64,1,2,3,4]);
await File.WriteAllBytesAsync(score,[0x50,0x4b,6,7,8]);
var files = new[] { new LibraryBackupSource("files/g1/music.mid",source), new LibraryBackupSource("files/g2/score.mscz",score) };
const string json = "{\"Library\":[{\"Title\":\"Música de teste\"}]}";
await LibraryBackupService.CreateAsync(backup,json,files,["ausente.mid"],null,default);
var report = await LibraryBackupService.InspectAsync(backup,null,default);
Check(report.Manifest.Files.Count==2 && report.Manifest.LibraryJson==json && report.Manifest.MissingFiles.Single()=="ausente.mid", "Roundtrip preserves musical choices and missing-file inventory");
var restored = await LibraryBackupService.RestoreAsync(report,Path.Combine(root,"restored"),null,default);
byte[] originalMidi = await File.ReadAllBytesAsync(source), originalScore = await File.ReadAllBytesAsync(score);
Check((await File.ReadAllBytesAsync(LibraryBackupService.SafePath(restored.Directory,files[0].RelativePath))).SequenceEqual(originalMidi),"MIDI bytes restored exactly");
Check((await File.ReadAllBytesAsync(LibraryBackupService.SafePath(restored.Directory,files[1].RelativePath))).SequenceEqual(originalScore),"MuseScore bytes restored exactly");
var reused = await LibraryBackupService.RestoreAsync(report,Path.Combine(root,"restored"),null,default);
Check(reused.Reused && reused.Directory==restored.Directory,"Restoring the same unchanged backup is idempotent");
await File.AppendAllTextAsync(LibraryBackupService.SafePath(restored.Directory,files[1].RelativePath),"user edit");
Check(await Reject<IOException>(()=>LibraryBackupService.RestoreAsync(report,Path.Combine(root,"restored"),null,default)),"Changed restored files are never overwritten");
Check(await Reject<IOException>(()=>LibraryBackupService.CreateAsync(backup,json,files,[],null,default)),"Existing backup cannot be overwritten");
foreach(string bad in new[]{"../outside.mid","files/../../outside.mid","files/C:/outside.mid","files/x\\..\\outside.mid","files/NUL.mid","files/test.exe","files/g1/trailing .mid/../outside.mid","files/./x.mid","files//x.mid","files/g1/x.mid:ads"})
{
    Check(await Reject<InvalidDataException>(()=>LibraryBackupService.CreateAsync(Path.Combine(root,Guid.NewGuid()+".zip"),json,[new(bad,source)],[],null,default)),"Unsafe export path rejected: "+bad);
}
Check(await Reject<InvalidDataException>(()=>LibraryBackupService.CreateAsync(Path.Combine(root,"duplicate.zip"),json,[files[0],files[0] with {RelativePath="files/G1/MUSIC.mid"}],[],null,default)),"Case-insensitive path collisions rejected");
using(var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    string cancelled=Path.Combine(root,"cancelled.zip");
    Check(await Reject<OperationCanceledException>(()=>LibraryBackupService.CreateAsync(cancelled,json,files,[],null,cancel.Token)) && !File.Exists(cancelled),"Cancelled export leaves no backup");
}
using(var cancel=new CancellationTokenSource())
{
    var progress=new SyncProgress(text=>{if(text.StartsWith("Restaurando"))cancel.Cancel();});
    string cancelRoot=Path.Combine(root,"cancelled-restore");
    Check(await Reject<OperationCanceledException>(()=>LibraryBackupService.RestoreAsync(report,cancelRoot,progress,cancel.Token)) && !Directory.EnumerateFileSystemEntries(cancelRoot).Any(),"Cancellation during extraction leaves no partial library");
}
Check(await Reject<IOException>(()=>LibraryBackupService.RestoreAsync(report with {Manifest=report.Manifest with {LibraryJson="{}"}},Path.Combine(root,"tamper-plan"),null,default)),"Caller-modified inspected manifest cannot redirect restore");
string tampered=Path.Combine(root,"tampered.zip"); File.Copy(backup,tampered);
using(var zip=ZipFile.Open(tampered,ZipArchiveMode.Update))
{var entry=zip.GetEntry(files[0].RelativePath)!; entry.Delete();using var output=zip.CreateEntry(files[0].RelativePath).Open();output.Write([9,9,9,9,9,9,9,9]);}
Check(await Reject<InvalidDataException>(()=>LibraryBackupService.InspectAsync(tampered,null,default)),"Changed archive bytes fail manifest integrity check");
string extra=Path.Combine(root,"extra.zip");File.Copy(backup,extra);
using(var zip=ZipFile.Open(extra,ZipArchiveMode.Update)){using var output=zip.CreateEntry("../escape.mid").Open();output.WriteByte(1);}
Check(await Reject<InvalidDataException>(()=>LibraryBackupService.InspectAsync(extra,null,default)),"Unexpected archive entries rejected before extraction");
string duplicate=Path.Combine(root,"duplicate-entry.zip");File.Copy(backup,duplicate);
using(var zip=ZipFile.Open(duplicate,ZipArchiveMode.Update)){using var output=zip.CreateEntry(files[0].RelativePath).Open();output.WriteByte(1);}
Check(await Reject<InvalidDataException>(()=>LibraryBackupService.InspectAsync(duplicate,null,default)),"Duplicate ZIP entries cannot shadow an asset");
string swapped=Path.Combine(root,"swapped.zip");File.Copy(backup,swapped);var old=await LibraryBackupService.InspectAsync(swapped,null,default);await File.AppendAllTextAsync(swapped,"changed");
Check(await Reject<IOException>(()=>LibraryBackupService.RestoreAsync(old,Path.Combine(root,"stale"),null,default)),"Changed package after inspection cannot be restored silently");
Check((await File.ReadAllBytesAsync(source)).SequenceEqual(new byte[]{0x4d,0x54,0x68,0x64,1,2,3,4}),"Original input remains intact");
Check(!Directory.EnumerateFiles(root,"*.tmp").Any(),"Failed exports leave no temporary package");
// Restore points: a local package (library JSON plus the affected scores) registered in the store, pruned to the newest five.
var points=new RestorePointStore(Path.Combine(root,"pontos-de-restauracao"));
var ids=new List<string>();
for(int i=0;i<RestorePointStore.Kept+2;i++)
{
    string id=points.NewId(DateTimeOffset.Now.AddMinutes(i));string package=points.PackagePath(id);
    await LibraryBackupService.CreateAsync(package,json,i==0?files:[files[1]],[],null,default);
    var removed=points.Register(new RestorePointInfo(id,"Piloto automático",DateTimeOffset.Now.AddMinutes(i),3,i==0?2:1,package));
    ids.Add(id);
    Check(removed.Count==(i>=RestorePointStore.Kept?1:0),"Restore point registration prunes beyond the newest five");
}
var kept=points.List();
Check(kept.Count==RestorePointStore.Kept && kept[0].Id==ids[^1] && !File.Exists(points.PackagePath(ids[0])) && !File.Exists(points.PackagePath(ids[1])),"Only the newest five restore points keep their packages");
Check(kept.All(p=>p.Label.StartsWith("Antes de: Piloto automático · ") && p.Label.EndsWith(" · 3 músicas") && p.IsAvailable),"Restore points are labelled for the Histórico");
var restoredPoint=await LibraryBackupService.RestoreAsync(await LibraryBackupService.InspectAsync(kept[0].Package,null,default),points.RestoreDirectory(kept[0].Id),null,default);
Check((await File.ReadAllBytesAsync(LibraryBackupService.SafePath(restoredPoint.Directory,files[1].RelativePath))).SequenceEqual(originalScore),"A restore point's score comes back byte for byte");
points.Remove(kept[0].Id);
Check(!File.Exists(kept[0].Package) && !Directory.Exists(points.RestoreDirectory(kept[0].Id)) && points.List().Count==RestorePointStore.Kept-1,"Removing a restore point deletes its package and extracted folder");
Check(await Reject<ArgumentException>(()=>Task.FromResult(points.Register(new RestorePointInfo("fora","op",DateTimeOffset.Now,1,0,Path.Combine(root,"fora.teclasbackup"))))),"A package outside the restore-point folder is rejected");
await File.WriteAllTextAsync(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS: {checks.Count} portable backup checks. {root}");
sealed class SyncProgress(Action<string> action):IProgress<string>{public void Report(string text)=>action(text);}
