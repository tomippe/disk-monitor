using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace DiskMonitor.Services;

public static class MenuActions
{
    public const string IntroUrl = "https://apps.tomippe.jp/disk-monitor/";

    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch { /* ignore */ }
        }
    }

    public static void OpenRecycleBin() =>
        OpenPath("shell:RecycleBinFolder");

    public static void EmptyRecycleBin()
    {
        try
        {
            SHEmptyRecycleBin(IntPtr.Zero, null, 0);
        }
        catch
        {
            // ignore
        }
    }

    public static long RecycleBinBytes()
    {
        try
        {
            long total = 0;
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) == 0)
                total = info.i64Size;
            return total;
        }
        catch
        {
            return 0;
        }
    }

    public static void OpenDiskManagement()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "diskmgmt.msc",
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }

    public static void CopyText(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* ignore */ }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    public static string AppVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public static void ShowAbout()
    {
        System.Windows.MessageBox.Show(
            string.Format(L.Get("about.format"), AppVersion(), AppVersion()),
            L.Get("menu.about"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }
        System.Windows.Application.Current.Shutdown();
    }

    public static bool IsOpenAtLogin()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("DiskMonitor") is string;
    }

    public static void SetOpenAtLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key is null) return;
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue("DiskMonitor", $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue("DiskMonitor", false);
        }
    }

    public static void EjectDrive(string rootPath)
    {
        try
        {
            var letter = Path.GetPathRoot(rootPath)?.TrimEnd('\\');
            if (string.IsNullOrEmpty(letter)) return;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic folder = shell.NameSpace(0x11); // Drives
            dynamic? item = folder.ParseName(letter + "\\");
            item?.InvokeVerb("Eject");
        }
        catch { /* ignore */ }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
}
