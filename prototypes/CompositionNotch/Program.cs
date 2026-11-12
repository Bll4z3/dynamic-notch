using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace CompositionNotch;

/// <summary>
/// PROTOTYPE: can a see-through, always-on-top, never-focused, click-through notch animate at the
/// display's full refresh rate if it's drawn by Windows.UI.Composition instead of WPF?
///
/// - Plain Win32 popup with WS_EX_NOREDIRECTIONBITMAP: no window bitmap at all, the DWM composites
///   our visual tree directly on the GPU (no per-frame CPU readback like a WPF layered window).
/// - The morph is a compositor-side spring (SpringVector2NaturalMotionAnimation), so frames are
///   produced by the compositor even when this process is busy; interrupting retargets the spring.
/// - Clicks pass through outside the notch: the window region is kept to the notch's bounds.
/// Hover the notch to expand it; move away to collapse. Right-click the notch to quit.
/// </summary>
internal static class Program
{
    // Sizes in DIPs (same as the real app's music page).
    private const float CollapsedW = 196, CollapsedH = 32, ExpandedW = 680, ExpandedH = 298, R = 22, Room = 40;
    private const float HostW = ExpandedW + 2 * R + 2 * Room, HostH = ExpandedH + Room;

    private static IntPtr _hwnd;
    private static float _scale = 1;
    private static Compositor _compositor = null!;
    private static DesktopWindowTarget _target = null!;
    private static CompositionRoundedRectangleGeometry _body = null!;
    private static ContainerVisual _expandedContent = null!, _collapsedContent = null!;
    private static bool _expanded, _tracking;
    private static WndProc? _proc;

