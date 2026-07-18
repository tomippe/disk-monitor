using System.IO;
using DiskMonitor.Helpers;

namespace DiskMonitor.Services;

public sealed record VolumeInfo(
    string Name,
    string RootPath,
    /// <summary>Null when free space could not be read — chip still appears without trailing size.</summary>
    long? AvailableBytes,
    long? TotalBytes,
    bool IsReady,
    DriveType DriveType);

public static class VolumeService
{
    /// <summary>Network IsReady / free-space probes stall for tens of seconds when offline.</summary>
    private static readonly TimeSpan NetworkProbeTimeout = TimeSpan.FromMilliseconds(600);

    private static readonly object Gate = new();
    private static IReadOnlyList<VolumeInfo> _cache = [];

    /// <summary>
    /// Cached volume list. Safe for UI-thread menu work — does not touch DriveInfo / shell
    /// unless the cache is empty (first call). Prefer <see cref="RefreshVolumes"/> off-UI.
    /// </summary>
    public static IReadOnlyList<VolumeInfo> ListVolumes()
    {
        lock (Gate)
        {
            if (_cache.Count > 0)
                return _cache;
        }

        return RefreshVolumes();
    }

    /// <summary>Re-enumerate drives (DriveInfo / SHGetFileInfo). Call off the UI thread.</summary>
    public static IReadOnlyList<VolumeInfo> RefreshVolumes()
    {
        IReadOnlyList<VolumeInfo> previous;
        lock (Gate)
            previous = _cache;

        var list = EnumerateVolumes(previous);
        lock (Gate)
            _cache = list;
        return list;
    }

    public static VolumeInfo? FindVolume(string rootPath)
    {
        var key = NormalizeRoot(rootPath);
        return ListVolumes().FirstOrDefault(v =>
            string.Equals(NormalizeRoot(v.RootPath), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Cache-only lookup — never enumerates drives (UI-safe).</summary>
    public static VolumeInfo? TryGetCachedVolume(string rootPath)
    {
        var key = NormalizeRoot(rootPath);
        lock (Gate)
        {
            if (_cache.Count == 0) return null;
            return _cache.FirstOrDefault(v =>
                string.Equals(NormalizeRoot(v.RootPath), key, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static VolumeInfo? SystemVolume()
    {
        var systemRoot = SystemRootPath();
        var volumes = ListVolumes();
        return volumes.FirstOrDefault(v =>
                   string.Equals(NormalizeRoot(v.RootPath), systemRoot, StringComparison.OrdinalIgnoreCase))
               ?? volumes.FirstOrDefault(v => v.DriveType == DriveType.Fixed)
               ?? volumes.FirstOrDefault();
    }

    /// <summary>System drive root without enumerating volumes (UI-safe).</summary>
    public static string SystemRootPath() =>
        NormalizeRoot(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");

    /// <summary>True for UNC or a cached DriveType.Network root (no I/O).</summary>
    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        return TryGetCachedVolume(root)?.DriveType == DriveType.Network;
    }

    private static IReadOnlyList<VolumeInfo> EnumerateVolumes(IReadOnlyList<VolumeInfo> previous)
    {
        var prevByRoot = previous.ToDictionary(
            v => NormalizeRoot(v.RootPath),
            StringComparer.OrdinalIgnoreCase);

        // Keep Explorer names for network roots — SHGetFileInfo on mapped drives stalls hard.
        ShellVolumeHelper.InvalidateMenuDisplayNames(
            preserveRoots: prevByRoot
                .Where(kv => kv.Value.DriveType == DriveType.Network)
                .Select(kv => kv.Key));

        var list = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            DriveType type;
            try
            {
                // Name / DriveType do not touch the share; IsReady / free space do.
                root = drive.Name;
                type = drive.DriveType;
            }
            catch
            {
                continue;
            }

            var key = NormalizeRoot(root);
            prevByRoot.TryGetValue(key, out var prev);

            if (type == DriveType.Network)
            {
                list.Add(ProbeNetworkVolume(drive, root, prev));
                continue;
            }

            string name;
            try
            {
                name = ShellVolumeHelper.GetMenuDisplayName(root);
            }
            catch
            {
                name = prev?.Name ?? key;
            }

            try
            {
                if (!drive.IsReady)
                {
                    list.Add(new VolumeInfo(name, root, null, null, false, type));
                    continue;
                }

                list.Add(new VolumeInfo(
                    name,
                    root,
                    drive.AvailableFreeSpace,
                    drive.TotalSize,
                    true,
                    type));
            }
            catch
            {
                list.Add(new VolumeInfo(name, root, null, null, false, type));
            }
        }

        return list
            .OrderBy(v => v.DriveType == DriveType.Fixed ? 0 : 1)
            .ThenBy(v => v.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static VolumeInfo ProbeNetworkVolume(DriveInfo drive, string root, VolumeInfo? prev)
    {
        // Prefer cached Explorer label — never block the refresh loop on SHGetFileInfo.
        var name = ShellVolumeHelper.GetMenuDisplayNameCachedOrFetch(root, allowFetch: prev is null);

        try
        {
            var probe = Task.Run(() =>
            {
                if (!drive.IsReady)
                    return new VolumeInfo(name, root, null, null, false, DriveType.Network);
                return new VolumeInfo(
                    name,
                    root,
                    drive.AvailableFreeSpace,
                    drive.TotalSize,
                    true,
                    DriveType.Network);
            });

            if (probe.Wait(NetworkProbeTimeout))
                return probe.Result with { Name = name };

            // Timed out — keep last known capacity so the bar does not blank out.
            if (prev is not null)
                return prev with { Name = name, DriveType = DriveType.Network };

            return new VolumeInfo(name, root, null, null, false, DriveType.Network);
        }
        catch
        {
            if (prev is not null)
                return prev with { Name = name, DriveType = DriveType.Network };
            return new VolumeInfo(name, root, null, null, false, DriveType.Network);
        }
    }

    private static string NormalizeRoot(string path) => path.TrimEnd('\\', '/');

    public static string FormatBytes(long bytes, bool concise = true)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];

        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string number;
        if (unit <= 1)
            number = ((long)value).ToString();
        else if (value >= 100)
            number = value.ToString("0");
        else if (value >= 10)
            number = value.ToString("0.#");
        else
            number = value.ToString("0.##");

        // Explorer / compact status style: "15.2GB"
        return concise ? $"{number}{units[unit]}" : $"{number} {units[unit]}";
    }

    /// <summary>AppBar / menu bar: free space only (Mac-style). Empty when unknown.</summary>
    public static string StatusLine(VolumeInfo vol) =>
        vol.AvailableBytes is long bytes ? FormatBytes(bytes, concise: true) : "";

    /// <summary>
    /// Clipboard text for 「状況をコピー」: one drive per line, name + free space.
    /// Drives without a known free space are omitted.
    /// </summary>
    public static string FormatAllVolumesStatus()
    {
        var lines = ListVolumes()
            .Where(v => v.AvailableBytes is not null)
            .Select(v => $"{v.Name}  {StatusLine(v)}");
        return string.Join(Environment.NewLine, lines);
    }
}
