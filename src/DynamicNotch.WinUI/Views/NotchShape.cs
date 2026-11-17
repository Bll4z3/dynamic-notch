using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// The notch, drawn entirely by the compositor: flush with the top edge, concave "shoulders" blending
/// into the screen edge, continuous ("squircle") bottom corners from Theme.CornerRadius, a soft drop shadow, and a 1px
/// ~8% white inner highlight along the sides and bottom (the top edge stays pure black).
/// Its size comes from two scalars on <see cref="Props"/> ("W", "H", in DIPs) that the window animates
/// with compositor springs; every piece of geometry follows them through expressions, so a morph costs
/// the app nothing per frame. <see cref="Body"/> is shared with the content clip, so content can never
/// overflow the animated shape.
/// </summary>
public sealed class NotchShape
{
    /// <summary>Shoulders are built at the largest radius and scaled down to the live one.</summary>
    private const float RB = (float)Theme.MaxCornerRadius;
    private readonly CompSpring _radius;
    private readonly CompositionColorGradientStop _hl1, _hl2;

    public NotchShape(UIElement host, double hostWidth, double hostHeight)
    {
        var c = ElementCompositionPreview.GetElementVisual(host).Compositor;
        Compositor = c;
        float hw = (float)hostWidth;

        Props = c.CreatePropertySet();
        Props.InsertScalar("W", (float)Theme.CollapsedWidth);
        Props.InsertScalar("H", (float)Theme.CollapsedHeight);
        Props.InsertScalar("ShadowO", (float)Theme.ShadowOpacity);
        _radius = new CompSpring(Props, "R", Motion.Notch, Theme.CornerRadius);

        // Body: a path with continuous ("squircle") bottom corners, its top pushed up above the screen
        // edge. It follows the W / H springs as a baked path animation (see Follow).
        _hostWidth = hostWidth;
        Body = c.CreatePathGeometry();
        Body.Path = BodyPath(Theme.CollapsedWidth, Theme.CollapsedHeight, Theme.CornerRadius);

        var size = new Vector2(hw, (float)hostHeight);
        var root = c.CreateContainerVisual();
        root.Size = size;

        // Soft shadow: cast by a sprite whose mask is a live surface of the body + shoulders.
        // (A LayerVisual shadow renders unscaled inside XAML, so it isn't used.)
        var shadow = c.CreateDropShadow();
        shadow.BlurRadius = (float)Theme.ShadowBlur;
        shadow.Offset = new Vector3(0, (float)Theme.ShadowOffsetY, 0);
        shadow.Color = Color.FromArgb(255, 0, 0, 0);
        // No shadow for the 6px minimal notch (it would read as a smudge).
        shadow.StartAnimation("Opacity", Expr(FormattableString.Invariant($"N.H > {Theme.MinimalHeight + 1} ? N.ShadowO : 0")));

        var fill = c.CreateShapeVisual();
        fill.Size = size;
        var body = c.CreateSpriteShape(Body);
        body.FillBrush = c.CreateColorBrush(Theme.NotchColor);
        fill.Shapes.Add(body);
        var bodyGroup = c.CreateContainerVisual();
        bodyGroup.Size = size;
        bodyGroup.Children.InsertAtTop(fill);
        bodyGroup.Children.InsertAtTop(Shoulder(left: true));
        bodyGroup.Children.InsertAtTop(Shoulder(left: false));

        var surface = c.CreateVisualSurface();
        surface.SourceVisual = bodyGroup;
        surface.SourceSize = size;
        shadow.Mask = c.CreateSurfaceBrush(surface);
        var caster = c.CreateSpriteVisual();
        caster.Size = size;
        caster.Shadow = shadow;
        root.Children.InsertAtTop(caster);
        root.Children.InsertAtTop(bodyGroup);

        // Inner highlight: a 2px stroke clipped to the body = 1px inside the edge, fading in from the top.
        var highlight = c.CreateShapeVisual();
        highlight.Size = size;
        var gradient = c.CreateLinearGradientBrush();
        gradient.MappingMode = CompositionMappingMode.Absolute;
        gradient.ColorStops.Add(c.CreateColorGradientStop(0, Color.FromArgb(0, 255, 255, 255)));
        _hl1 = c.CreateColorGradientStop(0.55f, Theme.HighlightColor);
        gradient.ColorStops.Add(_hl1);
        _hl2 = c.CreateColorGradientStop(1, Theme.HighlightColor);
        gradient.ColorStops.Add(_hl2);
        gradient.StartPoint = Vector2.Zero;
        gradient.StartAnimation("EndPoint", Expr("Vector2(0, Max(1, N.H))"));
        var outline = c.CreateSpriteShape(Body);
        outline.StrokeBrush = gradient;
        outline.StrokeThickness = 2;
        highlight.Shapes.Add(outline);
        highlight.Clip = c.CreateGeometricClip(Body);
        root.Children.InsertAtTop(highlight);

        // Drop-target glow: a brighter 4px stroke clipped to the body (2px inside), shown while files hover.
        Props.InsertScalar("Glow", 0);
        var glow = c.CreateShapeVisual();
        glow.Size = size;
        var glowLine = c.CreateSpriteShape(Body);
        glowLine.StrokeBrush = c.CreateColorBrush(Theme.DropHighlightColor);
        glowLine.StrokeThickness = 4;
        glow.Shapes.Add(glowLine);
        glow.Clip = c.CreateGeometricClip(Body);
        glow.StartAnimation("Opacity", Expr("Clamp(N.Glow, 0, 1)"));
        root.Children.InsertAtTop(glow);

        ElementCompositionPreview.SetElementChildVisual(host, root);

        ExpressionAnimation Expr(string e)
        {
            var a = c.CreateExpressionAnimation(e);
            a.SetReferenceParameter("N", Props);
            a.SetScalarParameter("RB", RB);
            a.SetScalarParameter("HW", hw);
            return a;
        }

        // A concave fillet of radius s = min(R, H/2) outside each top corner: the part of an s×s box
        // outside a circle centred on the box's outer-bottom corner. Drawn as a thick ring stroke clipped
        // to the box, built at s = R and scaled. It overlaps the body by 2px so no seam shows.
        ShapeVisual Shoulder(bool left)
        {
            var v = c.CreateShapeVisual();
            v.Size = new Vector2(RB + 2, RB);
            v.Clip = c.CreateInsetClip();
            var ring = c.CreateEllipseGeometry();
            ring.Center = new Vector2(left ? 0 : RB + 2, RB);
            ring.Radius = new Vector2(RB * 1.25f);
            var s = c.CreateSpriteShape(ring);
            s.StrokeBrush = c.CreateColorBrush(Theme.NotchColor);
            s.StrokeThickness = RB * 0.5f; // covers radii R..1.5R, past the box's far corner (√2·R)
            v.Shapes.Add(s);
            v.StartAnimation("Scale", Expr("Vector3(Min(N.R, N.H / 2) / RB, Min(N.R, N.H / 2) / RB, 1)"));
            v.StartAnimation("Offset", Expr(left
                ? "Vector3((HW - N.W) / 2 - Min(N.R, N.H / 2), 0, 0)"
                : "Vector3((HW + N.W) / 2 - 2 * Min(N.R, N.H / 2) / RB, 0, 0)"));
            return v;
        }
    }

