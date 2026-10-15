using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Teclas.Core;

public record ScoreMetadataSuggestion(string Title,string Artist,string Composer,string Explanation,bool NeedsReview);

/// <summary>Conservative, offline suggestions from the file name and declared MIDI metadata.</summary>
public static class ScoreMetadataOrganizer
{
    private static readonly TimeSpan MatchTimeout=TimeSpan.FromMilliseconds(100);
    private static Regex Pattern(string value,RegexOptions options=RegexOptions.None)=>
        new(value,options|RegexOptions.CultureInvariant,MatchTimeout);
    private static readonly Regex Spaces=Pattern(@"\s+");
    private static readonly Regex WebAddress=Pattern(@"(?:https?://|www\.)[^\s<>()\[\]{}]+|\b(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?\.)+(?:com|net|org|br|pt|info|biz|tv|midi|online|site|mus|cc|io)(?:\.[a-z]{2})?\b(?:/[^\s<>()\[\]{}]*)?",RegexOptions.IgnoreCase);
    private static readonly Regex Provider=Pattern(@"\bkaraoplay\b",RegexOptions.IgnoreCase);
    private static readonly Regex EmptyBrackets=Pattern(@"\(\s*\)|\[\s*\]|\{\s*\}");
    private static readonly Regex TechnicalSuffix=Pattern(@"(?:\s*[-–—|]?\s*(?:\((?:karaok[eê]|midi|playback|arquivo midi)(?:\s+(?:version|vers[aã]o))?\)|\[(?:karaok[eê]|midi|playback|arquivo midi)(?:\s+(?:version|vers[aã]o))?\]|\b(?:karaok[eê]|midi|playback)))\s*$",RegexOptions.IgnoreCase);
    private static readonly Regex Separator=Pattern(@"\s+[-–—]\s+");
    private static readonly Regex Label=Pattern(@"^\s*(?:@T\s*)?(title|t[ií]tulo|song(?:\s+name)?|music(?:\s+title)?|artist|artista|int[eé]rprete|performer|singer|composer|compositor(?:a)?|composed\s+by|author|autor(?:a)?)\s*[:=]\s*(.+)$",RegexOptions.IgnoreCase);
    private static readonly Regex Attribution=Pattern(@"(?:\bcopyright\b|©|\(c\)|\b(?:sequenced|sequence|arranged|arrangement|programmed|converted|created|edited|arranjo|sequenciad[oa]|programad[oa]|words|lyrics|music|letra|m[uú]sica)\s*(?:by|por|:))",RegexOptions.IgnoreCase);
    private static readonly Regex GenericName=Pattern(@"^(?:(?:track|pista|channel|canal|part|parte|staff|midi|kar|song|sequence|sequencer|untitled|sem t[ií]tulo|unnamed|instrument|instrumento|melody|melodia|vocal|voice|voz|lyrics|letra|conductor|master|tempo|control|solo|karaok[eê]|system|sistema)(?:\s*\d+)?|\d+)$",RegexOptions.IgnoreCase);
    private static readonly Regex InstrumentName=Pattern(@"\b(?:piano|guitar|guitarra|viol[aã]o|bass|baixo|drum|bateria|percussion|percuss[aã]o|organ|[oó]rg[aã]o|brass|trumpet|trompete|trombone|string|strings|violin|violino|cello|sax|saxophone|saxofone|flute|flauta|clarinet|clarinete|oboe|pad|synth|synthesizer|lead|choir|accordion|acordeon|sanfona|marimba|vibraphone|timpani|harmonica|banjo|harp|harpa)\b",RegexOptions.IgnoreCase);
    private static readonly Regex VersionName=Pattern(@"^(?:ao vivo|ac[uú]stico|instrumental|vers[aã]o|version|parte|part)\b",RegexOptions.IgnoreCase);
    private static readonly Regex PerformerRole=Pattern(@"^(?:banda|band|grupo|group|dupla|orquestra|orchestra|coral|cantor|cantora|artista|artist|dj|mc)\b",RegexOptions.IgnoreCase);
    private static readonly Regex EditionBrackets=Pattern(@"\([^)]*\)|\[[^]]*\]");
    private static readonly Regex NumericTitle=Pattern(@"^\d+\s+\p{L}");
    private static readonly HashSet<string> NameConnectors=new(StringComparer.Ordinal)
        {"a","o","as","os","de","da","do","das","dos","e","and","of","the","van","von","del","la","los","las"};
    // These describe a phrase, not a list of performers. Both filename orders
    // use the same rules, and uncertain names stay intact for review.
    private static readonly HashSet<string> PhraseWords=new(StringComparer.Ordinal)
        {"eu","voce","voces","meu","minha","meus","minhas","nosso","nossa","teu","tua","ela","ele","eles","elas",
         "ninguem","alguem","amo","amei","amar","amou","quero","queria","vou","vai","vem","venha","fico","fiquei",
         "deixa","deixe","pego","pegou","fez","faz","fazer","sinto","senti","sou","estou","disse","diga","chorar",
         "posso","nao","you","your","my","me","we","our","take","want","never","love"};

