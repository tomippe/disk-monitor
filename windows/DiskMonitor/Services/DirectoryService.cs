using System.IO;
using System.Windows.Media;
using DiskMonitor.Helpers;

namespace DiskMonitor.Services;

public sealed record DirectoryEntry(
    string Path,
    string Name,
    bool IsDirectory,
    bool IsAccessible,
    bool IsHidden);

public sealed record DirectorySnapshot(
    IReadOnlyList<DirectoryEntry> Entries,
    int TotalCount,
    string? Error);

public static class DirectoryService
{
    public static Task<DirectorySnapshot> ListAsync(
        string directoryPath,
        bool includeHidden = false,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => List(directoryPath, includeHidden, cancellationToken), cancellationToken);

    public static DirectorySnapshot List(
        string directoryPath,
        bool includeHidden = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directoryPath))
                return new DirectorySnapshot([], 0, "not found");

            var entries = new List<DirectoryEntry>();
            foreach (var path in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var name = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(name)) continue;
                    var attrs = File.GetAttributes(path);
                    var isHidden = (attrs & FileAttributes.Hidden) != 0
                                   || (attrs & FileAttributes.System) != 0;
                    if (!includeHidden && isHidden)
                        continue;

                    var isDir = (attrs & FileAttributes.Directory) != 0;
                    // Accessibility probe is relatively expensive — do it after sort, still off UI.
                    entries.Add(new DirectoryEntry(path, name, isDir, IsAccessible: true, isHidden));
                }
                catch
                {
                    var name = Path.GetFileName(path);
                    // Unknown attrs — only show when including hidden (likely protected).
                    if (!includeHidden) continue;
                    entries.Add(new DirectoryEntry(
                        path, name, IsDirectory: false, IsAccessible: false, IsHidden: true));
                }
            }

            entries.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory)
                    return a.IsDirectory ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            // Probe dirs only (files stay accessible) — keeps listing responsive on huge folders.
            for (var i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var e = entries[i];
                if (!e.IsDirectory) continue;
                if (!IsEntryAccessible(e.Path, isDirectory: true))
                    entries[i] = e with { IsAccessible = false };
            }

            return new DirectorySnapshot(entries, entries.Count, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DirectorySnapshot([], 0, ex.Message);
        }
    }

    /// <summary>
    /// Mac <c>isReadable && isExecutable</c> for dirs. Files that already yielded
    /// attributes are treated as accessible (avoid opening every file while listing).
    /// </summary>
    public static bool IsEntryAccessible(string path, bool isDirectory)
    {
        if (!isDirectory)
            return true;

        try
        {
            using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            _ = e.MoveNext();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Folder byte size (Mac <c>du -sk</c> equivalent). Honors cancellation / soft timeout.
    /// </summary>
    public static Task<long?> GetFolderSizeAsync(
        string directoryPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try
            {
                return (long?)MeasureFolderSize(directoryPath, linked.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }, CancellationToken.None);

    private static long MeasureFolderSize(string root, CancellationToken ct)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch
            {
                continue;
            }

            foreach (var path in entries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var attrs = File.GetAttributes(path);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        stack.Push(path);
                    }
                    else
                    {
                        total += new FileInfo(path).Length;
                    }
                }
                catch
                {
                    // skip inaccessible
                }
            }
        }
        return total;
    }

    public static ImageSource? GetEntryIcon(string path, int dipSize = 16) =>
        ShellVolumeHelper.GetPathIcon(path, dipSize);
}
