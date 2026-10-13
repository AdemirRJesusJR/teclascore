using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Teclas.Core;

internal static class HarmonyEditingChecks
{
    internal static void Run(Action<bool, string> check, string work)
    {
        var assembly = typeof(NativeScoreDocument).Assembly;
        var archiveType = assembly.GetType("Teclas.Core.NativeScoreArchive")!;
        var helper = assembly.GetType("Teclas.Core.NativeHarmonyEditing")!;
        const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(MethodInfo method, object? instance, params object?[] arguments)
        {
            try { return method.Invoke(instance, arguments); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        object Read(string path) => Call(archiveType.GetMethod("Read", Static)!, null, path)!;
        XElement Score(object archive) => (XElement)archiveType.GetProperty("Score", Instance)!.GetValue(archive)!;
        Dictionary<string, byte[]> Entries(object archive) => (Dictionary<string, byte[]>)archiveType.GetProperty("Entries", Instance)!.GetValue(archive)!;
        object Inspect(object archive, XElement h) => Call(helper.GetMethod("Inspect", Static)!, null, archive, h)!;
        T Field<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
        void Apply(object archive, XElement h, string root, string quality, int? inversion = null) =>
            Call(helper.GetMethod("Apply", Static)!, null, archive, h, root, quality, inversion);
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException) { rejected = true; }
            check(rejected, message);
        }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string md4 = (string)Call(helper.GetMethod("Md4", Static)!, null, Encoding.ASCII.GetBytes("abc"))!;
        check(md4 == "a448017aaf21d8525fc10ae87aa6729d", "Native diagram content names use the standard MD4 vector");
        check((string)Call(helper.GetMethod("Md4", Static)!, null, Array.Empty<byte>())! == "31d6cfe0d16ae931b73c59d7e0c089c0", "MD4 handles an empty resource");

        var folder = Path.Combine(work, "harmony-editing-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string Fixture(string label, string content, Dictionary<string, byte[]>? assets = null)
        {
            string path = Path.Combine(folder, label + ".mscz");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var output = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new UTF8Encoding(false)))
                output.Write("<museScore version=\"4.70\"><Score><Staff id=\"1\"><Measure><voice>" + content + "</voice></Measure></Staff></Score></museScore>");
            using (var output = new StreamWriter(zip.CreateEntry("META-INF/container.xml").Open(), new UTF8Encoding(false)))
                output.Write("<container><rootfiles><rootfile full-path=\"score.mscx\"/></rootfiles><retained>value</retained></container>");
            if (assets is not null) foreach (var (name, bytes) in assets)
            { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
            return path;
        }
        const string Harmony = "<Harmony><harmonyInfo><root>14</root></harmonyInfo><eid>h1</eid></Harmony>";
        const string Image = "<Image><path>old.svg</path><offset x=\"4.5\" y=\"-6.7\"/><size w=\"14\" h=\"4.17021\"/></Image>";
        const string Note = "<Chord><durationType>quarter</durationType><Note><pitch>60</pitch><tpc>14</tpc></Note></Chord>";
        // Fixture is independently written in the Python generator's SVG form.
        byte[] Keyboard()
        {
            var text = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"282\" height=\"84\" viewBox=\"0 0 282 84\"><title>Acorde em segunda inversão</title><desc>Quinta no grave; notas MIDI: 55,60,64</desc>");
            for (int i = 0; i < 14; i++) text.Append($"<rect x=\"{1 + i * 20}\" y=\"1\" width=\"20\" height=\"60\" fill=\"white\" stroke=\"#111111\" stroke-width=\"1\"/>");
            foreach (int x in new[] { 15, 35, 75, 95, 115, 155, 175, 215, 235, 255 })
                text.Append($"<rect x=\"{x}\" y=\"1\" width=\"12\" height=\"37\" fill=\"#111111\"/>");
            foreach (var (midi, x) in new[] { (55, 91), (60, 151), (64, 191) })
                text.Append($"<circle cx=\"{x}\" cy=\"49\" r=\"4.2\" fill=\"#111111\" data-midi=\"{midi}\"/>");
            foreach (int x in new[] { 11, 151 }) text.Append($"<text x=\"{x}\" y=\"79\" fill=\"#333333\" font-family=\"Arial\" font-size=\"16\" text-anchor=\"middle\">C</text>");
            return Encoding.UTF8.GetBytes(text.Append("</svg>").ToString());
        }
        byte[] originalAsset = Keyboard();
        var assets = new Dictionary<string, byte[]> { ["Pictures/old.svg"] = originalAsset };
        string original = Fixture("shared", Harmony + Image + Note + Harmony.Replace("h1", "h2") + Image + Note, assets);
        string originalHash = Hash(original);
        var archive = Read(original); var harmonies = Score(archive).Descendants("Harmony").ToArray();
        var info = Inspect(archive, harmonies[0]);
        check(Field<bool>(info, "CanEdit") && Field<bool>(info, "HasDiagram") && Field<int?>(info, "DiagramInversion") == 2,
            "Original TeclaScore SVG associates only with its immediate chord");
        string beforeNoOp = Score(archive).ToString(SaveOptions.DisableFormatting); int entriesBeforeNoOp = Entries(archive).Count;
        Apply(archive, harmonies[0], "C", "", null);
        check(Score(archive).ToString(SaveOptions.DisableFormatting) == beforeNoOp && Entries(archive).Count == entriesBeforeNoOp,
            "Unchanged chord and inversion do not rewrite the shared image asset");
        string untouchedHarmony = harmonies[1].ToString(SaveOptions.DisableFormatting);
        string unchangedNotes = string.Join("|", Score(archive).Descendants("Chord").Select(e => e.ToString(SaveOptions.DisableFormatting)));
        Apply(archive, harmonies[0], "F#", "m", 1);
        var images = Score(archive).Descendants("Image").ToArray();
        check(harmonies[0].Element("harmonyInfo")!.Element("root")!.Value == "20" && harmonies[0].Element("harmonyInfo")!.Element("name")!.Value == "m",
            "Native chord edit preserves F-sharp spelling as TPC 20");
        check(harmonies[0].Element("harmonyInfo")!.Elements().Select(e => e.Name.LocalName).SequenceEqual(new[] { "name", "root" }),
            "Edited chord suffix precedes its root in MuseScore serialization order");
        check(images[0].Element("path")!.Value != "old.svg" && images[1].Element("path")!.Value == "old.svg",
            "Editing one chord replaces only its image reference");
        check(Entries(archive)["Pictures/old.svg"].SequenceEqual(originalAsset), "Shared original diagram bytes are never overwritten");
        check(harmonies[1].ToString(SaveOptions.DisableFormatting) == untouchedHarmony &&
            string.Join("|", Score(archive).Descendants("Chord").Select(e => e.ToString(SaveOptions.DisableFormatting))) == unchangedNotes,
            "Unedited harmony and all notes remain exactly unchanged");
        var updated = Inspect(archive, harmonies[0]);
        check(Field<bool>(updated, "CanEdit") && Field<int?>(updated, "DiagramInversion") == 1,
            "Edited diagram remains recognizable with the selected inversion");
        var svg = XDocument.Parse(Encoding.UTF8.GetString(Entries(archive)["Pictures/" + images[0].Element("path")!.Value]));
        check(svg.Descendants().Where(e => e.Name.LocalName == "circle").Select(e => (int)e.Attribute("data-midi")!).SequenceEqual(new[] { 57, 61, 66 }),
            "F-sharp minor first inversion uses A3 C-sharp4 F-sharp4");
        check(images[0].Element("size")!.ToString() == images[1].Element("size")!.ToString() && images[0].Element("offset")!.Attribute("y")!.Value == "-6.7",
            "Diagram size and vertical placement survive the chord edit");
        check(XDocument.Parse(Encoding.UTF8.GetString(Entries(archive)["META-INF/container.xml"])).Root!.Element("retained")!.Value == "value",
            "Updating the image manifest preserves unrelated container entries");
        string destination = Path.Combine(folder, "new-copy.mscz");
        Call(archiveType.GetMethod("Save", Instance)!, archive, destination);
        check(File.Exists(destination) && Hash(original) == originalHash, "Editing saves a new archive without changing the original source hash");
        var reopened = Read(destination);
        check(Field<bool>(Inspect(reopened, Score(reopened).Descendants("Harmony").First()), "CanEdit"), "Saved native archive retains its new diagram link");

