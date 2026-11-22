using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// Seekable progress bar: a thin rounded track that thickens on hover and grows a draggable handle,
/// both with springs. Dragging scrubs; the position is committed on release. The fill is the music
/// page's album-art accent, blended between tracks. Drawn by the compositor: the track, fill and
/// handle follow a few scalars ("W" width, "V" value, "H" hover) through expressions.
/// </summary>
public sealed class SeekBar : Grid
{
    private readonly CompositionPropertySet _p;
    private readonly CompSpring _hover;
    private readonly CompositionLinearGradientBrush _fillBrush;
    private readonly CompositionColorGradientStop _stopA, _stopB;
    private bool _followAccent = true;
    private readonly ShapeVisual _shapes;
    private bool _dragging, _seekable = true, _over;
    private double _value, _dragValue;

    public SeekBar()
    {
        Height = Theme.SeekHeight;
        Background = Theme.TransparentBrush; // full-height hit area so the bar is easy to grab
        var c = ElementCompositionPreview.GetElementVisual(this).Compositor;

        _p = c.CreatePropertySet();
        _p.InsertScalar("W", 0);
        _p.InsertScalar("V", 0);
        _p.InsertScalar("Seekable", 1);
        _hover = new CompSpring(_p, "H", Motion.Hover, 0, precision: 0.5);

        _shapes = c.CreateShapeVisual();
        string t = $"({Anim.F(Theme.SeekThickness)} + {Anim.F(Theme.SeekThicknessHover - Theme.SeekThickness)} * Clamp(S.H, 0, 1.2))";
        string mid = Anim.F(Theme.SeekHeight / 2);

        var track = c.CreateRoundedRectangleGeometry();
        Bind(track, "Size", $"Vector2(S.W, {t})");
        Bind(track, "Offset", $"Vector2(0, {mid} - {t} / 2)");
        Bind(track, "CornerRadius", $"Vector2({t} / 2, {t} / 2)");
        var trackShape = c.CreateSpriteShape(track);
        trackShape.FillBrush = c.CreateColorBrush(Theme.TrackColor);
        _shapes.Shapes.Add(trackShape);

        var fill = c.CreateRoundedRectangleGeometry();
        Bind(fill, "Size", $"Vector2(S.V > 0 ? Max({t}, S.W * S.V) : 0, {t})");
        Bind(fill, "Offset", $"Vector2(0, {mid} - {t} / 2)");
        Bind(fill, "CornerRadius", $"Vector2({t} / 2, {t} / 2)");
        // Accent gradient fill: base colour at the start, the light tint at the playhead (absolute mapping, so
        // the brightest point always rides the leading edge as the fill grows).
        var pal = AccentPalette.From(Theme.AccentColor);
        _stopA = c.CreateColorGradientStop(0, pal.Base);
        _stopB = c.CreateColorGradientStop(1, pal.Light);
        _fillBrush = c.CreateLinearGradientBrush();
        _fillBrush.MappingMode = CompositionMappingMode.Absolute;
        _fillBrush.ColorStops.Add(_stopA);
        _fillBrush.ColorStops.Add(_stopB);
        _fillBrush.StartPoint = Vector2.Zero;
        var end = c.CreateExpressionAnimation("Vector2(Max(1, S.W * S.V), 0)");
        end.SetReferenceParameter("S", _p);
        _fillBrush.StartAnimation("EndPoint", end);
        // With no artwork the fill follows the live accent (including its cross-fades).
        AccentBinding.Track(_fillBrush, (_, p) => { if (_followAccent) Paint(p, animate: false); });
        var fillShape = c.CreateSpriteShape(fill);
        fillShape.FillBrush = _fillBrush;
        _shapes.Shapes.Add(fillShape);

        // Handle: grows in on hover (and stays while dragging).
        string hr = $"({Anim.F(Theme.SeekHandleSize / 2)} * Clamp(S.H, 0, 1.1) * S.Seekable)";
        var handle = c.CreateEllipseGeometry();
        Bind(handle, "Radius", $"Vector2({hr}, {hr})");
        Bind(handle, "Center", $"Vector2(Clamp(S.W * S.V, {hr}, Max({hr}, S.W - {hr})), {mid})");
        var handleShape = c.CreateSpriteShape(handle);
        handleShape.FillBrush = c.CreateColorBrush(Theme.PrimaryTextColor);
        _shapes.Shapes.Add(handleShape);

        ElementCompositionPreview.SetElementChildVisual(this, _shapes);
        SizeChanged += (_, e) =>
        {
            _shapes.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            _p.InsertScalar("W", (float)e.NewSize.Width);
        };

        PointerEntered += (_, _) => { _over = true; if (_seekable) _hover.SetTarget(1, Motion.Hover); };
        PointerExited += (_, _) => { _over = false; if (!_dragging) _hover.SetTarget(0, Motion.Hover); };
        PointerPressed += OnPressed;
        PointerMoved += (_, e) => { if (_dragging) Update(e); };
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => { if (_dragging) { _dragging = false; ScrubCommitted?.Invoke(_dragValue); if (!_over) _hover.SetTarget(0, Motion.Hover); } };

        void Bind(CompositionObject o, string prop, string expr)
        {
            var e = c.CreateExpressionAnimation(expr);
            e.SetReferenceParameter("S", _p);
            o.StartAnimation(prop, e);
        }
    }

