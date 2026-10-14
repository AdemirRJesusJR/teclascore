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
await File.WriteAllTextAsync(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS: {checks.Count} portable backup checks. {root}");
sealed class SyncProgress(Action<string> action):IProgress<string>{public void Report(string text)=>action(text);}