    [STAThread]
    private static void Main()
    {
        EnsureDispatcherQueue();
        _hwnd = CreateHostWindow();
        _scale = GetDpiForWindow(_hwnd) / 96f;
        PositionWindow();

        _compositor = new Compositor();
        _target = CreateDesktopTarget(_compositor, _hwnd);
        BuildVisuals();
        UpdateRegion(expanded: false);
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static float Px(float dip) => dip * _scale;

    // ---- Visual tree -------------------------------------------------------------------------
    private static void BuildVisuals()
    {
        var root = _compositor.CreateContainerVisual();
        root.Size = new Vector2(Px(HostW), Px(HostH));
        _target.Root = root;

        // Notch body: a rounded rectangle pushed up by R so its top corners sit above the screen edge.
        _body = _compositor.CreateRoundedRectangleGeometry();
        _body.CornerRadius = new Vector2(Px(R), Px(R));
        SetBody(CollapsedW, CollapsedH, animate: false);

        var shape = _compositor.CreateShapeVisual();
        shape.Size = root.Size;
        var fill = _compositor.CreateSpriteShape(_body);
        fill.FillBrush = _compositor.CreateColorBrush(Color.FromArgb(255, 0, 0, 0));
        fill.StrokeBrush = _compositor.CreateColorBrush(Color.FromArgb(0x14, 255, 255, 255)); // ~8% inner highlight
        fill.StrokeThickness = Px(1);
        shape.Shapes.Add(fill);

        // Soft shadow under the body (the layer casts the shadow of its children).
        var layer = _compositor.CreateLayerVisual();
        layer.Size = root.Size;
        var shadow = _compositor.CreateDropShadow();
        shadow.BlurRadius = Px(24);
        shadow.Offset = new Vector3(0, Px(6), 0);
        shadow.Color = Color.FromArgb(255, 0, 0, 0);
        shadow.Opacity = 0.55f;
        layer.Shadow = shadow;
        layer.Children.InsertAtTop(shape);
        root.Children.InsertAtTop(layer);

        // Content is clipped by the SAME animated geometry, so it can never overflow the morph.
        var content = _compositor.CreateContainerVisual();
        content.Size = root.Size;
        content.Clip = _compositor.CreateGeometricClip(_body);
        root.Children.InsertAtTop(content);

        // Expanded placeholder content (laid out for the expanded size, centred): "album art",
        // title / artist bars, a seek bar and three "buttons".
        _expandedContent = _compositor.CreateContainerVisual();
        _expandedContent.Size = root.Size;
        _expandedContent.Opacity = 0;
        float left = (HostW - ExpandedW) / 2 + 20;
        AddRect(_expandedContent, left, 12 + 26 + 8 + 30, 156, 156, Color.FromArgb(255, 0x2A, 0x2A, 0x30), R);
        AddRect(_expandedContent, left, 12 + 4, 18, 18, Color.FromArgb(255, 0x32, 0xD7, 0x4B), 9);
        AddRect(_expandedContent, left + 26, 12 + 6, 46, 14, Color.FromArgb(255, 0xF5, 0xF5, 0xF7), 7);
        float info = left + 156 + 20;
        AddRect(_expandedContent, info, 98, 240, 16, Color.FromArgb(255, 0xF5, 0xF5, 0xF7), 8);
        AddRect(_expandedContent, info, 122, 150, 12, Color.FromArgb(255, 0x8A, 0x8A, 0x90), 6);
        AddRect(_expandedContent, info, 162, 440, 5, Color.FromArgb(255, 0x44, 0x44, 0x4A), 2.5f);
        AddRect(_expandedContent, info, 162, 180, 5, Color.FromArgb(255, 0xFF, 0x37, 0x5F), 2.5f);
        for (int i = 0; i < 3; i++)
            AddRect(_expandedContent, info + 160 + i * 52, 188, 34, 34, Color.FromArgb(255, 0xE8, 0xE8, 0xEC), 17);
        content.Children.InsertAtTop(_expandedContent);

        // Collapsed placeholder content: battery dot (follows the left edge) and a centred "time".
        _collapsedContent = _compositor.CreateContainerVisual();
        _collapsedContent.Size = root.Size;
        AddRect(_collapsedContent, HostW / 2 - 18, 10, 36, 12, Color.FromArgb(255, 0xF5, 0xF5, 0xF7), 6);
        var ring = AddRect(_collapsedContent, 0, 8, 16, 16, Color.FromArgb(255, 0x32, 0xD7, 0x4B), 8);
        var follow = _compositor.CreateExpressionAnimation("Vector3(body.Offset.X + pad, ring.Offset.Y, 0)");
        follow.SetReferenceParameter("body", _body);
        follow.SetReferenceParameter("ring", ring);
        follow.SetScalarParameter("pad", Px(12));
        ring.StartAnimation("Offset", follow); // the ring rides the animated left edge, on the compositor
        content.Children.InsertAtTop(_collapsedContent);
    }

    private static SpriteVisual AddRect(ContainerVisual parent, float x, float y, float w, float h, Color c, float radius)
    {
        var host = _compositor.CreateShapeVisual();
        host.Size = new Vector2(Px(w), Px(h));
        var g = _compositor.CreateRoundedRectangleGeometry();
        g.Size = host.Size;
        g.CornerRadius = new Vector2(Px(Math.Min(radius, Math.Min(w, h) / 2)));
        var s = _compositor.CreateSpriteShape(g);
        s.FillBrush = _compositor.CreateColorBrush(c);
        host.Shapes.Add(s);
        host.Offset = new Vector3(Px(x), Px(y), 0);
        // Wrap in a SpriteVisual-compatible container so callers can animate Offset.
        var wrap = _compositor.CreateSpriteVisual();
        wrap.Size = host.Size;
        wrap.Offset = host.Offset;
        host.Offset = Vector3.Zero;
        wrap.Children.InsertAtTop(host);
        parent.Children.InsertAtTop(wrap);
        return wrap;
    }

    // ---- Springs (run inside the compositor) ----------------------------------------------------
    private static void SetBody(float w, float h, bool animate)
    {
        var size = new Vector2(Px(w), Px(h + R));
        var offset = new Vector2(Px((HostW - w) / 2), -Px(R));
        if (!animate)
        {
            _body.Size = size;
            _body.Offset = offset;
            return;
        }
        // Same feel as the app's Motion.Notch preset: ~0.52 s response, 0.8 damping (small overshoot).
        _body.StartAnimation("Size", Spring(size));
        _body.StartAnimation("Offset", Spring(offset));
    }

    private static SpringVector2NaturalMotionAnimation Spring(Vector2 to)
    {
        var a = _compositor.CreateSpringVector2Animation();
        a.DampingRatio = 0.8f;
        a.Period = TimeSpan.FromMilliseconds(190); // tuned to the app's ~0.5 s notch morph
        a.FinalValue = to;
        return a;
    }

    private static void Fade(Visual v, float to, int delayMs)
    {
        var a = _compositor.CreateSpringScalarAnimation();
        a.DampingRatio = 1f;
        a.Period = TimeSpan.FromMilliseconds(to > 0 ? 110 : 55);
        a.FinalValue = to;
        a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        v.StartAnimation("Opacity", a);
    }

    private static void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        UpdateRegion(expanded: true); // grow the clickable area first, then animate into it
        SetBody(ExpandedW, ExpandedH, animate: true);
        Fade(_collapsedContent, 0, 0);
        Fade(_expandedContent, 1, 110);
    }

