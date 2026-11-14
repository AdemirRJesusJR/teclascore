using System.IO.Compression;
using System.Text;
using Teclas.Core;

internal static class NotationSourceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(NotationSource.IsMusicXml(@"C:\a\Canção.MusicXML") && NotationSource.IsMusicXml("a.mxl") && NotationSource.IsMusicXml("a.xml") && !NotationSource.IsMusicXml("a.mid"),
            "MusicXML extensions are recognized without regard to case");
        check(new[] { "a.gp", "a.GP3", "a.gp4", "a.gp5", "a.gpx", "a.gtp" }.All(NotationSource.IsGuitarPro) && !NotationSource.IsGuitarPro("a.gpif") && !NotationSource.IsGuitarPro("a.mscz"),
            "Guitar Pro extensions are recognized");
        check(NotationSource.KindName("a.gp5") == "Guitar Pro" && NotationSource.KindName("a.mxl") == "MusicXML" && !NotationSource.IsSupported("a.mid") && !NotationSource.IsSupported("a.mscz"),
            "MIDI and MuseScore files are not notation sources");
        check(NotationSource.MusicXmlPattern == "*.musicxml;*.mxl;*.xml" && NotationSource.GuitarProPattern.StartsWith("*.gp;*.gp3"), "Dialog patterns list every extension");

        const string header = "<?xml version=\"1.0\"?><!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" \"http://www.musicxml.org/dtds/partwise.dtd\">";
        NotationMetadata Xml(string body) { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(header + body)); return NotationSource.ReadMusicXml(stream); }
        var full = Xml("<score-partwise><work><work-title>Asa  Branca</work-title></work><movement-title>Outro nome</movement-title><identification><creator type=\"lyricist\">Letrista</creator><creator type=\"composer\">Luiz Gonzaga</creator></identification><part-list/><part id=\"P1\"><measure><work-title>não é cabeçalho</work-title></measure></part></score-partwise>");
        check(full == new NotationMetadata("Asa Branca", "", "Luiz Gonzaga"), "MusicXML header: work title and composer, DTD ignored: " + full);
        check(Xml("<score-partwise><movement-title>Só o movimento</movement-title><part-list/></score-partwise>").Title == "Só o movimento", "MusicXML movement title is used when there is no work title");
        check(Xml("<score-partwise><work><work-title>Untitled score</work-title></work><identification><creator type=\"composer\">Composer / arranger</creator></identification></score-partwise>").IsEmpty,
            "Editor placeholders are not taken as title or composer");
        check(Xml("<score-partwise><work><work-title>Evidências</work-title></work><identification><miscellaneous><miscellaneous-field name=\"creationDate\">2026</miscellaneous-field><miscellaneous-field name=\"performer\">Chitãozinho &amp; Xororó</miscellaneous-field></miscellaneous></identification></score-partwise>")
            == new NotationMetadata("Evidências", "Chitãozinho & Xororó", ""), "MusicXML performer written by MuseScore is the artist");
        check(Xml("<score-timewise><work><work-title>Por tempo</work-title></work><measure/></score-timewise>").Title == "Por tempo" && Xml("<opml><work-title>x</work-title></opml>").IsEmpty,
            "Timewise scores are read; other XML documents give nothing");

        NotationMetadata Gpif(string body) { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body)); return NotationSource.ReadGpif(stream); }
        var gpif = Gpif("<GPIF><GPVersion>7</GPVersion><Score><Title><![CDATA[Evidências]]></Title><SubTitle><![CDATA[x]]></SubTitle><Artist><![CDATA[Chitãozinho & Xororó]]></Artist><Words><![CDATA[Letrista]]></Words><Music><![CDATA[José Augusto]]></Music></Score><Tracks><Track><Title>não</Title></Track></Tracks></GPIF>");
        check(gpif == new NotationMetadata("Evidências", "Chitãozinho & Xororó", "José Augusto"), "Guitar Pro 7 score header: " + gpif);
        check(Gpif("<GPIF><Score><Title/><Artist></Artist><Words>Só letra</Words></Score></GPIF>") == new NotationMetadata("", "", "Só letra"), "Guitar Pro 7: empty fields stay empty, words stand in for music");

        byte[] Binary(string version, params string[] fields)
        {
            var bytes = new List<byte> { (byte)version.Length }; bytes.AddRange(Encoding.ASCII.GetBytes(version.PadRight(30, '\0')));
            foreach (string field in fields) { byte[] data = Encoding.Latin1.GetBytes(field); bytes.AddRange(BitConverter.GetBytes(data.Length + 1)); bytes.Add((byte)data.Length); bytes.AddRange(data); }
            return [.. bytes];
        }
        NotationMetadata Gp(byte[] bytes) { using var stream = new MemoryStream(bytes); return NotationSource.ReadGuitarProBinary(stream); }
        check(Gp(Binary("FICHIER GUITAR PRO v5.00", "Fio de cabelo", "sub", "Chitãozinho", "álbum", "Letra", "Música", "c")) == new NotationMetadata("Fio de cabelo", "Chitãozinho", "Música"),
            "Guitar Pro 5 header: title, artist and music, Latin-1 accents");
        check(Gp(Binary("FICHIER GUITAR PRO v3.00", "Tocando em frente", "", "Almir Sater", "", "Renato Teixeira", "c")) == new NotationMetadata("Tocando em frente", "Almir Sater", "Renato Teixeira"), "Guitar Pro 3 header: the author stands in for the composer");
        check(Gp(Binary("OUTRO FORMATO", "x")).IsEmpty, "A file that is not Guitar Pro 3 to 5 gives no metadata");

        string folder = Path.Combine(Path.GetTempPath(), "teclas-notation-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            string truncated = Path.Combine(folder, "cortado.gp5"); File.WriteAllBytes(truncated, Binary("FICHIER GUITAR PRO v5.00", "Título")[..40]);
            string garbage = Path.Combine(folder, "lixo.mxl"); File.WriteAllBytes(garbage, [1, 2, 3, 4]);
            check(NotationSource.ReadMetadata(truncated).IsEmpty && NotationSource.ReadMetadata(garbage).IsEmpty && NotationSource.ReadMetadata(Path.Combine(folder, "falta.musicxml")).IsEmpty,
                "Damaged or missing files give no metadata instead of an error");

            string mxl = Path.Combine(folder, "compactada.mxl");
            using (var archive = ZipFile.Open(mxl, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("META-INF/container.xml").Open())) writer.Write("<container><rootfiles><rootfile full-path=\"dentro/p.xml\"/></rootfiles></container>");
                using (var writer = new StreamWriter(archive.CreateEntry("outra.musicxml").Open())) writer.Write("<score-partwise><work><work-title>Errada</work-title></work></score-partwise>");
                using (var writer = new StreamWriter(archive.CreateEntry("dentro/p.xml").Open())) writer.Write("<score-partwise><work><work-title>Certa</work-title></work></score-partwise>");
            }
            check(NotationSource.ReadMetadata(mxl).Title == "Certa", "Compressed MusicXML: the root file named by the container is read");
            string gp = Path.Combine(folder, "nova.gp");
            using (var archive = ZipFile.Open(gp, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("Content/score.gpif").Open())) writer.Write("<GPIF><Score><Title>Da GP 8</Title><Artist>Banda</Artist></Score></GPIF>");
            check(NotationSource.ReadMetadata(gp) == new NotationMetadata("Da GP 8", "Banda", ""), "Guitar Pro 7/8 archive is read");

            string plain = Path.Combine(folder, "dados.xml"), score = Path.Combine(folder, "partitura.xml");
            File.WriteAllText(plain, "<?xml version=\"1.0\"?><dados/>"); File.WriteAllText(score, header + "<score-partwise/>");
            check(!NotationSource.IsScoreFile(plain) && NotationSource.IsScoreFile(score) && NotationSource.IsScoreFile(truncated) && !NotationSource.IsScoreFile(Path.Combine(folder, "a.mid")),
                "A plain .xml is accepted only when it is a MusicXML score");

            // Stamping: the MIDI stays valid, its notes are the same, and the library reads the declared properties.
            byte[] track = [0, 0xFF, 0x03, 5, (byte)'P', (byte)'i', (byte)'a', (byte)'n', (byte)'o', 0, 0xC0, 21, 0, 0x90, 60, 90, 0x81, 0x70, 60, 0, 0, 0xFF, 0x2F, 0];
            byte[] midi = [.. "MThd"u8, 0, 0, 0, 6, 0, 0, 0, 1, 1, 224, .. "MTrk"u8, 0, 0, 0, (byte)track.Length, .. track];
            string hash = new string('a', 64);
            byte[] stamped = NotationSource.Stamp(midi, new("Canção do sertão", "Trio Nordestino", "João do Vale"), hash);
            var before = MidiReader.Read(midi); var after = MidiReader.Read(stamped);
            check(after.Notes.Count == 1 && (after.Notes[0].Pitch, after.Notes[0].Start, after.Notes[0].End, after.Notes[0].Program) == (before.Notes[0].Pitch, before.Notes[0].Start, before.Notes[0].End, before.Notes[0].Program) && after.Ppq == before.Ppq && after.Warnings.Count == 0 && after.Sources[0].Name == "Piano",
                "Stamped MIDI keeps its notes, resolution and track name");
            var suggestion = ScoreMetadataOrganizer.Suggest(Path.Combine(folder, "arquivo qualquer.mid"), after.TextEvents);
            check(suggestion.Title == "Canção do sertão" && suggestion.Artist == "Trio Nordestino" && suggestion.Composer == "João do Vale" && !suggestion.NeedsReview,
                $"Library reads the stamped title, artist and composer: {suggestion.Title} / {suggestion.Artist} / {suggestion.Composer}");
            var untitled = ScoreMetadataOrganizer.Suggest(Path.Combine(folder, "Zé Ramalho - Avôhai.mid"), MidiReader.Read(NotationSource.Stamp(midi, NotationMetadata.Empty, hash)).TextEvents);
            check(untitled == ScoreMetadataOrganizer.Suggest(Path.Combine(folder, "Zé Ramalho - Avôhai.mid"), before.TextEvents), "Without declared properties the file name is used as before: " + untitled.Title + " / " + untitled.Artist);
            string longTitle = string.Concat(Enumerable.Repeat("Título comprido ", 12)).Trim();
            check(MidiReader.Read(NotationSource.Stamp(midi, new(longTitle, "", ""), hash)).Notes.Count == 1, "A text longer than 127 bytes is written with a two-byte length");
            try { NotationSource.Stamp([1, 2, 3], NotationMetadata.Empty, hash); throw new Exception("Invalid MIDI was stamped"); } catch (InvalidDataException) { }

            // Guitar Pro 3-5 texts arrive from MuseScore with the sign extended; the accents come back and nothing else moves.
            byte[] name = Encoding.UTF8.GetBytes("Viol\uFFE3o \uFF96 a\uFFE7\uFFE3o");
            byte[] garbled = [0, 0xFF, 0x03, (byte)name.Length, .. name, 0, 0xC0, 25, 0, 0x90, 60, 90, 0x60, 64, 90, 0x60, 60, 0, 0, 64, 0, 0, 0xF0, 2, 1, 0xF7, 0, 0xFF, 0x2F, 0];
            byte[] fromGuitarPro = [.. "MThd"u8, 0, 0, 0, 6, 0, 1, 0, 2, 1, 224, .. "MTrk"u8, 0, 0, 0, 4, 0, 0xFF, 0x2F, 0, .. "MTrk"u8, 0, 0, 0, (byte)garbled.Length, .. garbled];
            var repaired = MidiReader.Read(NotationSource.RepairGuitarProTexts(fromGuitarPro)); var raw = MidiReader.Read(fromGuitarPro);
            check(repaired.Sources.Single().Name == "Violão – ação" && raw.Sources.Single().Name != repaired.Sources.Single().Name
                && repaired.Notes.Select(n => (n.Pitch, n.Start, n.End, n.Program)).SequenceEqual(raw.Notes.Select(n => (n.Pitch, n.Start, n.End, n.Program))) && repaired.Warnings.Count == 0,
                "Guitar Pro track names get their accents back: " + repaired.Sources.Single().Name);
            check(NotationSource.RepairGuitarProTexts(midi).AsSpan().SequenceEqual(midi) && NotationSource.RepairGuitarProTexts([1, 2, 3]).Length == 3
                && NotationSource.RepairGuitarProTexts(fromGuitarPro[..^6]).Length == fromGuitarPro.Length - 6, "A MIDI with nothing to repair, or damaged, is returned unchanged");

            // The stamp finds a previous conversion of the same content.
            string converted = Path.Combine(folder, "convertidos"); Directory.CreateDirectory(converted);
            File.WriteAllBytes(Path.Combine(converted, "a.mid"), midi); File.WriteAllBytes(Path.Combine(converted, "b.mid"), stamped);
            check(NotationSource.FindConverted(converted, hash) == Path.Combine(converted, "b.mid") && NotationSource.FindConverted(converted, new string('b', 64)) is null
                && NotationSource.FindConverted(Path.Combine(folder, "não existe"), hash) is null, "A previous conversion is found by the source's hash");

            // Conversion: the export step is injected; failures and empty scores leave no file.
            string source = Path.Combine(folder, "origem.musicxml"); File.WriteAllText(source, "<score-partwise><work><work-title>Da origem</work-title></work></score-partwise>");
            string target = Path.Combine(converted, "origem.mid");
            NotationSource.ConvertAsync((_, destination, _) => { File.WriteAllBytes(destination, midi); return Task.CompletedTask; }, source, target, CancellationToken.None).GetAwaiter().GetResult();
            check(NotationSource.FindConverted(converted, NotationSource.Sha256(source)) == target
                && ScoreMetadataOrganizer.Suggest(target, MidiReader.Read(target).TextEvents).Title == "Da origem", "Conversion stamps the source's hash and title");
            bool refused = false;
            try { NotationSource.ConvertAsync((_, destination, _) => { File.WriteAllBytes(destination, midi); return Task.CompletedTask; }, source, target, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (IOException) { refused = true; }
            check(refused && MidiReader.Read(target).Notes.Count == 1, "An existing destination is never replaced");
            string failed = Path.Combine(converted, "falhou.mid"); bool reported = false;
            try { NotationSource.ConvertAsync((_, destination, _) => { File.WriteAllBytes(destination, [0]); throw new InvalidOperationException("MuseScore 1"); }, source, failed, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { reported = true; }
            check(reported && !File.Exists(failed), "A failed conversion leaves no partial file");
            byte[] silent = [.. "MThd"u8, 0, 0, 0, 6, 0, 0, 0, 1, 1, 224, .. "MTrk"u8, 0, 0, 0, 4, 0, 0xFF, 0x2F, 0];
            reported = false;
            try { NotationSource.ConvertAsync((_, destination, _) => { File.WriteAllBytes(destination, silent); return Task.CompletedTask; }, source, failed, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { reported = true; }
            check(reported && !File.Exists(failed), "A score without notes is refused and leaves no file");
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