    public Compositor Compositor { get; }

    /// <summary>"W" and "H": the notch body size in DIPs (animated by the window's springs).</summary>
    public CompositionPropertySet Props { get; }

    /// <summary>The animated body outline, in host coordinates (top edge at y = 0).</summary>
    public CompositionPathGeometry Body { get; }

    // ---- Continuous corners ---------------------------------------------------------------------------
    private readonly double _hostWidth;
    private CompSpring? _w, _h;
    private const double PathKeysPerSecond = 60;

    /// <summary>
    /// Keeps the body path in step with the window's width / height springs. A path can't be computed
    /// by an expression, so on every retarget the outline is sampled along both springs' exact
    /// trajectories into a path keyframe animation (linear between keys, 60 a second), which the
    /// compositor plays alongside the scalar animations. Costs nothing per frame.
    /// </summary>
    public void Follow(CompSpring width, CompSpring height)
    {
        _w = width;
        _h = height;
        width.Changed += Rebake;
        height.Changed += Rebake;
        _radius.Changed += Rebake;
        Rebake();
    }

    /// <summary>Springs the corners (body and shoulders) to a new radius.</summary>
    public void SetRadius(double r, bool animate)
    {
        if (animate) _radius.SetTarget(r, Motion.Notch); else _radius.Snap(r);
        if (!animate) Rebake();
    }

