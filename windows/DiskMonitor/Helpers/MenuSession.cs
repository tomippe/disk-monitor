namespace DiskMonitor.Helpers;

/// <summary>Chain of separate menu popups (Mac-style), root first.</summary>
internal static class MenuSession
{
    private static readonly List<VolumeMenuWindow> Open = [];

    public static void Register(VolumeMenuWindow menu)
    {
        if (!Open.Contains(menu))
            Open.Add(menu);
    }

    public static void Unregister(VolumeMenuWindow menu) => Open.Remove(menu);

    public static bool AnyVisible() => Open.Any(m => m.IsVisible);

    public static bool IsPointerOverAny() => Open.Any(m => m.IsMouseOver);

    public static void CloseAll()
    {
        foreach (var m in Open.ToList())
        {
            try { m.ForceClose(); } catch { /* ignore */ }
        }
        Open.Clear();
    }

    public static void CloseFrom(VolumeMenuWindow menu)
    {
        var idx = Open.IndexOf(menu);
        if (idx < 0)
        {
            try { menu.ForceClose(); } catch { /* ignore */ }
            return;
        }
        foreach (var m in Open.Skip(idx).Reverse().ToList())
        {
            try { m.ForceClose(); } catch { /* ignore */ }
            Open.Remove(m);
        }
    }

    public static void CloseChildrenOf(VolumeMenuWindow parent)
    {
        var idx = Open.IndexOf(parent);
        if (idx < 0) return;
        foreach (var m in Open.Skip(idx + 1).Reverse().ToList())
        {
            try { m.ForceClose(); } catch { /* ignore */ }
            Open.Remove(m);
        }
    }
}
