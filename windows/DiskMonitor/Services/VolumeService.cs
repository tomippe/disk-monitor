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
    public static IReadOnlyList<VolumeInfo> ListVolumes()
    {
        var list = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            string name;
            DriveType type;
            try
            {
                root = drive.RootDirectory.FullName;
                name = ShellVolumeHelper.GetMenuDisplayName(root);
                type = drive.DriveType;
            }
            catch
            {
                continue;
            }

            try
            {
                if (!drive.IsReady)
                {
                    // Still show on the AppBar / menus — no capacity label.
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
                // Capacity (or readiness) failed — keep the volume, omit size.
                list.Add(new VolumeInfo(name, root, null, null, false, type));
            }
        }

        return list
            .OrderBy(v => v.DriveType == DriveType.Fixed ? 0 : 1)
            .ThenBy(v => v.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static VolumeInfo? SystemVolume()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var volumes = ListVolumes();
        return volumes.FirstOrDefault(v =>
                   string.Equals(v.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase))
               ?? volumes.FirstOrDefault(v => v.DriveType == DriveType.Fixed)
               ?? volumes.FirstOrDefault();
    }

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
}
