using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace DynamicNotch.Services;

public enum NotchKey { Left, Right, PlayPause, Escape }

/// <summary>
/// Global arrow-key paging without focus. A WH_KEYBOARD_LL hook lives on its own background thread
/// with its own message loop, so a busy UI thread can never delay system input. The callback only
/// reads a few volatile flags, posts the key to the UI dispatcher asynchronously and returns:
/// - keys are swallowed ONLY while the UI says the notch is open/hovered (<see cref="ArrowsActive"/> etc.);
/// - any key with Ctrl/Alt/Win/Shift held passes through (Win+←, Shift+→ selection keep working);
/// - otherwise every key goes straight to CallNextHookEx untouched.
/// A WH_MOUSE_LL hook is installed only while the notch is pinned, to unpin on an outside click.
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    private const int WM_QUIT = 0x0012, WM_APP_MOUSEHOOK = 0x8001, WM_APP_CAPS = 0x8002;
    private const int VK_CAPITAL = 0x14;
    private const int VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_SPACE = 0x20, VK_ESCAPE = 0x1B;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly DispatcherQueue _ui;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _kbHook, _mouseHook;
    private HookProc? _kbProc, _mouseProc; // kept alive for the hook's lifetime
    private readonly bool[] _swallowedDown = new bool[256]; // so the matching key-up is swallowed too

    // Written by the UI thread, read by the hook thread.
    private volatile bool _arrowsActive, _playPauseActive, _escapeActive, _typing;
    private IntPtr _passThroughWindow; // e.g. the Settings window: never intercept while it is in front
    private volatile int _rectL, _rectT, _rectR, _rectB; // notch bounds in screen pixels (for outside clicks)
    private bool _capsDown;      // hook thread only: ignore auto-repeat while Caps Lock is held
    private int _capsState = -1; // last Caps Lock state reported (-1 = unknown)

    public KeyboardHookService(DispatcherQueue ui) => _ui = ui;

    /// <summary>Raised on the UI thread for a swallowed key.</summary>
    public event Action<NotchKey>? KeyPressed;
    /// <summary>Raised on the UI thread when the user clicks outside the notch while it's pinned.</summary>
    public event Action? OutsideClick;
    /// <summary>Raised on the UI thread after Caps Lock was toggled (true = on). Never at start-up.</summary>
    public event Action<bool>? CapsLockChanged;

    public bool IsInstalled => _kbHook != IntPtr.Zero;

    private long _lastKeyTick = long.MinValue / 2;
    /// <summary>When any key last went down (Environment.TickCount64; hook thread writes, anyone reads).</summary>
    public long LastKeyTick => Volatile.Read(ref _lastKeyTick);
    private long _lastPrintScreenTick = long.MinValue / 2;
    /// <summary>When Print Screen last went down (the screenshot shelf tells a PrtScn capture from other image copies).</summary>
    public long LastPrintScreenTick => Volatile.Read(ref _lastPrintScreenTick);

    /// <summary>Notch expanded or hovered: ← / → change page.</summary>
    public bool ArrowsActive { get => _arrowsActive; set => _arrowsActive = value; }
    /// <summary>Hovering the music page: Space toggles play/pause.</summary>
    public bool PlayPauseActive { get => _playPauseActive; set => _playPauseActive = value; }
    /// <summary>Notch pinned open: Esc closes it.</summary>
    public bool EscapeActive { get => _escapeActive; set => _escapeActive = value; }
    /// <summary>A text box in the notch has keyboard focus: pass EVERY key through untouched.</summary>
    public bool TypingActive { get => _typing; set => _typing = value; }
    /// <summary>While this window is in the foreground every key passes through (set to IntPtr.Zero to clear).</summary>
    public IntPtr PassThroughWindow { get => Volatile.Read(ref _passThroughWindow); set => Volatile.Write(ref _passThroughWindow, value); }

    public void Start()
    {
        if (_thread != null) return;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => HookThread(ready)) { IsBackground = true, Name = "Notch keyboard hook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(2000);
    }

    /// <summary>Enable the outside-click mouse hook (only while pinned) and set the notch bounds.</summary>
    public void SetPinned(bool pinned, int left, int top, int right, int bottom)
    {
        _rectL = left; _rectT = top; _rectR = right; _rectB = bottom;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_APP_MOUSEHOOK, pinned ? 1 : 0, IntPtr.Zero);
    }

    private void HookThread(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, GetModuleHandle(null), 0);
        ready.Set();
        if (_kbHook == IntPtr.Zero) return;

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP_MOUSEHOOK)
                {
                    bool want = msg.wParam != IntPtr.Zero;
                    if (want && _mouseHook == IntPtr.Zero)
                        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
                    else if (!want && _mouseHook != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(_mouseHook);
                        _mouseHook = IntPtr.Zero;
                    }
                    continue;
                }
                if (msg.message == WM_APP_CAPS)
                {
                    // Outside the hook callback: read the toggle a moment later on a worker, once it has applied.
                    ThreadPool.QueueUserWorkItem(static s => ((KeyboardHookService)s!).ReadCapsLock(), this);
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            UnhookWindowsHookEx(_kbHook);
            _mouseHook = _kbHook = IntPtr.Zero;
        }
    }

    // Keep this tiny: no allocation, no locks, no waiting on the UI thread.
    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);           // KBDLLHOOKSTRUCT.vkCode
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            if (down) Volatile.Write(ref _lastKeyTick, Environment.TickCount64);
            if (down && vk == 0x2C) Volatile.Write(ref _lastPrintScreenTick, Environment.TickCount64); // VK_SNAPSHOT

            // Caps Lock: only noted (never swallowed or delayed); the state is read off this thread.
            if (vk == VK_CAPITAL)
            {
                if (down && !_capsDown) { _capsDown = true; PostThreadMessage(_threadId, WM_APP_CAPS, 0, IntPtr.Zero); }
                else if (up) _capsDown = false;
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            if (up && vk is >= 0 and < 256 && _swallowedDown[vk])
            {
                _swallowedDown[vk] = false;
                return (IntPtr)1;
            }

            bool passAll = _typing || (Volatile.Read(ref _passThroughWindow) is var pw && pw != IntPtr.Zero && GetForegroundWindow() == pw);
            if (down && !passAll && !ModifierHeld())
            {
                NotchKey? key = vk switch
                {
                    VK_LEFT when _arrowsActive => NotchKey.Left,
                    VK_RIGHT when _arrowsActive => NotchKey.Right,
                    VK_SPACE when _playPauseActive => NotchKey.PlayPause,
                    VK_ESCAPE when _escapeActive => NotchKey.Escape,
                    _ => null,
                };
                if (key is NotchKey k)
                {
                    _swallowedDown[vk] = true;
                    _ui.TryEnqueue(DispatcherQueuePriority.High, () => KeyPressed?.Invoke(k));
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            int x = Marshal.ReadInt32(lParam), y = Marshal.ReadInt32(lParam, 4); // MSLLHOOKSTRUCT.pt
            if (x < _rectL || x > _rectR || y < _rectT || y > _rectB)
                _ui.TryEnqueue(DispatcherQueuePriority.High, () => OutsideClick?.Invoke());
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void ReadCapsLock()
    {
        Thread.Sleep(DynamicNotch.Design.Motion.CapsReadDelayMs);
        int on = (GetKeyState(VK_CAPITAL) & 1) != 0 ? 1 : 0;
        if (Interlocked.Exchange(ref _capsState, on) == on) return; // no actual change
        _ui.TryEnqueue(() => CapsLockChanged?.Invoke(on == 1));
    }

    /// <summary>Records the start-up Caps Lock state, so only real changes raise <see cref="CapsLockChanged"/>.</summary>
    public void PrimeCapsLock() => Interlocked.Exchange(ref _capsState, (GetKeyState(VK_CAPITAL) & 1) != 0 ? 1 : 0);

    private static bool ModifierHeld() =>
        (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    public void Dispose()
    {
        _arrowsActive = _playPauseActive = _escapeActive = _typing = false;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, 0, IntPtr.Zero);
        _thread?.Join(500);
        _thread = null;
        _threadId = 0;
    }

    // ---- Interop ---------------------------------------------------------------------------
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, int msg, int wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