        var bare = Read(Fixture("without-diagram", Harmony + Note));
        var bareChord = Score(bare).Descendants("Harmony").Single();
        check(Field<bool>(Inspect(bare, bareChord), "CanEdit") && !Field<bool>(Inspect(bare, bareChord), "HasDiagram"), "A conventional chord without an image is editable");
        Apply(bare, bareChord, "Db", "maj7");
        check(bareChord.Element("harmonyInfo")!.Element("root")!.Value == "9" && !Score(bare).Descendants("Image").Any(),
            "D-flat is stored as TPC 9 without inserting an unrequested diagram");
        Apply(bare, bareChord, "C", "");
        check(bareChord.Element("harmonyInfo")!.Element("name") is null, "Changing to a major triad removes the previous chord suffix");
        foreach (var (badRoot, badQuality, badInversion) in new (string, string, int?)[] { ("H", "", null), ("C/G", "", null), ("C", "unrecognized", null), ("C", "", 3) })
            Reject(() => Apply(bare, bareChord, badRoot, badQuality, badInversion), "Unsupported root, quality or inversion is rejected before editing");
        foreach (var (label, content, resources) in new[]
        {
            ("unknown-image", Harmony + Image + Note, new Dictionary<string, byte[]> { ["Pictures/old.svg"] = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect/></svg>") }),
            ("missing-image", Harmony + Image + Note, new Dictionary<string, byte[]>()),
            ("two-images", Harmony + Image + Image + Note, assets),
            ("image-before", Image + Harmony + Note, assets),
            ("image-separated", Harmony + "<Segment/>" + Image + Note, assets),
            ("different-notes", Harmony.Replace("<root>14</root>", "<root>15</root>") + Image + Note, assets),
            ("slash", Harmony.Replace("</harmonyInfo>", "<base>15</base></harmonyInfo>") + Note, assets),
            ("roman", Harmony.Replace("<root>14</root>", "<function>V</function>") + Note, assets),
            ("nashville", Harmony.Replace("</harmonyInfo>", "</harmonyInfo><harmonyType>2</harmonyType>") + Note, assets),
            ("degrees", Harmony.Replace("</harmonyInfo>", "<degree>9</degree></harmonyInfo>") + Note, assets)
        })
        {
            var sample = Read(Fixture(label, content, resources)); var h = Score(sample).Descendants("Harmony").Single();
            var blocked = Inspect(sample, h);
            check(!Field<bool>(blocked, "CanEdit") && Field<string>(blocked, "Restriction").Length > 0, label + " gets a concrete preservation restriction");
            Reject(() => Apply(sample, h, "D", "m", 2), label + " cannot silently desynchronize or discard a diagram");
        }
        foreach (var (root, quality, inversion, expected) in new[]
        {
            (0, "", 0, new[] { 60, 64, 67 }), (0, "", 1, new[] { 52, 55, 60 }), (0, "", 2, new[] { 55, 60, 64 }),
            (6, "m", 0, new[] { 54, 57, 61 }), (6, "m", 1, new[] { 57, 61, 66 }), (6, "m", 2, new[] { 61, 66, 69 }),
            (5, "m7", 2, new[] { 60, 63, 65, 68 })
        })
            check(((int[])Call(helper.GetMethod("Pitches", Static)!, null, root, quality, inversion)!).SequenceEqual(expected),
                $"Native keyboard voicing matches expected notes for {root}/{quality}/{inversion}");
    }
}
