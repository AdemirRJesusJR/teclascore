using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Teclas.Core;

var work = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "TeclaScoreStudyTests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(work);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
XDocument Read(string path)
{ using var zip = ZipFile.OpenRead(path); using var input = zip.Entries.First(e => e.FullName.EndsWith(".mscx")).Open(); return XDocument.Load(input); }
string NotesOnly(XDocument document)
{ var copy = new XDocument(document); copy.Descendants("Tempo").Remove(); return copy.ToString(SaveOptions.DisableFormatting); }
var source = Path.Combine(work, "minimal.mscz");
using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
{
    using var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open());
    writer.Write("<museScore version='4.50'><Score><Staff id='1'><Measure><voice><Tempo><tempo>2</tempo><followText>1</followText><text>quarter = 120</text></Tempo><Chord><durationType>quarter</durationType><Note><pitch>69</pitch><tpc>17</tpc></Note></Chord></voice></Measure><Measure><voice><Tempo><tempo>1.5</tempo><followText>1</followText><text>quarter = 90</text></Tempo><Rest><durationType>whole</durationType></Rest></voice></Measure></Staff></Score></museScore>");
}
var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
foreach (var speed in new[] { .25, .5, .75, 1d, 1.25, 1.5 })
{
    var copy = Path.Combine(work, $"study-{speed.ToString(CultureInfo.InvariantCulture)}.mscz");
    ScoreStudyAudio.CreateTempoCopy(source, copy, speed);
    var doc = Read(copy);
    Check(NotesOnly(Read(source)) == NotesOnly(doc), "Non-tempo score contents changed");
    Check(doc.Descendants("tempo").Select(e => double.Parse(e.Value, CultureInfo.InvariantCulture)).SequenceEqual(new[] { 2 * speed, 1.5 * speed }), "All tempo events must scale");
    Check(doc.Descendants("followText").All(e => e.Value == "0"), "Text must not override study tempo");
}
Check(sourceHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), "Original preserved");
try { ScoreStudyAudio.CreateTempoCopy(source, source, .5); throw new Exception("Source overwrite accepted"); } catch (IOException) { checks++; }
try { ScoreStudyAudio.CreateTempoCopy(source, Path.Combine(work, "bad.mscz"), double.NaN); throw new Exception("Invalid speed accepted"); } catch (ArgumentOutOfRangeException) { checks++; }
var noTempo = Path.Combine(work, "no-tempo.mscz");
using (var zip = ZipFile.Open(noTempo, ZipArchiveMode.Create))
{
    var doc = Read(source); doc.Descendants("Tempo").Remove();
    using var stream = zip.CreateEntry("score.mscx").Open(); doc.Save(stream);
}
var defaultCopy = Path.Combine(work, "default-tempo.mscz");
ScoreStudyAudio.CreateTempoCopy(noTempo, defaultCopy, .5);
Check(Read(defaultCopy).Descendants("Tempo").Single().Element("tempo")!.Value == "1", "Implicit default tempo must scale");

var nativeResults = new List<object>();
if (args.Length > 1)
{
    var musicXml = Path.Combine(work, "study.musicxml");
    File.WriteAllText(musicXml, """
        <?xml version="1.0" encoding="utf-8"?>
        <score-partwise version="4.0"><work><work-title>Estudo sintético</work-title></work>
        <part-list><score-part id="P1"><part-name>Flute</part-name><score-instrument id="I1"><instrument-name>Flute</instrument-name></score-instrument><midi-instrument id="I1"><midi-channel>1</midi-channel><midi-program>74</midi-program></midi-instrument></score-part></part-list>
        <part id="P1"><measure number="1"><attributes><divisions>1</divisions><key><fifths>0</fifths></key><time><beats>4</beats><beat-type>4</beat-type></time><clef><sign>G</sign><line>2</line></clef></attributes><barline location="left"><repeat direction="forward"/></barline><direction><direction-type><metronome><beat-unit>quarter</beat-unit><per-minute>120</per-minute></metronome></direction-type><sound tempo="120"/></direction><note><pitch><step>A</step><octave>4</octave></pitch><duration>4</duration><type>whole</type></note></measure>
        <measure number="2"><direction><direction-type><metronome><beat-unit>quarter</beat-unit><per-minute>90</per-minute></metronome></direction-type><sound tempo="90"/></direction><note><pitch><step>C</step><octave>5</octave></pitch><duration>4</duration><type>whole</type></note><barline location="right"><repeat direction="backward"/></barline></measure></part></score-partwise>
        """);
    var native = Path.Combine(work, "native.mscz");
    var exporter = new ScoreExporter(args[1], Path.Combine(work, "cache"));
    await exporter.ExportNativeAsync(musicXml, native, CancellationToken.None);
    var service = new ScoreStudyAudio(args[1], Path.Combine(work, "cache"));
    var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native)));
    double normalDuration = 0, normalPitch = 0;
    foreach (var speed in new[] { 1d, .5, .25, 1.5 })
    {
        var audio = await service.PrepareAsync(native, speed, new Progress<string>(Console.WriteLine), CancellationToken.None);
        var frequency = EstimatePitch(audio.Path);
        if (speed == 1) { normalDuration = audio.Duration.TotalSeconds; normalPitch = frequency; }
        else
        {
            Check(Math.Abs(audio.Duration.TotalSeconds - normalDuration - (28d / 3) * (1 / speed - 1)) < .45, "Duration must respect both tempos and repeat");
            Check(Math.Abs(frequency / normalPitch - 1) < .02, "Pitch must remain unchanged");
        }
        Check(frequency is > 420 and < 460, "A4 frequency");
        nativeResults.Add(new { speed, duration = audio.Duration.TotalSeconds, frequency, path = audio.Path });
    }
    Check(originalHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native))), "Native original preserved");
}
var report = JsonSerializer.Serialize(new { checks, nativeResults, passed = true }, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(work, "report.json"), report);
Console.WriteLine(report);

static double EstimatePitch(string path)
{
    var info = ScoreExporter.ReadWaveInfo(path);
    using var reader = new BinaryReader(File.OpenRead(path));
    reader.BaseStream.Position = info.DataOffset + (long)(info.SampleRate * .6) * info.Channels * 2;
    var values = new double[info.SampleRate / 4];
    for (var i = 0; i < values.Length; i++)
    { for (var c = 0; c < info.Channels; c++) values[i] += reader.ReadInt16(); }
    var bestLag = 0; var best = double.NegativeInfinity;
    for (var lag = (int)(info.SampleRate / 480); lag <= info.SampleRate / 400; lag++)
    {
        double sum = 0, energyA = 0, energyB = 0;
        for (var i = 0; i < values.Length - lag; i++)
        { sum += values[i] * values[i + lag]; energyA += values[i] * values[i]; energyB += values[i + lag] * values[i + lag]; }
        var correlation = sum / Math.Sqrt(energyA * energyB);
        if (correlation > best) { best = correlation; bestLag = lag; }
    }
    return bestLag == 0 ? 0 : info.SampleRate / (double)bestLag;
}
