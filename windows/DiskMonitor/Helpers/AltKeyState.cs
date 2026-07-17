using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DiskMonitor.Helpers;

/// <summary>
/// Physical Alt for hidden items (Mac Option).
/// Uses Raw Input with RIDEV_INPUTSINK so Alt is tracked even when the AppBar
/// is not foreground — GetAsyncKeyState often returns 0 in that case.
/// </summary>
internal static class AltKeyState
{
    private const int VkMenu = 0x12;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const int WmInput = 0x00FF;
    private const int WmSysCommand = 0x0112;
    private const int ScKeyMenu = 0xF100;
    private const int RidInput = 0x10000003;
    private const int RimTypeKeyboard = 1;
    private const int RidevInputSink = 0x00000100;
    private const int RidevRemove = 0x00000001;
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort HidUsageKeyboard = 0x06;
    private const ushort RiKeyBreak = 0x01;

    private static bool _leftDown;
    private static bool _rightDown;
    private static bool _gestureAlt;
    private static bool? _capture;
    private static IntPtr _sinkHwnd = IntPtr.Zero;
    private static bool _rawRegistered;
    private static HwndSourceHook? _hook;

    /// <summary>Hook window for SC_KEYMENU; first caller (AppBar) registers Raw Input sink.</summary>
    public static void Attach(HwndSource source)
    {
        if (source.Handle == IntPtr.Zero) return;

        _hook ??= WndProc;
        source.AddHook(_hook);
        // Only one INPUTSINK target — must be a long-lived HWND (AppBar).
        if (!_rawRegistered)
            RegisterSink(source.Handle);
    }

    public static void EnsureMessageHook()
    {
        // Kept for call sites; real tracking starts at Attach(AppBar HwndSource).
        if (_rawRegistered) return;
        // Best-effort sync if Attach already happened and Alt is held.
        SyncFromAsync();
    }

    /// <summary>Call from MouseEnter / click before the menu opens.</summary>
    public static void NotePointerGesture()
    {
        SyncFromAsync();
        _gestureAlt = IsDown();
        _capture = _gestureAlt;
    }

    public static void ClearGesture()
    {
        _gestureAlt = false;
    }

    public static void Capture(bool? forced = null)
    {
        if (forced is bool f)
        {
            _capture = f;
            return;
        }

        SyncFromAsync();
        var down = IsDown() || _gestureAlt;
        if (_capture == true && !down) return;
        _capture = down;
    }

    public static bool TakeCapturedOrRead()
    {
        if (_capture is bool v)
        {
            _capture = null;
            _gestureAlt = false;
            return v;
        }

        SyncFromAsync();
        return IsDown();
    }

    public static bool IsDown()
    {
        if (_leftDown || _rightDown) return true;
        return AsyncAltDown();
    }

    private static void RegisterSink(IntPtr hwnd)
    {
        _sinkHwnd = hwnd;
        var rid = new RawInputDevice
        {
            usUsagePage = HidUsagePageGeneric,
            usUsage = HidUsageKeyboard,
            dwFlags = RidevInputSink,
            hwndTarget = hwnd
        };
        _rawRegistered = RegisterRawInputDevices([rid], 1, Marshal.SizeOf<RawInputDevice>());
        SyncFromAsync();
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand && ((int)(wParam.ToInt64() & 0xFFF0)) == ScKeyMenu)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmInput)
            ProcessRawInput(lParam);

        return IntPtr.Zero;
    }

    private static void ProcessRawInput(IntPtr lParam)
    {
        uint size = 0;
        GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, Marshal.SizeOf<RawInputHeader>());
        if (size == 0) return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var read = GetRawInputData(lParam, RidInput, buffer, ref size, Marshal.SizeOf<RawInputHeader>());
            if (read == unchecked((uint)(-1)) || read == 0) return;

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.dwType != RimTypeKeyboard) return;

            var kbPtr = IntPtr.Add(buffer, Marshal.SizeOf<RawInputHeader>());
            var kb = Marshal.PtrToStructure<RawKeyboard>(kbPtr);
            var down = (kb.Flags & RiKeyBreak) == 0;
            SetAltVk(kb.VKey, down);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetAltVk(int vk, bool down)
    {
        switch (vk)
        {
            case VkLMenu:
                _leftDown = down;
                break;
            case VkRMenu:
                _rightDown = down;
                break;
            case VkMenu:
                if (down) _leftDown = true;
                else
                {
                    _leftDown = (GetAsyncKeyState(VkLMenu) & 0x8000) != 0;
                    _rightDown = (GetAsyncKeyState(VkRMenu) & 0x8000) != 0;
                }
                break;
        }
    }

    private static void SyncFromAsync()
    {
        if (AsyncAltDown())
        {
            _leftDown = (GetAsyncKeyState(VkLMenu) & 0x8000) != 0
                        || (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
            _rightDown = (GetAsyncKeyState(VkRMenu) & 0x8000) != 0;
        }
    }

    private static bool AsyncAltDown() =>
        (GetAsyncKeyState(VkMenu) & 0x8000) != 0
        || (GetAsyncKeyState(VkLMenu) & 0x8000) != 0
        || (GetAsyncKeyState(VkRMenu) & 0x8000) != 0
        || (GetKeyState(VkMenu) & 0x8000) != 0
        || (GetKeyState(VkLMenu) & 0x8000) != 0
        || (GetKeyState(VkRMenu) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public int dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public int dwType;
        public int dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] pRawInputDevices, uint uiNumDevices, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(
        IntPtr hRawInput, int uiCommand, IntPtr pData, ref uint pcbSize, int cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
