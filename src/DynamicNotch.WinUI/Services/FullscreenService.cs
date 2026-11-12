using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;

namespace DynamicNotch.Services;

/// <summary>
/// Tells the notch when another app is truly fullscreen on the notch's monitor (video players, browser
/// video fullscreen, games, presentations). A foreground-change hook gives an instant re-check and a
/// light poll (every <see cref="PollMs"/> ms: a handful of cheap Win32 calls) catches apps that go
/// fullscreen without changing the foreground window (F11, a video's fullscreen button) and makes sure
/// the notch can never stay hidden: as soon as the fullscreen window closes, crashes or shrinks, the
/// next check reports false.
///
/// Rule: SHQueryUserNotificationState says D3D fullscreen or presentation mode, or it says "busy"
/// (a fullscreen app) AND the foreground window covers its whole monitor. The desktop, the taskbar and
/// this app's own windows never count. The notch lives on the primary monitor only, so a fullscreen
/// window on another monitor doesn't hide it.
/// </summary>
public sealed class FullscreenService : IDisposable
{
    private const int PollMs = 750, SettleMs = 250;
    private static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    private readonly Func<IntPtr> _notchWindow;
    private readonly DispatcherQueueTimer _poll, _settle;
    private readonly WinEventDelegate _hookProc;
    private readonly uint _pid = (uint)Environment.ProcessId;
    private IntPtr _hook;
    private bool _enabled;

    public FullscreenService(Func<IntPtr> notchWindow)
    {
        _notchWindow = notchWindow;
        var q = DispatcherQueue.GetForCurrentThread();
        _poll = q.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(PollMs);
        _poll.Tick += (_, _) => Check();
        _settle = q.CreateTimer();
        _settle.IsRepeating = false;
        _settle.Interval = TimeSpan.FromMilliseconds(SettleMs);
        _settle.Tick += (_, _) => { _settle.Stop(); Check(); };
        _hookProc = (_, _, _, _, _, _, _) => { if (_enabled) { _settle.Stop(); _settle.Start(); } }; // let the window settle first
    }

    /// <summary>True while a fullscreen app covers the notch's monitor (raised on the UI thread on change).</summary>
    public bool IsFullscreen { get; private set; }
    public event Action<bool>? Changed;

    /// <summary>Turns detection on/off (the Settings toggle). Off reports "not fullscreen" right away.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value)
            {
                if (_hook == IntPtr.Zero)
                    _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
                _poll.Start();
                Check();
            }
            else
            {
                _poll.Stop();
                _settle.Stop();
                if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
                Report(false);
            }
        }
    }

    private void Check()
    {
        bool full;
        try { full = _enabled && Detect(); }
        catch { full = false; } // never get stuck hidden because of an API hiccup
        Report(full);
    }

    private void Report(bool full)
    {
        if (full == IsFullscreen) return;
        IsFullscreen = full;
        Changed?.Invoke(full);
    }

    private bool Detect()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsWindow(fg) || !IsWindowVisible(fg) || IsIconic(fg)) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == _pid) return false; // the notch, Settings, the region selector
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (ShellClasses.Contains(cls.ToString())) return false;

        // Only the notch's monitor matters.
        var notch = _notchWindow();
        var mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONULL);
        if (mon == IntPtr.Zero || (notch != IntPtr.Zero && mon != MonitorFromWindow(notch, MONITOR_DEFAULTTOPRIMARY))) return false;

        if (SHQueryUserNotificationState(out int state) != 0) state = 0;
        if (state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE) return true;
        if (state != QUNS_BUSY) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(fg, out var r)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    public void Dispose()
    {
        _poll.Stop();
        _settle.Stop();
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    // ---- Win32 ----------------------------------------------------------------------------------------
    private const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;
    private const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1;

    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
