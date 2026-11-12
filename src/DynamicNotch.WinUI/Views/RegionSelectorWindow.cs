using DynamicNotch.Design;
using DynamicNotch.Interop;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace DynamicNotch.Views;

/// <summary>
/// Full-screen dimmed overlay for picking a screen region (same behaviour as the WPF one). One
/// see-through window per monitor, each placed in physical pixels so every monitor keeps its own DPI.
/// Drag to draw a rounded, dashed selection; release to capture it. Enter (without dragging) takes the
/// whole monitor under the cursor; Esc cancels. <see cref="SelectAsync"/> returns the region in
/// physical screen pixels, or null when cancelled. Nothing here animates per frame.
/// </summary>
internal sealed class RegionSelectorWindow : Window
{
    private readonly Native.RECT _monitor;
    private readonly CrossGrid _root = new();
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection = new()
    {
        Stroke = Theme.AccentBrush,
        StrokeThickness = Theme.SelectionStrokeThickness,
        StrokeDashArray = new DoubleCollection { Theme.SelectionDash, Theme.SelectionDash }, // in stroke widths, as in WPF
        StrokeDashCap = PenLineCap.Round,
        Fill = new SolidColorBrush(Theme.SelectionFillColor),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };
    private readonly IntPtr _hwnd;
    private Point _start;
    private bool _dragging;
    private Action<Native.RECT?>? _done;

    private RegionSelectorWindow(Native.RECT monitor)
    {
        _monitor = monitor;
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Select area";
        SystemBackdrop = new TransparentBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        Native.MakeSeeThroughPopup(_hwnd);
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST, Native.WS_EX_APPWINDOW);

        _canvas.Background = new SolidColorBrush(Theme.OverlayDimColor); // (a UserControl doesn't draw its own Background)
        _root.IsTabStop = true;
        _root.UseSystemFocusVisuals = false;
        _canvas.Children.Add(_selection);
        _root.Content = _canvas;
        Content = _root;

        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += (_, e) => { if (_dragging) UpdateSelection(e.GetCurrentPoint(_canvas).Position); };
        _canvas.PointerReleased += OnReleased;
        _root.KeyDown += OnKeyDown;
    }

    /// <summary>Shows an overlay on every monitor and waits for a selection.</summary>
    public static Task<Native.RECT?> SelectAsync()
    {
        var tcs = new TaskCompletionSource<Native.RECT?>();
        var monitors = Native.AllMonitors();
        if (monitors.Count == 0) { tcs.SetResult(null); return tcs.Task; }
        var windows = monitors.Select(m => new RegionSelectorWindow(m)).ToList();
        bool finished = false;
        void Finish(Native.RECT? result)
        {
            if (finished) return;
            finished = true;
            foreach (var w in windows) { try { w.Close(); } catch { } }
            tcs.TrySetResult(result);
        }

        Native.GetCursorPos(out var cursor);
        foreach (var w in windows)
        {
            w._done = Finish;
            w.Closed += (_, _) => Finish(null); // closed from outside (e.g. Alt+F4)
            var b = w._monitor;
            w.AppWindow.MoveAndResize(new RectInt32(b.Left, b.Top, b.Width, b.Height));
            w.AppWindow.Show(activateWindow: false);
            Native.MakeSeeThroughPopup(w._hwnd); // showing re-applies the presenter frame
            Native.SetWindowPos(w._hwnd, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_NOACTIVATE);
        }
        // Keyboard goes to the overlay on the monitor under the cursor.
        var active = windows.FirstOrDefault(w => cursor.X >= w._monitor.Left && cursor.X < w._monitor.Right && cursor.Y >= w._monitor.Top && cursor.Y < w._monitor.Bottom) ?? windows[0];
        active.Activate();
        Native.SetForegroundWindow(active._hwnd);
        active._root.Focus(FocusState.Programmatic);
        return tcs.Task;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        _start = e.GetCurrentPoint(_canvas).Position;
        _dragging = true;
        _canvas.CapturePointer(e.Pointer);
        UpdateSelection(_start);
        _selection.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _canvas.ReleasePointerCapture(e.Pointer);
        var end = e.GetCurrentPoint(_canvas).Position;
        var r = new Rect(_start, end);
        if (r.Width < 6 || r.Height < 6)
        {
            // A click without a drag: keep waiting for a real selection.
            _selection.Visibility = Visibility.Collapsed;
            return;
        }
        // DIPs inside this monitor's window → physical screen pixels.
        double k = _root.XamlRoot?.RasterizationScale ?? 1;
        int l = _monitor.Left + (int)Math.Round(r.Left * k), t = _monitor.Top + (int)Math.Round(r.Top * k);
        int rr = _monitor.Left + (int)Math.Round(r.Right * k), b = _monitor.Top + (int)Math.Round(r.Bottom * k);
        _done?.Invoke(new Native.RECT(l, t, rr, b));
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                _done?.Invoke(null);
                e.Handled = true;
                break;
            case VirtualKey.Enter when !_dragging:
                Native.GetCursorPos(out var c);
                var screen = Native.AllMonitors().FirstOrDefault(m => c.X >= m.Left && c.X < m.Right && c.Y >= m.Top && c.Y < m.Bottom);
                _done?.Invoke(screen.Width > 0 ? screen : _monitor);
                e.Handled = true;
                break;
        }
    }

    private void UpdateSelection(Point p)
    {
        var r = new Rect(_start, p);
        Canvas.SetLeft(_selection, r.X);
        Canvas.SetTop(_selection, r.Y);
        _selection.Width = r.Width;
        _selection.Height = r.Height;
        _selection.RadiusX = _selection.RadiusY = Math.Min(Theme.CornerRadius, Math.Min(r.Width, r.Height) / 2);
    }

    /// <summary>The overlay root: focusable (for Esc / Enter) and showing the crosshair cursor.</summary>
    private sealed class CrossGrid : UserControl
    {
        public CrossGrid() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    }
}
