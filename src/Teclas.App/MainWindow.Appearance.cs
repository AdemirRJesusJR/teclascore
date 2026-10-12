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

    public IReadOnlyList<ThemeDefinition> ThemeOptions => ThemeManager.Themes;
    public ThemeDefinition SelectedTheme
    {
        get => ThemeManager.Themes.First(theme => theme.Id == ThemeManager.NormalizeId(settings.ThemeId));
        set
        {
            if (value is null || value.Id == settings.ThemeId) return;
            settings.ThemeId = ThemeManager.Apply(value.Id).Id;
            RefreshWindowTheme();
            foreach (var row in Files) row.RefreshTheme();
            Notify(nameof(SelectedTheme));
            Notify(nameof(CurrentThemeName));
            PersistPreferences();
        }
    }

    public string CurrentThemeName => SelectedTheme.Name;
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
    public string SidebarToggleLabel => IsSidebarCollapsed ? "Expandir menu lateral" : "Recolher menu lateral";

    private void InitializeAppearance()
    {
        settings.ThemeId = ThemeManager.NormalizeId(settings.ThemeId);
        RefreshSidebarAppearance();
        Notify(nameof(SelectedTheme));
        Notify(nameof(CurrentThemeName));
    }

    private void RefreshSidebarAppearance()
    {
        foreach (var name in new[] { nameof(IsSidebarCollapsed), nameof(SidebarWidth),
            nameof(ExpandedSidebarVisible), nameof(SidebarContentAlignment),
            nameof(SidebarToggleScaleX), nameof(SidebarToggleLabel) }) Notify(name);
    }

    private void ToggleSidebarClick(object sender, RoutedEventArgs e) => IsSidebarCollapsed = !IsSidebarCollapsed;
    public void SetThemeForSmoke(string id) => SelectedTheme = ThemeOptions.First(theme => theme.Id == ThemeManager.NormalizeId(id));
    public void SetSidebarForSmoke(bool collapsed) => IsSidebarCollapsed = collapsed;
}
