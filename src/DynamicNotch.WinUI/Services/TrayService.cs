using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>
/// Notification-area icon: Settings, Minimal notch, Start with Windows, notification access, Quit.
/// Plain Shell_NotifyIcon on a hidden message window (on the UI thread), so WinForms isn't loaded.
/// Re-adds itself when Explorer restarts.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 1;
    private const int WM_COMMAND = 0x0111, WM_RBUTTONUP = 0x0205, WM_LBUTTONUP = 0x0202, WM_CONTEXTMENU = 0x007B, WM_NULL = 0;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, TPM_BOTTOMALIGN = 0x20;
    private const int CmdSettings = 1, CmdMinimal = 2, CmdStartup = 3, CmdNotifications = 4, CmdQuit = 5;

    private readonly WndProcDelegate _proc;
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private bool _minimal, _startup, _added;
    private NotificationAccess _access = NotificationAccess.Unknown;

    public event Action<bool>? MinimalToggled;
    public event Action<bool>? StartupToggled;
    public event Action? NotificationAccessRequested;
    public event Action? QuitRequested;
    public event Action? SettingsRequested;

    /// <summary>False greys the Settings item out (the app sets it once the Settings window is wired).</summary>
    public bool SettingsAvailable { get; set; }

    public TrayService()
    {
        _proc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "DynamicNotch.Tray",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, wc.lpszClassName, "Dynamic Notch tray", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */,
            IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadIcon();
        Add();
    }

    public void SetMinimal(bool on) => _minimal = on;
    public void SetStartup(bool on) => _startup = on;
    public void SetNotificationAccess(NotificationAccess access) => _access = access;

    private void Add()
    {
        var data = Data();
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = 4; // NOTIFYICON_VERSION_4
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = "Dynamic Notch",
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)((long)lParam & 0xFFFF); // version 4: the event is in LOWORD(lParam)
            if (ev is WM_RBUTTONUP or WM_CONTEXTMENU or WM_LBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            Add(); // Explorer restarted
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING | (SettingsAvailable ? 0 : MF_GRAYED), CmdSettings, "Settings…");
        AppendMenu(menu, MF_STRING | (_minimal ? MF_CHECKED : 0), CmdMinimal, "Minimal notch");
        AppendMenu(menu, MF_STRING | (_startup ? MF_CHECKED : 0), CmdStartup, "Start with Windows");
        if (_access is NotificationAccess.Denied or NotificationAccess.NoPackageIdentity)
        {
            bool needsPackage = _access == NotificationAccess.NoPackageIdentity;
            AppendMenu(menu, MF_STRING | (needsPackage ? MF_GRAYED : 0), CmdNotifications,
                needsPackage ? "App notifications need the packaged build" : "Enable app notifications…");
        }
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CmdQuit, "Quit");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // required so the menu closes when the user clicks elsewhere
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        switch (cmd)
        {
            case CmdSettings: SettingsRequested?.Invoke(); break;
            case CmdMinimal: _minimal = !_minimal; MinimalToggled?.Invoke(_minimal); break;
            case CmdStartup: StartupToggled?.Invoke(!_startup); break;
            case CmdNotifications: NotificationAccessRequested?.Invoke(); break;
            case CmdQuit: QuitRequested?.Invoke(); break;
        }
    }

    private static IntPtr LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        int size = GetSystemMetrics(49 /* SM_CXSMICON */);
        IntPtr h = File.Exists(path) ? LoadImage(IntPtr.Zero, path, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */) : IntPtr.Zero;
        return h != IntPtr.Zero ? h : LoadIconW(IntPtr.Zero, new IntPtr(32512) /* IDI_APPLICATION */);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }

    // ---- Interop -----------------------------------------------------------------------------------
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
