using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiskMonitor.Helpers;

/// <summary>
/// Explorer-style volume display names and icons via SHGetFileInfo.
/// </summary>
public static class ShellVolumeHelper
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_DISPLAYNAME = 0x000000200;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private static readonly Regex ExplorerDriveName =
        new(@"^(.*) \(([A-Za-z]:)\)$", RegexOptions.CultureInvariant);

    public static string GetDisplayName(string rootPath)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(
            NormalizeRoot(rootPath),
            0,
            ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_DISPLAYNAME);

        if (result != IntPtr.Zero && !string.IsNullOrWhiteSpace(info.szDisplayName))
            return info.szDisplayName;

        return rootPath.TrimEnd('\\');
    }

    /// <summary>
    /// Menu label: "C:) ローカルディスク" (drive letter first, no opening parenthesis).
    /// </summary>
    public static string GetMenuDisplayName(string rootPath)
    {
        var display = GetDisplayName(rootPath);
        var m = ExplorerDriveName.Match(display);
        if (m.Success)
            return $"{m.Groups[2].Value}) {m.Groups[1].Value}";

        var letter = Path.GetPathRoot(rootPath)?.TrimEnd('\\');
        if (!string.IsNullOrEmpty(letter) && letter.Length == 2 && letter[1] == ':')
        {
            if (display.Equals(letter, StringComparison.OrdinalIgnoreCase)
                || display.Equals(letter + "\\", StringComparison.OrdinalIgnoreCase))
                return $"{letter})";
            return $"{letter}) {display}";
        }

        return display;
    }

    public static ImageSource? GetDriveIcon(string rootPath, int dipSize = 20) =>
        GetPathIcon(NormalizeRoot(rootPath), dipSize);

    public static ImageSource? GetPathIcon(string path, int dipSize = 16)
    {
        var flags = SHGFI_ICON | (dipSize > 16 ? SHGFI_LARGEICON : SHGFI_SMALLICON);
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(
            path,
            0,
            ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            flags);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(dipSize, dipSize));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    public static System.Drawing.Icon? GetDriveIconForTray(string rootPath)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(
            NormalizeRoot(rootPath),
            0,
            ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            using var temp = System.Drawing.Icon.FromHandle(info.hIcon);
            return (System.Drawing.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return "C:\\";
        return rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
