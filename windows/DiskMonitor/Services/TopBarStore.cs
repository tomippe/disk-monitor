using System.IO;
using System.Text.Json;
using IODriveType = System.IO.DriveType;

namespace DiskMonitor.Services;

/// <summary>
/// Which volumes appear on the AppBar (top bar). Defaults: system, optical, removable.
/// User overrides are persisted under LocalAppData.
/// </summary>
public static class TopBarStore
{
    /// <summary>Override key for Recycle Bin (not a drive root).</summary>
    public const string TrashKey = "shell:RecycleBinFolder";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskMonitor",
        "topbar.json");

    private static readonly object Gate = new();
    private static Dictionary<string, bool>? _overrides;

    public static event Action? Changed;

    public static bool IsDefaultOnTopBar(VolumeInfo vol)
    {
        var system = VolumeService.SystemVolume();
        if (system is not null
            && string.Equals(vol.RootPath, system.RootPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return vol.DriveType is IODriveType.Removable or IODriveType.CDRom;
    }

    public static bool IsShownOnTopBar(VolumeInfo vol)
    {
        var key = Normalize(vol.RootPath);
        lock (Gate)
        {
            EnsureLoaded();
            if (_overrides!.TryGetValue(key, out var forced))
                return forced;
        }

        return IsDefaultOnTopBar(vol);
    }

    /// <summary>Recycle Bin defaults to on the AppBar.</summary>
    public static bool IsTrashShownOnTopBar()
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (_overrides!.TryGetValue(TrashKey, out var forced))
                return forced;
        }

        return true;
    }

    public static void SetTrashShownOnTopBar(bool shown)
    {
        lock (Gate)
        {
            EnsureLoaded();
            // Default is shown — only persist when hidden.
            if (shown)
                _overrides!.Remove(TrashKey);
            else
                _overrides![TrashKey] = false;

            Save_NoLock();
        }

        Changed?.Invoke();
    }

    public static void SetShownOnTopBar(string rootPath, bool shown)
    {
        var key = Normalize(rootPath);
        lock (Gate)
        {
            EnsureLoaded();
            var vol = VolumeService.ListVolumes().FirstOrDefault(v =>
                string.Equals(Normalize(v.RootPath), key, StringComparison.OrdinalIgnoreCase));

            // Store override only when it differs from the default rule.
            if (vol is not null && IsDefaultOnTopBar(vol) == shown)
                _overrides!.Remove(key);
            else
                _overrides![key] = shown;

            Save_NoLock();
        }

        Changed?.Invoke();
    }

    private static void EnsureLoaded()
    {
        if (_overrides is not null) return;
        _overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<TopBarData>(json);
            if (data?.Overrides is null) return;
            foreach (var (path, shown) in data.Overrides)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    _overrides[Normalize(path)] = shown;
            }
        }
        catch
        {
            // keep empty overrides
        }
    }

    private static void Save_NoLock()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var data = new TopBarData
            {
                Overrides = _overrides!.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
        }
        catch
        {
            // ignore
        }
    }

    private static string Normalize(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return "";
        var full = Path.GetFullPath(rootPath);
        return full.EndsWith('\\') ? full : full + "\\";
    }

    private sealed class TopBarData
    {
        public Dictionary<string, bool>? Overrides { get; set; }
    }
}
