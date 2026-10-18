using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace Teclas.App;

/// <summary>A complete palette. Score images retain their original white paper.</summary>
public sealed class ThemeDefinition
{
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public bool IsDark { get; }
    public IReadOnlyDictionary<string, string> Palette { get; }
    public SolidColorBrush Background => Brush("Background");
    public SolidColorBrush Sidebar => Brush("Sidebar");
    public SolidColorBrush Accent => Brush("Accent");
    public SolidColorBrush Surface => Brush("Card");
    public SolidColorBrush Text => Brush("Ink");

    internal ThemeDefinition(string id, string name, string description, bool isDark, Dictionary<string, string> palette)
    {
        Id = id; Name = name; Description = description; IsDark = isDark;
        Palette = new ReadOnlyDictionary<string, string>(palette);
    }

    internal SolidColorBrush Brush(string key)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[key]));
        brush.Freeze();
        return brush;
    }
}

public static class ThemeManager
{
    public const string DefaultId = "classic";

    public static IReadOnlyList<ThemeDefinition> Themes { get; } = Array.AsReadOnly(new[]
    {
        Create("classic", "Verde clássico", "O verde original do seu estúdio de partituras.", false,
            "#F5F7F6", "#FFFFFF", "#20363D", "#617379", "#0F766E", "#FFFFFF", "#E2E9E8",
            "#172E37", "#FFFFFF", "#AFC2C8", "#2B4650", "#223D46", "#42606A",
            "#175C53", "#277D6B", "#FFFFFF", "#F2FCF7", "#346F62",
            "#E7F3EF", "#16695F", "#E8F3EE", "#90BFAE", "#F0F5F3", "#52675F", "#FAFCFB", "#EEF2EF"),
        Create("ocean", "Oceano", "Azul marcante com uma barra lateral escura.", false,
            "#F3F6FC", "#FFFFFF", "#20334D", "#5F6F84", "#2563EB", "#FFFFFF", "#DEE5EF",
            "#162B49", "#FFFFFF", "#B4C6E2", "#2D466C", "#223C60", "#496083",
            "#E5EDFB", "#DAE7FF", "#20334D", "#465A78", "#CDDCF7",
            "#E8EFFB", "#2453A4", "#E5EDFC", "#9BB8E9", "#EDF2FA", "#516680", "#F8FAFE", "#E9EFF8"),
        Create("indigo", "Índigo", "Roxo suave para um espaço musical criativo.", false,
            "#F6F4FB", "#FFFFFF", "#302C48", "#71667F", "#6D4CC4", "#FFFFFF", "#E6DFEF",
            "#272044", "#FFFFFF", "#C4BBDE", "#473665", "#352B54", "#66537E",
            "#EAE3F8", "#E1D6F5", "#302C48", "#615175", "#D9CDEE",
            "#F0EAFA", "#6242AA", "#ECE5F8", "#BBA5DD", "#F0ECF6", "#6E5F7C", "#FBFAFD", "#ECE6F3"),
        Create("graphite", "Grafite e âmbar", "Um tema escuro com detalhes em dourado.", true,
            "#202329", "#2B3038", "#F2F4F7", "#B3BAC6", "#F2BD62", "#191C22", "#424955",
            "#15171C", "#F2F4F7", "#B3BAC6", "#40382A", "#24272E", "#444A54",
            "#342D22", "#413526", "#F2F4F7", "#DECBAA", "#53432D",
            "#3F382E", "#F2C878", "#40382A", "#B69353", "#353B45", "#CCD3DF", "#2F353E", "#3F4651"),
        Create("light", "Azul claro", "Interface clara, leve e discreta em todos os painéis.", false,
            "#F6F8FC", "#FFFFFF", "#21344D", "#607086", "#2563EB", "#FFFFFF", "#DEE6F1",
            "#E8EFFA", "#21344D", "#52657D", "#CDDBF4", "#DAE5F6", "#B7C9E5",
            "#DEE9FA", "#E8F0FE", "#21344D", "#48617F", "#C9DCF5",
            "#E9F0FC", "#2453A4", "#E4EDFC", "#9DB9E5", "#EDF3FC", "#536C86", "#F9FBFE", "#E8EFF9"),
        Create(ContrastId, "Alto contraste", "Preto, branco e amarelo, com bordas fortes, para enxergar melhor. Usado sozinho quando o Windows está em alto contraste.", true,
            "#000000", "#0A0A0A", "#FFFFFF", "#E8E8E8", "#FFE500", "#000000", "#FFFFFF",
            "#000000", "#FFFFFF", "#E8E8E8", "#3D3700", "#0A0A0A", "#FFFFFF",
            "#000000", "#141400", "#FFFFFF", "#F2F2F2", "#2E2A00",
            "#1A1A1A", "#FFE500", "#3D3700", "#FFE500", "#1A1A1A", "#FFFFFF", "#101010", "#333333")
    });

    public const string ContrastId = "contrast";

