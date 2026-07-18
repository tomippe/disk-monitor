using System.Windows;
using System.Windows.Threading;

namespace DiskMonitor.Helpers;

/// <summary>Chain of separate menu popups (Mac-style), root first.</summary>
internal static class MenuSession
{
    private const int AwayCloseDelayMs = 1500;
    private const int WatchIntervalMs = 100;
    private const int AwayCloseTicks = AwayCloseDelayMs / WatchIntervalMs;

    private static readonly List<VolumeMenuWindow> Open = [];
    private static DispatcherTimer? _sessionWatch;
    private static int _graceTicks;
    private static int _awayTicks;

    public static void Register(VolumeMenuWindow menu)
    {
        AltKeyState.EnsureMessageHook();
        if (!Open.Contains(menu))
            Open.Add(menu);
        // Ignore blips right after a menu opens / Activate().
        _graceTicks = 3;
        _awayTicks = 0;
        EnsureSessionWatch();
    }

    public static void Unregister(VolumeMenuWindow menu)
    {
        Open.Remove(menu);
        if (Open.Count == 0)
            StopSessionWatch();
    }

    public static bool AnyVisible() => Open.Any(m => m.IsVisible);

    public static bool IsPointerOverAny() => Open.Any(m => m.IsMouseOver);

    /// <summary>Pointer is over a menu popup or the AppBar (chip that opened the menu).</summary>
    public static bool IsPointerInSafeZone()
    {
        if (IsPointerOverAny()) return true;
        var app = System.Windows.Application.Current;
        if (app is null) return false;
        foreach (Window w in app.Windows)
        {
            if (w is AppBarWindow { IsVisible: true, IsMouseOver: true })
                return true;
        }
        return false;
    }

    public static void CloseAll()
    {
        StopSessionWatch();
        AltKeyState.ClearGesture();
        foreach (var m in Open.ToList())
        {
            try { m.ForceClose(); } catch { /* ignore */ }
        }
        Open.Clear();
    }

    private static void EnsureSessionWatch()
    {
        if (_sessionWatch is not null) return;
        _sessionWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WatchIntervalMs) };
        _sessionWatch.Tick += (_, _) =>
        {
            if (Open.Count == 0)
            {
                StopSessionWatch();
                return;
            }

            if (_graceTicks > 0)
            {
                _graceTicks--;
                _awayTicks = 0;
            }
            else if (IsPointerInSafeZone())
            {
                // Mouse still on menu / AppBar — keep open (ignore focus).
                _awayTicks = 0;
            }
            else
            {
                // Mouse left menus/AppBar — close after ~1.5s.
                _awayTicks++;
                if (_awayTicks >= AwayCloseTicks)
                {
                    CloseAll();
                    return;
                }
            }

            // Deepest directory menu first (Mac Option while browsing).
            for (var i = Open.Count - 1; i >= 0; i--)
            {
                if (Open[i].PollAltForRelist())
                    break;
            }
        };
        _sessionWatch.Start();
    }

    private static void StopSessionWatch()
    {
        if (_sessionWatch is null) return;
        _sessionWatch.Stop();
        _sessionWatch = null;
        _graceTicks = 0;
        _awayTicks = 0;
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
