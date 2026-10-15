using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Teclas.Core;

namespace Teclas.App;

public partial class ScorePropertiesWindow : Window
{
    private sealed record InversionChoice(int Value, string Label);
    private readonly string? initialSubtitle;
    private ScoreMetadataSuggestion? metadataSuggestion;
    private string organizationNotice = "";
    public string TitleValue => ScoreTitleInput.Text.Trim();
    public string? SubtitleValue => initialSubtitle is null && string.IsNullOrWhiteSpace(ScoreSubtitleInput.Text)
        ? null : ScoreSubtitleInput.Text.Trim();
    public string ComposerValue => ScoreComposerInput.Text.Trim();
    public string ArtistValue => ScoreArtistInput.Text.Trim();
    public bool UsedMetadataSuggestion { get; private set; }
    public string OrganizationNoticeValue => organizationNotice;
    public int? InversionOverride => DiagramInversionCombo.SelectedValue is int value && value is >= 0 and <= 2 ? value : null;

    public ScorePropertiesWindow(string title, string? subtitle, string composer, int? diagramInversionOverride, int defaultInversion,
        string artist = "", string organizationNotice = "")
    {
        defaultInversion = Math.Clamp(defaultInversion, 0, 2);
        initialSubtitle = subtitle;
        InitializeComponent();
        ScoreTitleInput.Text = title;
        ScoreComposerInput.Text = composer;
        ScoreArtistInput.Text = artist;
        this.organizationNotice = organizationNotice;
        SetOrganizationNotice(organizationNotice);
        DiagramInversionCombo.ItemsSource = new[]
        {
            new InversionChoice(-1, $"Padrão adotado ({InversionName(defaultInversion)})"),
            new InversionChoice(0, "Posição fundamental"),
            new InversionChoice(1, "Primeira inversão"),
            new InversionChoice(2, "Segunda inversão")
        };
        DiagramInversionCombo.SelectedValue = diagramInversionOverride is >= 0 and <= 2 ? diagramInversionOverride.Value : -1;
        ScoreSubtitleInput.Text = subtitle ?? "";
        SourceInitialized += (_, _) => ApplyNativeTheme();
        Activated += (_, _) => ApplyNativeTheme();
        Deactivated += (_, _) => ApplyNativeTheme();
        Loaded += (_, _) => ScoreTitleInput.Focus();
    }

    public void ConfigureMetadataSuggestion(ScoreMetadataSuggestion suggestion)
    {
        metadataSuggestion = suggestion;
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(suggestion.Title)) details.Add($"Título: {suggestion.Title}");
        details.Add(string.IsNullOrWhiteSpace(suggestion.Artist) ? "Artista: não identificado; o campo ficará vazio ao usar a sugestão." : $"Artista: {suggestion.Artist}");
        if (string.IsNullOrWhiteSpace(ComposerValue) && !string.IsNullOrWhiteSpace(suggestion.Composer))
            details.Add($"Autor: {suggestion.Composer}");
        if (!string.IsNullOrWhiteSpace(suggestion.Explanation)) details.Add(suggestion.Explanation);
        if (!string.IsNullOrWhiteSpace(ComposerValue)) details.Add("O autor preenchido será preservado.");
        MetadataSuggestionPreview.Text = string.Join(Environment.NewLine, details);
        MetadataSuggestionPanel.Visibility = Visibility.Visible;
    }

    private void ApplyMetadataSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (metadataSuggestion is not {} suggestion) return;
        if (!string.IsNullOrWhiteSpace(suggestion.Title)) ScoreTitleInput.Text = suggestion.Title;
        ScoreArtistInput.Text = suggestion.Artist;
        if (string.IsNullOrWhiteSpace(ComposerValue) && !string.IsNullOrWhiteSpace(suggestion.Composer))
            ScoreComposerInput.Text = suggestion.Composer;
        MetadataSuggestionPanel.Visibility = Visibility.Collapsed;
        UsedMetadataSuggestion = true;
        organizationNotice = suggestion.Explanation;
        SetOrganizationNotice("Sugestões aplicadas. Confira o cabeçalho e clique em Aplicar e atualizar para salvar.");
        PropertiesValidationMessage.Visibility = Visibility.Collapsed;
    }

    private void SetOrganizationNotice(string notice)
    {
        MetadataOrganizationNoticeText.Text = notice;
        MetadataOrganizationNotice.Visibility = string.IsNullOrWhiteSpace(notice) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SwapTitleArtistClick(object sender, RoutedEventArgs e)
    {
        string title = ScoreTitleInput.Text, artist = ScoreArtistInput.Text;
        if (artist.Length > 160 || title.Length > 120)
        {
            PropertiesValidationMessage.Text = "O intérprete aceita até 120 caracteres. Ajuste os textos antes de trocar.";
            PropertiesValidationMessage.Visibility = Visibility.Visible;
            return;
        }
        ScoreTitleInput.Text = artist;
        ScoreArtistInput.Text = title;
        UsedMetadataSuggestion = false;
        organizationNotice = "";
        SetOrganizationNotice("Título e intérprete trocados. Confira os campos e use Aplicar e atualizar para salvar.");
        PropertiesValidationMessage.Visibility = Visibility.Collapsed;
    }

    private static string InversionName(int value) => value switch
    {
        0 => "posição fundamental", 1 => "primeira inversão", _ => "segunda inversão"
    };

    private void SavePropertiesClick(object sender, RoutedEventArgs e)
    {
        if (TitleValue.Length > 160 || (SubtitleValue?.Length ?? 0) > 200 || ComposerValue.Length > 120 || ArtistValue.Length > 120)
        {
            PropertiesValidationMessage.Text = "Use até 160 caracteres no título, 200 no subtítulo e 120 no artista e no autor.";
            PropertiesValidationMessage.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private void ApplyNativeTheme()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            uint dark = ThemeManager.Current.IsDark ? 1u : 0u;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(uint));
            foreach (var (attribute, resource) in new[] { (35u, "Background"), (36u, "Ink"), (34u, "Line") })
            {
                var color = (Color)ColorConverter.ConvertFromString(ThemeManager.ColorHex(resource));
                uint value = (uint)(color.R | color.G << 8 | color.B << 16);
                _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);
}
