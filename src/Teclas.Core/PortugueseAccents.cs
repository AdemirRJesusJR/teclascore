using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Teclas.Core;

/// <summary>
/// Restores Portuguese accents in names written without them ("Coracao esta em pedacos" → "Coração está em pedaços"),
/// with the same word list as the engine (engine/lyrics_accents.py). Only unambiguous words change: a form that is also
/// a correct word without the accent (e/é, pais/país, sabia/sabiá...) is kept. When the engine ships its extended list
/// (engine/lyrics_accents_pt.txt) it is read too; without it the base list is used.
/// </summary>
public static partial class PortugueseAccents
{
    // Same words as engine/lyrics_accents.py (_WORDS).
    private const string BaseWords = """
        você vocês não então também além ninguém alguém porém até após atrás aliás já só pé fé céu réu mão mãos chão
        irmão irmã irmãos coração corações paixão paixões emoção emoções canção canções oração orações solidão
        razão perdão sertão verão nação nações ilusão ilusões multidão escuridão imensidão estação estações
        tão são vão estão serão terão darão farão dirão então cristão pão leão sabão sabões feijão algodão
        mãe mães pães cães alemão capitão violão violões sanfona mamãe papai vovó vovô
        será terá dará fará dirá virá estará ficará voltará chegará verá lembrará esquecerá morrerá viverá
        amará chorará cantará sonhará
        está estás
        três mês português inglês francês freguês burguês
        café cafuné bebê nenê buquê clichê
        você pescoço moço moça moços moças coraçãozinho laço abraço abraços braço braços pedaço pedaços
        espaço cansaço fracasso começo começa começou começar caçar caço criança crianças lembrança lembranças
        esperança esperanças dança danças balança mudança mudanças confiança segurança saudação maçã
        música músicas lágrima lágrimas lâmpada fácil difícil único única últimas último última próximo próxima
        sábado domingo número números história histórias memória memórias glória vitória sozinho
        coração amável impossível possível incrível terrível horrível sensível invisível
        água águas mágico mágica rápido rápida pássaro pássaros árvore árvores lábios lábio
        olá adeus ninguém
        avião aviões razões perdões ilusória canções
        cruéis anéis papéis pastéis fiéis hotéis
        herói heróis dói dói constrói destrói
        saí caí traí distraí atraí sorrí
        aí
        """;

    // Same as engine/lyrics_accents.py (_AMBIGUOUS): written without the accent they are also correct words.
    private static readonly HashSet<string> BaseAmbiguous = new(StringComparer.Ordinal)
    {
        "e", "esta", "estas", "pais", "sabia", "secretaria", "amem", "nos", "la", "ai", "avo", "magoa", "pe", "ha", "da", "de", "do",
        "se", "so", "sai", "cai", "trai", "sorri", "nossa", "publico", "pratica", "musica", "duvida", "valido", "transito",
        "critica", "fabrica", "cantara", "chorara", "sonhara", "amara", "morrera", "vivera", "esquecera", "fara", "vira",
        "sera", "tera", "dara", "ficara", "voltara", "chegara", "vera", "lembrara", "estara", "dira"
    };

    /// <summary>
    /// "esta" is "está" (is) before a place, a manner or a gerund; the demonstrative "esta" (this) comes before a noun.
    /// </summary>
    private static readonly HashSet<string> AfterEsta = new(StringComparer.Ordinal)
    {
        "em", "no", "na", "nos", "nas", "com", "sem", "bem", "mal", "aqui", "ali", "la", "lá", "so", "só", "tao", "tão", "muito",
        "demais", "sozinho", "sozinha", "chorando", "chegando", "doendo", "faltando", "acabando", "voltando", "pensando",
        "esperando", "sofrendo", "morrendo", "partindo", "indo", "vindo", "fazendo", "louco", "louca", "apaixonado", "apaixonada"
    };

