using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Teclas.Core;

string reportDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "recovery-tests");
string work = Path.Combine(reportDirectory, "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var checks = new List<string>();
void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
string JsonHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
int fixtureIndex = 0;
string Fixture()
{
    string path = Path.Combine(work, $"original-{++fixtureIndex}.mscz");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using (var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open()))
        writer.Write("<museScore version='4.70'><Score><metaTag name='workTitle'>Peça original</metaTag><Part><Staff id='1'><StaffType group='pitched'/></Staff><Instrument id='piano'/></Part><Staff id='1'><Measure><voice><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig><Chord><durationType>whole</durationType><Note><pitch>60</pitch><tpc>14</tpc></Note></Chord></voice></Measure></Staff></Score></museScore>");
    using (var stream = zip.CreateEntry("Pictures/preserved.bin").Open()) stream.Write([1, 3, 5, 7, 255]);
    return path;
}
const string initial = "{\"Notes\":{},\"Rhythms\":{},\"Harmonies\":{}}";
const string second = "{\"Notes\":{\"n1\":\"F#4\"},\"Rhythms\":{},\"Harmonies\":{}}";
const string third = "{\"Notes\":{\"n1\":\"G4\"},\"Rhythms\":{},\"Harmonies\":{}}";
string root = Path.Combine(work, "recovery");
var store = new EditorDraftRecoveryStore(root);
Check(store.List().Drafts.Count == 0 && !Directory.Exists(root), "Listing absent recovery storage creates no files");
string source = Fixture(), originalHash = Hash(source);
var created = store.Create(source, originalHash.ToLowerInvariant(), "Peça de teste", initial);
Check(created.Id.Length == 32 && Guid.TryParseExact(created.Id, "N", out _) && created.SourceSha256 == originalHash, "Creation uses a validated independent session ID and normalized hash");
Check(created.SnapshotPath != source && Hash(created.SnapshotPath) == originalHash && Hash(source) == originalHash, "Creation copies the original byte-for-byte without modifying it");
Check(store.Read(created.Id)?.Payload == initial && store.List().Drafts.Count == 1 && store.List().Warnings.Count == 0, "Created draft is immediately readable and discoverable");
string sessionDirectory = Path.GetDirectoryName(created.SnapshotPath)!;
string checkpointPath = Path.Combine(sessionDirectory, "draft.json"), backupPath = checkpointPath + ".bak";
var saved = store.Save(created, second);
Check(store.Read(created.Id)?.Payload == second && saved.UpdatedAt >= created.UpdatedAt, "Checkpoint update publishes the new complete editor payload");
Check(JsonNode.Parse(File.ReadAllText(backupPath))!["Payload"]!.GetValue<string>() == initial, "Atomic replacement retains the preceding valid checkpoint as backup");
Check(!Directory.EnumerateFiles(sessionDirectory, "*.tmp").Any(), "Successful saves leave no partial temporary checkpoint");
string lastGood = Hash(checkpointPath);
Check(Throws<JsonException>(() => store.Save(saved, "{truncated")) && Hash(checkpointPath) == lastGood, "Malformed new JSON cannot replace the last valid draft");
Check(Throws<InvalidDataException>(() => store.Save(saved, "[]")) && Hash(checkpointPath) == lastGood, "Non-object editor state is rejected without replacing the draft");
Check(Throws<InvalidDataException>(() => store.Save(saved, "{\"text\":\"" + new string('x', 4 * 1024 * 1024) + "\"}")) && Hash(checkpointPath) == lastGood, "Oversized payload cannot erase the last valid draft");
File.WriteAllText(checkpointPath, "{\"Version\":1,");
Check(store.Read(created.Id)?.Payload == initial, "Truncated current checkpoint falls back to the complete backup");
string goodBackup = Hash(backupPath);
Check(Throws<IOException>(() => store.Save(saved, third)), "A checkpoint loaded before storage changed cannot silently replace a fallback version");
saved = store.Save(store.Read(created.Id)!, third);
Check(store.Read(created.Id)?.Payload == third && Hash(backupPath) == goodBackup, "Saving after truncation preserves the good backup instead of replacing it with corrupt data");
var tampered = JsonNode.Parse(File.ReadAllText(checkpointPath))!;
tampered["Payload"] = second;
File.WriteAllText(checkpointPath, tampered.ToJsonString());
Check(store.Read(created.Id)?.Payload == initial, "Payload hash mismatch falls back to the valid backup");
saved = store.Save(store.Read(created.Id)!, third);
File.WriteAllText(source, "Original externally changed by its owner");
Check(Hash(created.SnapshotPath) == originalHash && store.Read(created.Id)?.Payload == third, "External source changes do not change the immutable recovery base");
File.Delete(source);
Check(store.Read(created.Id)?.SnapshotPath == created.SnapshotPath && Hash(created.SnapshotPath) == originalHash, "A deleted original still leaves a verifiable base and draft to recover");
using (var zip = ZipFile.OpenRead(created.SnapshotPath))
using (var stream = zip.GetEntry("Pictures/preserved.bin")!.Open())
{ using var memory = new MemoryStream(); stream.CopyTo(memory); Check(memory.ToArray().SequenceEqual(new byte[] { 1, 3, 5, 7, 255 }), "Recovery snapshot includes embedded score assets unchanged"); }

