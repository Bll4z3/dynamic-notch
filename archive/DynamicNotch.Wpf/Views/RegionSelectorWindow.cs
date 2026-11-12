using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using Forms = System.Windows.Forms;

namespace DynamicNotch.Views;

/// <summary>
/// Full-screen dimmed overlay for picking a screen region. One window per monitor, each placed in
/// physical pixels so every monitor keeps its own DPI. Drag to draw a rounded selection; release to
/// capture it. Enter (without dragging) takes the whole monitor under the cursor; Esc cancels.
/// <see cref="SelectAsync"/> returns the region in physical screen pixels, or null when cancelled.
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    private readonly Forms.Screen _screen;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection = new()
    {
        Stroke = Theme.SelectionStrokeBrush,
        StrokeThickness = Theme.SelectionStrokeThickness,
        StrokeDashArray = new DoubleCollection { Theme.SelectionDash, Theme.SelectionDash },
        StrokeDashCap = PenLineCap.Round,
        Fill = Theme.SelectionFillBrush,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };
    private Point _start;
    private bool _dragging;
    private Action<Int32Rect?>? _done;

    private RegionSelectorWindow(Forms.Screen screen)
    {
        _screen = screen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Theme.OverlayDimBrush;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Title = "Select area";
        // Placeholder size; the real bounds are set in physical pixels once the HWND exists.
        Left = -32000; Top = -32000; Width = 10; Height = 10;
        _canvas.Background = Brushes.Transparent;
        _canvas.Children.Add(_selection);
        Content = _canvas;
    }

    /// <summary>Shows an overlay on every monitor and waits for a selection.</summary>
    public static Task<Int32Rect?> SelectAsync()
    {
        var tcs = new TaskCompletionSource<Int32Rect?>();
        var windows = Forms.Screen.AllScreens.Select(s => new RegionSelectorWindow(s)).ToList();
        bool finished = false;
        void Finish(Int32Rect? result)
        {
            if (finished) return;
            finished = true;
            foreach (var w in windows) w.Close();
            tcs.TrySetResult(result);
        }

        Native.GetCursorPos(out var cursor);
        foreach (var w in windows)
        {
            w._done = Finish;
            w.Show();
        }
        // Keyboard goes to the overlay on the monitor under the cursor.
        var active = windows.FirstOrDefault(w => w._screen.Bounds.Contains(cursor.X, cursor.Y)) ?? windows[0];
        active.Activate();
        active.Focus();
        return tcs.Task;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var b = _screen.Bounds; // physical pixels (per-monitor DPI aware process)
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_SHOWWINDOW);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _dragging = true;
        CaptureMouse();
        UpdateSelection(_start);
        _selection.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) UpdateSelection(e.GetPosition(_canvas));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var end = e.GetPosition(_canvas);
        var r = new Rect(_start, end);
        if (r.Width < 6 || r.Height < 6)
        {
            // A click without a drag: keep waiting for a real selection.
            _selection.Visibility = Visibility.Collapsed;
            return;
        }
        // Convert both corners to physical screen pixels.
        var tl = PointToScreen(r.TopLeft);
        var br = PointToScreen(r.BottomRight);
        _done?.Invoke(new Int32Rect((int)Math.Round(tl.X), (int)Math.Round(tl.Y),
            (int)Math.Round(br.X - tl.X), (int)Math.Round(br.Y - tl.Y)));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _done?.Invoke(null);
                e.Handled = true;
                break;
            case Key.Enter when !_dragging:
                Native.GetCursorPos(out var c);
                var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(c.X, c.Y)) ?? _screen;
                var b = screen.Bounds;
                _done?.Invoke(new Int32Rect(b.Left, b.Top, b.Width, b.Height));
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
        double radius = Math.Min(Theme.CornerRadius, Math.Min(r.Width, r.Height) / 2);
        _selection.RadiusX = _selection.RadiusY = radius;
    }
}
