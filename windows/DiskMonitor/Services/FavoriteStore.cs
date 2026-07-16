using System.IO;
using System.Text.Json;

namespace DiskMonitor.Services;

public static class FavoriteStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskMonitor",
        "favorites.json");

    public static IReadOnlyList<string> LoadPaths()
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

    public static bool Contains(string path) =>
        LoadPaths().Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    public static bool IsAvailable(string path) => Directory.Exists(path);

    public static void Add(string path)
    {
        var paths = LoadPaths().ToList();
        if (paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            return;
        paths.Add(path);
        Save(paths);
    }

    public static void Remove(string path)
    {
        var paths = LoadPaths()
            .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Save(paths);
    }

    private static void Save(List<string> paths)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(paths));
    }
}