string stableSource = Fixture(); string stableHash = Hash(stableSource);
var removable = store.Create(stableSource, stableHash, "Outra peça", initial);
string removableDirectory = Path.GetDirectoryName(removable.SnapshotPath)!;
File.WriteAllText(Path.Combine(removableDirectory, "unknown-user-file.txt"), "preserve unrelated data");
store.Discard(removable.Id);
Check(File.Exists(stableSource) && Hash(stableSource) == stableHash, "Discarding a recovery draft never deletes or modifies the original");
Check(!File.Exists(removable.SnapshotPath) && File.Exists(Path.Combine(removableDirectory, "unknown-user-file.txt")), "Discard removes only the store's known files and preserves unrelated files");
store.Discard(removable.Id);
Check(File.Exists(Path.Combine(removableDirectory, "unknown-user-file.txt")), "Repeated discard is safe and leaves unrelated files alone");

foreach (string id in new[] { "..", "../outside", "..\\outside", Path.GetFullPath(work), "not-a-guid", "00000000-0000-0000-0000-000000000000" })
    Check(Throws<InvalidDataException>(() => store.Read(id)) && Throws<InvalidDataException>(() => store.Discard(id)), "Invalid or traversing recovery IDs are rejected: " + id);
string absentId = Guid.NewGuid().ToString("N");
store.Discard(absentId);
Check(store.Read(absentId) is null, "Unknown but well-formed ID does not create or erase arbitrary data");

var corruptBase = store.Create(stableSource, stableHash, "Base a conferir", second);
string corruptJson = Path.Combine(Path.GetDirectoryName(corruptBase.SnapshotPath)!, "draft.json");
string beforeCorruption = Hash(corruptJson);
File.WriteAllText(corruptBase.SnapshotPath, "modified recovery copy");
Check(Throws<InvalidDataException>(() => store.Read(corruptBase.Id)), "A modified base snapshot fails integrity checking instead of restoring against different music");
Check(Throws<InvalidDataException>(() => store.Save(corruptBase, third)) && Hash(corruptJson) == beforeCorruption, "Save refuses a modified snapshot while preserving the prior checkpoint");
Check(store.List().Warnings.Count == 1 && File.Exists(corruptJson) && File.Exists(corruptBase.SnapshotPath), "Unavailable draft is reported and preserved for inspection");

int directoriesBefore = Directory.GetDirectories(root).Length;
Check(Throws<IOException>(() => store.Create(stableSource, new string('0', 64), "Hash errado", initial)) && Directory.GetDirectories(root).Length == directoriesBefore,
    "Wrong source hash rejects creation and cleans only its new session files");
string wrongExtension = Path.ChangeExtension(stableSource, ".mid"); File.Copy(stableSource, wrongExtension);
Check(Throws<InvalidDataException>(() => store.Create(wrongExtension, stableHash, "MIDI", initial)), "Only MuseScore files can be the protected editing base");
Check(Throws<InvalidDataException>(() => store.Save(saved with { SnapshotPath = stableSource }, second)) && Hash(stableSource) == stableHash,
    "A caller-supplied snapshot path cannot redirect reads or writes to the original");

