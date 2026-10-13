using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Teclas.App;

public partial class ScorePropertiesWindow : Window
{
    private sealed record InversionChoice(int Value, string Label);
    private readonly int defaultInversion;
    private bool initializing = true;
    private bool updatingSubtitle;
    private bool automaticSubtitle;

    public string TitleValue => ScoreTitleInput.Text.Trim();
    public string? SubtitleValue => automaticSubtitle ? null : ScoreSubtitleInput.Text.Trim();
    public string ComposerValue => ScoreComposerInput.Text.Trim();
    public int? InversionOverride => DiagramInversionCombo.SelectedValue is int value && value is >= 0 and <= 2 ? value : null;

    public ScorePropertiesWindow(string title, string? subtitle, string composer, int? diagramInversionOverride, int defaultInversion)
    {
        this.defaultInversion = Math.Clamp(defaultInversion, 0, 2);
        InitializeComponent();
        ScoreTitleInput.Text = title;
        ScoreComposerInput.Text = composer;
        DiagramInversionCombo.ItemsSource = new[]
        {
            new InversionChoice(-1, $"Padrão adotado ({InversionName(this.defaultInversion)})"),
            new InversionChoice(0, "Posição fundamental"),
            new InversionChoice(1, "Primeira inversão"),
            new InversionChoice(2, "Segunda inversão")
        };
        DiagramInversionCombo.SelectedValue = diagramInversionOverride is >= 0 and <= 2 ? diagramInversionOverride.Value : -1;
        automaticSubtitle = subtitle is null;
        AutomaticSubtitleCheck.IsChecked = automaticSubtitle;
        ScoreSubtitleInput.Text = subtitle ?? ScoreMetadataDefaults.Subtitle(InversionOverride ?? this.defaultInversion);
        initializing = false;
        SourceInitialized += (_, _) => ApplyNativeTheme();
        Activated += (_, _) => ApplyNativeTheme();
        Deactivated += (_, _) => ApplyNativeTheme();
        Loaded += (_, _) => ScoreTitleInput.Focus();
    }

    private static string InversionName(int value) => value switch
    {
        0 => "posição fundamental", 1 => "primeira inversão", _ => "segunda inversão"
    };

    private void UpdateAutomaticSubtitle()
    {
        updatingSubtitle = true;
        try { ScoreSubtitleInput.Text = ScoreMetadataDefaults.Subtitle(InversionOverride ?? defaultInversion); }
        finally { updatingSubtitle = false; }
    }

    private void SubtitleChanged(object sender, TextChangedEventArgs e)
    {
        if (initializing || updatingSubtitle) return;
        automaticSubtitle = false;
        AutomaticSubtitleCheck.IsChecked = false;
    }

    private void AutomaticSubtitleChanged(object sender, RoutedEventArgs e)
    {
        if (initializing) return;
        automaticSubtitle = AutomaticSubtitleCheck.IsChecked == true;
        if (automaticSubtitle) UpdateAutomaticSubtitle();
    }

    private void InversionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing && automaticSubtitle) UpdateAutomaticSubtitle();
    }

    private void SavePropertiesClick(object sender, RoutedEventArgs e)
    {
        if (TitleValue.Length > 160 || (SubtitleValue?.Length ?? 0) > 200 || ComposerValue.Length > 120)
        {
            PropertiesValidationMessage.Text = "Use até 160 caracteres no título, 200 no subtítulo e 120 no autor.";
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
