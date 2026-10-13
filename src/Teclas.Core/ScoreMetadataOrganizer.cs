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

    public static ScoreMetadataSuggestion Suggest(string path,IEnumerable<MidiTextEvent>? textEvents=null)
    {
        string fileName=Path.GetFileNameWithoutExtension(path);
        string cleanedFile=Clean(fileName);
        if(cleanedFile.Length==0)cleanedFile="Sem título";
        var fileParts=SplitArtistTitle(cleanedFile);
        string title=fileParts.Title,artist=fileParts.Artist,composer="";
        bool needsReview=fileParts.HasSeparator;
        bool conflictingDeclarations=false;
        string origin="Nome do arquivo organizado automaticamente.";
        var explicitValues=new Dictionary<string,string>(StringComparer.Ordinal);
        var karaoke=new List<string>();
        var sequenceNames=new List<string>();
        foreach(var item in (textEvents??[]).Take(256))
        {
            if(item.Type is not (1 or 3) || item.Tick!=0)continue; // Copyright, lyrics and later annotations are not header evidence.
            string text=(item.Text??"");
            if(text.Length>2048)text=text[..2048];
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
        if(karaoke.Count>0)
        {
            var embedded=SplitArtistTitle(karaoke[0]);
            title=embedded.Title;
            if(embedded.Artist.Length>0)artist=embedded.Artist;
            else if(fileParts.Artist.Length>0 && Same(title,fileParts.Title))artist=fileParts.Artist;
            else if(karaoke.Count==2 && IsPersonOrBand(karaoke[1]))artist=karaoke[1];
            origin="Cabeçalho karaoke e nome do arquivo organizados.";
            needsReview=true; // @T fields have no standardized composer/performer distinction.
        }
        else if(sequenceNames.Count>0)
        {
            string candidate=sequenceNames[0];
            var embedded=SplitArtistTitle(candidate);
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

    private static (string Title,string Artist,bool HasSeparator) SplitArtistTitle(string value)
    {
        string[] parts=Separator.Split(value);
        if(parts.Length==2 && IsPersonOrBand(parts[0]) && IsUseful(parts[1]) && !VersionName.IsMatch(parts[1]))return(parts[1],parts[0],true);
        return(value,"",parts.Length>1);
    }

    private static bool IsPersonOrBand(string value)=>value.Length is >=2 and <=100 &&
        value.Any(char.IsLetter) && !GenericName.IsMatch(value) && !Attribution.IsMatch(value) &&
        !value.StartsWith('@') && !Separator.IsMatch(value) &&
        !VersionName.IsMatch(value);
    private static bool IsUseful(string value)=>value.Length>0 && value.Any(char.IsLetter) &&
        !GenericName.IsMatch(value) && !Attribution.IsMatch(value) && !value.StartsWith('@');
    private static bool Same(string left,string right)=>string.Equals(left,right,StringComparison.OrdinalIgnoreCase);
    private static string Limit(string value,int length)
    {
        if(value.Length<=length)return value;
        int count=length-1;
        if(char.IsHighSurrogate(value[count-1]))count--;
        return value[..count].TrimEnd()+"…";
    }

    private static string Clean(string value)
    {
        if(value.Length>2048)value=value[..2048];
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
}
