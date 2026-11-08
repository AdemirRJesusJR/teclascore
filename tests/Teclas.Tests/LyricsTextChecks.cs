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
    }
}
