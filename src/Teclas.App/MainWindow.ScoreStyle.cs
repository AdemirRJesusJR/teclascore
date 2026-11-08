using System.Text.Json.Nodes;

namespace Teclas.App;

/// <summary>Music font and staff size for newly generated scores (Configurações). Scores already generated keep their look.</summary>
public partial class MainWindow
{
    public sealed record StyleOption(string Value, string Label);
    public IReadOnlyList<StyleOption> MusicFontOptions { get; } =
        [new("Leland", "Leland (padrão do MuseScore)"), new("Bravura", "Bravura (traço forte)"), new("Emmentaler", "Emmentaler (clássica)"), new("Petaluma", "Petaluma (manuscrita)")];
    public IReadOnlyList<StyleOption> ScoreSizeOptions { get; } =
        [new("normal", "Normal"), new("grande", "Grande (leitura de longe)"), new("muito-grande", "Muito grande")];
    public IReadOnlyList<StyleOption> ScoreSpacingOptions { get; } =
        [new("compacto", "Compacto (mais por página)"), new("normal", "Normal"), new("amplo", "Amplo (espaço para anotar)")];

    public string ScoreMusicFont
    {
        get => settings.ScoreMusicFont;
        set { if (value is null || settings.ScoreMusicFont == value) return; settings.ScoreMusicFont = value; Notify(); PersistPreferences(); Status = "Fonte das próximas partituras alterada. Gere de novo para aplicar a uma partitura existente."; }
    }
    public string ScoreSize
    {
        get => settings.ScoreSize;
        set { if (value is null || settings.ScoreSize == value) return; settings.ScoreSize = value; Notify(); PersistPreferences(); Status = "Tamanho das próximas partituras alterado. Partituras maiores ocupam mais páginas."; }
    }

    /// <summary>Space between the systems: compact fits more music on each page, ample leaves room to write.</summary>
    public string ScoreSpacing
    {
        get => settings.ScoreSpacing;
        set
        {
            if (value is null || settings.ScoreSpacing == value) return;
            settings.ScoreSpacing = value; Notify(); PersistPreferences();
            Status = "Espaço entre as pautas alterado: vale para as próximas partituras geradas (as já geradas mudam ao gerar de novo).";
        }
    }

    /// <summary>Request part for the engine; null keeps MuseScore's defaults.</summary>
    internal JsonObject? ScoreStyleRequest()
    {
        if (settings.ScoreMusicFont == "Leland" && settings.ScoreSize == "normal" && settings.ScoreSpacing == "normal") return null;
        var style = new JsonObject { ["musicFont"] = settings.ScoreMusicFont, ["size"] = settings.ScoreSize };
        if (settings.ScoreSpacing != "normal") style["spacing"] = settings.ScoreSpacing;
        return style;
    }
}
