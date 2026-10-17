using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private HwndSource? headerDragSource;

    private void InitializeWindowTheme()
    {
        SourceInitialized += (_, _) =>
        {
            headerDragSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            headerDragSource?.AddHook(HeaderDragHitTest);
            RefreshWindowTheme();
        };
        Closed += (_, _) => { headerDragSource?.RemoveHook(HeaderDragHitTest); headerDragSource = null; };
        Activated += (_, _) => RefreshWindowTheme();
        Deactivated += (_, _) => RefreshWindowTheme();
        RefreshWindowTheme();
    }

    private IntPtr HeaderDragHitTest(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0084 || !IsVisible) return IntPtr.Zero; // WM_NCHITTEST
        long coordinates = lParam.ToInt64();
        var point = PointFromScreen(new Point(unchecked((short)(coordinates & 0xffff)), unchecked((short)((coordinates >> 16) & 0xffff))));
        if (!IsHeaderDragPoint(point)) return IntPtr.Zero;
        // Let Windows move/restore/snap the window and handle double-click like the title bar.
        handled = true;
        return new IntPtr(2); // HTCAPTION
    }

    private bool IsHeaderDragPoint(Point point)
    {
        if (point.X < 8 || point.Y < 0 || point.X >= ActualWidth - 8 || point.Y >= ActualHeight) return false;
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(this,
            candidate => candidate is UIElement ui && (!ui.IsVisible || !ui.IsHitTestVisible)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
        while (hit is not null)
        {
            // Exclude buttons even when disabled; clicks there must never move the window.
            if (hit is ButtonBase or TextBoxBase or Selector or RangeBase or ScrollViewer) return false;
            if (ReferenceEquals(hit, WorkspaceDragRegion)) return true;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return false;
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
