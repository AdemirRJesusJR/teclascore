using System.Text;
using Teclas.Core;

internal static class NotationLyricsChecks
{
    /// <summary>One track at 120 bpm, 480 ticks per quarter: <paramref name="notes"/> quarter notes from tick 0, with optional lyric events before some of them.</summary>
    private static byte[] Midi(int notes, Func<int, string?>? museScoreLyric = null)
    {
        var track = new List<byte> { 0, 0xFF, 0x51, 3, 0x07, 0xA1, 0x20, 0, 0xFF, 0x03, 3, (byte)'V', (byte)'o', (byte)'z', 0, 0xC0, 52 };
        for (int i = 0; i < notes; i++)
        {
            if (museScoreLyric?.Invoke(i) is { } text) { byte[] bytes = Encoding.UTF8.GetBytes(text); track.AddRange([0, 0xFF, 0x05, (byte)bytes.Length]); track.AddRange(bytes); }
            track.AddRange([0, 0x90, (byte)(60 + i % 5), 90, 0x83, 0x60, 0x80, (byte)(60 + i % 5), 0]);
        }
        track.AddRange([0, 0xFF, 0x2F, 0]);
        return [.. "MThd"u8, 0, 0, 0, 6, 0, 1, 0, 1, 1, 224, .. "MTrk"u8, 0, 0, (byte)(track.Count >> 8), (byte)track.Count, .. track];
    }

    private static string Positions(params (int Measure, double Milliseconds)[] events) =>
        "<?xml version=\"1.0\"?><score><elements/><events>" + string.Concat(events.Select(e => FormattableString.Invariant($"<event elid=\"{e.Measure}\" position=\"{e.Milliseconds}\"/>"))) + "</events></score>";

    private static string Note(int duration, params (string Verse, string Syllabic, string Text)[] lyrics) =>
        $"<note><pitch><step>C</step><octave>4</octave></pitch><duration>{duration}</duration><voice>1</voice><staff>1</staff>" +
        string.Concat(lyrics.Select(l => $"<lyric number=\"{l.Verse}\"><syllabic>{l.Syllabic}</syllabic><text>{l.Text}</text></lyric>")) + "</note>";

    private static byte[] GuitarPro(string version, int track, int startMeasure, string lyrics)
    {
        var bytes = new List<byte> { (byte)version.Length }; bytes.AddRange(Encoding.ASCII.GetBytes(version.PadRight(30, '\0')));
        bool five = version.Contains("v5");
        for (int field = 0; field < (five ? 9 : 8); field++) { byte[] data = Encoding.Latin1.GetBytes("campo " + field); bytes.AddRange(BitConverter.GetBytes(data.Length + 1)); bytes.Add((byte)data.Length); bytes.AddRange(data); }
        bytes.AddRange(BitConverter.GetBytes(1)); bytes.AddRange(BitConverter.GetBytes(5)); bytes.Add(4); bytes.AddRange("nota"u8); // one notice line
        if (!five) bytes.Add(0);
        byte[] line = Encoding.Latin1.GetBytes(lyrics);
        bytes.AddRange(BitConverter.GetBytes(track)); bytes.AddRange(BitConverter.GetBytes(startMeasure)); bytes.AddRange(BitConverter.GetBytes(line.Length)); bytes.AddRange(line);
        for (int i = 0; i < 4; i++) { bytes.AddRange(BitConverter.GetBytes(1)); bytes.AddRange(BitConverter.GetBytes(0)); }
        bytes.AddRange(new byte[64]);
        return [.. bytes];
    }

