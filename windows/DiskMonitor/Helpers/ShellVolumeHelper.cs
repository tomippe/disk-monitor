using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiskMonitor.Helpers;

/// <summary>
/// Explorer-style volume display names and icons via shell APIs.
/// </summary>
public static class ShellVolumeHelper
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_DISPLAYNAME = 0x000000200;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private static readonly Guid ShellItemImageFactoryIid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly Regex ExplorerDriveName =
        new(@"^(.*) \(([A-Za-z]:)\)$", RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, string> MenuDisplayNameCache =
        new(StringComparer.OrdinalIgnoreCase);

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
    /// Cached — SHGetFileInfo on the UI thread causes hitching.
    /// </summary>
    public static string GetMenuDisplayName(string rootPath)
    {
        var key = NormalizeRoot(rootPath);
        if (MenuDisplayNameCache.TryGetValue(key, out var cached))
            return cached;

        var display = GetDisplayName(rootPath);
        var m = ExplorerDriveName.Match(display);
        string name;
        if (m.Success)
        {
            name = $"{m.Groups[2].Value}) {m.Groups[1].Value}";
        }
        else
        {
            var letter = Path.GetPathRoot(rootPath)?.TrimEnd('\\');
            if (!string.IsNullOrEmpty(letter) && letter.Length == 2 && letter[1] == ':')
            {
                if (display.Equals(letter, StringComparison.OrdinalIgnoreCase)
                    || display.Equals(letter + "\\", StringComparison.OrdinalIgnoreCase))
                    name = $"{letter})";
                else
                    name = $"{letter}) {display}";
            }
            else
            {
                name = display;
            }
        }

        MenuDisplayNameCache[key] = name;
        return name;
    }

    public static ImageSource? GetDriveIcon(string rootPath, int dipSize = 20) =>
        GetPathIcon(NormalizeRoot(rootPath), dipSize);

    public static ImageSource? GetPathIcon(string path, int dipSize = 16)
    {
        // IShellItemImageFactory matches Explorer (incl. .url favicons / custom icons).
        var viaShellItem = TryGetPathIconViaShellItem(path, dipSize);
        if (viaShellItem is BitmapSource shellBmp)
            return CenterOpaqueIcon(shellBmp, dipSize);

        var viaShGet = TryGetPathIconViaShGetFileInfo(path, dipSize);
        if (viaShGet is BitmapSource shBmp)
            return CenterOpaqueIcon(shBmp, dipSize);

        if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            var viaUrl = TryGetUrlShortcutIcon(path, dipSize);
            if (viaUrl is BitmapSource urlBmp)
                return CenterOpaqueIcon(urlBmp, dipSize);
            return viaUrl;
        }

        return viaShellItem ?? viaShGet;
    }

    /// <summary>
    /// Shell drive icons often sit high in the bitmap; re-center on opaque bounds.
    /// </summary>
    private static ImageSource CenterOpaqueIcon(BitmapSource source, int dipSize)
    {
        try
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var w = bgra.PixelWidth;
            var h = bgra.PixelHeight;
            if (w < 2 || h < 2) return source;

            var stride = w * 4;
            var pixels = new byte[h * stride];
            bgra.CopyPixels(pixels, stride, 0);

            var minX = w;
            var minY = h;
            var maxX = -1;
            var maxY = -1;
            for (var y = 0; y < h; y++)
            {
                var row = y * stride;
                for (var x = 0; x < w; x++)
                {
                    if (pixels[row + x * 4 + 3] < 24) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0) return source;

            var contentW = maxX - minX + 1;
            var contentH = maxY - minY + 1;
            var side = Math.Max(contentW, contentH);
            // Keep a little padding so icons don't look cropped.
            side = Math.Min(Math.Max(side + 2, contentW), Math.Max(w, h));

            var dest = new WriteableBitmap(side, side, 96, 96, PixelFormats.Bgra32, null);
            var ox = (side - contentW) / 2;
            var oy = (side - contentH) / 2;
            var cropped = new byte[contentH * contentW * 4];
            for (var y = 0; y < contentH; y++)
            {
                Buffer.BlockCopy(
                    pixels,
                    (minY + y) * stride + minX * 4,
                    cropped,
                    y * contentW * 4,
                    contentW * 4);
            }

            dest.WritePixels(
                new Int32Rect(ox, oy, contentW, contentH),
                cropped,
                contentW * 4,
                0);

            var scaled = new TransformedBitmap(
                dest,
                new ScaleTransform(
                    (double)dipSize / side,
                    (double)dipSize / side));
            scaled.Freeze();
            return scaled;
        }
        catch
        {
            return source;
        }
    }

    private static ImageSource? TryGetPathIconViaShellItem(string path, int dipSize)
    {
        var iid = ShellItemImageFactoryIid;
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var unk);
        if (hr != 0 || unk == IntPtr.Zero)
            return null;

        try
        {
            var factory = (IShellItemImageFactory)Marshal.GetTypedObjectForIUnknown(unk, typeof(IShellItemImageFactory));
            var px = Math.Max(16, dipSize);
            var size = new SIZE { cx = px, cy = px };
            factory.GetImage(size, SIIGBF_RESIZETOFIT | SIIGBF_ICONONLY, out var hbitmap);
            if (hbitmap == IntPtr.Zero)
                return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hbitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(dipSize, dipSize));
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.Release(unk);
        }
    }

    private static ImageSource? TryGetPathIconViaShGetFileInfo(string path, int dipSize)
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

    /// <summary>
    /// Fallback for Internet Shortcut (.url) IconFile= / IconIndex=.
    /// </summary>
    private static ImageSource? TryGetUrlShortcutIcon(string path, int dipSize)
    {
        try
        {
            string? iconFile = null;
            var iconIndex = 0;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    iconFile = Environment.ExpandEnvironmentVariables(line["IconFile=".Length..].Trim().Trim('"'));
                else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(line["IconIndex=".Length..].Trim(), out var idx))
                    iconIndex = idx;
            }

            if (string.IsNullOrWhiteSpace(iconFile) || !File.Exists(iconFile))
                return null;

            var small = new IntPtr[1];
            var large = new IntPtr[1];
            var useLarge = dipSize > 16;
            var count = ExtractIconEx(iconFile, iconIndex, large, small, 1);
            if (count <= 0)
                return null;

            var hIcon = useLarge && large[0] != IntPtr.Zero ? large[0] : small[0];
            var other = hIcon == large[0] ? small[0] : large[0];
            if (other != IntPtr.Zero && other != hIcon)
                DestroyIcon(other);
            if (hIcon == IntPtr.Zero)
                return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(dipSize, dipSize));
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    private const int SIIGBF_RESIZETOFIT = 0x00000000;
    private const int SIIGBF_ICONONLY = 0x00000020;

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        [Out] IntPtr[]? phiconLarge,
        [Out] IntPtr[]? phiconSmall,
        int nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
