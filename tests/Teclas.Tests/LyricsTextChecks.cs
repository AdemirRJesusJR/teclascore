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
    }
}
