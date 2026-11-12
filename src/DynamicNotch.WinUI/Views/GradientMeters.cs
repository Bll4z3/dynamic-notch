using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>Shared gradient helpers: a two-stop linear brush whose colours blend smoothly on change.</summary>
internal sealed class GradientPaint
{
    private readonly CompositionColorGradientStop _a, _b;
    private bool _hasColor;

    public GradientPaint(Compositor c, Color a, Color b, Vector2? start = null, Vector2? end = null)
    {
        Brush = c.CreateLinearGradientBrush();
        Brush.StartPoint = start ?? new Vector2(0, 0);
        Brush.EndPoint = end ?? new Vector2(1, 1);
        _a = c.CreateColorGradientStop(0, a);
        _b = c.CreateColorGradientStop(1, b);
        Brush.ColorStops.Add(_a);
        Brush.ColorStops.Add(_b);
        A = a; B = b;
    }

    public CompositionLinearGradientBrush Brush { get; }
    public Color A { get; private set; }
    public Color B { get; private set; }

    /// <summary>Blends both stops to new colours (~600 ms), or snaps on the first call / with animations off.</summary>
    public void Set(Color a, Color b, bool animate = true)
    {
        if (_hasColor && a == A && b == B) return;
        bool blend = animate && _hasColor && Anim.Enabled;
        _hasColor = true;
        A = a; B = b;
        foreach (var (stop, colour) in new[] { (_a, a), (_b, b) })
        {
            if (!blend) { stop.StopAnimation("Color"); stop.Color = colour; continue; }
            var anim = stop.Compositor.CreateColorKeyFrameAnimation();
            anim.InsertKeyFrame(1, colour);
            anim.Duration = TimeSpan.FromMilliseconds(600);
            stop.StartAnimation("Color", anim);
        }
    }
}

/// <summary>
/// A gradient progress ring with a soft glow behind it (usage meters). Fills clockwise from 12 o'clock;
/// the fill springs to new values, the gradient and glow blend between colours. All compositor.
/// </summary>
public sealed class GradientRing : Grid
{
    private readonly double _size, _thickness;
    private readonly Border _glowHost = new() { IsHitTestVisible = false }, _ringHost = new() { IsHitTestVisible = false };
    private GradientPaint? _paint;
    private CompositionColorGradientStop? _glowStop;
    private CompSpring? _fill, _glow;
    private double _value;
    private (Color a, Color b) _colors = (Theme.UsageCalmA, Theme.UsageCalmB);

    public GradientRing(double size, double thickness)
    {
        _size = size;
        _thickness = thickness;
        Width = Height = size;
        _glowHost.Width = _glowHost.Height = size;
        Children.Add(_glowHost);
        Children.Add(_ringHost);
        Loaded += (_, _) => Build();
    }

    /// <summary>The centre content (e.g. the percentage).</summary>
    public UIElement? Center
    {
        set { if (Children.Count > 2) Children.RemoveAt(2); if (value != null) Children.Add(value); }
    }

    private void Build()
    {
        if (_fill != null) return;
        var c = ElementCompositionPreview.GetElementVisual(this).Compositor;
        float size = (float)_size, th = (float)_thickness, r = (size - th) / 2;
        var centre = new Vector2(size / 2);

        // Glow: a radial gradient larger than the ring, centred on it (spills softly around the ring).
        float g = (float)Theme.UsageGlowSize;
        var radial = c.CreateRadialGradientBrush();
        _glowStop = c.CreateColorGradientStop(0, WithAlpha(_colors.b, 0xFF));
        radial.ColorStops.Add(_glowStop);
        radial.ColorStops.Add(c.CreateColorGradientStop(1, WithAlpha(_colors.b, 0)));
        var glow = c.CreateSpriteVisual();
        glow.Size = new Vector2(g);
        glow.Offset = new Vector3((size - g) / 2, (size - g) / 2, 0);
        glow.Brush = radial;
        var props = c.CreatePropertySet();
        _glow = new CompSpring(props, "g", Motion.RingFill, 0);
        glow.StartAnimation("Opacity", Anim.Expr(c, $"Clamp(p.g, 0, 1) * {Anim.F(Theme.UsageGlowOpacity)}", ("p", props)));
        ElementCompositionPreview.SetElementChildVisual(_glowHost, glow);

        var shapes = c.CreateShapeVisual();
        shapes.Size = new Vector2(size);
        shapes.CenterPoint = new Vector3(centre, 0);
        shapes.RotationAngleInDegrees = -90; // ellipse paths start at 3 o'clock; the fill starts at 12
        var trackGeo = c.CreateEllipseGeometry();
        trackGeo.Center = centre;
        trackGeo.Radius = new Vector2(r);
        var track = c.CreateSpriteShape(trackGeo);
        track.StrokeBrush = c.CreateColorBrush(Theme.TrackColor);
        track.StrokeThickness = th;
        shapes.Shapes.Add(track);

        var arcGeo = c.CreateEllipseGeometry();
        arcGeo.Center = centre;
        arcGeo.Radius = new Vector2(r);
        _paint = new GradientPaint(c, _colors.a, _colors.b);
        var arc = c.CreateSpriteShape(arcGeo);
        arc.StrokeBrush = _paint.Brush;
        arc.StrokeThickness = th;
        arc.StrokeStartCap = arc.StrokeEndCap = CompositionStrokeCap.Round;
        shapes.Shapes.Add(arc);
        _fill = new CompSpring(arcGeo, "TrimEnd", Motion.RingFill, 0, precision: 0.5);
        ElementCompositionPreview.SetElementChildVisual(_ringHost, shapes);
        Apply(animate: false);
        _fill.SetTarget(_value, Motion.RingFill); // springs in from empty the first time
        _glow.SetTarget(1, Motion.RingFill);
    }