    internal static void Run(Action<bool, string> check)
    {
        var chunks = NotationLyrics.Chunks("Eu can-to  a+a [coro] can--ção\r\nfim-");
        check(chunks.SequenceEqual([("Eu", false), ("can", true), ("to", false), ("a a", false), ("can", true), ("ção", false), ("fim", true)]),
            "Guitar Pro lyric line: spaces, hyphens, + and [comments]: " + string.Join("|", chunks.Select(c => c.Text + (c.Continues ? "-" : ""))));
        check(NotationLyrics.Chunks(" [só comentário] - ").Count == 0, "A lyric line with only a comment has no syllables");

        string folder = Path.Combine(Path.GetTempPath(), "teclas-letra-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            string gp4 = Path.Combine(folder, "quatro.gp4"), gp5 = Path.Combine(folder, "cinco.gp5"), gp3 = Path.Combine(folder, "tres.gp3");
            File.WriteAllBytes(gp4, GuitarPro("FICHIER GUITAR PRO v4.06", 2, 2, "A-mor de ver-ão"));
            File.WriteAllBytes(gp5, GuitarPro("FICHIER GUITAR PRO v5.10", 1, 3, "Co-ra-ção"));
            File.WriteAllBytes(gp3, GuitarPro("FICHIER GUITAR PRO v3.00", 1, 1, "x"));
            var four = NotationLyrics.ReadGuitarProLyrics(gp4); var five = NotationLyrics.ReadGuitarProLyrics(gp5);
            check(four is { Track: 2 } && four.Lines.Count == 5 && four.Lines[0] == new NotationLyrics.GuitarProLine(2, "A-mor de ver-ão") && four.Lines[1].Text == "", "Guitar Pro 4 lyric block: track, first measure and text with accents");
            check(five is { Track: 1 } && five.Lines[0] == new NotationLyrics.GuitarProLine(3, "Co-ra-ção"), "Guitar Pro 5 lyric block comes after the music field and the notice");
            check(NotationLyrics.ReadGuitarProLyrics(gp3) is null && NotationLyrics.ReadGuitarProLyrics(Path.Combine(folder, "falta.gp5")) is null, "Guitar Pro 3 and missing files have no lyric block");
            File.WriteAllBytes(Path.Combine(folder, "cortado.gp4"), File.ReadAllBytes(gp4)[..60]);
            check(NotationLyrics.ReadGuitarProLyrics(Path.Combine(folder, "cortado.gp4")) is null, "A truncated Guitar Pro header gives no lyric instead of an error");

            // Two measures played twice: the second pass sings verse 2 where there is one and repeats verse 1 elsewhere.
            string xml = Path.Combine(folder, "partitura.musicxml"), positions = Path.Combine(folder, "compassos.mpos"), source = Path.Combine(folder, "origem.musicxml");
            File.WriteAllText(source, "<score-partwise/>");
            File.WriteAllText(xml, "<?xml version=\"1.0\"?><!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" \"http://www.musicxml.org/dtds/partwise.dtd\">" +
                "<score-partwise><part-list/><part id=\"P1\"><measure number=\"1\"><attributes><divisions>2</divisions></attributes>" +
                Note(2, ("1", "single", "Um"), ("2", "single", "Lá")) + Note(2, ("1", "begin", "do"), ("2", "single", "sol")) + Note(2, ("1", "end", "is")) + Note(2, ("1", "begin", "qua")) + "</measure>" +
                "<measure number=\"2\">" + Note(2, ("1", "end", "tro")) + "<note><chord/><pitch><step>E</step><octave>4</octave></pitch><duration>2</duration></note>" +
                "<note><rest/><duration>2</duration></note>" +
                "<note><pitch><step>D</step><octave>4</octave></pitch><duration>4</duration><lyric number=\"1\"><syllabic>single</syllabic><text>que</text><elision/><syllabic>single</syllabic><text>eu</text></lyric></note>" +
                "<backup><duration>8</duration></backup><note><pitch><step>C</step><octave>3</octave></pitch><duration>8</duration><voice>2</voice></note></measure></part>" +
                "<part id=\"P2\"><measure number=\"1\">" + Note(8, ("1", "single", "só")) + "</measure><measure number=\"2\">" + Note(8) + "</measure></part></score-partwise>");
            File.WriteAllText(positions, Positions((0, 0), (1, 2000), (0, 4000), (1, 6000)));
            var midi = MidiReader.Read(Midi(24));
            var sung = NotationLyrics.Read(source, xml, positions, midi);
            string text = string.Join("|", sung.Select(s => $"{s.Tick}:{s.Text}"));
            check(text == "0:Um |480:do-|960:is |1440:qua-|1920:tro |2880:que eu |3840:Lá |4320:sol |5760:tro |6720:que eu ",
                "Lyrics follow the playback order, the verse of each pass, chords, rests, voices and elisions: " + text);

            // Millisecond positions round: a syllable a couple of ticks off its note is put on it.
            File.WriteAllText(positions, Positions((0, 2.2), (1, 1998.4)));
            check(string.Join(",", NotationLyrics.Read(source, xml, positions, midi).Select(s => s.Tick)) == "0,480,960,1440,1920,2880", "Rounded positions snap to the notes");

            File.WriteAllText(positions, Positions((0, 0), (7, 2000)));
            bool refused = false; try { NotationLyrics.Read(source, xml, positions, midi); } catch (InvalidDataException) { refused = true; }
            check(refused, "Positions for a measure the score does not have are refused");
            File.WriteAllText(positions, Positions((0, 130), (1, 2130)));
            refused = false; try { NotationLyrics.Read(source, xml, positions, midi); } catch (InvalidDataException) { refused = true; }
            check(refused, "Syllables that do not fall on the notes are refused");
            File.WriteAllText(positions, Positions((0, 0), (1, 2000)));
            check(NotationLyrics.Read(source, Path.Combine(folder, "falta.musicxml"), positions, midi).Count == 0 && NotationLyrics.Read(source, xml, Path.Combine(folder, "falta.mpos"), midi).Count == 0,
                "Without MuseScore's other exports there is simply no lyric");
            string plain = Path.Combine(folder, "sem-letra.musicxml");
            File.WriteAllText(plain, "<score-partwise><part id=\"P1\"><measure number=\"1\">" + Note(4) + "</measure></part></score-partwise>");
            check(NotationLyrics.Read(source, plain, positions, midi).Count == 0, "A score without lyrics gives none");

            // Guitar Pro 4: the declared line goes on the beats with notes of its track, from its first measure; MuseScore's words are dropped.
            File.WriteAllText(xml, "<score-partwise><part id=\"P1\"><measure number=\"1\">" + Note(4, ("1", "single", "errada")) + "</measure><measure number=\"2\">" + Note(4) + "</measure></part>" +
                "<part id=\"P2\"><measure number=\"1\"><attributes><divisions>1</divisions></attributes>" + Note(1, ("1", "single", "A-mor")) + Note(1) + Note(1) + Note(1) + "</measure>" +
                "<measure number=\"2\">" + Note(1) + "<note><rest/><duration>1</duration><voice>1</voice><staff>1</staff></note>" + Note(1) + Note(1) +
                "<backup><duration>4</duration></backup><note><pitch><step>C</step><octave>4</octave></pitch><duration>4</duration><voice>5</voice><staff>2</staff></note></measure></part></score-partwise>");
            text = string.Join("|", NotationLyrics.Read(gp4, xml, positions, midi).Select(s => $"{s.Tick}:{s.Text}"));
            check(text == "1920:A-|2880:mor |3360:de ", "Guitar Pro 4 line is laid on its own track, skipping rests and the tablature staff: " + text);
            File.WriteAllBytes(gp4, GuitarPro("FICHIER GUITAR PRO v4.06", 9, 1, "A-mor"));
            check(NotationLyrics.Read(gp4, xml, positions, midi).Count == 0, "A lyric track the score does not have gives no lyric");
            check(NotationLyrics.Read(gp3, xml, positions, midi).Count == 0, "Guitar Pro 3 never takes MuseScore's scattered words");

            // Writing: lyric events in a new last track; the tracks with notes keep their numbers.
            byte[] plainMidi = Midi(24), withLyrics = NotationLyrics.Append(plainMidi, sung);
            var written = MidiReader.Read(withLyrics);
            check(written.Warnings.Count == 0 && written.Sources.Select(s => s.Key).SequenceEqual(midi.Sources.Select(s => s.Key)) && written.Notes.Count == 24
                && written.TextEvents.Where(e => e.Type == 5).Select(e => $"{e.Tick}:{e.Text}").SequenceEqual(sung.Select(s => $"{s.Tick}:{s.Text}"))
                && written.TextEvents.Where(e => e.Type == 5).All(e => e.Track == 1), "Lyric events are written at their ticks in a new last track");
            byte[] single = [.. "MThd"u8, 0, 0, 0, 6, 0, 0, 0, 1, 1, 224, .. plainMidi.AsSpan(14)];
            check(MidiReader.Read(NotationLyrics.Append(single, sung)) is { Format: 1, Warnings.Count: 0 }, "A single-track (format 0) MIDI becomes format 1 with the lyric track");

            // MuseScore's own lyric events come out without moving the notes after them.
            byte[] museScore = Midi(24, i => i % 3 == 0 ? "sí " : null);
            var cleaned = MidiReader.Read(NotationSource.RemoveLyricEvents(museScore)); var before = MidiReader.Read(museScore);
            check(before.TextEvents.Count(e => e.Type == 5) == 8 && cleaned.TextEvents.All(e => e.Type != 5) && cleaned.Warnings.Count == 0
                && cleaned.Notes.Select(n => (n.Pitch, n.Start, n.End)).SequenceEqual(before.Notes.Select(n => (n.Pitch, n.Start, n.End))) && cleaned.Sources[0].Name == "Voz",
                "Removing lyric events keeps every note and name where it was");
            check(NotationSource.RemoveLyricEvents([1, 2, 3]).Length == 3, "A damaged MIDI is returned as it came");

            // The whole conversion: our lyric replaces MuseScore's; without the other exports MuseScore's stays, except for Guitar Pro 3-5.
            File.WriteAllText(source, "<score-partwise><work><work-title>Com letra</work-title></work></score-partwise>");
            string song = Path.Combine(folder, "partitura-com-letra.musicxml");
            File.WriteAllText(song, "<score-partwise><part id=\"P1\">" + string.Concat(Enumerable.Range(0, 5).Select(m => $"<measure number=\"{m + 1}\">" +
                string.Concat(Enumerable.Range(0, 4).Select(b => Note(1, ("1", b % 2 == 0 ? "begin" : "end", "s" + (m * 4 + b))))) + "</measure>")) + "</part></score-partwise>");
            string target = Path.Combine(folder, "convertida.mid");
            var conversion = NotationSource.ConvertAsync((_, exports, _) =>
            {
                File.WriteAllBytes(exports.Midi, museScore); File.Copy(song, exports.MusicXml);
                File.WriteAllText(exports.MeasurePositions, Positions((0, 0), (1, 2000), (2, 4000), (3, 6000), (4, 8000)));
                return Task.CompletedTask;
            }, source, target, folder, CancellationToken.None).GetAwaiter().GetResult();
            var converted = MidiReader.Read(target);
            check(conversion is { Syllables: 20, LyricsNote: null } && converted.TextEvents.Count(e => e.Type == 5) == 20 && converted.TextEvents.First(e => e.Type == 5).Text == "s0-"
                && LyricsTrack.SyllableEvents(converted).Count == 20 && LyricsTrack.Coverage(converted)[0] is { Hits: 20 }
                && ScoreMetadataOrganizer.Suggest(target, converted.TextEvents).Title == "Com letra" && Directory.GetDirectories(folder, "importar-*").Length == 0,
                $"Conversion writes our lyric instead of MuseScore's, keeps the title and cleans its work folder ({conversion.Syllables} sílabas, {conversion.LyricsNote})");
            string alone = Path.Combine(folder, "so-midi.mid");
            conversion = NotationSource.ConvertAsync((_, exports, _) => { File.WriteAllBytes(exports.Midi, museScore); return Task.CompletedTask; }, source, alone, folder, CancellationToken.None).GetAwaiter().GetResult();
            check(conversion.Syllables == 8 && MidiReader.Read(alone).TextEvents.Count(e => e.Type == 5) == 8, "Without the other exports, MuseScore's own lyric events stay");
            string scattered = Path.Combine(folder, "gp.mid");
            conversion = NotationSource.ConvertAsync((_, exports, _) => { File.WriteAllBytes(exports.Midi, museScore); return Task.CompletedTask; }, gp3, scattered, folder, CancellationToken.None).GetAwaiter().GetResult();
            check(conversion.Syllables == 0 && MidiReader.Read(scattered).TextEvents.All(e => e.Type != 5), "Guitar Pro 3-5 never keeps MuseScore's scattered lyric events");
            string mismatched = Path.Combine(folder, "desencontrada.mid");
            conversion = NotationSource.ConvertAsync((_, exports, _) =>
            {
                File.WriteAllBytes(exports.Midi, Midi(24)); File.Copy(song, exports.MusicXml); File.WriteAllText(exports.MeasurePositions, Positions((0, 0), (9, 2000)));
                return Task.CompletedTask;
            }, source, mismatched, folder, CancellationToken.None).GetAwaiter().GetResult();
            check(conversion is { Syllables: 0, LyricsNote.Length: > 0 } && MidiReader.Read(mismatched).Notes.Count == 24, "A lyric that cannot be placed never fails the conversion: " + conversion.LyricsNote);
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
