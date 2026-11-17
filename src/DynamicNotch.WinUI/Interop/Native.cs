using System.Runtime.InteropServices;

namespace DynamicNotch.Interop;

/// <summary>Win32 interop for the notch window, its region, hotkey, tray and system settings.</summary>
internal static class Native
{
    // ---- Window styles -----------------------------------------------------------------------
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_APPWINDOW = 0x00040000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
                      SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;

    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_ACTIVATE = 0x0006;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static void SetExStyle(IntPtr hwnd, int add, int remove)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = (style | (uint)add) & ~(uint)remove;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    // ---- Dragging a borderless window by its content ----------------------------------------------------
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Starts a system move of the window, as if its title bar had been pressed.</summary>
    public static void BeginWindowDrag(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(2) /* HTCAPTION */, IntPtr.Zero);
    }

    /// <summary>Starts a system resize from the bottom-right corner (the Settings window's grip).</summary>
    public static void BeginWindowResize(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(17) /* HTBOTTOMRIGHT */, IntPtr.Zero);
    }

    // ---- Keeping the app's own windows out of screenshots ---------------------------------------------
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>Hides the window from screen capture (Windows 10 2004+). Returns false where unsupported.</summary>
    public static bool ExcludeFromCapture(IntPtr hwnd, bool exclude)
    {
        try { return SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE); }
        catch { return false; }
    }

    /// <summary>Waits for the desktop compositor to present a frame (so a hide is on screen before a capture).</summary>
    public static void WaitForCompositor()
    {
        try { DwmFlush(); } catch { }
    }

    // ---- Monitors ---------------------------------------------------------------------------------------
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    /// <summary>Every monitor's bounds in physical pixels.</summary>
    public static List<RECT> AllMonitors()
    {
        var list = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref RECT r, IntPtr _) => { list.Add(r); return true; }, IntPtr.Zero);
        return list;
    }

    public static void ReassertTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    // ---- See-through window (the DWM composites the WinUI content with per-pixel alpha) ---------------
    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int l, r, t, b; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    /// <summary>Borderless popup whose whole client area is "glass", so transparent pixels show the desktop.</summary>
    public static void MakeSeeThroughPopup(IntPtr hwnd)
    {
        long visible = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_VISIBLE;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr((long)WS_POPUP | visible));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        int none = DWMWA_COLOR_NONE, square = DWMWCP_DONOTROUND; // Windows 11; ignored on Windows 10
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, 4);
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref square, 4);
        var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    // ---- Window region (click-through outside the notch) -----------------------------------------
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);

    /// <summary>Only these rectangles (window pixels) take the mouse and are drawn; the rest is click-through.</summary>
    public static void SetRegion(IntPtr hwnd, IReadOnlyList<RECT> rects)
    {
        IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
        foreach (var r in rects)
        {
            IntPtr part = CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);
            CombineRgn(rgn, rgn, part, 2 /* RGN_OR */);
            DeleteObject(part);
        }
        if (SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn); // on success the system owns it
        // Keep the whole window "glass" after the region changes (the see-through area stays transparent).
        var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    // ---- Monitors / DPI ------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Bounds (physical pixels) and scale factor of the primary monitor.</summary>
    public static (RECT bounds, double scale) GetPrimaryMonitor() => GetMonitor(new POINT { X = 0, Y = 0 });

    /// <summary>The monitor under the mouse pointer (Settings ▸ Customisation ▸ Behaviour ▸ Monitor).</summary>
    public static (RECT bounds, double scale) GetMonitorUnderCursor() =>
        GetCursorPos(out var pt) ? GetMonitor(pt, nearest: true) : GetPrimaryMonitor();

    private static (RECT bounds, double scale) GetMonitor(POINT at, bool nearest = false)
    {
        var hmon = MonitorFromPoint(at, nearest ? 2u /* MONITOR_DEFAULTTONEAREST */ : 1u /* MONITOR_DEFAULTTOPRIMARY */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hmon, ref mi);
        double scale = 1;
        try { if (GetDpiForMonitor(hmon, 0, out uint dx, out _) == 0) scale = dx / 96.0; } catch { }
        return (mi.rcMonitor, scale);
    }

    // ---- Foreground change hook (to stay above full-screen apps) -------------------------------
    public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    // ---- System parameters -------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool result, uint winIni);
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    public static bool ClientAreaAnimationsEnabled()
    {
        try { return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out bool on, 0) || on; }
        catch { return true; }
    }
}
