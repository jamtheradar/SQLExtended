using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SQLExtended.SchemaCompare.UI;

/// <summary>A dark window with a white Windows title bar is the giveaway of a half-themed app. Windows 10 20H1 and later.</summary>
public static class WindowTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmUseImmersiveDarkMode = 20;

    public static void Apply(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch
        {
            // Older Windows: the title bar stays the system's.
        }
    }

    /// <summary>Applies now (once the handle exists) and on every theme change until the window closes.</summary>
    public static void Follow(Window window)
    {
        var host = CompareHost.Current;
        EventHandler changed = (_, _) => Apply(window, host.IsDark);
        window.SourceInitialized += changed;
        host.ThemeChanged += changed;
        window.Closed += (_, _) => host.ThemeChanged -= changed;
    }
}