    /// <summary>Border highlight colour and shadow strength (Settings ▸ Customisation ▸ Notch look).</summary>
    public void SetSurface(Windows.UI.Color highlight, double shadowOpacity)
    {
        _hl1.Color = _hl2.Color = highlight;
        Props.InsertScalar("ShadowO", (float)shadowOpacity);
    }

    private void Rebake()
    {
        if (_w == null || _h == null) return;
        var wm = _w.Model;
        var hm = _h.Model;
        var rm = _radius.Model;
        double now = SpringModel.Now;
        double duration = Math.Max(Math.Max(wm.RemainingDuration, hm.RemainingDuration), rm.RemainingDuration);
        if (!Anim.Enabled || duration <= 0.001)
        {
            Body.StopAnimation("Path");
            Body.Path = BodyPath(wm.ValueAt(now), hm.ValueAt(now), rm.ValueAt(now));
            return;
        }
        var a = Compositor.CreatePathKeyFrameAnimation();
        var linear = Compositor.CreateLinearEasingFunction();
        int n = Math.Clamp((int)Math.Ceiling(duration * PathKeysPerSecond), 2, 400);
        for (int i = 0; i <= n; i++)
        {
            double t = now + duration * i / n;
            a.InsertKeyFrame((float)i / n, BodyPath(wm.ValueAt(t), hm.ValueAt(t), rm.ValueAt(t)), linear);
        }
        a.Duration = TimeSpan.FromSeconds(duration);
        Body.StartAnimation("Path", a);
    }

    /// <summary>
    /// The body outline for a W × H notch (same curve as the WPF app): straight sides from above the
    /// screen edge down to the corners, then continuous corners. Each corner's curve starts
    /// (1 + <see cref="Theme.CornerSmoothing"/>) × r from the corner and its control points sit close
    /// to the corner (0.22 of that length), giving a tight turn with a long ease instead of a circular arc.
    /// The command sequence never changes, so any two outlines interpolate cleanly.
    /// </summary>
    private CompositionPath BodyPath(double w, double h, double radius)
    {
        w = Math.Max(0, w);
        h = Math.Max(0, h);
        double x0 = (_hostWidth - w) / 2, x1 = x0 + w, top = -Theme.MaxCornerRadius;
        double r = Math.Min(Math.Max(0, radius), Math.Min(h / 2, w / 2));
        double l = Math.Max(0, Math.Min(r * (1 + Theme.CornerSmoothing), Math.Min(h, w / 2)));
        double d = l * 0.22;
        Vector2 P(double x, double y) => new((float)x, (float)y);
        using var b = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice());
        b.BeginFigure(P(x0, top));
        b.AddLine(P(x0, h - l));
        b.AddCubicBezier(P(x0, h - d), P(x0 + d, h), P(x0 + l, h));
        b.AddLine(P(x1 - l, h));
        b.AddCubicBezier(P(x1 - d, h), P(x1, h - d), P(x1, h - l));
        b.AddLine(P(x1, top));
        b.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Closed);
        return new CompositionPath(Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(b));
    }

    /// <summary>A clip that follows the animated body (for a visual laid out over the whole host).</summary>
    public CompositionGeometricClip CreateBodyClip() => Compositor.CreateGeometricClip(Body);
}