    public event Action? ScrubStarted;
    public event Action<double>? Scrubbing;
    public event Action<double>? ScrubCommitted;

    public double Value
    {
        get => _value;
        set
        {
            double previous = _value;
            _value = value;
            if (_dragging) return;
            float v = (float)Math.Clamp(value, 0, 1);
            // Small forward steps (the playback clock ticks every 250 ms) glide linearly to the next value, so
            // the fill moves continuously; jumps (a seek, a new track) land at once.
            double step = value - previous;
            if (Anim.Enabled && step > 0 && step < 0.02)
            {
                var a = _p.Compositor.CreateScalarKeyFrameAnimation();
                a.InsertKeyFrame(1, v, _p.Compositor.CreateLinearEasingFunction());
                a.Duration = TimeSpan.FromMilliseconds(Motion.SeekGlideMs);
                _p.StartAnimation("V", a);
            }
            else
            {
                _p.StopAnimation("V");
                _p.InsertScalar("V", v);
            }
        }
    }

    public bool IsSeekable
    {
        get => _seekable;
        set { _seekable = value; _p.InsertScalar("Seekable", value ? 1 : 0); if (!value) _hover.SetTarget(0, Motion.Hover); }
    }

    /// <summary>Blends the fill to a new accent colour (~0.7 s, like the WPF ring/accent spring).</summary>
    public void SetAccent(Color color, bool animate)
    {
        _followAccent = false;
        Paint(AccentPalette.From(color), animate);
    }

    /// <summary>Follows the live accent palette (no artwork to take a colour from).</summary>
    public void FollowAccent()
    {
        _followAccent = true;
        Paint(Theme.Palette, animate: false);
    }

    private void Paint(AccentPalette p, bool animate)
    {
        foreach (var (stop, color) in new[] { (_stopA, p.Base), (_stopB, p.Light) })
        {
            if (!animate || !Anim.Enabled) { stop.StopAnimation("Color"); stop.Color = color; continue; }
            var a = stop.Compositor.CreateColorKeyFrameAnimation();
            a.InsertKeyFrame(1, color);
            a.Duration = TimeSpan.FromMilliseconds(Services.ThemeService.AlbumFadeMs);
            stop.StartAnimation("Color", a);
        }
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_seekable) return;
        _dragging = true;
        CapturePointer(e.Pointer);
        ScrubStarted?.Invoke();
        Update(e);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        if (!_over) _hover.SetTarget(0, Motion.Hover);
        ScrubCommitted?.Invoke(_dragValue);
        e.Handled = true;
    }

    private void Update(PointerRoutedEventArgs e)
    {
        _dragValue = ActualWidth > 0 ? Math.Clamp(e.GetCurrentPoint(this).Position.X / ActualWidth, 0, 1) : 0;
        _p.InsertScalar("V", (float)_dragValue);
        Scrubbing?.Invoke(_dragValue);
    }
}