    /// <summary>0..1 fill with its gradient colours.</summary>
    public void Set(double value, Color a, Color b)
    {
        _value = Math.Clamp(value, 0, 1);
        _colors = (a, b);
        if (_fill == null) return;
        _fill.SetTarget(_value, Motion.RingFill);
        Apply(animate: true);
    }

    private void Apply(bool animate)
    {
        _paint?.Set(_colors.a, _colors.b, animate);
        if (_glowStop == null) return;
        if (!animate || !Anim.Enabled) { _glowStop.Color = WithAlpha(_colors.b, 0xFF); return; }
        var anim = _glowStop.Compositor.CreateColorKeyFrameAnimation();
        anim.InsertKeyFrame(1, WithAlpha(_colors.b, 0xFF));
        anim.Duration = TimeSpan.FromMilliseconds(600);
        _glowStop.StartAnimation("Color", anim);
    }

    internal static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>
/// A thin rounded gradient bar (track + fill), optionally with a glowing dot riding the leading edge.
/// The fill springs (or, with <see cref="SetLinear"/>, glides at a constant rate on the compositor, for
/// a running timer that needs no per-second UI work).
/// </summary>
public sealed class GradientBar : Grid
{
    private readonly CompositionPropertySet _p;
    private readonly CompSpring _v, _dot;
    private readonly GradientPaint _paint;
    private readonly ShapeVisual _shapes;
    private readonly SpriteVisual? _lead;
    private readonly CompositionColorBrush _trackBrush;
    private double _value;

    public GradientBar(double thickness, bool leadingDot = false, Color? track = null)
    {
        Height = Math.Max(thickness, leadingDot ? Theme.TimerLeadDot + 8 : thickness);
        IsHitTestVisible = false;
        var c = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _p = c.CreatePropertySet();
        _p.InsertScalar("W", 0);
        _p.InsertScalar("Pulse", 1);
        _v = new CompSpring(_p, "V", Motion.RingFill, 0, precision: 0.5);
        _dot = new CompSpring(_p, "D", Motion.Reveal, 0);
        string t = Anim.F(thickness), mid = Anim.F(Height / 2);

        _shapes = c.CreateShapeVisual();
        var trackGeo = c.CreateRoundedRectangleGeometry();
        Bind(trackGeo, "Size", $"Vector2(S.W, {t})");
        trackGeo.Offset = new Vector2(0, (float)(Height / 2 - thickness / 2));
        trackGeo.CornerRadius = new Vector2((float)thickness / 2);
        _trackBrush = c.CreateColorBrush(track ?? Theme.TrackColor);
        var trackShape = c.CreateSpriteShape(trackGeo);
        trackShape.FillBrush = _trackBrush;
        _shapes.Shapes.Add(trackShape);

        var fillGeo = c.CreateRoundedRectangleGeometry();
        Bind(fillGeo, "Size", $"Vector2(S.V > 0.0005 ? Max({t}, S.W * Clamp(S.V, 0, 1)) : 0, {t})");
        fillGeo.Offset = trackGeo.Offset;
        fillGeo.CornerRadius = trackGeo.CornerRadius;
        _paint = new GradientPaint(c, Theme.FocusA, Theme.FocusB, new Vector2(0, 0.5f), new Vector2(1, 0.5f));
        var fillShape = c.CreateSpriteShape(fillGeo);
        fillShape.FillBrush = _paint.Brush;
        _shapes.Shapes.Add(fillShape);
        ElementCompositionPreview.SetElementChildVisual(this, _shapes);

        if (leadingDot)
        {
            // A soft halo plus a bright core at the fill's leading edge; the halo breathes while "Pulse" loops.
            var host = c.CreateContainerVisual();
            _lead = c.CreateSpriteVisual();
            float halo = (float)(Theme.TimerLeadDot * 2.4), core = (float)Theme.TimerLeadDot;
            var haloBrush = c.CreateRadialGradientBrush();
            HaloStop = c.CreateColorGradientStop(0, Theme.FocusB);
            haloBrush.ColorStops.Add(HaloStop);
            HaloEdge = c.CreateColorGradientStop(1, GradientRing.WithAlpha(Theme.FocusB, 0));
            haloBrush.ColorStops.Add(HaloEdge);
            _lead.Brush = haloBrush;
            _lead.Size = new Vector2(halo);
            _lead.CenterPoint = new Vector3(halo / 2, halo / 2, 0);
            var coreVisual = c.CreateShapeVisual();
            coreVisual.Size = new Vector2(core);
            var coreGeo = c.CreateEllipseGeometry();
            coreGeo.Center = new Vector2(core / 2);
            coreGeo.Radius = new Vector2(core / 2 - 1);
            var coreShape = c.CreateSpriteShape(coreGeo);
            coreShape.FillBrush = c.CreateColorBrush(Theme.PrimaryTextColor);
            coreVisual.Shapes.Add(coreShape);
            host.Children.InsertAtTop(_lead);
            host.Children.InsertAtTop(coreVisual);
            string x = "(S.W * Clamp(S.V, 0, 1))";
            var haloOffset = Anim.Expr(c, $"Vector3({x} - {Anim.F(halo / 2)}, {mid} - {Anim.F(halo / 2)}, 0)", ("S", _p));
            _lead.StartAnimation("Offset", haloOffset);
            coreVisual.StartAnimation("Offset", Anim.Expr(c, $"Vector3({x} - {Anim.F(core / 2)}, {mid} - {Anim.F(core / 2)}, 0)", ("S", _p)));
            _lead.StartAnimation("Opacity", Anim.Expr(c, "Clamp(S.D, 0, 1) * (0.45 + 0.4 * S.Pulse)", ("S", _p)));
            _lead.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.8 + 0.25 * S.Pulse, 0.8 + 0.25 * S.Pulse, 1)", ("S", _p)));
            coreVisual.StartAnimation("Opacity", Anim.Expr(c, "Clamp(S.D, 0, 1)", ("S", _p)));
            _shapes.Children.InsertAtTop(host);
        }

