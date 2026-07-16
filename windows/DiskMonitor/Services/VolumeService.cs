using System.IO;
using DiskMonitor.Helpers;

namespace DiskMonitor.Services;

public sealed record VolumeInfo(
    string Name,
    string RootPath,
    long AvailableBytes,
    long TotalBytes,
    bool IsReady,
    DriveType DriveType);

public static class VolumeService
{
    public static IReadOnlyList<VolumeInfo> ListVolumes()
    {
        var list = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var root = drive.RootDirectory.FullName;
                var name = ShellVolumeHelper.GetMenuDisplayName(root);

                if (!drive.IsReady)
                {
                    list.Add(new VolumeInfo(name, root, 0, 0, false, drive.DriveType));
                    continue;
                }

                list.Add(new VolumeInfo(
                    name,
                    root,
                    drive.AvailableFreeSpace,
                    drive.TotalSize,
                    true,
                    drive.DriveType));
            }
            catch
            {
                // Skip inaccessible volumes
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
        return ListVolumes().FirstOrDefault(v =>
            string.Equals(v.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase))
            ?? ListVolumes().FirstOrDefault(v => v.IsReady && v.DriveType == DriveType.Fixed);
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

    /// <summary>AppBar / menu bar: free space only (Mac-style).</summary>
    public static string StatusLine(VolumeInfo vol) =>
        FormatBytes(vol.AvailableBytes, concise: true);

}
