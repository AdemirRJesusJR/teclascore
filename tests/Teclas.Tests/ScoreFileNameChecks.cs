using Teclas.Core;
using System.Text;

internal static class ScoreFileNameChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var cases = new (string? Artist, string? Title, string? Fallback, string Expected)[]
        {
            ("Gustavo Lima", "A gente fez amor", "arquivo-errado.mid", "Gustavo Lima - A gente fez amor"),
            ("Roberto Carlos", "Luz divina", "karaoplay.mid", "Roberto Carlos - Luz divina"),
            ("", "Canção", null, "Canção"),
            (null, "Canção 2.0", null, "Canção 2.0"),
            ("  Artista  ", " Título  ", null, "Artista - Título"),
            ("Artista\nA", "Minha\tcanção", null, "Artista A - Minha canção"),
            ("Artista/Grupo", "Título: \"ao vivo\"?", null, "Artista-Grupo - Título- -ao vivo--"),
            ("", "../Nome: música?", null, "-Nome- música-"),
            ("", "CON", null, "Partitura - CON"),
            ("", "LPT¹.txt", null, "Partitura - LPT¹.txt"),
            ("", "..", null, "Partitura"),
            ("", "", @"C:\coleção\Fonte.mid", "Fonte"),
            ("Artista", "", @"C:\coleção\Fonte.mscz", "Artista - Fonte"),
            (null, null, null, "Partitura"),
            ("", "Título\0final", null, "Título-final"),
            ("", "Canção... ", null, "Canção")
        };
        foreach (var item in cases)
            check(ScoreFileName.Stem(item.Artist, item.Title, item.Fallback) == item.Expected, "Property filename: " + item.Expected);
        string unicode = ScoreFileName.Stem("", string.Concat(Enumerable.Repeat("🎹", 140)));
        check(unicode.EnumerateRunes().Count() == 100 && !unicode.Contains('\uFFFD'), "Long filename preserves complete Unicode characters");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        check(ScoreFileName.Unique("Artista - Canção", ".mscz", names) == "Artista - Canção.mscz"
            && ScoreFileName.Unique("artista - canção", ".mscz", names) == "artista - canção (2).mscz"
            && ScoreFileName.Unique("Artista - Canção", ".mscz", names) == "Artista - Canção (3).mscz", "MuseScore duplicate exports use readable numbered names");
    }
}
