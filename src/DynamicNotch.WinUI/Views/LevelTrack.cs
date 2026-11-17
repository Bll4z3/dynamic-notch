using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// A fully round level / progress track. The fill is a full-width rounded bar slid into place by a
/// compositor translation inside a rounded clip (a transform, never a width change), so the leading
/// edge stays round at every level. When <see cref="Interactive"/>, hovering springs a thumb in and
/// dragging sets the value (<see cref="UserChanged"/>). <see cref="Indeterminate"/> swaps the fill for
/// a looping shimmer that runs entirely on the compositor.
/// </summary>
public sealed class LevelTrack : Grid
{
    private readonly Grid _clipHost = new();
    private readonly Border _track = new(), _fill = new(), _shimmer = new();
    private readonly Ellipse _thumb = new();
    private readonly SolidColorBrush _fillBrush = new(Theme.VolumeColor);
    // (Interactive sliders and progress bars are filled with Theme.AccentBrush via FillBrush.)
    private readonly CompositionPropertySet _p;
    private readonly CompSpring _level, _hover;
    private readonly CompositionRoundedRectangleGeometry _clipGeometry;
    private readonly double _height;
    private bool _dragging, _indeterminate, _over;

    public LevelTrack(double height = Theme.ActivityTrackHeight)
    {
        _height = height;
        Height = Theme.ActivityThumbSize + 4;
        Background = Theme.TransparentBrush; // the whole row is grabbable, not just the thin bar
        VerticalAlignment = VerticalAlignment.Center;

        var radius = new CornerRadius(height / 2);
        _clipHost.Height = height;
        _clipHost.VerticalAlignment = VerticalAlignment.Center;
        _clipHost.IsHitTestVisible = false;
        _track.Background = Theme.TrackBrush;
        _track.CornerRadius = radius;
        _fill.Background = _fillBrush;
        _fill.CornerRadius = radius;
        _shimmer.Background = _fillBrush;
        _shimmer.CornerRadius = radius;
        _shimmer.HorizontalAlignment = HorizontalAlignment.Left;
        _shimmer.Visibility = Visibility.Collapsed;
        _clipHost.Children.Add(_track);
        _clipHost.Children.Add(_fill);
        _clipHost.Children.Add(_shimmer);
        Children.Add(_clipHost);

        _thumb.Width = _thumb.Height = Theme.ActivityThumbSize;
        _thumb.Fill = new SolidColorBrush(Theme.PrimaryTextColor);
        _thumb.HorizontalAlignment = HorizontalAlignment.Left;
        _thumb.VerticalAlignment = VerticalAlignment.Center;
        _thumb.IsHitTestVisible = false;
        Children.Add(_thumb);

        var host = Anim.VisualOf(_clipHost);
        var c = host.Compositor;
        _p = c.CreatePropertySet();
        _p.InsertScalar("w", 1);
        _p.InsertScalar("s", 0);
        _level = new CompSpring(_p, "v", Motion.ActivityFill, 0, precision: 0.25);
        _hover = new CompSpring(_p, "h", Motion.Pop, 0);

        _clipGeometry = c.CreateRoundedRectangleGeometry();
        _clipGeometry.CornerRadius = new Vector2((float)height / 2);
        host.Clip = c.CreateGeometricClip(_clipGeometry);

        Anim.VisualOf(_fill, translation: true).StartAnimation("Translation", Anim.Expr(c, "Vector3((Clamp(p.v, 0, 1) - 1) * p.w, 0, 0)", ("p", _p)));
        Anim.VisualOf(_shimmer, translation: true).StartAnimation("Translation", Anim.Expr(c, "Vector3((p.s * 1.4 - 0.4) * p.w, 0, 0)", ("p", _p)));
        var tv = Anim.VisualOf(_thumb, translation: true);
        float half = (float)Theme.ActivityThumbSize / 2;
        tv.CenterPoint = new Vector3(half, half, 0);
        var tMove = Anim.Expr(c, "Vector3(Clamp(p.v, 0, 1) * p.w - T, 0, 0)", ("p", _p));
        tMove.SetScalarParameter("T", half);
        tv.StartAnimation("Translation", tMove);
        tv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.h, 0, 1)", ("p", _p)));
        tv.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.4 + 0.6 * Clamp(p.h, 0, 1.15), 0.4 + 0.6 * Clamp(p.h, 0, 1.15), 1)", ("p", _p)));

        SizeChanged += (_, e) =>
        {
            float w = (float)e.NewSize.Width;
            _p.InsertScalar("w", w);
            _clipGeometry.Size = new Vector2(w, (float)_height);
            _shimmer.Width = Math.Max(0, w * 0.4);
        };

        PointerEntered += (_, _) => { _over = true; if (Interactive) _hover.SetTarget(1, Motion.Pop); HoverChanged?.Invoke(true); };
        PointerExited += (_, _) => { _over = false; if (!_dragging) _hover.SetTarget(0, Motion.FadeOut); HoverChanged?.Invoke(false); };
        PointerPressed += OnPressed;
        PointerMoved += (_, e) => { if (_dragging) DragTo(e); };
        PointerReleased += (_, e) => EndDrag(e);
        PointerCaptureLost += (_, _) => { _dragging = false; if (!_over) _hover.SetTarget(0, Motion.FadeOut); };
        Tapped += (_, e) => { if (Interactive) e.Handled = true; };
    }

    /// <summary>Dragging / clicking sets the value (raised with 0..1 while the user drags).</summary>
    public event Action<double>? UserChanged;
    /// <summary>The pointer entered / left the track (the pill stays open while hovered).</summary>
    public event Action<bool>? HoverChanged;

    public bool Interactive { get; set; }
    public bool IsDragging => _dragging;
    public double Value => _level.Target;

    public Color FillColor { set { _fillBrush.Color = value; _fill.Background = _shimmer.Background = _fillBrush; } }

    /// <summary>Draws the fill with a shared brush (e.g. <see cref="Theme.AccentBrush"/>, so it follows the accent live).</summary>
    public Brush FillBrush { set => _fill.Background = _shimmer.Background = value; }

    /// <summary>The user let go after dragging (commit point for continuous values).</summary>
    public event Action? DragEnded;

    /// <summary>Keeps the thumb shown (keyboard focus on a slider).</summary>
    public void ShowThumb(bool on) => _hover.SetTarget(on || _over || _dragging ? 1 : 0, on ? Motion.Pop : Motion.FadeOut);

    /// <summary>Animates the fill to <paramref name="value"/> (0..1). While the user drags, outside updates are ignored.</summary>
    public void SetValue(double value, bool animate = true)
    {
        if (_dragging) return;
        value = Math.Clamp(value, 0, 1);
        if (animate) _level.SetTarget(value, Motion.ActivityFill); else _level.Snap(value);
    }

    /// <summary>Dims the fill (muted volume).</summary>
    public bool Dimmed { set => _fill.Opacity = value ? 0.35 : 1; }

    public bool Indeterminate
    {
        get => _indeterminate;
        set
        {
            if (value == _indeterminate) return;
            _indeterminate = value;
            _fill.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _shimmer.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value && Anim.Enabled)
            {
                var a = _p.Compositor.CreateScalarKeyFrameAnimation();
                a.InsertKeyFrame(0, 0);
                a.InsertKeyFrame(1, 1, _p.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.55f, 1)));
                a.Duration = TimeSpan.FromSeconds(Motion.ShimmerPeriod);
                a.IterationBehavior = AnimationIterationBehavior.Forever;
                _p.StartAnimation("s", a);
            }
            else
            {
                _p.StopAnimation("s");
                _p.InsertScalar("s", value ? 0.3f : 0);
            }
        }
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!Interactive) return;
        _dragging = CapturePointer(e.Pointer);
        _hover.SetTarget(1, Motion.Pop);
        DragTo(e);
        e.Handled = true;
    }

    private void DragTo(PointerRoutedEventArgs e)
    {
        double w = ActualWidth;
        if (w <= 0) return;
        double v = Math.Clamp(e.GetCurrentPoint(this).Position.X / w, 0, 1);
        _level.SetTarget(v, Motion.Press);
        UserChanged?.Invoke(v);
        e.Handled = true;
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        if (!_over) _hover.SetTarget(0, Motion.FadeOut);
        e.Handled = true;
        DragEnded?.Invoke();
    }
}