    /// <summary>The saved theme, or high contrast while Windows itself is in a high-contrast mode and no theme was chosen.</summary>
    public static string StartupId(string? saved, bool windowsHighContrast) =>
        windowsHighContrast && (string.IsNullOrEmpty(saved) || saved == DefaultId) ? ContrastId : NormalizeId(saved);

    public static ThemeDefinition Current { get; private set; } = Themes[0];
    public static string NormalizeId(string? id) => Themes.FirstOrDefault(theme => theme.Id == id)?.Id ?? DefaultId;
    public static string ColorHex(string key) => Current.Palette[key];

    public static ThemeDefinition Apply(string? id)
    {
        var theme = Themes.First(theme => theme.Id == NormalizeId(id));
        var application = Application.Current;
        application?.Dispatcher.VerifyAccess();
        if (application is not null)
        {
            foreach (var pair in theme.Palette)
            {
                var resourceKey = pair.Key == "Card" ? "CardBrush" : pair.Key;
                application.Resources[resourceKey] = pair.Key.EndsWith("Color", StringComparison.Ordinal)
                    ? (object)(Color)ColorConverter.ConvertFromString(pair.Value)
                    : theme.Brush(pair.Key);
            }
            // WPF popups and editing controls also consult system-color resource keys.
            var systemBrushes = new Dictionary<ResourceKey, string>
            {
                [SystemColors.WindowBrushKey] = "Card", [SystemColors.WindowTextBrushKey] = "Ink",
                [SystemColors.ControlBrushKey] = "Card", [SystemColors.ControlTextBrushKey] = "Ink",
                [SystemColors.ControlLightBrushKey] = "Hover", [SystemColors.ControlLightLightBrushKey] = "Card",
                [SystemColors.ControlDarkBrushKey] = "InputBorder", [SystemColors.ControlDarkDarkBrushKey] = "Muted",
                [SystemColors.GrayTextBrushKey] = "Muted", [SystemColors.HighlightBrushKey] = "Selection",
                [SystemColors.HighlightTextBrushKey] = "Ink", [SystemColors.InactiveSelectionHighlightBrushKey] = "Selection",
                [SystemColors.InactiveSelectionHighlightTextBrushKey] = "Ink",
                [SystemColors.InfoBrushKey] = "Card", [SystemColors.InfoTextBrushKey] = "Ink",
                [SystemColors.ScrollBarBrushKey] = "Background"
            };
            foreach (var pair in systemBrushes) application.Resources[pair.Key] = theme.Brush(pair.Value);
        }
        Current = theme;
        return theme;
    }

    private static ThemeDefinition Create(string id, string name, string description, bool dark,
        string background, string card, string ink, string muted, string accent, string accentInk, string line,
        string sidebar, string sidebarInk, string sidebarMuted, string sidebarActive, string sidebarPanel, string sidebarLine,
        string heroStart, string heroEnd, string heroInk, string heroMuted, string heroPanel,
        string soft, string softInk, string selection, string selectionBorder, string tableHeader, string tableHeaderInk,
        string tableAlternate, string chartTrack)
    {
        var palette = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Background"] = background, ["Card"] = card, ["Ink"] = ink, ["Muted"] = muted,
            ["Accent"] = accent, ["AccentInk"] = accentInk, ["Line"] = line,
            ["InputBorder"] = id == ContrastId ? "#FFFFFF" : dark ? "#667180" : "#8B9C9F", ["Soft"] = soft, ["SoftInk"] = softInk,
            ["Hover"] = selection, ["Selection"] = selection, ["SelectionBorder"] = selectionBorder,
            ["Sidebar"] = sidebar, ["SidebarInk"] = sidebarInk, ["SidebarMuted"] = sidebarMuted,
            ["SidebarActive"] = sidebarActive, ["SidebarPanel"] = sidebarPanel, ["SidebarLine"] = sidebarLine,
            ["HeroStartColor"] = heroStart, ["HeroEndColor"] = heroEnd, ["HeroInk"] = heroInk,
            ["HeroMuted"] = heroMuted, ["HeroPanel"] = heroPanel,
            ["KeyboardWhite"] = "#FAFCFF", ["KeyboardBlack"] = "#1C2735",
            ["KeyboardActive"] = dark ? "#F2BD62" : id is "ocean" or "light" ? "#9CBFEB" : id == "indigo" ? "#BDA4E8" : "#B2D5BF",
            ["Ready"] = id == ContrastId ? "#7CFFB2" : dark ? "#77D7B8" : "#16715C", ["Generated"] = id == ContrastId ? "#9FD0FF" : dark ? "#9ABDEB" : "#42689C",
            ["Review"] = id == ContrastId ? "#FFE500" : dark ? "#EAC17B" : "#8A6225", ["Error"] = id == ContrastId ? "#FF9C8F" : dark ? "#F0A298" : "#A94C43",
            ["ChartTrack"] = chartTrack, ["TableHeader"] = tableHeader, ["TableHeaderInk"] = tableHeaderInk,
            ["TableAlternate"] = tableAlternate, ["PreviewCanvas"] = dark ? "#181B21" : "#E8EEEB"
        };
        return new(id, name, description, dark, palette);
    }
}
