using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class App
{
    // MusicXML and Guitar Pro added to the library: converted into MIDI in the output folder, title and names kept,
    // the same file converted only once, failures shown. MuseScore's part is replaced by a fixture unless a real
    // MuseScore is given as the third argument (then the same files also go through the real conversion).
    private async void VerifyNotationImportUi(string reportPath, string? museScore)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "notation-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(museScore is null ? 60 : 240) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "notation import smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            string settingsPath = Path.Combine(work, "settings.json"), output = Path.Combine(work, "saida"), folder = Path.Combine(output, NotationSource.Folder);
            new Settings { OutputDirectory = output, CurrentPage = "library" }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.importDuplicatesDialog = w => { foreach (var row in w.Rows) row.Both.IsChecked = true; return true; };
            string? shown = null; window.showFailure = (title, message) => shown = title + ": " + message;
            window.Show(); Flush();

            string sources = Path.Combine(work, "originais"); Directory.CreateDirectory(sources);
            string xml = Path.Combine(sources, "arquivo de outro editor.musicxml"), mxl = Path.Combine(sources, "compactada.mxl"),
                tab = Path.Combine(sources, "tablatura.gp3"), other = Path.Combine(sources, "planilha.xml"), midi = Path.Combine(sources, "comum.mid");
            File.WriteAllText(xml, NotationMusicXml("Canção em XML", "Maria Compositora"), new UTF8Encoding(false));
            using (var archive = ZipFile.Open(mxl, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("META-INF/container.xml").Open()))
                    writer.Write("<?xml version=\"1.0\"?><container><rootfiles><rootfile full-path=\"partitura.musicxml\"/></rootfiles></container>");
                using (var writer = new StreamWriter(archive.CreateEntry("partitura.musicxml").Open(), new UTF8Encoding(false)))
                    writer.Write(NotationMusicXml("Valsa compactada", ""));
            }
            File.WriteAllBytes(tab, NotationGuitarPro("Canção de teste", "Artista GP"));
            File.WriteAllText(other, "<?xml version=\"1.0\"?><planilha><linha/></planilha>");
            File.WriteAllBytes(midi, TwoPartMidi(9));
            var originals = new[] { xml, mxl, tab, other, midi }.ToDictionary(path => path, Hash);

            // The dialog offers the new formats and the menu names them.
            Check(global::Teclas.App.MainWindow.LibraryImportFilter.Contains("*.musicxml") && global::Teclas.App.MainWindow.LibraryImportFilter.Contains("*.mxl") && global::Teclas.App.MainWindow.LibraryImportFilter.Contains("*.gp5")
                && global::Teclas.App.MainWindow.LibraryImportFilter.Contains("*.gpx") && global::Teclas.App.MainWindow.LibraryImportFilter.Contains("*.mid;"), "dialog-filter-offers-musicxml-and-guitar-pro");
            var button = (Button)window.FindName("LibraryImportButton");
            Check(LogicalChildren(button).OfType<TextBlock>().Any(t => t.Text == "MIDI, MusicXML ou Guitar Pro") && ((string)button.ToolTip).Contains(".gp5"), "add-menu-names-the-formats");

            int shift = 0, exported = 0;
            window.notationMidiExporter = (source, exports, _) =>
            {
                exported++; File.WriteAllBytes(exports.Midi, TwoPartMidi(++shift));
                // MuseScore's other exports for a MusicXML source: the score itself and its four measures played once, two seconds each.
                if (Path.GetExtension(source) == ".musicxml")
                {
                    File.Copy(source, exports.MusicXml);
                    File.WriteAllText(exports.MeasurePositions, "<score><elements/><events>" + string.Concat(Enumerable.Range(0, 4).Select(m => $"<event elid=\"{m}\" position=\"{m * 2000}\"/>")) + "</events></score>");
                }
                return Task.CompletedTask;
            };

            // MusicXML, compressed MusicXML and Guitar Pro enter as MIDIs, next to an ordinary MIDI chosen with them.
            await window.ImportFilesForSmoke([xml, mxl, tab, midi]); Flush();
            Check(window.IsIdle && window.Files.Count == 4 && exported == 3 && shown is null, $"three-sources-and-a-midi-enter ({window.Files.Count} cards, {exported} conversões, {shown})");
            MidiRow Row(string stem) => window.Files.Single(row => Path.GetFileNameWithoutExtension(row.FullPath) == stem);
            var fromXml = Row("arquivo de outro editor"); var fromMxl = Row("compactada"); var fromTab = Row("tablatura");
            Check(new[] { fromXml, fromMxl, fromTab }.All(row => Path.GetDirectoryName(row.FullPath) == folder && Path.GetExtension(row.FullPath) == ".mid" && row.Document is not null && !row.IsNativeScore),
                "converted-midis-live-in-the-output-folder");
            Check(fromXml.ScoreTitle == "Canção em XML" && fromXml.ScoreComposer == "Maria Compositora", $"musicxml-title-and-composer-are-kept ({fromXml.ScoreTitle} / {fromXml.ScoreComposer})");
            Check(fromMxl.ScoreTitle == "Valsa compactada", $"compressed-musicxml-title-is-kept ({fromMxl.ScoreTitle})");
            Check(fromTab.ScoreTitle == "Canção de teste" && fromTab.ScoreArtist == "Artista GP", $"guitar-pro-title-and-artist-are-kept ({fromTab.ScoreTitle} / {fromTab.ScoreArtist})");
            var sung = fromXml.Document!.TextEvents.Where(e => e.Type == 5).ToArray();
            Check(sung.Length == 16 && sung[0].Text == "Can-" && sung[1].Text == "to " && sung[^1] is { Tick: 15 * 480, Text: "já " } && fromTab.Document!.TextEvents.All(e => e.Type != 5)
                && window.Status.Contains("1 com a letra"), $"musicxml-lyrics-reach-the-midi ({sung.Length} sílabas) " + window.Status);
            Check(window.Files.Single(row => row.FullPath == midi).Document is not null && window.Status.Contains("3 arquivo(s) em MIDI") && window.Status.Contains(NotationSource.Folder),
                "status-says-what-was-converted: " + window.Status);
            Check(window.Files.Select(row => Path.GetFileNameWithoutExtension(row.FullPath)).SequenceEqual(["arquivo de outro editor", "compactada", "tablatura", "comum"]) && ReferenceEquals(window.SelectedFile, fromXml),
                "cards-follow-the-chosen-order");
            Check(window.CurrentPage == "library" && window.SelectedFile is { IsNativeScore: false } && ((TabControl)window.FindName("EditorTabs")).SelectedIndex == 0, "import-opens-the-melody-step");
            CaptureFolderWindow(window, Path.Combine(reports, "notation-import.png"));

            // The same file again: no second conversion, no second card, no new file.
            int files = Directory.GetFiles(folder).Length;
            await window.ImportFilesForSmoke([xml]); Flush();
            Check(exported == 3 && window.Files.Count == 4 && Directory.GetFiles(folder).Length == files && ReferenceEquals(window.SelectedFile, fromXml) && shown is null,
                "same-source-is-not-converted-twice");

            // A changed file with the same name is another song: converted to a numbered name.
            File.WriteAllText(xml, NotationMusicXml("Canção em XML, segunda versão", "Maria Compositora"), new UTF8Encoding(false));
            await window.ImportFilesForSmoke([xml]); Flush();
            Check(exported == 4 && window.Files.Count == 5 && File.Exists(Path.Combine(folder, "arquivo de outro editor (2).mid"))
                && window.SelectedFile?.ScoreTitle == "Canção em XML, segunda versão", "changed-source-becomes-a-new-midi");
            originals[xml] = Hash(xml);

            // A plain .xml that is not a score, and a file MuseScore cannot read: shown, nothing enters, nothing is left behind.
            files = Directory.GetFiles(folder).Length;
            await window.ImportFilesForSmoke([other]); Flush();
            Check(window.Files.Count == 5 && exported == 4 && shown is not null && shown.Contains("planilha.xml") && shown.Contains("não é uma partitura MusicXML"), "plain-xml-is-refused: " + shown);
            shown = null;
            string broken = Path.Combine(sources, "quebrada.gp5"); File.WriteAllBytes(broken, [1, 2, 3]);
            window.notationMidiExporter = (_, exports, _) => { File.WriteAllBytes(exports.Midi, [0]); throw new InvalidOperationException("Não foi possível exportar a partitura (MuseScore 1)."); };
            await window.ImportFilesForSmoke([broken, midi]); Flush();
            Check(window.IsIdle && window.Files.Count == 5 && shown is not null && shown.Contains("quebrada.gp5") && Directory.GetFiles(folder).Length == files, "failed-conversion-is-shown-and-leaves-no-file: " + shown);
            shown = null;

            // A conversion that gives a MIDI without notes is refused too.
            window.notationMidiExporter = (_, exports, _) => { File.WriteAllBytes(exports.Midi, [.. "MThd"u8, 0, 0, 0, 6, 0, 0, 0, 1, 1, 224, .. "MTrk"u8, 0, 0, 0, 4, 0, 0xFF, 0x2F, 0]); return Task.CompletedTask; };
            await window.ImportFilesForSmoke([broken]); Flush();
            Check(window.Files.Count == 5 && shown is not null && Directory.GetFiles(folder).Length == files, "score-without-notes-is-refused: " + shown);
            shown = null;

            // Without MuseScore the reason is said and the MIDIs chosen with the file still enter.
            window.notationMidiExporter = null; window.MuseScorePath = Path.Combine(work, "sem-musescore.exe");
            string second = Path.Combine(sources, "outro.mid"); File.WriteAllBytes(second, TwoPartMidi(11));
            await window.ImportFilesForSmoke([tab.Replace("tablatura", "nova"), broken, second]); Flush();
            Check(window.Files.Count == 6 && shown is not null && shown.Contains("MuseScore") && window.Files.Any(row => row.FullPath == second), "missing-musescore-is-explained-and-midis-still-enter: " + shown);
            shown = null;

            Check(originals.All(pair => Hash(pair.Key) == pair.Value), "original-files-are-untouched");

            // Optional: the real MuseScore converts the same fixtures.
            bool native = museScore is not null && File.Exists(museScore);
            if (native)
            {
                window.MuseScorePath = museScore!;
                string realXml = Path.Combine(sources, "real.musicxml"), realTab = Path.Combine(sources, "real.gp3"), realLyrics = Path.Combine(sources, "real com letra.gp4");
                File.WriteAllText(realXml, NotationMusicXml("Conversão real em XML", "Maria Compositora"), new UTF8Encoding(false));
                File.WriteAllBytes(realTab, NotationGuitarPro("Conversão real em GP", "Artista GP"));
                File.WriteAllBytes(realLyrics, NotationGuitarPro("Letra real em GP", "Artista GP", "Eu can-to a can-ção-zi-nha [refrão] do mar e vol-to pra ca-sa bem de-va-gar"));
                await window.ImportFilesForSmoke([realXml, realTab, realLyrics]); Flush();
                Check(shown is null && window.Files.Count == 9 && window.Status.Contains("2 com a letra"), "native-conversion-adds-three-cards: " + shown + " | " + window.Status);
                var realFromXml = Row("real"); var realFromTab = Row("real (2)");
                Check(realFromXml.ScoreTitle == "Conversão real em XML" && realFromXml.Tracks.Count >= 2 && realFromXml.Tracks.Any(t => t.Source.Count == 16),
                    $"native-musicxml-has-its-parts ({realFromXml.ScoreTitle}; {string.Join(", ", realFromXml.Tracks.Select(t => t.Source.Name + "=" + t.Source.Count))})");
                Check(realFromTab.ScoreTitle == "Conversão real em GP" && realFromTab.ScoreArtist == "Artista GP" && realFromTab.Tracks.Count == 2
                    && realFromTab.Tracks[0].Source.Count == 16 && realFromTab.Tracks[1].Source.Count == 24
                    && realFromTab.Tracks.Select(t => t.Source.Name).SequenceEqual(["Vocal", "Violão"]),
                    $"native-guitar-pro-has-its-tracks ({realFromTab.ScoreTitle}; {string.Join(", ", realFromTab.Tracks.Select(t => t.Source.Name + "=" + t.Source.Count))})");
            }
            if (native)
            {
                // MuseScore's MIDI has no usable lyric for these: the syllables come from the score and from the Guitar Pro lyric line.
                static string Sung(MidiRow row) => string.Concat(row.Document!.TextEvents.Where(e => e.Type == 5).Select(e => e.Text));
                var xmlRow = window.Files.Single(row => Path.GetFileNameWithoutExtension(row.FullPath) == "real"); var gpRow = window.Files.Single(row => Path.GetFileNameWithoutExtension(row.FullPath) == "real com letra");
                Check(Sung(xmlRow) == "Can-to a can-ção-zi-nha do mar e vol-to pra ca-sa já " && LyricsTrack.Coverage(xmlRow.Document!)[0] is { Hits: 16 }, "native-musicxml-lyrics-are-on-the-notes: " + Sung(xmlRow));
                Check(Sung(gpRow) == "Eu can-to a can-ção-zi-nha do mar e vol-to pra ca-sa " && LyricsTrack.Coverage(gpRow.Document!)[0] is { Hits: 16, Source.Track: 0 }
                    && window.Files.Single(row => Path.GetFileNameWithoutExtension(row.FullPath) == "real (2)").Document!.TextEvents.All(e => e.Type != 5),
                    "native-guitar-pro-lyrics-follow-the-lyric-line: " + Sung(gpRow));
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, nativeMuseScore = native }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }

    /// <summary>A valid two-part MusicXML: sixteen quarter notes for the voice (with a chord symbol and a syllable on each), four whole notes for the piano.</summary>
    private static string NotationMusicXml(string title, string composer)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" \"http://www.musicxml.org/dtds/partwise.dtd\">\n<score-partwise version=\"4.0\">");
        xml.Append($"<work><work-title>{System.Security.SecurityElement.Escape(title)}</work-title></work><identification>");
        if (composer.Length > 0) xml.Append($"<creator type=\"composer\">{System.Security.SecurityElement.Escape(composer)}</creator>");
        xml.Append("</identification><part-list><score-part id=\"P1\"><part-name>Voz</part-name></score-part><score-part id=\"P2\"><part-name>Piano</part-name></score-part></part-list>");
        string[] steps = ["C", "D", "E", "G"];
        (string Text, string Syllabic)[] syllables = [("Can", "begin"), ("to", "end"), ("a", "single"), ("can", "begin"), ("ção", "middle"), ("zi", "middle"), ("nha", "end"), ("do", "single"),
            ("mar", "single"), ("e", "single"), ("vol", "begin"), ("to", "end"), ("pra", "single"), ("ca", "begin"), ("sa", "end"), ("já", "single")];
        for (int part = 1; part <= 2; part++)
        {
            xml.Append($"<part id=\"P{part}\">");
            for (int measure = 1; measure <= 4; measure++)
            {
                xml.Append($"<measure number=\"{measure}\">");
                if (measure == 1) xml.Append($"<attributes><divisions>1</divisions><key><fifths>0</fifths></key><time><beats>4</beats><beat-type>4</beat-type></time><clef><sign>{(part == 1 ? "G" : "F")}</sign><line>{(part == 1 ? 2 : 4)}</line></clef></attributes>");
                if (part == 1)
                {
                    if (measure == 1) xml.Append("<harmony><root><root-step>C</root-step></root><kind>major</kind></harmony>");
                    for (int beat = 0; beat < 4; beat++)
                        xml.Append($"<note><pitch><step>{steps[(beat + measure) % 4]}</step><octave>5</octave></pitch><duration>1</duration><type>quarter</type><lyric number=\"1\"><syllabic>{syllables[(measure - 1) * 4 + beat].Syllabic}</syllabic><text>{syllables[(measure - 1) * 4 + beat].Text}</text></lyric></note>");
                }
                else xml.Append($"<note><pitch><step>{steps[measure % 4]}</step><octave>3</octave></pitch><duration>4</duration><type>whole</type></note>");
                xml.Append("</measure>");
            }
            xml.Append("</part>");
        }
        return xml.Append("</score-partwise>").ToString();
    }

    /// <summary>
    /// A small Guitar Pro file: "Vocal" with sixteen quarter notes and "Violão" with eight three-note chords, four 4/4
    /// measures. Version 3 without lyrics; version 4 when a lyric line (for the Vocal track, from measure 1) is given.
    /// </summary>
    private static byte[] NotationGuitarPro(string title, string artist, string? lyrics = null)
    {
        var latin = Encoding.Latin1; var bytes = new List<byte>();
        void Int(int value) => bytes.AddRange(BitConverter.GetBytes(value));
        void Text(string value) { byte[] data = latin.GetBytes(value); Int(data.Length + 1); bytes.Add((byte)data.Length); bytes.AddRange(data); }
        byte[] version = Encoding.ASCII.GetBytes(lyrics is null ? "FICHIER GUITAR PRO v3.00" : "FICHIER GUITAR PRO v4.06");
        bytes.Add((byte)version.Length); bytes.AddRange(version); bytes.AddRange(new byte[30 - version.Length]);
        foreach (string text in new[] { title, "", artist, "", "", "", "", "" }) Text(text); // title, subtitle, artist, album, words, copyright, tab, instructions
        Int(0);            // notice lines
        bytes.Add(0);      // triplet feel
        if (lyrics is not null)
        {
            byte[] line = latin.GetBytes(lyrics);
            Int(1); Int(1); Int(line.Length); bytes.AddRange(line); // lyric track, first measure of line 1, its text
            for (int other = 0; other < 4; other++) { Int(1); Int(0); }
        }
        Int(100); Int(0);  // tempo, key
        if (lyrics is not null) bytes.Add(0); // octave
        for (int channel = 0; channel < 64; channel++)
        {
            Int(channel < 2 ? 73 : channel < 4 ? 25 : channel % 16 == 9 ? 0 : 24);
            bytes.AddRange([13, 8, 0, 0, 0, 0, 0, 0]); // volume, balance, chorus, reverb, phaser, tremolo, two blanks
        }
        Int(4); Int(2);    // measures, tracks
        for (int measure = 0; measure < 4; measure++) bytes.AddRange(measure == 0 ? [3, 4, 4] : new byte[] { 0 });
        foreach (var (name, channel) in new[] { ("Vocal", 1), ("Violão", 3) })
        {
            byte[] raw = latin.GetBytes(name);
            bytes.Add(0); bytes.Add((byte)raw.Length); bytes.AddRange(raw); bytes.AddRange(new byte[40 - raw.Length]);
            Int(6); foreach (int tuning in new[] { 64, 59, 55, 50, 45, 40, 0 }) Int(tuning);
            Int(1); Int(channel); Int(channel + 1); Int(24); Int(0); bytes.AddRange([255, 0, 0, 0]);
        }
        int[][] melody = [[0, 2, 3, 5], [7, 5, 3, 2], [0, 3, 7, 3], [0, 0, 0, 0]];
        for (int measure = 0; measure < 4; measure++)
            for (int track = 0; track < 2; track++)
            {
                if (track == 0)
                {
                    Int(4); // quarter notes on the first string
                    foreach (int fret in melody[measure]) bytes.AddRange([0, 0, 0x40, 0x20, 1, (byte)fret]);
                }
                else
                {
                    Int(2); // half-note chords on strings 2 to 4
                    for (int beat = 0; beat < 2; beat++) bytes.AddRange([0, 0xFF, 0x38, 0x20, 1, 1, 0x20, 1, 0, 0x20, 1, 2]);
                }
            }
        return [.. bytes];
    }
}