        SizeChanged += (_, e) =>
        {
            _shapes.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            _p.InsertScalar("W", (float)e.NewSize.Width);
        };
    }

    private CompositionColorGradientStop? HaloStop { get; }
    private CompositionColorGradientStop? HaloEdge { get; }

    public double Value => _value;

    public void SetColors(Color a, Color b, bool animate = true)
    {
        _paint.Set(a, b, animate);
        if (HaloStop != null) HaloStop.Color = b;
        if (HaloEdge != null) HaloEdge.Color = GradientRing.WithAlpha(b, 0);
    }

    public void SetTrack(Color c) => _trackBrush.Color = c;

    /// <summary>Spring to a value (0..1), starting from wherever the bar is now (also mid-glide).</summary>
    public void SetValue(double value, bool animate = true)
    {
        _value = Math.Clamp(value, 0, 1);
        if (_linear is { } l)
        {
            _linear = null;
            double k = l.dur <= 0 ? 1 : Math.Clamp((SpringModel.Now - l.start) / l.dur, 0, 1);
            _v.Snap(l.from + (l.to - l.from) * k); // the spring model didn't follow the glide: resync it
        }
        if (animate) _v.SetTarget(_value, Motion.RingFill); else _v.Snap(_value);
    }

    private (double start, double dur, double from, double to)? _linear;

    /// <summary>
    /// Glide from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/> at a constant
    /// rate, entirely on the compositor (a running timer). The UI re-syncs it on pause / phase change.
    /// </summary>
    public void SetLinear(double from, double to, TimeSpan duration)
    {
        _value = Math.Clamp(to, 0, 1);
        _linear = null;
        if (duration <= TimeSpan.Zero || !Anim.Enabled) { _v.Snap(to); return; }
        _v.Snap(from);
        _linear = (SpringModel.Now, duration.TotalSeconds, from, to);
        var a = _p.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, (float)from);
        a.InsertKeyFrame(1, (float)to, _p.Compositor.CreateLinearEasingFunction());
        a.Duration = duration;
        _p.StartAnimation("V", a);
    }

    /// <summary>Shows / hides the leading dot (springs) and its breathing glow.</summary>
    public void SetDot(bool shown, bool pulsing)
    {
        _dot.SetTarget(shown ? 1 : 0, Motion.Reveal);
        if (_lead == null) return;
        if (!pulsing || !Anim.Enabled) { _p.StopAnimation("Pulse"); _p.InsertScalar("Pulse", 1); return; }
        var a = _p.Compositor.CreateScalarKeyFrameAnimation();
        var linear = _p.Compositor.CreateLinearEasingFunction();
        const int keys = 60;
        for (int i = 0; i <= keys; i++)
        {
            double t = (double)i / keys;
            a.InsertKeyFrame((float)t, (float)(0.5 + 0.5 * Math.Cos(2 * Math.PI * t)), linear);
        }
        a.Duration = TimeSpan.FromSeconds(Motion.TimerPulsePeriod);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        _p.StartAnimation("Pulse", a);
    }

    private void Bind(CompositionObject target, string property, string expression) =>
        target.StartAnimation(property, Anim.Expr(_p.Compositor, expression, ("S", _p)));
}
