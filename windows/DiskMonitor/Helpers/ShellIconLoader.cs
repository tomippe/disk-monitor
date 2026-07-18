using System.Collections.Concurrent;
using System.Windows.Media;

namespace DiskMonitor.Helpers;

/// <summary>
/// Loads shell icons on a dedicated STA background thread so the UI never blocks
/// (SHGetFileInfo / IShellItemImageFactory).
/// </summary>
internal static class ShellIconLoader
{
    private readonly record struct Request(
        string Path,
        int DipSize,
        TaskCompletionSource<ImageSource?> Completion,
        CancellationToken Token);

    private static readonly BlockingCollection<Request> Queue = new();

    static ShellIconLoader()
    {
        var thread = new Thread(Worker)
        {
            IsBackground = true,
            Name = "DiskMonitor.ShellIconLoader"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static Task<ImageSource?> GetAsync(
        string path,
        int dipSize = 18,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ImageSource?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            tcs.TrySetCanceled(cancellationToken);
            return tcs.Task;
        }

        Queue.Add(new Request(path, dipSize, tcs, cancellationToken));
        return tcs.Task;
    }

    private static void Worker()
    {
        foreach (var req in Queue.GetConsumingEnumerable())
        {
            if (req.Token.IsCancellationRequested)
            {
                req.Completion.TrySetCanceled(req.Token);
                continue;
            }

            try
            {
                var icon = ShellVolumeHelper.GetPathIcon(req.Path, req.DipSize);
                req.Completion.TrySetResult(icon);
            }
            catch (Exception ex)
            {
                req.Completion.TrySetException(ex);
            }
        }
    }
}
