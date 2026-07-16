using System.IO;
using System.Windows.Media;
using DiskMonitor.Helpers;

namespace DiskMonitor.Services;

public sealed record DirectoryEntry(
    string Path,
    string Name,
    bool IsDirectory,
    bool IsAccessible);

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
                    if (!includeHidden
                        && ((attrs & FileAttributes.Hidden) != 0 || (attrs & FileAttributes.System) != 0))
                        continue;

                    var isDir = (attrs & FileAttributes.Directory) != 0;
                    entries.Add(new DirectoryEntry(path, name, isDir, IsAccessible: true));
                }
                catch
                {
                    var name = Path.GetFileName(path);
                    entries.Add(new DirectoryEntry(path, name, false, IsAccessible: false));
                }
            }

            entries.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory)
                    return a.IsDirectory ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

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