var locked = store.Create(stableSource, stableHash, "Save failure", initial);
string lockedPath = Path.Combine(Path.GetDirectoryName(locked.SnapshotPath)!, "draft.json");
string lockedHash = Hash(lockedPath);
using (var fileLock = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    Check(Throws<IOException>(() => store.Save(locked, second)), "A locked destination reports a failed save rather than pretending success");
Check(Hash(lockedPath) == lockedHash && store.Read(locked.Id)?.Payload == initial && !Directory.GetFiles(Path.GetDirectoryName(lockedPath)!, "*.tmp").Any(),
    "A failed checkpoint replacement leaves the last good draft intact and cleans its temporary file");

var interrupted = store.Create(stableSource, stableHash, "Power-loss artifacts", initial);
string interruptedDirectory = Path.GetDirectoryName(interrupted.SnapshotPath)!;
File.WriteAllText(Path.Combine(interruptedDirectory, "draft-interrupted.tmp"), "partial write");
Check(store.Read(interrupted.Id)?.Payload == initial, "An abandoned temporary write is ignored after an interruption");
string invalidDirectory = Path.Combine(root, "unrelated-directory"); Directory.CreateDirectory(invalidDirectory); File.WriteAllText(Path.Combine(invalidDirectory, "draft.json"), "{} ");
Check(store.List().Drafts.All(d => d.Id != "unrelated-directory"), "Unrelated storage directories are ignored");

var invalidMetadata = store.Create(stableSource, stableHash, "Invalid stored hash", initial);
string invalidJson = Path.Combine(Path.GetDirectoryName(invalidMetadata.SnapshotPath)!, "draft.json");
var invalid = JsonNode.Parse(File.ReadAllText(invalidJson))!; invalid["SourceSha256"] = new string('z', 64); File.WriteAllText(invalidJson, invalid.ToJsonString());
Check(store.Read(invalidMetadata.Id) is null && File.Exists(invalidMetadata.SnapshotPath), "Invalid stored source hashes are rejected without discarding recovery data");
invalid["SourceSha256"] = stableHash; invalid["Id"] = "wrong-id"; File.WriteAllText(invalidJson, invalid.ToJsonString());
Check(store.Read(invalidMetadata.Id) is null, "Checkpoint ID must match its containing session directory");
invalid["Id"] = invalidMetadata.Id; invalid["SourcePath"] = "relative.mscz"; File.WriteAllText(invalidJson, invalid.ToJsonString());
Check(store.Read(invalidMetadata.Id) is null, "Relative original paths are not accepted as valid recovery metadata");
invalid["SourcePath"] = stableSource; invalid["PayloadSha256"] = JsonHash(second); File.WriteAllText(invalidJson, invalid.ToJsonString());
Check(store.Read(invalidMetadata.Id) is null, "A mismatched checksum cannot mark payload data valid");

var shared = store.Create(stableSource, stableHash, "Multiple editor instances", initial);
var otherStore = new EditorDraftRecoveryStore(root);
var oldInOtherEditor = otherStore.Read(shared.Id)!;
var newest = store.Save(shared, second);
Check(Throws<IOException>(() => otherStore.Save(oldInOtherEditor, third)) && store.Read(shared.Id)?.Payload == second,
    "An outdated editor cannot silently overwrite a newer checkpoint from another instance");
Check(Throws<IOException>(() => otherStore.Discard(oldInOtherEditor)) && File.Exists(newest.SnapshotPath) && store.Read(shared.Id)?.Payload == second,
    "An outdated editor cannot discard a newer draft after publishing or undoing its own changes");
var currentInOtherEditor = otherStore.Read(shared.Id)!;
otherStore.Discard(currentInOtherEditor);
Check(store.Read(shared.Id) is null && Hash(stableSource) == stableHash, "Discarding an expected current checkpoint safely removes only its recovery copy");

var repeated = store.Create(stableSource, stableHash, "Repeated autosave", initial);
repeated = store.Save(repeated, second);
repeated = store.Save(repeated, third);
string repeatedPath = Path.Combine(Path.GetDirectoryName(repeated.SnapshotPath)!, "draft.json");
Check(store.Read(repeated.Id)?.Payload == third && JsonNode.Parse(File.ReadAllText(repeatedPath + ".bak"))!["Payload"]!.GetValue<string>() == second,
    "Repeated autosaves replace the prior backup with the last complete checkpoint");
File.WriteAllText(repeatedPath, "truncated"); File.WriteAllText(repeatedPath + ".bak", "truncated too");
Check(store.Read(repeated.Id) is null && File.Exists(repeated.SnapshotPath) && Throws<IOException>(() => store.Save(repeated, initial)),
    "Two damaged checkpoints remain preserved and cannot be silently replaced as a new session");

var concurrent = store.Create(stableSource, stableHash, "Concurrent saves", initial);
using (var barrier = new Barrier(3))
{
    bool Writer(string payload)
    {
        barrier.SignalAndWait();
        try { new EditorDraftRecoveryStore(root).Save(concurrent, payload); return true; }
        catch (IOException) { return false; }
    }
    var left = Task.Run(() => Writer(second)); var right = Task.Run(() => Writer(third));
    barrier.SignalAndWait(); Task.WaitAll(left, right);
    Check((left.Result ? 1 : 0) + (right.Result ? 1 : 0) == 1, "Concurrent writers are serialized and only the current checkpoint can commit");
    string expectedWinner = left.Result ? second : third;
    Check(store.Read(concurrent.Id)?.Payload == expectedWinner && Hash(concurrent.SnapshotPath) == stableHash,
        "Concurrent saves retain one complete winning payload and the unmodified base");
}

File.WriteAllText(Path.Combine(reportDirectory, "report.json"), JsonSerializer.Serialize(new { status = "passed", checks, work }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks.Count} recovery checks");
