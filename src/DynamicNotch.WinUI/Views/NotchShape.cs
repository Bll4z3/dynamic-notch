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
    private ShapeVisual _dropGlowVisual = null!;

    public NotchShape(UIElement host, double hostWidth, double hostHeight)
    {
        var c = ElementCompositionPreview.GetElementVisual(host).Compositor;
        Compositor = c;
        float hw = (float)hostWidth;

        Props = c.CreatePropertySet();
        Props.InsertScalar("W", (float)Theme.CollapsedWidth);
        Props.InsertScalar("H", (float)Theme.CollapsedHeight);
        Props.InsertScalar("ShadowO", (float)Theme.ShadowOpacity);
        Props.InsertScalar("Full", (float)Theme.ExpandedHeight); // the window keeps this at the current expanded height
        _radius = new CompSpring(Props, "R", Motion.Notch, Theme.CornerRadius);

        // Body: a path with continuous ("squircle") bottom corners, its top pushed up above the screen
        // edge. It follows the W / H springs as a baked path animation (see Follow).
        _hostWidth = hostWidth;
        Body = c.CreatePathGeometry();
        Body.Path = BodyPath(Theme.CollapsedWidth, Theme.CollapsedHeight, Theme.CornerRadius);

        var size = new Vector2(hw, (float)hostHeight);
        var root = c.CreateContainerVisual();
        root.Size = size;

        // Soft shadow: a pre-rendered blurred rounded rectangle (SoftShadow, drawn once) stretched by a nine-grid
        // brush to the body's live size through expressions. It used to be a drop shadow masked by a live surface
        // of the body, which re-rendered that surface and re-blurred it on every frame of every morph: the single
        // most expensive thing the compositor did while the notch moved. Now a morph only resizes a sprite.
        var caster = _shadowCaster = SoftShadow.Create(c, Theme.CornerRadius, ShadowSoftness, 1, Theme.ShadowOffsetY);
        caster.Properties.TryGetScalar("Margin", out float margin);
        var size0 = Expr(FormattableString.Invariant($"Vector2(N.W + {2 * margin}, N.H + {RB} + {2 * margin})"));
        caster.StartAnimation("Size", size0);
        caster.StartAnimation("Offset", Expr(FormattableString.Invariant($"Vector3((HW - N.W) / 2 - {margin}, -{RB} - {margin} + {Theme.ShadowOffsetY}, 0)")));
        // No shadow for the 6px minimal notch (it would read as a smudge).
        caster.StartAnimation("Opacity", Expr(FormattableString.Invariant($"N.H > {Theme.MinimalHeight + 1} ? N.ShadowO : 0")));

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

        root.Children.InsertAtTop(caster);
        root.Children.InsertAtTop(bodyGroup);

        // Inner highlight: a thin white line just inside the sides and bottom, fading in from the top (the top
        // edge stays pure black). One unclipped stroke along a baked inset edge path, instead of a double-width
        // stroke clipped to the animated body (a per-frame path clip).
        Edge = c.CreatePathGeometry();
        Edge.Path = EdgePath(Theme.CollapsedWidth, Theme.CollapsedHeight, Theme.CornerRadius, EdgeInset);
        var edgeVisual = c.CreateShapeVisual();
        edgeVisual.Size = size;
        var edgeBrush = c.CreateLinearGradientBrush();
        edgeBrush.MappingMode = CompositionMappingMode.Absolute;
        edgeBrush.StartPoint = Vector2.Zero;
        edgeBrush.StartAnimation("EndPoint", Expr("Vector2(0, Max(1, N.H))"));
        edgeBrush.ColorStops.Add(c.CreateColorGradientStop(0, Color.FromArgb(0, 255, 255, 255)));
        _hl1 = c.CreateColorGradientStop(0.55f, Theme.HighlightColor);
        _hl2 = c.CreateColorGradientStop(1, Theme.HighlightColor);
        edgeBrush.ColorStops.Add(_hl1);
        edgeBrush.ColorStops.Add(_hl2);
        var edgeLine = c.CreateSpriteShape(Edge);
        edgeLine.StrokeBrush = edgeBrush;
        edgeLine.StrokeThickness = (float)(2 * EdgeInset); // covers exactly the band from the body edge inward
        edgeVisual.Shapes.Add(edgeLine);
        root.Children.InsertAtTop(edgeVisual);

        // Drop-target glow: a brighter stroke along the same edge, shown while files hover over the notch.
        // Hidden (not just transparent) when unused, so it costs nothing outside a drag.
        Props.InsertScalar("Glow", 0);
        var glow = _dropGlowVisual = c.CreateShapeVisual();
        glow.Size = size;
        var glowLine = c.CreateSpriteShape(Edge);
        glowLine.StrokeBrush = c.CreateColorBrush(Theme.DropHighlightColor);
        glowLine.StrokeThickness = (float)(2 * EdgeInset);
        glow.Shapes.Add(glowLine);
        glow.StartAnimation("Opacity", Expr("Clamp(N.Glow, 0, 1)"));
        glow.IsVisible = false;
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
        // The pre-rendered shadow takes the new corner (one small cached texture per radius).
        _shadowCaster.Brush = SoftShadow.Create(Compositor, r, ShadowSoftness, 1, Theme.ShadowOffsetY).Brush;
    }

    /// <summary>Border highlight colour and shadow strength (Settings ▸ Customisation ▸ Notch look).</summary>
    public void SetSurface(Windows.UI.Color highlight, double shadowOpacity)
    {
        _hl1.Color = _hl2.Color = highlight;
        Props.InsertScalar("ShadowO", (float)shadowOpacity);
    }

    /// <summary>Blur of the pre-rendered shadow (about the old drop shadow's spread).</summary>
    private const double ShadowSoftness = 12;
    private readonly SpriteVisual _shadowCaster = null!;
    private readonly CompositionColorGradientStop _hl1 = null!, _hl2 = null!;

    /// <summary>Show / hide the drop-target glow (visible from the start of a drag; hidden once it has faded out).</summary>
    public void SetDropGlow(bool on)
    {
        _dropGlowHide?.Stop();
        if (on) { _dropGlowVisual.IsVisible = true; return; }
        _dropGlowHide ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _dropGlowHide.IsRepeating = false;
        _dropGlowHide.Interval = TimeSpan.FromMilliseconds(600);
        _dropGlowHide.Tick += HideGlow;
        _dropGlowHide.Start();
        void HideGlow(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object o) { t.Tick -= HideGlow; t.Stop(); _dropGlowVisual.IsVisible = false; }
    }
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dropGlowHide;

    /// <summary>The highlight / rim / drop-glow band: the edge path sits this far inside the body outline and is stroked 2× as wide.</summary>
    private const double EdgeInset = 0.5; // a 1px band, as the original highlight

    /// <summary>The inset edge (sides + bottom) that the highlight, rim and drop glow are stroked along; follows the body.</summary>
    public CompositionPathGeometry Edge { get; } = null!;

    private bool _rebakeQueued;

    /// <summary>
    /// Width, height and radius springs often retarget together (one state change = two or three Changed
    /// events): the bake runs once, right after them, instead of once per spring.
    /// </summary>
    private void Rebake()
    {
        if (_rebakeQueued) return;
        var q = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (q == null) { RebakeNow(); return; }
        _rebakeQueued = true;
        q.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () => { _rebakeQueued = false; RebakeNow(); });
    }

    private void RebakeNow()
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
            Edge.StopAnimation("Path");
            double w = wm.ValueAt(now), h = hm.ValueAt(now), r = rm.ValueAt(now);
            Body.Path = BodyPath(w, h, r);
            Edge.Path = EdgePath(w, h, r, EdgeInset);
            return;
        }
        var body = Compositor.CreatePathKeyFrameAnimation();
        var edge = Compositor.CreatePathKeyFrameAnimation();
        var linear = Compositor.CreateLinearEasingFunction();
        int n = Math.Clamp((int)Math.Ceiling(duration * PathKeysPerSecond), 2, 400);
        for (int i = 0; i <= n; i++)
        {
            double t = now + duration * i / n;
            double w = wm.ValueAt(t), h = hm.ValueAt(t), r = rm.ValueAt(t);
            body.InsertKeyFrame((float)i / n, BodyPath(w, h, r), linear);
            edge.InsertKeyFrame((float)i / n, EdgePath(w, h, r, EdgeInset), linear);
        }
        body.Duration = edge.Duration = TimeSpan.FromSeconds(duration);
        Body.StartAnimation("Path", body);
        Edge.StartAnimation("Path", edge);
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

    /// <summary>
    /// The body outline pulled in by <paramref name="inset"/> on the sides and bottom, as an open figure (no top
    /// edge): the same continuous corners with the radius reduced by the inset, so a stroke along it sits
    /// evenly inside the body. Same command sequence every time, so outlines interpolate cleanly.
    /// </summary>
    private CompositionPath EdgePath(double w, double h, double radius, double inset)
    {
        w = Math.Max(0, w);
        h = Math.Max(0, h);
        double x0 = (_hostWidth - w) / 2 + inset, x1 = (_hostWidth + w) / 2 - inset, top = -Theme.MaxCornerRadius;
        double bottom = Math.Max(0, h - inset), iw = Math.Max(0, x1 - x0);
        double r = Math.Min(Math.Max(0, Math.Min(radius, Math.Min(h / 2, w / 2)) - inset), Math.Min(bottom / 2, iw / 2));
        double l = Math.Max(0, Math.Min(r * (1 + Theme.CornerSmoothing), Math.Min(bottom, iw / 2)));
        double d = l * 0.22;
        Vector2 P(double x, double y) => new((float)x, (float)y);
        using var b = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice());
        b.BeginFigure(P(x0, top), Microsoft.Graphics.Canvas.Geometry.CanvasFigureFill.DoesNotAffectFills);
        b.AddLine(P(x0, bottom - l));
        b.AddCubicBezier(P(x0, bottom - d), P(x0 + d, bottom), P(x0 + l, bottom));
        b.AddLine(P(x1 - l, bottom));
        b.AddCubicBezier(P(x1 - d, bottom), P(x1, bottom - d), P(x1, bottom - l));
        b.AddLine(P(x1, top));
        b.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
        return new CompositionPath(Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(b));
    }

    /// <summary>A clip that follows the animated body (for a visual laid out over the whole host).</summary>
    public CompositionGeometricClip CreateBodyClip() => Compositor.CreateGeometricClip(Body);
}
