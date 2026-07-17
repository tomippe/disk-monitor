using Microsoft.Win32;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;

namespace DiskMonitor.Helpers;

/// <summary>Windows light/dark (AppsUseLightTheme, fallback SystemUsesLightTheme).</summary>
internal static class AppTheme
{
    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // App mode first (Settings → Colors → Choose your mode).
            var apps = key?.GetValue("AppsUseLightTheme");
            if (apps is int a) return a == 0;
            var system = key?.GetValue("SystemUsesLightTheme");
            if (system is int s) return s == 0;
        }
        catch
        {
            // fall through
        }

        return false;
    }

    public static (WpfColor Bg, WpfColor Fg, WpfColor Muted, WpfColor Border, WpfColor Hover) AppBarColors()
    {
        if (IsDark())
        {
            return (
                WpfColor.FromRgb(0x2C, 0x2C, 0x2C),
                WpfColor.FromRgb(0xF0, 0xF0, 0xF0),
                WpfColor.FromRgb(0xB0, 0xB0, 0xB0),
                WpfColor.FromRgb(0x3A, 0x3A, 0x3A),
                WpfColor.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        }

        return (
            WpfColor.FromRgb(0xF2, 0xF2, 0xF2),
            WpfColor.FromRgb(0x1A, 0x1A, 0x1A),
            WpfColor.FromRgb(0x5A, 0x5A, 0x5A),
            WpfColor.FromRgb(0xD0, 0xD0, 0xD0),
            WpfColor.FromArgb(0x14, 0x00, 0x00, 0x00));
    }

    public static (WpfColor Bg, WpfColor Border, WpfColor Fg, WpfColor Muted, WpfColor Hover) MenuColors()
    {
        if (IsDark())
        {
            return (
                WpfColor.FromRgb(0x2C, 0x2C, 0x2C),
                WpfColor.FromRgb(0x45, 0x45, 0x45),
                WpfColor.FromRgb(0xF3, 0xF3, 0xF3),
                WpfColor.FromRgb(0xC0, 0xC0, 0xC0),
                WpfColor.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        }

        return (
            WpfColor.FromRgb(0xF9, 0xF9, 0xF9),
            WpfColor.FromRgb(0xE5, 0xE5, 0xE5),
            WpfColor.FromRgb(0x1A, 0x1A, 0x1A),
            WpfColor.FromRgb(0x5A, 0x5A, 0x5A),
            WpfColor.FromArgb(0x14, 0x00, 0x00, 0x00));
    }

    public static SolidColorBrush FgBrush()
    {
        var (_, fg, _, _, _) = MenuColors();
        var b = new SolidColorBrush(fg);
        b.Freeze();
        return b;
    }
}
