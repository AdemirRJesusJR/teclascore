using System.Windows;
using System.Diagnostics;
using System.Reflection;

namespace Teclas.App;

public partial class MainWindow
{
    public string AppDisplayName => "TeclaScore";
    public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    public string AppVersionLabel => $"Versão {AppVersion}";
    public string RepositoryUrl => "https://github.com/AdemirRJesusJR/teclascore";

    private void OpenRepositoryClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception error) { Status = $"Não foi possível abrir o GitHub: {error.Message}"; }
    }

    /// <summary>Smoke windows keep each palette in its original version unless a check picks a mode.</summary>
    private bool naturalThemeVersion;
    private bool? systemPrefersDark;
    private IReadOnlyList<ThemeDefinition>? themeOptions;

    public IReadOnlyList<ThemeModeOption> ThemeModeOptions { get; } =
    [
        new("light", "Claro", "\uE706", "Sempre a versão clara da paleta."),
        new("dark", "Escuro", "\uE708", "Sempre a versão escura da paleta."),
        new("system", "Sistema", "\uE7F4", "Acompanha o modo claro ou escuro do Windows e muda sozinho quando ele mudar.")
    ];

    public string AppearanceMode
    {
        get => settings.ThemeMode;
        set
        {
            if (value is not ("light" or "dark" or "system") || value == settings.ThemeMode && !naturalThemeVersion) return;
            settings.ThemeMode = value;
            naturalThemeVersion = false;
            ApplyThemeChoice();
            PersistPreferences();
        }
    }

    private bool SystemPrefersDark => systemPrefersDark ??= ThemeManager.WindowsPrefersDark();
    private bool? EffectiveDark => naturalThemeVersion ? null : settings.ThemeMode switch
    {
        "dark" => true,
        "light" => false,
        _ => SystemPrefersDark
    };

    /// <summary>The palettes drawn in the version the current mode uses.</summary>
    public IReadOnlyList<ThemeDefinition> ThemeOptions =>
        themeOptions ??= ThemeManager.Themes.Select(theme => ThemeManager.Resolve(theme.Id, EffectiveDark)).ToArray();

    public ThemeDefinition SelectedTheme
    {
        get => ThemeOptions.First(theme => theme.Id == ThemeManager.NormalizeId(settings.ThemeId));
        set
        {
            if (value is null || value.Id == settings.ThemeId && ReferenceEquals(ThemeManager.Current, value)) return;
            settings.ThemeId = ThemeManager.NormalizeId(value.Id);
            ApplyThemeChoice();
            PersistPreferences();
        }
    }

    public string CurrentThemeName => SelectedTheme.Name + " · " + (!ThemeManager.HasBothModes(settings.ThemeId) ? "versão única"
        : naturalThemeVersion ? (SelectedTheme.IsDark ? "escuro" : "claro")
        : settings.ThemeMode switch { "dark" => "escuro", "light" => "claro", _ => SystemPrefersDark ? "escuro, como o Windows" : "claro, como o Windows" });

    public string ThemeModeHint => !ThemeManager.HasBothModes(settings.ThemeId)
        ? "O alto contraste tem uma versão só."
        : settings.ThemeMode == "system" ? (SystemPrefersDark ? "Agora escuro, como o Windows." : "Agora claro, como o Windows.") : "";

    private void ApplyThemeChoice()
    {
        ThemeManager.Apply(settings.ThemeId, EffectiveDark);
        themeOptions = null;
        RefreshWindowTheme();
        foreach (var row in Files) row.RefreshTheme();
        NotifyThemeChoice();
    }

    private void NotifyThemeChoice()
    {
        foreach (var name in new[] { nameof(ThemeOptions), nameof(SelectedTheme), nameof(CurrentThemeName), nameof(AppearanceMode), nameof(ThemeModeHint) }) Notify(name);
    }

    private void UserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        // Windows reports a light/dark switch as a General ("ImmersiveColorSet") change.
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color
            or Microsoft.Win32.UserPreferenceCategory.VisualStyle)) return;
        Dispatcher.BeginInvoke(() =>
        {
            bool now = ThemeManager.WindowsPrefersDark();
            if (now == systemPrefersDark) return;
            systemPrefersDark = now;
            if (settings.ThemeMode == "system" && !naturalThemeVersion) ApplyThemeChoice();
            else NotifyThemeChoice();
        });
    }

    public bool IsSidebarCollapsed
    {
        get => settings.IsSidebarCollapsed;
        set
        {
            if (settings.IsSidebarCollapsed == value) return;
            settings.IsSidebarCollapsed = value;
            RefreshSidebarAppearance();
            PersistPreferences();
        }
    }

    public GridLength SidebarWidth => new(IsSidebarCollapsed ? 72 : 232);
    public Visibility ExpandedSidebarVisible => IsSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
    public HorizontalAlignment SidebarContentAlignment => IsSidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    public double SidebarToggleScaleX => IsSidebarCollapsed ? -1 : 1;
    public string SidebarToggleLabel => IsSidebarCollapsed ? "Mostrar os nomes no menu" : "Mostrar só os ícones no menu";

    private void InitializeAppearance()
    {
        settings.ThemeId = ThemeManager.NormalizeId(settings.ThemeId);
        RefreshSidebarAppearance();
        themeOptions = null;
        NotifyThemeChoice();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
    }

    private void RefreshSidebarAppearance()
    {
        foreach (var name in new[] { nameof(IsSidebarCollapsed), nameof(SidebarWidth),
            nameof(ExpandedSidebarVisible), nameof(SidebarContentAlignment),
            nameof(SidebarToggleScaleX), nameof(SidebarToggleLabel), nameof(NavLabelVisibility), nameof(NavStackedLabelVisibility) }) Notify(name);
    }

    private void ToggleSidebarClick(object sender, RoutedEventArgs e) => IsSidebarCollapsed = !IsSidebarCollapsed;
    public void SetThemeForSmoke(string id)
    {
        naturalThemeVersion = true; themeOptions = null;
        SelectedTheme = ThemeOptions.First(theme => theme.Id == ThemeManager.NormalizeId(id));
    }

    /// <summary>Picks a mode as the user would; systemDark stands in for the Windows setting.</summary>
    public void SetThemeModeForSmoke(string mode, bool? systemDark = null)
    {
        if (systemDark is not null) systemPrefersDark = systemDark;
        AppearanceMode = mode;
    }

    /// <summary>Simulates Windows switching between light and dark while the app is open.</summary>
    public void SimulateWindowsModeForSmoke(bool dark)
    {
        systemPrefersDark = dark;
        if (settings.ThemeMode == "system" && !naturalThemeVersion) ApplyThemeChoice();
    }
    public void SetSidebarForSmoke(bool collapsed) => IsSidebarCollapsed = collapsed;
}

public sealed record ThemeModeOption(string Value, string Label, string Glyph, string Description);