    public static ScoreMetadataSuggestion Suggest(string path,IEnumerable<MidiTextEvent>? textEvents=null)
    {
        string fileName=Path.GetFileNameWithoutExtension(path);
        string cleanedFile=Clean(fileName);
        if(cleanedFile.Length==0)cleanedFile="Sem título";
        bool conflictingDeclarations=false;
        var explicitValues=new Dictionary<string,string>(StringComparer.Ordinal);
        var karaoke=new List<string>();
        var sequenceNames=new List<string>();
        foreach(var item in (textEvents??[]).Take(256))
        {
            if(item.Type is not (1 or 3) || item.Tick!=0)continue; // Copyright, lyrics and later annotations are not header evidence.
            string text=(item.Text??"");
            text=TruncateText(text,2048);
            foreach(string rawLine in text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries))
            {
                string line=rawLine.Trim('\0',' ','\t');
                Match label=Label.Match(line);
                if(label.Success)
                {
                    string value=Clean(label.Groups[2].Value);
                    if(value.Length==0 || Attribution.IsMatch(value))continue;
                    string key=LabelKey(label.Groups[1].Value);
                    if(explicitValues.TryGetValue(key,out string? previous))
                    {
                        if(!Same(previous,value))conflictingDeclarations=true;
                    }
                    else explicitValues[key]=value;
                    continue;
                }
                if(line.StartsWith("@T",StringComparison.OrdinalIgnoreCase))
                {
                    string value=Clean(line[2..]);
                    if(IsUseful(value) && !karaoke.Any(existing=>Same(existing,value)))karaoke.Add(value);
                }
                else if(item.Type==3 && item.Track==0 && item.Tick==0)
                {
                    string value=Clean(line);
                    if(IsUseful(value) && !InstrumentName.IsMatch(value))sequenceNames.Add(value);
                }
            }
        }
        // Declared MIDI fields can disambiguate either side of the filename.
        // Never let a title/artist convention override an explicit declaration.
        var fileParts=SplitArtistTitle(cleanedFile,explicitValues.GetValueOrDefault("title"),explicitValues.GetValueOrDefault("artist"));
        string title=fileParts.Title,artist=fileParts.Artist,composer="",origin=fileParts.Explanation;
        bool needsReview=fileParts.HasSeparator;
        if(karaoke.Count>0)
        {
            var embedded=SplitArtistTitle(karaoke[0],explicitValues.GetValueOrDefault("title"),explicitValues.GetValueOrDefault("artist"));
            title=embedded.Title;
            artist="";
            if(embedded.Artist.Length>0)artist=embedded.Artist;
            else if(fileParts.Artist.Length>0 && Same(title,fileParts.Title))artist=fileParts.Artist;
            else if(karaoke.Count==2 && IsPlausibleArtist(karaoke[1]))artist=karaoke[1];
            origin="Cabeçalho karaoke e nome do arquivo organizados.";
            needsReview=true; // @T fields have no standardized composer/performer distinction.
        }
        else if(sequenceNames.Count>0)
        {
            string candidate=sequenceNames[0];
            var embedded=SplitArtistTitle(candidate,explicitValues.GetValueOrDefault("title"),explicitValues.GetValueOrDefault("artist"));
            // An unlabelled track name may name an instrument. Only use it when it
            // corroborates the file name or carries the artist/title convention.
            if(embedded.Artist.Length>0 || Same(candidate,title))
            {
                title=embedded.Title;
                if(embedded.Artist.Length>0)artist=embedded.Artist;
                origin="Nome da sequência MIDI e arquivo organizados.";
                needsReview=true;
            }
        }
        if(explicitValues.Count>0)
        {
            if(explicitValues.TryGetValue("title",out string? declaredTitle))title=declaredTitle;
            if(explicitValues.TryGetValue("artist",out string? declaredArtist))artist=declaredArtist;
            if(explicitValues.TryGetValue("composer",out string? declaredComposer))composer=declaredComposer;
            origin="Campos identificados nos metadados do MIDI.";
            if(explicitValues.ContainsKey("title") && explicitValues.ContainsKey("artist") && explicitValues.Count>=2)
            {
                // Conflicting embedded values still deserve review; a conventional
                // file-name split alone does not make explicit metadata uncertain.
                needsReview=false;
            }
        }
        if(title.Length==0)title=cleanedFile;
        needsReview|=conflictingDeclarations;
        if(title=="Sem título")needsReview=true;
        if(title.Length>160 || artist.Length>120 || composer.Length>120)needsReview=true;
        title=Limit(title,160);artist=Limit(artist,120);composer=Limit(composer,120);
        if(needsReview)origin+=" Confira as sugestões.";
        return new(title,artist,composer,origin,needsReview);
    }

    private static string LabelKey(string key)
    {
        string normalized=key.ToLowerInvariant();
        if(normalized is "artist" or "artista" or "intérprete" or "interprete" or "performer" or "singer")return "artist";
        if(normalized.StartsWith("compos",StringComparison.Ordinal) || normalized is "author" or "autor" or "autora")return "composer";
        return "title";
    }

    private static (string Title,string Artist,bool HasSeparator,string Explanation) SplitArtistTitle(string value,string? declaredTitle=null,string? declaredArtist=null)
    {
        string[] parts=Separator.Split(value);
        const string unchanged="Nome do arquivo organizado automaticamente.";
        const string ambiguous="A ordem de título e intérprete no nome do arquivo é ambígua; o nome completo foi preservado no título.";
        if(parts.Length!=2)return(value,"",parts.Length>1,parts.Length>1?ambiguous:unchanged);
        string left=parts[0].Trim(),right=parts[1].Trim();
        bool titleLeft=!string.IsNullOrWhiteSpace(declaredTitle) && Same(declaredTitle,left);
        bool titleRight=!string.IsNullOrWhiteSpace(declaredTitle) && Same(declaredTitle,right);
        bool artistLeft=!string.IsNullOrWhiteSpace(declaredArtist) && Same(declaredArtist,left);
        bool artistRight=!string.IsNullOrWhiteSpace(declaredArtist) && Same(declaredArtist,right);
        if((titleLeft || artistRight) && !(titleRight || artistLeft) && IsPersonOrBand(right) && IsUseful(left))
            return(left,right,true,"Ordem título – intérprete identificada no arquivo e corroborada pelos campos do MIDI.");
        if((titleRight || artistLeft) && !(titleLeft || artistRight) && IsPersonOrBand(left) && IsUseful(right))
            return(right,left,true,"Ordem intérprete – título identificada no arquivo e corroborada pelos campos do MIDI.");
        if(VersionName.IsMatch(left) || VersionName.IsMatch(right))return(value,"",true,ambiguous);
        bool roleLeft=PerformerRole.IsMatch(left),roleRight=PerformerRole.IsMatch(right);
        int phraseLeft=PhraseEvidence(left),phraseRight=PhraseEvidence(right);
        if(IsPlausibleArtist(right) && IsUseful(left) && ((phraseLeft>=2 && phraseRight==0) || (roleRight && !roleLeft)))
            return(left,right,true,"Ordem título – intérprete sugerida pelo texto do nome do arquivo.");
        if(IsPlausibleArtist(left) && IsUseful(right) && ((phraseRight>=2 && phraseLeft==0) || (roleLeft && !roleRight)))
            return(right,left,true,"Ordem intérprete – título sugerida pelo texto do nome do arquivo.");
        return(value,"",true,ambiguous);
    }

    private static int PhraseEvidence(string value)
    {
        // Edition labels are meaningful title suffixes, but are not evidence
        // that the adjacent performer name is a song (e.g. "Grupo (Ao vivo)").
        string core=EditionBrackets.Replace(value,"").Trim();
        if(NumericTitle.IsMatch(core))return 3;
        string[] words=Spaces.Split(core).Where(word=>word.Any(char.IsLetter)).ToArray();
        if(words.Length<2)return 0;
        if(words.Select(Fold).Any(PhraseWords.Contains))return 4;
        // Sentence case is useful supporting evidence; ordinary Portuguese
        // particles in a personal/band name must not look like a song phrase.
        if(char.IsUpper(words[0].First(char.IsLetter)) && words.Skip(1).Any(word=>!NameConnectors.Contains(Fold(word)) && char.IsLower(word.First(char.IsLetter))))return 2;
        return 0;
    }

    private static string Fold(string value)=>new(ValidUnicode(value).Normalize(NormalizationForm.FormD)
        .Where(character=>CharUnicodeInfo.GetUnicodeCategory(character)!=UnicodeCategory.NonSpacingMark)
        .Select(char.ToLowerInvariant).ToArray());
    private static bool IsPlausibleArtist(string value)=>IsPersonOrBand(value) && (PerformerRole.IsMatch(value) || PhraseEvidence(value)==0);

    private static bool IsPersonOrBand(string value)=>value.Length is >=2 and <=100 &&
        value.Any(char.IsLetter) && !GenericName.IsMatch(value) && !Attribution.IsMatch(value) &&
        !value.StartsWith('@') && !Separator.IsMatch(value) &&
        !VersionName.IsMatch(value);
    private static bool IsUseful(string value)=>value.Length>0 && value.Any(char.IsLetter) &&
        !GenericName.IsMatch(value) && !Attribution.IsMatch(value) && !value.StartsWith('@');
    private static bool Same(string left,string right)=>string.Equals(Fold(left),Fold(right),StringComparison.Ordinal);
    private static string Limit(string value,int length)
    {
        if(value.Length<=length)return value;
        int count=length-1;
        if(char.IsHighSurrogate(value[count-1]))count--;
        return value[..count].TrimEnd()+"…";
    }

    private static string Clean(string value)
    {
        value=ValidUnicode(TruncateText(value,2048));
        value=new string(value.Select(character=>char.IsControl(character)?' ':character).ToArray()).Replace('_',' ');
        value=WebAddress.Replace(value,"");
        value=Provider.Replace(value,"");
        value=Spaces.Replace(value," ").Trim();
        string previous;
        do
        {
            previous=value;
            value=EmptyBrackets.Replace(value,"");
            value=TechnicalSuffix.Replace(value,"");
            value=Spaces.Replace(value," ").Trim(' ','-','–','—','|');
        }while(value!=previous);
        return value;
    }

    private static string TruncateText(string value,int maximum)
    {
        if(value.Length<=maximum)return value;
        int count=maximum;
        if(count>0 && char.IsHighSurrogate(value[count-1]))count--;
        return value[..count];
    }

    private static string ValidUnicode(string value)
    {
        for(int index=0;index<value.Length;index++)
        {
            if(char.IsHighSurrogate(value[index]) && index+1<value.Length && char.IsLowSurrogate(value[index+1])) {index++;continue;}
            if(char.IsSurrogate(value[index]))
                return string.Concat(value.EnumerateRunes().Select(rune=>rune.ToString()));
        }
        return value;
    }
}
