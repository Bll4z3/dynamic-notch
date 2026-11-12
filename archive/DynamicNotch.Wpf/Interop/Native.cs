using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DynamicNotch.Interop;

/// <summary>Win32 interop used by the notch window, clipboard listener and shelf.</summary>
internal static class Native
{
    // ---- Window styles -----------------------------------------------------------------------
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_APPWINDOW = 0x00040000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_SETTINGCHANGE = 0x001A;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    public static void SetExStyle(IntPtr hwnd, int add, int remove)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = (style | (uint)add) & ~(uint)remove;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    // ---- Focus, hotkeys, capture exclusion ------------------------------------------------------
    public const int WM_HOTKEY = 0x0312;
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>Keeps a window out of screenshots (Windows 10 2004+). Returns false if unsupported.</summary>
    public static bool ExcludeFromCapture(IntPtr hwnd, bool exclude)
    {
        try { return SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE); }
        catch { return false; }
    }

    /// <summary>Waits for the compositor to present the next frame (so hidden windows are really gone).</summary>
    public static void WaitForCompositor()
    {
        try { DwmFlush(); } catch { }
    }

    public static void ReassertTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    // ---- Monitors / DPI ------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Bounds (physical pixels) and scale factor of the primary monitor.</summary>
    public static (RECT bounds, double scale) GetPrimaryMonitor()
    {
        var hmon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hmon, ref mi);
        double scale = 1;
        try { if (GetDpiForMonitor(hmon, 0, out uint dx, out _) == 0) scale = dx / 96.0; } catch { /* pre-8.1 */ }
        return (mi.rcMonitor, scale);
    }

    // ---- Foreground change hook (to stay above full-screen apps) -------------------------------
    public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    // ---- Clipboard listener ------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);

    // ---- System parameters -------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool result, uint winIni);
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    public static bool ClientAreaAnimationsEnabled()
    {
        try { return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out bool on, 0) || on; }
        catch { return true; }
    }

    // ---- Shell icons -------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;

    /// <summary>Returns the shell's large (32px) icon for a path; works for missing files via the extension.</summary>
    public static BitmapSource? GetShellIcon(string path, bool exists, bool isDirectory)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON;
        uint attrs = 0;
        if (!exists) { flags |= SHGFI_USEFILEATTRIBUTES; attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL; }
        IntPtr r = SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DestroyIcon(info.hIcon); }
    }
}
