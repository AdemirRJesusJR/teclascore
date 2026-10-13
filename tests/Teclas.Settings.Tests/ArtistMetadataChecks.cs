using System.Text.Json;
using Teclas.App;

internal static class ArtistMetadataChecks
{
    public static void Run(string folder, Action<bool,string> check)
    {
        var path=Path.Combine(folder,"artist-metadata.json");
        var state=new Settings { Library=[new SavedMidi {
            FullPath=Path.Combine(folder,"Roberto Carlos - Luz divina (karaoplay.com).mid"),
            ScoreTitle="Luz divina",ScoreArtist="Roberto Carlos",ScoreComposer="",
            MetadataOrganizationNotice="Sugestão a partir do nome do arquivo.",
            Versions=[new SavedScoreVersion { ScorePath=Path.Combine(folder,"old.mscz"),
                Title="Título anterior",Artist="Intérprete anterior",Composer="Compositor informado" }]
        }] };
        state.Save(path);
        var restored=Settings.Load(path).Library.Single();
        check(restored.ScoreTitle=="Luz divina" && restored.ScoreArtist=="Roberto Carlos" && restored.ScoreComposer=="",
            "Artist and composer are persisted separately without inventing authorship");
        check(restored.MetadataOrganizationNotice==state.Library[0].MetadataOrganizationNotice,
            "Source of automatic metadata suggestions survives reopening");
        check(restored.Versions[0].Artist=="Intérprete anterior" && restored.Versions[0].Composer=="Compositor informado",
            "History stores independent artist and composer credits");
        restored.ScoreTitle="Meu título";restored.ScoreArtist="";restored.ScoreComposer="Ana & João";
        new Settings {Library=[restored]}.Save(path);
        var edited=Settings.Load(path).Library.Single();
        check(edited.ScoreTitle=="Meu título" && edited.ScoreArtist=="" && edited.ScoreComposer=="Ana & João",
            "Manual header edits and explicit empty artist survive save/load");
        check(edited.Versions[0].Artist=="Intérprete anterior","Current edits never rewrite historical artist credits");
        File.WriteAllText(path,"""
        {"Library":[{"FullPath":"missing.mid","ScoreTitle":"Título manual antigo","ScoreComposer":"Autor antigo",
          "Versions":[{"ScorePath":"missing.mscz","Title":"Antigo","Composer":"Autor"}]}]}
        """);
        var legacy=Settings.Load(path).Library.Single();
        check(legacy.ScoreTitle=="Título manual antigo" && legacy.ScoreComposer=="Autor antigo" && legacy.ScoreArtist is null,
            "Legacy saved titles and composer credits are never reclassified as artist");
        check(legacy.Versions[0].Artist=="" && legacy.MetadataOrganizationNotice=="",
            "Legacy sessions and history have safe empty metadata defaults");
        File.WriteAllText(path,JsonSerializer.Serialize(new {Library=new[]{new {FullPath="missing.mid",MetadataOrganizationNotice=(string?)null,
            Versions=new[]{new {ScorePath="missing.mscz",Artist=(string?)null,MetadataOrganizationNotice=(string?)null}}}}}));
        var normalized=Settings.Load(path).Library.Single();
        check(normalized.MetadataOrganizationNotice=="" && normalized.Versions[0].Artist=="" && normalized.Versions[0].MetadataOrganizationNotice=="",
            "Null artist history and suggestion notices are normalized safely");
    }
}
