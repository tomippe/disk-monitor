using System.IO;
using System.Text.Json;

namespace DiskMonitor.Services;

public static class FavoriteStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskMonitor",
        "favorites.json");

    private static readonly object Gate = new();
    private static List<string>? _cache;

    public static IReadOnlyList<string> LoadPaths()
    {
        lock (Gate)
        {
            if (_cache is not null)
                return _cache;
            _cache = ReadFromDisk();
            return _cache;
        }
    }

    public static bool Contains(string path)
    {
        lock (Gate)
        {
            _cache ??= ReadFromDisk();
            return _cache.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static bool IsAvailable(string path)
    {
        // Directory.Exists on mapped / UNC paths can stall for tens of seconds.
        if (VolumeService.IsNetworkPath(path))
            return true;
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static void Add(string path)
    {
        lock (Gate)
        {
            _cache ??= ReadFromDisk();
            if (_cache.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                return;
            _cache.Add(path);
            Save_NoLock(_cache);
        }
    }

    public static void Remove(string path)
    {
        lock (Gate)
        {
            _cache ??= ReadFromDisk();
            _cache = _cache
                .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Save_NoLock(_cache);
        }
    }

    private static List<string> ReadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = File.ReadAllText(FilePath);
            var paths = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return paths.Where(p => !string.IsNullOrWhiteSpace(p) && seen.Add(p)).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void Save_NoLock(List<string> paths)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(paths));
    }
}
