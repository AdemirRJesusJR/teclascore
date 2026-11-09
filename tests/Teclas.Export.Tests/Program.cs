using System.Security.Cryptography;
using System.IO;
using Teclas.Core;

int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
string work = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "teclas-export-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(work);
string raw = Path.Combine(work, "float-source.wav"), pcm = Path.Combine(work, "pcm16.wav");
float[] values = [-1, -.5f, 0, .5f, 1.5f, float.NaN];
using (var writer = new BinaryWriter(File.Create(raw)))
{
    writer.Write("RIFF"u8); writer.Write((uint)(36 + values.Length * 4)); writer.Write("WAVEfmt "u8); writer.Write(16u);
    writer.Write((ushort)3); writer.Write((ushort)1); writer.Write(44100u); writer.Write(176400u); writer.Write((ushort)4); writer.Write((ushort)32);
    writer.Write("data"u8); writer.Write((uint)(values.Length * 4)); foreach (float value in values) writer.Write(value);
}
var input = ScoreExporter.ReadWaveInfo(raw);
var converted = ScoreExporter.ConvertToPcm16(raw, pcm, CancellationToken.None);
Check(input.Format == 3 && input.BitsPerSample == 32, "Read float WAV");
Check(converted.Format == 1 && converted.BitsPerSample == 16, "PCM16 conversion");
Check(Math.Abs(input.Duration.TotalSeconds - converted.Duration.TotalSeconds) < .000001, "Preserve duration");
using (var reader = new BinaryReader(File.OpenRead(pcm)))
{
    reader.BaseStream.Position = 44;
    short[] expected = [short.MinValue, -16384, 0, 16384, short.MaxValue, 0];
    foreach (short value in expected) Check(reader.ReadInt16() == value, "PCM amplitude, clipping and nonfinite handling");
}
try { ScoreExporter.ConvertToPcm16(raw, raw, CancellationToken.None); throw new Exception("Source overwrite accepted"); }
catch (InvalidOperationException) { checks++; }
string truncated = Path.Combine(work, "truncated.wav");
File.WriteAllBytes(truncated, File.ReadAllBytes(raw)[..^2]);
try { ScoreExporter.ReadWaveInfo(truncated); throw new Exception("Truncated WAV accepted"); }
catch (InvalidDataException) { checks++; }

