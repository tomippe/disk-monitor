using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiskMonitor.Helpers;

/// <summary>
/// Full-width bottom AppBar (SHAppBarMessage), stacked above the taskbar.
/// Chip fit uses window width minus the more-button width.
/// </summary>
public sealed class AppBarHelper : IDisposable
{
    public const int BarHeightDip = 36;

    private readonly Window _window;
    private readonly HwndSource _source;
    private readonly uint _callbackMsg;
    private bool _registered;
    private bool _disposed;

    public AppBarHelper(Window window)
    {
        _window = window;
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
            helper.EnsureHandle();

        _source = HwndSource.FromHwnd(helper.Handle)
            ?? throw new InvalidOperationException("HwndSource unavailable");
        _callbackMsg = RegisterWindowMessage("DiskMonitorAppBarCallback");
        _source.AddHook(WndProc);
    }

    public void Register()
    {
        if (_registered) return;

        var abd = NewData();
        SHAppBarMessage(ABM_NEW, ref abd);
        _registered = true;
        PositionBar();
    }

    public void PositionBar()
    {
        if (!_registered) return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var heightPx = (int)Math.Ceiling(BarHeightDip * dpi / 96.0);

        var screen = System.Windows.Forms.Screen.PrimaryScreen
            ?? System.Windows.Forms.Screen.AllScreens.First();
        var leftBound = screen.Bounds.Left;
        var rightBound = screen.Bounds.Right;
        var bottom = screen.Bounds.Bottom;
        var widthPx = rightBound - leftBound;
        var widthDip = widthPx * 96.0 / dpi;

        var abd = NewData();
        abd.uEdge = ABE_BOTTOM;
        abd.rc.left = leftBound;
        abd.rc.right = rightBound;
        abd.rc.bottom = bottom;
        abd.rc.top = bottom - heightPx;

        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        abd.rc.left = leftBound;
        abd.rc.right = rightBound;
        abd.rc.top = abd.rc.bottom - heightPx;
        SHAppBarMessage(ABM_SETPOS, ref abd);

        var top = abd.rc.bottom - heightPx;
        SetWindowPos(
            hwnd, HWND_TOPMOST,
            leftBound, top,
            widthPx, heightPx,
            SWP_SHOWWINDOW);

        _window.Left = leftBound * 96.0 / dpi;
        _window.Top = top * 96.0 / dpi;
        _window.Width = widthDip;
        _window.Height = BarHeightDip;
    }

    public void Unregister()
    {
        if (!_registered) return;
        var abd = NewData();
        SHAppBarMessage(ABM_REMOVE, ref abd);
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        _source.RemoveHook(WndProc);
    }

    private APPBARDATA NewData()
    {
        return new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = new WindowInteropHelper(_window).Handle,
            uCallbackMessage = _callbackMsg,
            uEdge = ABE_BOTTOM
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == _callbackMsg && wParam.ToInt32() == ABN_POSCHANGED)
            PositionBar();
        else if (msg is WM_DISPLAYCHANGE or WM_SETTINGCHANGE)
            PositionBar();

        return IntPtr.Zero;
    }

    private const int ABE_BOTTOM = 3;
    private const int ABM_NEW = 0x00000000;
    private const int ABM_REMOVE = 0x00000001;
    private const int ABM_QUERYPOS = 0x00000002;
    private const int ABM_SETPOS = 0x00000003;
    private const int ABN_POSCHANGED = 1;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
