using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Teclas.App;

public partial class MainWindow
{
    // Keep the native caption buttons, resize behavior and system menu.
    // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
    private const uint DwmUseImmersiveDarkMode = 20;
    private const uint DwmBorderColor = 34;
    private const uint DwmCaptionColor = 35;
    private const uint DwmTextColor = 36;

    public bool NativeCaptionThemeApplied { get; private set; }

    private void InitializeWindowTheme()
    {
        SourceInitialized += (_, _) => RefreshWindowTheme();
        Activated += (_, _) => RefreshWindowTheme();
        Deactivated += (_, _) => RefreshWindowTheme();
        RefreshWindowTheme();
    }

    private void RefreshWindowTheme()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            uint dark = ThemeManager.Current.IsDark ? 1u : 0u;
            uint caption = NativeColor(ThemeManager.ColorHex("Background"));
            uint text = NativeColor(ThemeManager.ColorHex("Ink"));
            uint border = NativeColor(ThemeManager.ColorHex("Line"));
            _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(uint));
            var captionResult = DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(uint));
            var textResult = DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(uint));
            _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(uint));
            NativeCaptionThemeApplied = captionResult >= 0 && textResult >= 0;
        }
        catch (DllNotFoundException) { NativeCaptionThemeApplied = false; }
        catch (EntryPointNotFoundException) { NativeCaptionThemeApplied = false; }
        // Older Windows versions can reject individual attributes; their standard title bar remains usable.
    }

    private static uint NativeColor(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        return (uint)(color.R | color.G << 8 | color.B << 16);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);
}