if (args.Length >= 3)
{
    string source = Path.GetFullPath(args[1]), executable = Path.GetFullPath(args[2]);
    var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
    var exporter = new ScoreExporter(executable, Path.Combine(work, "cache"));
    string pdf = Path.Combine(work, "partitura.pdf");
    File.WriteAllText(pdf, "existing PDF must survive cancellation");
    byte[] previous = File.ReadAllBytes(pdf);
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { await exporter.ExportPdfAsync(source, pdf, null, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { checks++; }
    }
    Check(File.ReadAllBytes(pdf).SequenceEqual(previous), "Cancellation preserves existing PDF");
    Check(!Directory.EnumerateFiles(work, ".teclas-*.pdf").Any(), "Cancellation cleans PDF temporary files");
    using (var duringExport = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
    {
        try { await exporter.ExportPdfAsync(source, pdf, null, duringExport.Token); throw new Exception("Running export ignored cancellation"); }
        catch (OperationCanceledException) { checks++; }
    }
    Check(File.ReadAllBytes(pdf).SequenceEqual(previous), "Interrupted native export preserves existing PDF");
    Check(!Directory.EnumerateFiles(work, ".teclas-*.pdf").Any(), "Interrupted native export cleans temporary files");
    await exporter.ExportPdfAsync(source, pdf, null, CancellationToken.None);
    Check(File.ReadAllBytes(pdf).AsSpan(0, 5).SequenceEqual("%PDF-"u8), "MuseScore exports real PDF");
    var audio = await exporter.PrepareAudioAsync(source, null, CancellationToken.None);
    var info = ScoreExporter.ReadWaveInfo(audio.Path);
    Check(info.Format == 1 && info.BitsPerSample == 16 && info.Channels == 2, "Compatible stereo PCM playback file");
    Check(info.Duration.TotalSeconds > 1 && info.Duration == audio.Duration, "Audio duration available before playback");
    DateTime timestamp = File.GetLastWriteTimeUtc(audio.Path);
    var cached = await exporter.PrepareAudioAsync(source, null, CancellationToken.None);
    Check(cached.Path == audio.Path && File.GetLastWriteTimeUtc(audio.Path) == timestamp, "Repeat playback reuses audio cache");
    Check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(sourceHash), "Score source preserved after exports");
    Console.WriteLine(await MediaPlaybackSmoke.RunAsync(audio.Path)); checks++;
    Console.WriteLine($"PDF: {pdf}\nAudio: {audio.Path}\nDuration: {audio.Duration}");
}
// Chord page and document properties appended to a MuseScore-style PDF (classic xref table), without MuseScore.
{
    string pdfPath = Path.Combine(work, "acordes.pdf"), scorePath = Path.Combine(work, "acordes.mscz");
    var body = new System.Text.StringBuilder("%PDF-1.4\n");
    var offsets = new List<int>();
    void Obj(string text) { offsets.Add(body.Length); body.Append($"{offsets.Count} 0 obj\n{text}\nendobj\n"); }
    Obj("<<\n/Title (Old)\n>>");
    Obj("<<\n/Type /Catalog\n/Pages 3 0 R\n>>");
    Obj("<<\n/Type /Pages\n/Kids \n[\n4 0 R\n]\n/Count 1\n>>");
    Obj("<<\n/Type /Page\n/Parent 3 0 R\n/MediaBox [0 0 595.000000 842.000000]\n>>");
    int xrefAt = body.Length;
    body.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
    foreach (int offset in offsets) body.Append($"{offset:D10} 00000 n \n");
    body.Append($"trailer\n<<\n/Size {offsets.Count + 1} \n/Info 1 0 R\n/Root 2 0 R\n/ID [ <00> <00> ]\n>>\nstartxref\n{xrefAt} \n%%EOF\n");
    File.WriteAllText(pdfPath, body.ToString(), System.Text.Encoding.Latin1);
    var svg = KeyboardDiagramSvg.Build(0, "m", 2, KeyboardDiagramSvg.Highlight, false, "C");
    using (var zip = System.IO.Compression.ZipFile.Open(scorePath, System.IO.Compression.ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open()))
            writer.Write("<museScore><Score><metaTag name=\"workTitle\">Luz divina</metaTag><metaTag name=\"artist\">Roberto Carlos</metaTag><Staff><Measure><voice>" +
                "<Harmony><harmonyInfo><name>m</name><root>14</root></harmonyInfo></Harmony><Image><path>cm.svg</path></Image>" +
                "<Harmony><harmonyInfo><name>m</name><root>14</root></harmonyInfo></Harmony><Image><path>cm.svg</path></Image>" +
                "<Harmony><harmonyInfo><root>12</root></harmonyInfo></Harmony></voice></Measure></Staff></Score></museScore>");
        using (var writer = new StreamWriter(zip.CreateEntry("Pictures/cm.svg").Open())) writer.Write(svg.ToString());
    }
    var (songTitle, songArtist, songChords) = PdfChordPage.ReadScore(scorePath);
    Check(songTitle == "Luz divina" && songArtist == "Roberto Carlos", "Chord page reads title and performer");
    Check(songChords.Select(c => c.Label).SequenceEqual(["Cm", "Bb"]) && songChords[0].Diagram is not null && songChords[1].Diagram is null, "Chord page lists each chord once, with its diagram");
    PdfChordPage.Append(pdfPath, songTitle, songArtist, songChords);
    string finished = File.ReadAllText(pdfPath, System.Text.Encoding.Latin1);
    Check(finished.StartsWith(body.ToString(), StringComparison.Ordinal), "Original PDF bytes kept (incremental update)");
    Check(System.Text.RegularExpressions.Regex.IsMatch(finished, @"3 0 obj\s*<<\s*/Type /Pages\s*/Kids \[4 0 R \d+ 0 R\]\s*/Count 2"), "Page tree gains the chord page");
    Check(finished.Contains("/Prev " + xrefAt) && finished.Contains("/Author <FEFF0052006F0062"), "New trailer links the old one and names the performer");
    int lastXref = int.Parse(System.Text.RegularExpressions.Regex.Matches(finished, @"startxref\s+(\d+)")[^1].Groups[1].Value);
    Check(finished.AsSpan(lastXref, 4).SequenceEqual("xref"), "startxref points at the new table");
    foreach (System.Text.RegularExpressions.Match entry in System.Text.RegularExpressions.Regex.Matches(finished[lastXref..], @"(\d{10}) 00000 n "))
        Check(System.Text.RegularExpressions.Regex.IsMatch(finished[int.Parse(entry.Groups[1].Value)..], @"^\d+ 0 obj"), "Every new xref offset points at an object");
    Check(finished.Contains("(Acordes desta música - Luz divina) Tj"), "Heading written in WinAnsi");
    string mapScore = Path.Combine(work, "mapa.mscz");
    using (var zip = System.IO.Compression.ZipFile.Open(mapScore, System.IO.Compression.ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open()))
    {
        string Bar(string content, string extra = "") => $"<Measure>{extra}<voice>{content}<Chord/></voice></Measure>";
        string Chord(int root, string name = "") => $"<Harmony><harmonyInfo><name>{name}</name><root>{root}</root></harmonyInfo></Harmony>";
        string Mark(string text) => $"<RehearsalMark><text>{text}</text></RehearsalMark>";
        writer.Write("<museScore><Score><Staff>" + Bar(Mark("Intro") + Chord(14, "m")) + Bar("") + Bar(Mark("Refrão") + Chord(12)) + Bar(Chord(12) + Chord(14, "m"))
            + Bar(Chord(12)) + Bar(Chord(12) + Chord(14, "m")) + Bar(Mark("Final") + Chord(16, "7"), "<startRepeat/>") + Bar(Chord(16, "7"), "<endRepeat>3</endRepeat>")
            + "</Staff></Score></museScore>");
    }
    var map = PdfChordPage.ReadMap(mapScore);
    Check(map.Count == 8 && map[2].Section == "Refrão" && map[6].StartRepeat && map[7].EndRepeat == 3, "Music map reads parts and repeats");
    var mapRows = PdfChordPage.MapRows(map);
    Check(mapRows.Count == 3 && mapRows[0].Bars.SequenceEqual(["Cm", "%"]), "Music map shows a held chord as %");
    Check(mapRows[1].Section == "Refrão" && mapRows[1].Bars.SequenceEqual(["Bb", "Bb  Cm"]) && mapRows[1].Times == 2, "Music map folds a repeated passage");
    Check(mapRows[2].Bars.SequenceEqual(["|: D7", "% :| 3×"]), "Music map writes repeat barlines");
    string mapPdf = Path.Combine(work, "mapa.pdf");
    File.WriteAllText(mapPdf, body.ToString(), System.Text.Encoding.Latin1);
    PdfChordPage.Append(mapPdf, "Luz divina", "", [], true, map);
    string withMap = File.ReadAllText(mapPdf, System.Text.Encoding.Latin1);
    Check(withMap.Contains("(Mapa da música - Luz divina) Tj") && withMap.Contains("(Refrão) Tj") && withMap.Contains("/Count 2"), "Music map page appended to the PDF");
    // "Letra e cifras": lines and stanzas of the lyric with each chord above the syllable where it starts.
    string lyricScore = Path.Combine(work, "Casa.mscz");
    using (var zip = System.IO.Compression.ZipFile.Open(lyricScore, System.IO.Compression.ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new System.Text.UTF8Encoding(false)))
    {
        static string H(int root, string name = "") => $"<Harmony><harmonyInfo>{(name.Length > 0 ? $"<name>{name}</name>" : "")}<root>{root}</root></harmonyInfo></Harmony>";
        static string N(string text, string syllabic = "single", string length = "quarter") =>
            $"<Chord><durationType>{length}</durationType><Lyrics>{(syllabic == "single" ? "" : $"<syllabic>{syllabic}</syllabic>")}<text>{text}</text></Lyrics><Note><pitch>72</pitch><tpc>14</tpc></Note></Chord>";
        static string R(string length) => $"<Rest><durationType>{length}</durationType></Rest>";
        string bars = "<Measure><voice><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>" + H(14) + N("Que") + N("sau", "begin") + N("da", "middle") + N("de", "end") + "</voice></Measure>"
            + "<Measure><voice>" + H(17, "m") + N("de", "single", "half") + R("half") + "</voice></Measure>"
            + "<Measure><voice>" + H(13) + R("measure") + "</voice></Measure>"
            + "<Measure><voice>" + H(15, "7") + N("vo", "begin") + N("cê", "end") + H(14) + N("meu") + N("bem") + "</voice></Measure>";
        writer.Write("<museScore version='4.70'><programVersion>4.7.5</programVersion><Score><metaTag name='workTitle'>Casa</metaTag><Part><Staff id='1'><StaffType group='pitched'><name>stdNormal</name></StaffType></Staff><Instrument id='piano'><trackName>Piano</trackName></Instrument></Part><Staff id='1'>"
            + bars + "</Staff></Score></museScore>");
    }
    var lyricLines = PdfLyricsPage.Read(lyricScore);
    string Show(LyricSheetLine line) => string.Concat(line.Parts.Select(p => (p.Chord.Length > 0 ? "[" + p.Chord + "]" : "") + p.Text + (p.JoinsNext ? "-" : ""))).Trim();
    Check(lyricLines.Select(Show).SequenceEqual(["[C]Que sau-da-de [Am]de", "[F]", "[G7]vo-cê [C]meu bem"]),
        "Without a report the lines come from the rests and the chords sit above their syllables: " + string.Join(" | ", lyricLines.Select(Show)));
    Check(lyricLines[1].Instrumental && lyricLines[2].StanzaStart && !lyricLines[0].StanzaStart && lyricLines[0].Words == "Que saudade de", "Instrumental chords get their own line and a long rest starts a stanza");
    // The engine report (validacao.json) gives the lines, paragraphs and a line sung by another part; origin removed.
    string review = Path.Combine(work, "Casa - revisao"); Directory.CreateDirectory(review);
    File.WriteAllText(Path.Combine(review, "validacao.json"), """
        {"origin_removed_quarters": 1, "melody_analysis": {"lyrics": {"lines": [
          {"tick": 480, "quarter": 1, "text": "Que saudade de", "paragraph": 1},
          {"quarter": 13, "text": "você", "paragraph": 2},
          {"quarter": 15, "texto": "meu bem", "parágrafo": 2},
          {"quarter": 21, "text": "la la", "paragraph": 3},
          {"tick": "estranho"}, 7]}}}
        """);
    lyricLines = PdfLyricsPage.Read(lyricScore);
    Check(lyricLines.Select(Show).SequenceEqual(["[C]Que sau-da-de [Am]de", "[F]", "[G7]vo-cê", "[C]meu bem", "la la"]) && lyricLines[4].StanzaStart && !lyricLines[3].StanzaStart,
        "Report lines split the verses, paragraphs start stanzas and lines without notes are kept: " + string.Join(" | ", lyricLines.Select(Show)));
    File.WriteAllText(Path.Combine(review, "validacao.json"), "{ quebrado");
    Check(PdfLyricsPage.Read(lyricScore).Count == 3, "A broken report falls back to the rests");
    // Karaoke marks in the MIDI: "/" new line, "\" new stanza (at the start of a syllable or as its own event).
    var karaoke = Enumerable.Range(0, 16).Select(i => new WrittenLyric(i, i switch { 8 => "/la", 12 => "\\la", _ => "la" })).ToList();
    karaoke.Add(new WrittenLyric(3.5, "/"));
    var marked = MidiReader.Read(SimpleMidiWriter.Write(120, 4, [new WrittenTrack("Voz", 0, 52, Enumerable.Range(0, 16).Select(i => new WrittenNote(i, 1, 60)).ToArray()) { Lyrics = karaoke.OrderBy(l => l.Start).ToArray() }]));
    var starts = PdfLyricsPage.FromMidi(marked, 1);
    Check(starts.Select(s => (s.Quarter, s.Stanza)).SequenceEqual([(3.0, false), (7.0, false), (11.0, true)]), "MIDI karaoke marks give line and stanza starts: " + string.Join(", ", starts));
    // A score whose syllables are all single (the engine's karaoke lyrics): the MIDI's spaces tell where words end.
    string[] sung = ["Eu ", "ta", "va ", "na ", "ba", "la", "da ", "com ", "meu ", "par", "cei", "ro,", "/Du", "du ", "can-", "tou"];
    string singleScore = Path.Combine(work, "Balada.mscz");
    using (var zip = System.IO.Compression.ZipFile.Open(singleScore, System.IO.Compression.ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("score.mscx").Open(), new System.Text.UTF8Encoding(false)))
    {
        // "can-" and "tou" crowded on one half note, as the engine writes two syllables on one note.
        string Note(string text) => text == "can-" ? "<Chord><durationType>half</durationType><Lyrics><text>can tou</text></Lyrics><Note><pitch>72</pitch><tpc>14</tpc></Note></Chord>"
            : text == "tou" ? "" : $"<Chord><durationType>quarter</durationType><Lyrics><text>{text.Trim(' ', '/').TrimEnd('-')}</text></Lyrics><Note><pitch>72</pitch><tpc>14</tpc></Note></Chord>";
        string measures = string.Concat(sung.Chunk(4).Select((four, i) => "<Measure><voice>" + (i == 0 ? "<TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>" : "") + string.Concat(four.Select(Note)) + "</voice></Measure>"));
        writer.Write("<museScore version='4.70'><programVersion>4.7.5</programVersion><Score><Part><Staff id='1'><StaffType group='pitched'><name>stdNormal</name></StaffType></Staff><Instrument id='piano'><trackName>Piano</trackName></Instrument></Part><Staff id='1'>"
            + measures + "</Staff></Score></museScore>");
    }
    string sungMidi = Path.Combine(work, "balada.mid");
    File.WriteAllBytes(sungMidi, SimpleMidiWriter.Write(120, 4, [new WrittenTrack("Voz", 0, 52, Enumerable.Range(0, 17).Select(i => new WrittenNote(i, 1, 60)).ToArray())
        { Lyrics = sung.Select((text, i) => new WrittenLyric(i + 1, text)).ToArray() }]));
    Directory.CreateDirectory(Path.Combine(work, "Balada - revisao"));
    File.WriteAllText(Path.Combine(work, "Balada - revisao", "validacao.json"), System.Text.Json.JsonSerializer.Serialize(new { origin_removed_quarters = 1, source = sungMidi }));
    var joined = PdfLyricsPage.Read(singleScore);
    Check(joined.Select(Show).SequenceEqual(["Eu ta-va na ba-la-da com meu par-cei-ro,", "Du-du cantou"]) && joined[0].Words == "Eu tava na balada com meu parceiro,",
        "Single syllables (and two crowded on one note) are joined into words by the MIDI's spaces and hyphens: " + string.Join(" | ", joined.Select(Show)));
    // The MIDI's lyric events a sixteenth after the score's syllables: line starts and words follow the measured offset.
    string lateScore = Path.Combine(work, "Balada atrasada.mscz"), lateMidi = Path.Combine(work, "balada-atrasada.mid");
    File.Copy(singleScore, lateScore);
    File.WriteAllBytes(lateMidi, SimpleMidiWriter.Write(120, 4, [new WrittenTrack("Voz", 0, 52, Enumerable.Range(0, 17).Select(i => new WrittenNote(i, 1, 60)).ToArray())
        { Lyrics = sung.Select((text, i) => new WrittenLyric(i + 1.25, text)).ToArray() }]));
    Directory.CreateDirectory(Path.Combine(work, "Balada atrasada - revisao"));
    File.WriteAllText(Path.Combine(work, "Balada atrasada - revisao", "validacao.json"), System.Text.Json.JsonSerializer.Serialize(new { origin_removed_quarters = 1, source = lateMidi }));
    var late = PdfLyricsPage.Read(lateScore);
    Check(late.Select(Show).SequenceEqual(["Eu ta-va na ba-la-da com meu par-cei-ro,", "Du-du cantou"]),
        "A constant offset between the MIDI's lyrics and the score's syllables is measured and removed: " + string.Join(" | ", late.Select(Show)));
    var leading = PdfLyricsPage.WordMarks(MidiReader.Read(SimpleMidiWriter.Write(120, 4, [new WrittenTrack("Voz", 0, 52, [new WrittenNote(0, 1, 60)])
        { Lyrics = Enumerable.Range(0, 16).Select(i => new WrittenLyric(i, (i % 3 == 0 ? " " : "") + "la")).ToArray() }])), 0);
    Check(leading.Count == 16 && leading[0].Continues && leading[1].Continues && !leading[2].Continues && leading[3].Continues && leading[0].Text == "la",
        "A space before the syllable also starts a word");
    Check(PdfLyricsPage.WordMarks(MidiReader.Read(SimpleMidiWriter.Write(120, 4, [new WrittenTrack("Voz", 0, 52, [new WrittenNote(0, 1, 60)])
        { Lyrics = Enumerable.Range(0, 16).Select(i => new WrittenLyric(i, "la")).ToArray() }])), 0).Count == 0, "Lyrics without any spaces say nothing about words");
    string lyricPdf = Path.Combine(work, "letra.pdf");
    File.WriteAllText(lyricPdf, body.ToString(), System.Text.Encoding.Latin1);
    PdfChordPage.Append(lyricPdf, "Casa", "Grupo", [], true, null, lyricLines);
    string withLyrics = File.ReadAllText(lyricPdf, System.Text.Encoding.Latin1);
    Check(withLyrics.Contains("(Letra e cifras - Casa) Tj") && withLyrics.Contains("(Grupo) Tj") && withLyrics.Contains("(Que) Tj") && withLyrics.Contains("(cê) Tj")
        && withLyrics.Contains("(G7) Tj") && withLyrics.Contains("(Am) Tj") && withLyrics.Contains("/Count 2"), "Lyrics page appended with words and chords");
    int queAt = withLyrics.IndexOf("(Que) Tj", StringComparison.Ordinal), chordAt = withLyrics.LastIndexOf("(C) Tj", queAt, StringComparison.Ordinal);
    double Td(int at, int group) => double.Parse(System.Text.RegularExpressions.Regex.Matches(withLyrics[..at], @"(-?[\d.]+) (-?[\d.]+) Td")[^1].Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
    Check(chordAt > 0 && Td(chordAt, 2) > Td(queAt, 2) && Math.Abs(Td(chordAt, 1) - Td(queAt, 1)) < .01, "The chord is printed right above its syllable");
    string noLyricsPdf = Path.Combine(work, "sem-letra.pdf");
    File.WriteAllText(noLyricsPdf, body.ToString(), System.Text.Encoding.Latin1);
    PdfChordPage.Append(noLyricsPdf, "Casa", "", [], true, null, []);
    Check(!File.ReadAllText(noLyricsPdf, System.Text.Encoding.Latin1).Contains("Letra e cifras"), "Scores without lyrics get no lyrics page");
    Check(KeyboardDiagramSvg.ToneNames(0, KeyboardDiagramVoicing.IntervalsOf("m"), "C", false, false)!.Values.Order().SequenceEqual(["C", "Eb", "G"]), "Diagram names follow the chord");
    Check(KeyboardDiagramSvg.MarkedPitches(svg).SequenceEqual(KeyboardDiagramVoicing.Pitches(0, "m", 2)), "Highlighted diagram marks its keys");
    var dots = KeyboardDiagramSvg.Build(0, "m", 2, KeyboardDiagramSvg.Dots, false, "C");
    var circles = dots.Elements().Where(e => e.Name.LocalName == "circle").ToArray();
    Check(KeyboardDiagramSvg.MarkedPitches(dots).SequenceEqual(KeyboardDiagramVoicing.Pitches(0, "m", 2)) && circles.Length == 3
        && circles.Count(c => (string?)c.Attribute("fill") == "none") == 1, "Dotted diagram marks its keys, the bass as a ring");
    Check(!dots.Elements().Any(e => e.Name.LocalName == "text"), "Dotted diagram has no letters");
    Check(KeyboardDiagramSvg.Normalize(null) == KeyboardDiagramSvg.Dots && KeyboardDiagramSvg.Normalize("destacado") == KeyboardDiagramSvg.Highlight, "Dots is the default diagram style");
}
Console.WriteLine($"PASS: {checks} export checks.");