    private static void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        SetBody(CollapsedW, CollapsedH, animate: true);
        Fade(_expandedContent, 0, 0);
        Fade(_collapsedContent, 1, 180);
        SetTimer(_hwnd, TimerShrinkRegion, 650, IntPtr.Zero); // shrink the clickable area once it's small
    }

    // ---- Window --------------------------------------------------------------------------------
    private const int TimerCollapse = 1, TimerShrinkRegion = 2;

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0200: // WM_MOUSEMOVE
                KillTimer(hwnd, TimerCollapse);
                if (!_tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 /* TME_LEAVE */, hwndTrack = hwnd };
                    TrackMouseEvent(ref tme);
                    _tracking = true;
                }
                Expand();
                return IntPtr.Zero;
            case 0x02A3: // WM_MOUSELEAVE
                _tracking = false;
                SetTimer(hwnd, TimerCollapse, 450, IntPtr.Zero);
                return IntPtr.Zero;
            case 0x0113: // WM_TIMER
                KillTimer(hwnd, wParam);
                if (wParam == TimerCollapse && !CursorInWindow()) Collapse();
                if (wParam == TimerShrinkRegion && !_expanded) UpdateRegion(expanded: false);
                return IntPtr.Zero;
            case 0x0021: // WM_MOUSEACTIVATE: never take focus
                return new IntPtr(3); // MA_NOACTIVATE
            case 0x0205: // WM_RBUTTONUP: quit the prototype
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static bool CursorInWindow()
    {
        GetCursorPos(out var p);
        return WindowFromPoint(p) == _hwnd;
    }

    /// <summary>Only the notch (plus a small hover margin) takes the mouse; everything else is click-through.</summary>
    private static void UpdateRegion(bool expanded)
    {
        float w = expanded ? ExpandedW + 2 * R : CollapsedW + 60, h = expanded ? ExpandedH + 8 : CollapsedH + 6;
        int l = (int)Px((HostW - w) / 2), r = (int)Px((HostW + w) / 2);
        SetWindowRgn(_hwnd, CreateRectRgn(l, 0, r, (int)Px(h)), true);
    }

    private static IntPtr CreateHostWindow()
    {
        _proc = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "CompositionNotchPrototype",
            hCursor = LoadCursor(IntPtr.Zero, 32512),
        };
        RegisterClassEx(ref wc);
        const uint exStyle = 0x00200000 /* NOREDIRECTIONBITMAP */ | 0x00000008 /* TOPMOST */ | 0x00000080 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */;
        return CreateWindowEx(exStyle, wc.lpszClassName, "Composition notch prototype", 0x80000000 /* WS_POPUP */,
            0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private static void PositionWindow()
    {
        var mon = MonitorFromPoint(new POINT(), 1);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        int w = (int)Px(HostW), h = (int)Px(HostH);
        int x = mi.rcMonitor.left + (mi.rcMonitor.right - mi.rcMonitor.left - w) / 2;
        SetWindowPos(_hwnd, new IntPtr(-1), x, mi.rcMonitor.top, w, h, 0x0010 /* NOACTIVATE */);
    }

    // ---- Composition interop -----------------------------------------------------------------------
    private static IntPtr _dispatcherQueueController;

    private static void EnsureDispatcherQueue()
    {
        var options = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
        CreateDispatcherQueueController(options, out _dispatcherQueueController);
    }

    private static unsafe DesktopWindowTarget CreateDesktopTarget(Compositor compositor, IntPtr hwnd)
    {
        var unknown = ((WinRT.IWinRTObject)compositor).NativeObject.ThisPtr;
        var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"); // ICompositorDesktopInterop
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out var interop));
        try
        {
            // vtable: IUnknown (3 slots), then CreateDesktopWindowTarget(HWND, BOOL isTopmost, out IDesktopWindowTarget*)
            var vtbl = *(IntPtr**)interop;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)vtbl[3];
            IntPtr target;
            Marshal.ThrowExceptionForHR(create(interop, hwnd, 1, &target));
            return DesktopWindowTarget.FromAbi(target);
        }
        finally { Marshal.Release(interop); }
    }

    // ---- P/Invoke --------------------------------------------------------------------------------
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr fn);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr h, IntPtr id);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr inst, int id);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
