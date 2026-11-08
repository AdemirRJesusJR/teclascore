using Teclas.Core;

/// <summary>Portuguese text helpers for lyrics and titles: accents restored from file names and syllable division.</summary>
internal static class LyricsTextChecks
{
    public static void Run(Action<bool,string> check)
    {
        PortugueseAccents.UseEngineList(null);
        check(PortugueseAccents.Restore("Coracao esta em pedacos")=="Coração está em pedaços","Unambiguous words get their accents; \"esta em\" is \"está\"");
        check(PortugueseAccents.Restore("Mamae que e mamae")=="Mamãe que e mamãe","\"e\" is ambiguous and stays as written");
        check(PortugueseAccents.Restore("Esta noite")=="Esta noite","Demonstrative \"esta\" before a noun stays");
        check(PortugueseAccents.Restore("CORACAO DE MAE")=="CORAÇÃO DE MÃE" && PortugueseAccents.Restore("Voce nao sabe")=="Você não sabe","Capital letters are kept");
        check(PortugueseAccents.Restore("Coração esta em pedaços")=="Coração está em pedaços","A partly accented title gets the missing accents");
        check(PortugueseAccents.Restore("Sexta-feira rotineira")=="Sexta-feira rotineira" && PortugueseAccents.Restore("Pais e filhos")=="Pais e filhos","Unknown and ambiguous words are untouched");

        // The engine's extended list (C1) is read when present; "sem=com" pairs, plain words and "!palavra" exceptions.
        string engine=Path.Combine(Path.GetTempPath(),"teclas-acentos-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(engine);
        try
        {
            File.WriteAllText(Path.Combine(engine,"lyrics_accents_pt.txt"),"# lista ampliada\nsaudade forró\nxodo=xodó\n!mamae\n");
            PortugueseAccents.UseEngineList(engine);
            check(PortugueseAccents.Restore("Forro do xodo")=="Forró do xodó","Extended list adds words and pairs");
            check(PortugueseAccents.Restore("Mamae")=="Mamae","An exception in the extended list keeps the word as written");
            PortugueseAccents.UseEngineList(Path.Combine(engine,"nao-existe"));
            check(PortugueseAccents.Restore("Mamae")=="Mamãe" && PortugueseAccents.Restore("Forro")=="Forro","Without the file the base list is used");
        }
        finally { PortugueseAccents.UseEngineList(null); Directory.Delete(engine,true); }

        // Rule-based syllable division used when a whole lyric is pasted over the melody.
        static string S(string word) => string.Join("-",PortugueseSyllables.Split(word));
        var expected=new Dictionary<string,string>
        {
            ["casa"]="ca-sa",["carro"]="car-ro",["passado"]="pas-sa-do",["chuva"]="chu-va",["filho"]="fi-lho",["sonho"]="so-nho",
            ["prato"]="pra-to",["abraço"]="a-bra-ço",["saudade"]="sau-da-de",["coração"]="co-ra-ção",["queria"]="que-ri-a",
            ["guitarra"]="gui-tar-ra",["dia"]="di-a",["rainha"]="ra-i-nha",["cair"]="ca-ir",["noite"]="noi-te",["descer"]="des-cer",
            ["Você"]="Vo-cê",["amor,"]="a-mor,",["pedaços"]="pe-da-ços",["mãe"]="mãe",["estrela"]="es-tre-la",["perto"]="per-to",["quando"]="quan-do"
        };
        var wrong=expected.Where(p=>S(p.Key)!=p.Value).Select(p=>$"{p.Key}→{S(p.Key)}").ToArray();
        check(wrong.Length==0,"Portuguese syllables follow the division rules"+(wrong.Length>0?": "+string.Join(", ",wrong):""));
        var lyric=PortugueseSyllables.FromLyrics("Sexta-feira rotineira\n\nCasa azul");
        check(lyric.Count==12 && lyric[0].LineStart && lyric[1].Text=="ta" && lyric[1].Continues && lyric[2].Text=="fei" && !lyric[2].WordStart,
            "A hyphenated word continues across the hyphen");
        check(lyric[8].Text=="Ca" && lyric[8].StanzaStart && lyric[8].LineStart && !lyric[7].Continues && lyric[10].WordStart,
            "A blank line starts a new stanza and words start new syllable groups");
        check(PortugueseSyllables.FromLyrics("  \n ").Count==0 && PortugueseSyllables.Join(["Ca","sa"])=="Casa","Empty lyrics give no syllables");

        // The engine report on the lyrics (frente C1) as guided review points, tolerating older reports.
        static System.Text.Json.JsonElement J(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;
        const string bars="\"origin_removed_quarters\": 1, \"bars\": [{\"start\": 0, \"end\": 3840, \"meter\": [4,4]}, {\"start\": 3840, \"end\": 7680, \"meter\": [4,4]}, {\"start\": 7680, \"end\": 10560, \"meter\": [6,8]}]";
        var review=LyricsReviewReport.Points(J("{"+bars+", \"melody_analysis\": {\"lyrics\": {\"mode\": \"notes\", "+
            "\"dropped\": [{\"quarter\": 2.5, \"measure\": 1, \"text\": \"la\"}, {\"quarter\": 3, \"measure\": 1, \"text\": \"ra\"}, {\"quarter\": 99}], "+
            "\"crowded\": [{\"quarter\": 6, \"text\": \"que eu te\"}], \"spoken\": [{\"quarter\": 9.5, \"measure\": 3, \"text\": \"(falado: alô alô)\", \"kind\": \"spoken\"}]}}}"));
        check(review.Count==3 && review[0].Kind=="lyrics_dropped" && review[0].Measure==1 && review[0].Message.Contains("2 sílaba(s)") && review[0].Message.Contains("“la”, “ra”")
            && Math.Abs(review[0].Beat-2.5)<1e-9 && review[0].MeasureStartQuarter==1 && review[0].MeasureEndQuarter==5,"Dropped syllables become one point per measure, with the origin");
        check(review[1].Kind=="lyrics_crowded" && review[1].Measure==2 && review[1].Message.Contains("que eu te") && review[2].Kind=="lyrics_spoken" && review[2].Measure==3
            && review[2].Message.Contains("“alô alô”") && Math.Abs(review[2].Beat-2)<1e-9,"Crowded words and speech are placed by quarter or measure (6/8 counts eighths)");
        var lines=LyricsReviewReport.Points(J("{"+bars+", \"melody_analysis\": {\"lyrics\": {\"mode\": \"lines\", \"match_estimate\": 0.12, \"dropped\": [{\"quarter\": 2, \"text\": \"x\"}]}}}"));
        check(lines.Count==1 && lines[0].Kind=="lyrics_lines" && lines[0].Measure==1 && lines[0].Message.Contains("12"),"A lyric written by lines gives one point instead of every syllable");
        var old=LyricsReviewReport.Points(J("{"+bars+", \"melody_analysis\": {\"lyrics\": {\"syllables\": 10, \"crowded_notes\": [4000]}}}"));
        check(old.Count==1 && old[0].Kind=="lyrics_crowded" && old[0].Measure==2,"Older reports with crowded_notes still give the point");
        check(LyricsReviewReport.Points(J("{"+bars+"}")).Count==0 && LyricsReviewReport.Points(J("{\"melody_analysis\": {}}")).Count==0 && LyricsReviewReport.Points(J("[1]")).Count==0
            && LyricsReviewReport.Points(Path.Combine(Path.GetTempPath(),"nao-existe-"+Guid.NewGuid().ToString("N")+".json")).Count==0,"Reports without lyrics data give no points");
    }
}
