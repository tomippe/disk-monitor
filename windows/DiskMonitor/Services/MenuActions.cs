using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using DiskMonitor.Helpers;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace DiskMonitor.Services;

public static class MenuActions
{
    public const string IntroUrl = "https://apps.tomippe.jp/disk-monitor/";
    public const string StartupTaskId = "DiskMonitorStartupTask";

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

    private static readonly object TrashSizeGate = new();
    private static long _trashBytesCached;
    private static long _trashBytesCachedAtMs = -10_000;
    private const int TrashCacheTtlMs = 8_000;

    /// <summary>Last known size — never blocks (UI-safe).</summary>
    public static long PeekRecycleBinBytes()
    {
        lock (TrashSizeGate) return _trashBytesCached;
    }

    public static long RecycleBinBytes(bool forceRefresh = false)
    {
        lock (TrashSizeGate)
        {
            var age = Environment.TickCount64 - _trashBytesCachedAtMs;
            if (!forceRefresh && age >= 0 && age < TrashCacheTtlMs)
                return _trashBytesCached;
        }

        long total = 0;
        try
        {
            // Default packing (24 bytes): Pack=4 made cbSize=20 and Windows often returns 0.
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) == 0)
            {
                total = info.i64Size;
            }
            else
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                    if (SHQueryRecycleBin(drive.Name, ref info) == 0)
                        total += info.i64Size;
                }
            }
        }
        catch
        {
            total = 0;
        }

        lock (TrashSizeGate)
        {
            _trashBytesCached = total;
            _trashBytesCachedAtMs = Environment.TickCount64;
        }

        return total;
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
        AppDialog.Information(
            L.Get("menu.about"),
            string.Format(L.Get("about.format"), AppVersion(), AppVersion()));
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
        if (TryGetStartupTask(out var task))
        {
            return task!.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("DiskMonitor") is string;
    }

    public static void SetOpenAtLogin(bool enabled)
    {
        // Packaged (Store/MSIX): use declared StartupTask — no HKCU\Run, no unvirtualizedResources.
        if (TryGetStartupTask(out var task))
        {
            ClearLegacyRunKey();
            if (enabled)
            {
                if (task!.State is StartupTaskState.Disabled or StartupTaskState.DisabledByUser)
                    task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
            }
            else if (task!.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
            {
                task.Disable();
            }
            return;
        }

        // Unpackaged EXE (sideload / local build): HKCU Run is fine.
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

    private static bool TryGetStartupTask(out StartupTask? task)
    {
        task = null;
        try
        {
            // Throws / fails when not running inside an MSIX package.
            _ = Package.Current.Id;
            task = StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            return task is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("DiskMonitor", throwOnMissingValue: false);
        }
        catch
        {
            // ignore
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

    [StructLayout(LayoutKind.Sequential)]
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
