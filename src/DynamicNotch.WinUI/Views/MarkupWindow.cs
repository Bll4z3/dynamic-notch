using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using DynamicNotch.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage;
using Windows.UI;
using VirtualKey = Windows.System.VirtualKey;
using Colors = Microsoft.UI.Colors;

namespace DynamicNotch.Views;

/// <summary>
/// A small mark-up editor for one screenshot: pen, highlighter, arrow, text and crop, five colours, undo
/// (Ctrl+Z too), Cancel / Save (Ctrl+S). Black, like Settings, and it eases in (scale + fade) when it opens.
/// Marks are kept in image pixels, drawn over the picture with Win2D, and Save renders them at the image's
/// full resolution (cropped if a crop was made) into a new PNG the app owns; the original file is untouched.
/// </summary>
internal sealed class MarkupWindow : Window
{
    private enum Tool { Pen, Highlighter, Arrow, Text, Crop }

    private abstract record Mark(Color Color);
    private sealed record Stroke(Color Color, List<Vector2> Points, float Width, bool Highlight) : Mark(Color);
    private sealed record Arrow(Color Color, Vector2 From, Vector2 To, float Width) : Mark(Color);
    private sealed record Label(Color Color, Vector2 At, string Text, float Size) : Mark(Color);
    private sealed record CropMark(Rect Area) : Mark(Colors.Transparent);
    private readonly record struct Rect(float X, float Y, float W, float H);

    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 0xFF, 0x3B, 0x30), Color.FromArgb(255, 0xFF, 0xCC, 0x00), Color.FromArgb(255, 0x34, 0xC7, 0x59),
        Color.FromArgb(255, 0x0A, 0x84, 0xFF), Color.FromArgb(255, 0xFF, 0xFF, 0xFF),
    };

    private readonly string _path;
    private readonly Func<string, Task> _saved;
    private readonly Grid _root = new(), _stage = new();
    private readonly CanvasControl _canvas = new();
    private readonly Canvas _overlay = new();
    private readonly List<Mark> _marks = new();
    private readonly Dictionary<Tool, IconButton> _toolButtons = new();
    private readonly List<(Color color, Border dot)> _dots = new();
    private CanvasBitmap? _image;
    private Tool _tool = Tool.Pen;
    private Color _color = Palette[0];
    private Mark? _live;          // the mark being drawn
    private Vector2 _cropStart;
    private TextBox? _editing;
    private Vector2 _editingAt;
    private bool _closing, _saving;
    private readonly IntPtr _hwnd;
    private readonly InputNonClientPointerSource _nonClient;
    private FrameworkElement _dragArea = null!;

    private MarkupWindow(string path, Func<string, Task> saved)
    {
        _path = path;
        _saved = saved;
        Title = "Mark up - Dynamic Notch";
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        // Borderless-looking: no title bar and no minimise / maximise / close buttons (Cancel and Save are in
        // the toolbar). The thin resize border and rounded corners stay. It drags by the toolbar's empty middle
        // (a native caption region, so Windows moves it smoothly); every button stays clickable.
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(true, false);
            p.IsMinimizable = p.IsMaximizable = false;
            p.IsResizable = true;
            p.IsAlwaysOnTop = true; // in front of everything while you mark up (it closes when you're done)
        }
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        _root.Background = new SolidColorBrush(Colors.Black);
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(Toolbar());

        _stage.Margin = new Thickness(Theme.S4, Theme.S2, Theme.S4, Theme.S4);
        _canvas.ClearColor = Colors.Black;
        _canvas.CreateResources += (s, a) => a.TrackAsyncAction(LoadAsync(s).AsAsyncAction());
        _canvas.Draw += OnDraw;
        _stage.Children.Add(_canvas);
        _overlay.Background = Theme.TransparentBrush;
        _overlay.PointerPressed += OnPressed;
        _overlay.PointerMoved += OnMoved;
        _overlay.PointerReleased += OnReleased;
        _overlay.PointerCaptureLost += (_, _) => Commit();
        _stage.Children.Add(_overlay);
        Grid.SetRow(_stage, 1);
        _root.Children.Add(_stage);
        _root.KeyDown += OnKey;
        Content = _root;
        SelectTool(Tool.Pen);
        SelectColor(_color);

        // Opening: eases up into place (scale + fade, ~250 ms).
        _root.Loaded += (_, _) =>
        {
            if (!Anim.Enabled) return;
            var v = Anim.VisualOf(_root);
            var c = v.Compositor;
            v.CenterPoint = new Vector3((float)_root.ActualWidth / 2, (float)_root.ActualHeight / 2, 0);
            var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));
            var o = c.CreateScalarKeyFrameAnimation(); o.InsertKeyFrame(0, 0); o.InsertKeyFrame(1, 1, ease); o.Duration = TimeSpan.FromMilliseconds(240);
            var sc = c.CreateVector3KeyFrameAnimation(); sc.InsertKeyFrame(0, new Vector3(0.96f, 0.96f, 1)); sc.InsertKeyFrame(1, Vector3.One, ease); sc.Duration = TimeSpan.FromMilliseconds(260);
            v.StartAnimation("Opacity", o);
            v.StartAnimation("Scale", sc);
        };
        _root.SizeChanged += (_, _) => UpdateDragRegion();
        _dragArea.SizeChanged += (_, _) => UpdateDragRegion();
        Closed += (_, _) => { _canvas.RemoveFromVisualTree(); _image?.Dispose(); };
    }

    /// <summary>Opens the editor for <paramref name="path"/>; <paramref name="saved"/> gets the marked-up file.</summary>
    public static void Open(string path, Func<string, Task> saved)
    {
        var w = new MarkupWindow(path, saved);
        var (bounds, scale) = Native.GetPrimaryMonitor();
        int width = (int)Math.Min(bounds.Width * 0.8, 1100 * scale), height = (int)Math.Min(bounds.Height * 0.8, 760 * scale);
        w.AppWindow.MoveAndResize(new RectInt32(bounds.Left + (bounds.Width - width) / 2, bounds.Top + (bounds.Height - height) / 2, width, height));
        // Shown restored and brought to the front with focus. (Activate alone, from a click on the no-activate
        // notch, could leave it behind other windows or only flashing in the taskbar.)
        if (w.AppWindow.Presenter is OverlappedPresenter p) p.Restore();
        w.AppWindow.Show(activateWindow: true);
        w.Activate();
        w.AppWindow.MoveInZOrderAtTop();
        Native.SetForegroundWindow(w._hwnd);
        w._root.Focus(FocusState.Programmatic);
    }

    // ---- Toolbar ----------------------------------------------------------------------------------------
    private FrameworkElement Toolbar()
    {
        var bar = new Grid { Padding = new Thickness(Theme.S4, 0, Theme.S3, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = "Mark up", Style = (Style)Application.Current.Resources["BaseText"], FontFamily = Theme.FontSemiBold, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Theme.S4, 0) });
        foreach (var (tool, glyph, tip) in new[] { (Tool.Pen, Icons.Pen, "Pen"), (Tool.Highlighter, Icons.Highlighter, "Highlighter"), (Tool.Arrow, Icons.ArrowTool, "Arrow"), (Tool.Text, Icons.TextTool, "Text"), (Tool.Crop, Icons.Crop, "Crop") })
        {
            var b = new IconButton(32, Theme.IconSmall + 1) { Icon = glyph, Foreground = Theme.SecondaryText, Tooltip = tip };
            b.Click += () => SelectTool(tool);
            _toolButtons[tool] = b;
            left.Children.Add(b);
        }
        left.Children.Add(new Border { Width = 1, Height = 20, Background = Theme.CardBorderBrush, Margin = new Thickness(Theme.S2, 0, Theme.S2, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (var col in Palette)
        {
            var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(col), BorderThickness = new Thickness(2), Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            var hit = new Grid { Width = 26, Height = 26, Background = Theme.TransparentBrush, Children = { dot } };
            hit.Tapped += (_, _) => SelectColor(col);
            Motions.PressScale(hit);
            _dots.Add((col, dot));
            left.Children.Add(hit);
        }
        left.Children.Add(new Border { Width = 1, Height = 20, Background = Theme.CardBorderBrush, Margin = new Thickness(Theme.S2, 0, Theme.S2, 0), VerticalAlignment = VerticalAlignment.Center });
        var undo = new IconButton(32, Theme.IconSmall + 1) { Icon = Icons.Undo, Foreground = Theme.SecondaryText, Tooltip = "Undo (Ctrl+Z)" };
        undo.Click += Undo;
        left.Children.Add(undo);
        bar.Children.Add(left);
        // The empty middle of the bar drags the window (see UpdateDragRegion).
        _dragArea = new Grid { Background = Theme.TransparentBrush };
        Grid.SetColumn(_dragArea, 1);
        bar.Children.Add(_dragArea);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S2, VerticalAlignment = VerticalAlignment.Center };
        var cancel = new PillButton(null, "Cancel", quiet: true);
        cancel.Click += Cancel;
        cancel.Tooltip = "Cancel (Esc)";
        var save = new PillButton(Icons.Check, "Save");
        save.Click += async () => await SaveAsync();
        save.Tooltip = "Save (Ctrl+S)";
        right.Children.Add(cancel);
        right.Children.Add(save);
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);
        return bar;
    }

    private void SelectTool(Tool t)
    {
        Commit();
        _tool = t;
        foreach (var (tool, b) in _toolButtons)
        {
            b.Foreground = tool == t ? Theme.PrimaryText : Theme.SecondaryText;
            b.Backing = tool == t ? Theme.CardHoverBrush : null;
        }
        _overlay.ProtectedCursorSet(t == Tool.Text ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Cross);
    }

    private void SelectColor(Color c)
    {
        _color = c;
        foreach (var (col, dot) in _dots) dot.BorderBrush = new SolidColorBrush(col == c ? Colors.Black : Colors.Transparent);
        foreach (var (col, dot) in _dots) Anim.VisualOf(dot).Scale = col == c ? new Vector3(1.25f) : Vector3.One;
        foreach (var (col, dot) in _dots) { var v = Anim.VisualOf(dot); v.CenterPoint = new Vector3(9, 9, 0); }
        if (_editing != null) _editing.Foreground = new SolidColorBrush(c);
    }

    // ---- Image ↔ screen ---------------------------------------------------------------------------------
    private async Task LoadAsync(CanvasControl sender)
    {
        var file = await StorageFile.GetFileFromPathAsync(_path);
        using var stream = await file.OpenReadAsync();
        _image = await CanvasBitmap.LoadAsync(sender, stream, 96);
    }

    private Rect Crop => _marks.OfType<CropMark>().LastOrDefault()?.Area ?? new Rect(0, 0, (float)(_image?.SizeInPixels.Width ?? 1), (float)(_image?.SizeInPixels.Height ?? 1));

    /// <summary>Scale and offset that fit the (cropped) image into the canvas.</summary>
    private (float scale, Vector2 offset) Fit()
    {
        var crop = Crop;
        float w = (float)_canvas.ActualWidth, h = (float)_canvas.ActualHeight;
        float k = Math.Min(w / crop.W, h / crop.H);
        k = Math.Min(k, 2f);
        return (k, new Vector2((w - crop.W * k) / 2 - crop.X * k, (h - crop.H * k) / 2 - crop.Y * k));
    }

    private Vector2 ToImage(Windows.Foundation.Point p)
    {
        var (k, off) = Fit();
        return (new Vector2((float)p.X, (float)p.Y) - off) / k;
    }

    private float BaseWidth => _image == null ? 4 : Math.Max(3f, (float)Math.Max(_image.SizeInPixels.Width, _image.SizeInPixels.Height) / 320f);

    // ---- Drawing --------------------------------------------------------------------------------------
    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_image == null) return;
        var ds = args.DrawingSession;
        var (k, off) = Fit();
        var crop = Crop;
        var cropOnScreen = new Windows.Foundation.Rect(off.X + crop.X * k, off.Y + crop.Y * k, crop.W * k, crop.H * k);
        ds.Transform = Matrix3x2.CreateScale(k) * Matrix3x2.CreateTranslation(off);
        using (ds.CreateLayer(1f, new Windows.Foundation.Rect(crop.X, crop.Y, crop.W, crop.H)))
        {
            ds.DrawImage(_image);
            foreach (var m in _marks) DrawMark(ds, m);
            if (_live is not null and not CropMark) DrawMark(ds, _live);
        }
        ds.Transform = Matrix3x2.Identity;
        if (_live is CropMark live)
        {
            var r = new Windows.Foundation.Rect(off.X + live.Area.X * k, off.Y + live.Area.Y * k, live.Area.W * k, live.Area.H * k);
            using var outside = CanvasGeometry.CreateRectangle(ds, cropOnScreen).CombineWith(CanvasGeometry.CreateRectangle(ds, r), Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            ds.FillGeometry(outside, Color.FromArgb(0x99, 0, 0, 0));
            ds.DrawRectangle(r, Colors.White, 1.5f);
        }
    }

    private void DrawMark(CanvasDrawingSession ds, Mark m)
    {
        var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        switch (m)
        {
            case Stroke s when s.Points.Count > 0:
            {
                if (s.Points.Count == 1) { ds.FillCircle(s.Points[0], s.Width / 2, s.Highlight ? WithAlpha(s.Color, 0x66) : s.Color); break; }
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(s.Points[0]);
                for (int i = 1; i < s.Points.Count; i++) pb.AddLine(s.Points[i]);
                pb.EndFigure(CanvasFigureLoop.Open);
                using var geo = CanvasGeometry.CreatePath(pb);
                // One geometry per stroke, so a highlighter's overlaps don't darken.
                ds.DrawGeometry(geo, s.Highlight ? WithAlpha(s.Color, 0x66) : s.Color, s.Width, round);
                break;
            }
            case Arrow a:
            {
                var dir = a.To - a.From;
                float len = dir.Length();
                if (len < 1) break;
                dir /= len;
                float head = Math.Min(len * 0.45f, a.Width * 5f);
                var normal = new Vector2(-dir.Y, dir.X);
                ds.DrawLine(a.From, a.To - dir * head * 0.5f, a.Color, a.Width, round);
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(a.To);
                pb.AddLine(a.To - dir * head + normal * head * 0.6f);
                pb.AddLine(a.To - dir * head - normal * head * 0.6f);
                pb.EndFigure(CanvasFigureLoop.Closed);
                using var geo = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(geo, a.Color);
                break;
            }
            case Label l:
            {
                using var fmt = new CanvasTextFormat { FontFamily = "Segoe UI Variable Display", FontSize = l.Size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                ds.DrawText(l.Text, l.At, l.Color, fmt);
                break;
            }
        }
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    // ---- Input ----------------------------------------------------------------------------------------
    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_image == null || !e.GetCurrentPoint(_overlay).Properties.IsLeftButtonPressed) return;
        var at = ToImage(e.GetCurrentPoint(_canvas).Position);
        if (_tool == Tool.Text) { BeginText(e.GetCurrentPoint(_overlay).Position, at); e.Handled = true; return; }
        Commit();
        _overlay.CapturePointer(e.Pointer);
        float w = BaseWidth;
        _live = _tool switch
        {
            Tool.Pen => new Stroke(_color, new List<Vector2> { at }, w, false),
            Tool.Highlighter => new Stroke(_color, new List<Vector2> { at }, w * 4.5f, true),
            Tool.Arrow => new Arrow(_color, at, at, w * 1.2f),
            Tool.Crop => new CropMark(new Rect(at.X, at.Y, 0, 0)),
            _ => null,
        };
        _cropStart = at;
        _canvas.Invalidate();
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_live == null) return;
        var at = ToImage(e.GetCurrentPoint(_canvas).Position);
        switch (_live)
        {
            case Stroke s:
                if (s.Points.Count == 0 || Vector2.Distance(s.Points[^1], at) > 1.5f) s.Points.Add(at);
                break;
            case Arrow a:
                _live = a with { To = at };
                break;
            case CropMark:
                var full = new Rect(0, 0, _image!.SizeInPixels.Width, _image.SizeInPixels.Height);
                float x0 = Math.Clamp(Math.Min(_cropStart.X, at.X), 0, full.W), y0 = Math.Clamp(Math.Min(_cropStart.Y, at.Y), 0, full.H);
                float x1 = Math.Clamp(Math.Max(_cropStart.X, at.X), 0, full.W), y1 = Math.Clamp(Math.Max(_cropStart.Y, at.Y), 0, full.H);
                _live = new CropMark(new Rect(x0, y0, x1 - x0, y1 - y0));
                break;
        }
        _canvas.Invalidate();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _overlay.ReleasePointerCapture(e.Pointer);
        Commit();
    }

    /// <summary>Finishes the mark being drawn (or the text being typed).</summary>
    private void Commit()
    {
        if (_editing != null) FinishText();
        if (_live == null) return;
        var m = _live;
        _live = null;
        bool keep = m switch
        {
            CropMark c => c.Area.W >= 8 && c.Area.H >= 8,
            Arrow a => Vector2.Distance(a.From, a.To) >= 4,
            _ => true,
        };
        if (keep) _marks.Add(m);
        _canvas.Invalidate();
    }

    private void BeginText(Windows.Foundation.Point screen, Vector2 at)
    {
        if (_editing != null) FinishText();
        var (k, _) = Fit();
        float size = BaseWidth * 7f;
        _editingAt = at;
        _editing = new TextBox
        {
            FontSize = Math.Max(10, size * k), FontFamily = new FontFamily("Segoe UI Variable Display"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_color), Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
            BorderThickness = new Thickness(0), MinWidth = 60, Padding = new Thickness(0), AcceptsReturn = false, PlaceholderText = "Type…",
        };
        Canvas.SetLeft(_editing, screen.X);
        Canvas.SetTop(_editing, screen.Y - _editing.FontSize * 0.7);
        _editing.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { FinishText(); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { _overlay.Children.Clear(); _editing = null; e.Handled = true; }
        };
        _editing.LostFocus += (_, _) => FinishText();
        _overlay.Children.Add(_editing);
        _editing.Focus(FocusState.Programmatic);
    }

    private void FinishText()
    {
        var box = _editing;
        if (box == null) return;
        _editing = null;
        _overlay.Children.Remove(box);
        if (!string.IsNullOrWhiteSpace(box.Text))
            _marks.Add(new Label(((SolidColorBrush)box.Foreground).Color, _editingAt - new Vector2(0, BaseWidth * 7f * 0.7f), box.Text.Trim(), BaseWidth * 7f));
        _canvas.Invalidate();
        _root.Focus(FocusState.Programmatic);
    }

    private void Undo()
    {
        if (_editing != null) { _overlay.Children.Clear(); _editing = null; return; }
        if (_marks.Count == 0) return;
        _marks.RemoveAt(_marks.Count - 1);
        _canvas.Invalidate();
    }

    private async void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (_editing != null) return;
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.S) { e.Handled = true; await SaveAsync(); }
        else if (e.Key == VirtualKey.Escape) { e.Handled = true; Cancel(); }
    }

    // ---- Save / close ---------------------------------------------------------------------------------
    private async Task SaveAsync()
    {
        Commit();
        if (_image == null || _closing || _saving) return;
        _saving = true;
        var crop = Crop;
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            using var target = new CanvasRenderTarget(device, crop.W, crop.H, 96);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                ds.Transform = Matrix3x2.CreateTranslation(-crop.X, -crop.Y);
                using var img = CanvasBitmap.CreateFromBytes(device, _image.GetPixelBytes(), (int)_image.SizeInPixels.Width, (int)_image.SizeInPixels.Height, _image.Format, 96);
                ds.DrawImage(img);
                foreach (var m in _marks) DrawMark(ds, m);
            }
            Directory.CreateDirectory(ScreenshotService.OwnFolder);
            string name = Path.GetFileNameWithoutExtension(_path);
            if (!name.EndsWith("(marked up)", StringComparison.Ordinal)) name += " (marked up)";
            string outPath = Path.Combine(ScreenshotService.OwnFolder, name + ".png");
            for (int n = 2; File.Exists(outPath) && !outPath.Equals(_path, StringComparison.OrdinalIgnoreCase); n++)
                outPath = Path.Combine(ScreenshotService.OwnFolder, $"{name} {n}.png");
            // Through a stream (a path given to Win2D directly can be refused outside an app package).
            using (var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                await target.SaveAsync(ms, CanvasBitmapFileFormat.Png);
                var bytes = new byte[ms.Size];
                ms.Seek(0);
                await System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(ms).ReadExactlyAsync(bytes);
                await File.WriteAllBytesAsync(outPath, bytes);
            }
            await _saved(outPath);
            CloseAnimated();
        }
        catch (Exception ex) { App.LogError(ex); }
        finally { _saving = false; }
    }

    /// <summary>Cancel / Esc: closes without saving (asks first when there are marks).</summary>
    private async void Cancel()
    {
        if (_closing || _saving) return;
        if (_editing != null) { _overlay.Children.Clear(); _editing = null; }
        if (_marks.Count > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot, Title = "Discard your changes?", Content = "Your marks on this screenshot will be lost.",
                PrimaryButtonText = "Discard", CloseButtonText = "Keep editing", DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ElementTheme.Dark,
            };
            try { if (await dialog.ShowAsync() != ContentDialogResult.Primary) return; } catch { return; }
        }
        CloseAnimated();
    }

    /// <summary>Caption (drag) = the empty middle of the toolbar, in physical pixels of the client area.</summary>
    private void UpdateDragRegion()
    {
        if (_dragArea.XamlRoot == null || _dragArea.ActualWidth <= 0) return;
        double k = _dragArea.XamlRoot.RasterizationScale;
        var o = _dragArea.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(0, 0));
        try
        {
            _nonClient.SetRegionRects(NonClientRegionKind.Caption, new[]
            {
                new RectInt32((int)(o.X * k), 0, (int)(_dragArea.ActualWidth * k), (int)(48 * k)),
            });
        }
        catch { }
    }

    private void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        if (!Anim.Enabled) { Close(); return; }
        var v = Anim.VisualOf(_root);
        var c = v.Compositor;
        var o = c.CreateScalarKeyFrameAnimation(); o.InsertKeyFrame(1, 0); o.Duration = TimeSpan.FromMilliseconds(160);
        var sc = c.CreateVector3KeyFrameAnimation(); sc.InsertKeyFrame(1, new Vector3(0.97f, 0.97f, 1)); sc.Duration = TimeSpan.FromMilliseconds(160);
        v.CenterPoint = new Vector3((float)_root.ActualWidth / 2, (float)_root.ActualHeight / 2, 0);
        v.StartAnimation("Opacity", o);
        v.StartAnimation("Scale", sc);
        var t = DispatcherQueue.CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromMilliseconds(170);
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }
}