    private static readonly object Gate = new();
    private static string? loadedFrom;
    private static Dictionary<string, string> table = Build(null);

    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    /// <summary>Uses the engine's extended list in <paramref name="engineDirectory"/> (lyrics_accents_pt.txt) when it exists.</summary>
    public static void UseEngineList(string? engineDirectory)
    {
        string? path = engineDirectory is null ? null : Path.Combine(engineDirectory, "lyrics_accents_pt.txt");
        lock (Gate)
        {
            if (path == loadedFrom) return;
            string? text = null;
            try { if (path is not null && File.Exists(path)) text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            table = Build(text);
            loadedFrom = path;
        }
    }

    /// <summary>
    /// Reads the extended list tolerantly: words separated by spaces or lines, "#" comments, "sem=com" pairs and
    /// "!palavra" for a form that must stay as written.
    /// </summary>
    private static Dictionary<string, string> Build(string? extra)
    {
        var ambiguous = new HashSet<string>(BaseAmbiguous, StringComparer.Ordinal);
        var words = BaseWords.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var pairs = new List<(string Plain, string Accented)>();
        foreach (string raw in (extra ?? "").Split('\n'))
        {
            string line = raw.Split('#')[0].Trim();
            foreach (string token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = token.Trim(',', ';').Normalize(NormalizationForm.FormC).ToLowerInvariant();
                if (item.Length == 0) continue;
                if (item.StartsWith('!')) ambiguous.Add(Plain(item[1..]));
                else if (item.Split('=') is [var plain, var accented] && plain.Length > 0 && accented.Length > 0) pairs.Add((Plain(plain), accented));
                else words.Add(item);
            }
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in words)
        {
            string word = entry.Normalize(NormalizationForm.FormC), plain = Plain(word);
            if (plain == word || ambiguous.Contains(plain) || plain.Length != word.Length) continue;
            result[plain] = word;
        }
        foreach (var (plain, accented) in pairs) if (!ambiguous.Contains(plain) && plain != accented) result[plain] = accented;
        // "só" and "pé" are words of their own; "so"/"pe" never are in Portuguese song names.
        result["so"] = "só"; result["pe"] = "pé";
        return result;
    }

    public static string Plain(string word) => new string(word.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC);

    /// <summary>A name with the accents of its unambiguous words restored (words already accented stay as they are).</summary>
    public static string Restore(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        Dictionary<string, string> words;
        lock (Gate) words = table;
        var matches = Word().Matches(text);
        var result = new StringBuilder(text);
        for (int index = matches.Count - 1; index >= 0; index--)
        {
            var match = matches[index];
            string lower = match.Value.ToLowerInvariant();
            string? accented = words.GetValueOrDefault(lower);
            if (accented is null && lower is "esta" or "estas" && index + 1 < matches.Count && AfterEsta.Contains(matches[index + 1].Value.ToLowerInvariant())
                && text[(match.Index + match.Length)..matches[index + 1].Index].Trim().Length == 0)
                accented = lower == "esta" ? "está" : "estás";
            if (accented is null || accented.Length != match.Length) continue;
            string cased = new(match.Value.Zip(accented, (original, fixedLetter) => char.IsUpper(original) ? char.ToUpperInvariant(fixedLetter) : fixedLetter).ToArray());
            result.Remove(match.Index, match.Length).Insert(match.Index, cased);
        }
        return result.ToString();
    }

    /// <summary>
    /// Accents taken from a reference spelling of the same words (the engine's suggestedTitle, made from the file name
    /// with its 12 thousand word list): each word of <paramref name="text"/> that matches a reference word except for
    /// accents takes its accents, keeping the case of <paramref name="text"/>. Words without a match are untouched.
    /// </summary>
    public static string FromReference(string text, string? reference)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(reference)) return text;
        var spelled = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match word in Word().Matches(reference))
        {
            string lower = word.Value.ToLowerInvariant(), plain = Plain(lower);
            if (lower != plain && !spelled.ContainsKey(plain)) spelled[plain] = lower;
        }
        var result = new StringBuilder(text);
        var matches = Word().Matches(text);
        for (int index = matches.Count - 1; index >= 0; index--)
        {
            var match = matches[index];
            string lower = match.Value.ToLowerInvariant();
            if (lower != Plain(lower) || !spelled.TryGetValue(lower, out var accented) || accented.Length != match.Length) continue;
            string cased = new(match.Value.Zip(accented, (original, fixedLetter) => char.IsUpper(original) ? char.ToUpperInvariant(fixedLetter) : fixedLetter).ToArray());
            result.Remove(match.Index, match.Length).Insert(match.Index, cased);
        }
        return result.ToString();
    }
}
