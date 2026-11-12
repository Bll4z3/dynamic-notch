using System.Windows;
using System.Windows.Media;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// Draws the notch: flush with the top edge, concave "shoulders" blending into the screen edge,
/// and continuous (squircle-style) bottom corners, all from Theme.CornerRadius. Adds a soft layered
/// shadow, a 1px ~8% white inner highlight along the sides and bottom (the top edge stays pure
/// black so it melts into the bezel) and the drop-target glow. Hit-testing follows the body only,
/// so the transparent rest of the window never steals the mouse.
/// </summary>
public sealed class NotchShape : FrameworkElement
{
    private double _w = Theme.CollapsedWidth, _h = Theme.CollapsedHeight;
    private double _glow;
    private Geometry? _geometry, _outline;

    private static readonly Pen HighlightPen = MakeHighlightPen();
    private static readonly Brush ShadowBrush = Frozen(new SolidColorBrush(Color.FromArgb(Theme.ShadowAlpha, 0, 0, 0)));

    public Brush Fill { get; set; } = Theme.NotchBrush;

    public double ShapeWidth => _w;
    public double ShapeHeight => _h;

    public void SetSize(double width, double height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        if (Math.Abs(width - _w) < 0.01 && Math.Abs(height - _h) < 0.01) return;
        _w = width;
        _h = height;
        _geometry = _outline = null;
        InvalidateVisual();
    }

    /// <summary>Opacity of the drop-target outline (0..1).</summary>
    public double Glow
    {
        get => _glow;
        set { if (Math.Abs(value - _glow) < 0.002) return; _glow = value; InvalidateVisual(); }
    }

    /// <summary>The body rectangle (without shoulders) in local coordinates.</summary>
    public Rect BodyRect => new((ActualWidth - _w) / 2, 0, _w, _h);

    /// <summary>The body outline (without shoulders), shifted by <paramref name="dx"/>; used to clip content layers.</summary>
    public Geometry BodyClip(double dx) => Build(includeShoulders: false, dx, closed: true);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _geometry = _outline = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _geometry ??= Build(includeShoulders: true, 0, closed: true);
        _outline ??= Build(includeShoulders: false, 0, closed: false);
        var body = BodyRect;

        // Soft layered shadow: stacked, growing translucent rounded rects under the body.
        if (_h > Theme.MinimalHeight + 1)
        {
            double r = CornerFor(_w, _h);
            for (int i = Theme.ShadowLayers; i >= 1; i--)
            {
                double sp = i * Theme.ShadowSpread;
                var rect = new Rect(body.X - sp, 0, body.Width + 2 * sp, body.Height + sp * 1.5);
                dc.DrawRoundedRectangle(ShadowBrush, null, rect, r + sp, r + sp);
            }
        }

        dc.DrawGeometry(Fill, null, _geometry);

        // Inner highlight: a 2px stroke clipped to the body = 1px inside the edge.
        dc.PushClip(_geometry);
        dc.DrawGeometry(null, HighlightPen, _outline);
        if (_glow > 0.01)
        {
            dc.PushOpacity(Math.Clamp(_glow, 0, 1));
            dc.DrawGeometry(null, new Pen(Theme.DropHighlightBrush, 4), _outline);
            dc.Pop();
        }
        dc.Pop();
    }

    private static double CornerFor(double w, double h) => Math.Min(Theme.CornerRadius, Math.Min(h / 2, w / 2));

    private Geometry Build(bool includeShoulders, double dx, bool closed)
    {
        double R = Theme.CornerRadius;
        double cx = ActualWidth / 2 + dx;
        double x0 = cx - _w / 2, x1 = cx + _w / 2, h = _h;
        double s = Math.Min(R, h / 2);                          // shoulder (concave) radius
        double r = CornerFor(_w, h);                            // bottom corner radius
        // Continuous corner: the curve starts further from the corner than a circular arc would.
        double L = Math.Max(0, Math.Min(r * (1 + Theme.CornerSmoothing), Math.Min(h - (includeShoulders ? s : 0), _w / 2)));
        double d = L * 0.22;                                    // control points close to the corner = tight turn, long ease

        var g = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var c = g.Open())
        {
            if (includeShoulders)
            {
                c.BeginFigure(new Point(x0 - s, 0), true, closed);
                c.ArcTo(new Point(x0, s), new Size(s, s), 0, false, SweepDirection.Clockwise, true, true);
            }
            else
            {
                c.BeginFigure(new Point(x0, closed ? 0 : s), closed, closed);
            }
            c.LineTo(new Point(x0, h - L), true, true);
            c.BezierTo(new Point(x0, h - d), new Point(x0 + d, h), new Point(x0 + L, h), true, true);
            c.LineTo(new Point(x1 - L, h), true, true);
            c.BezierTo(new Point(x1 - d, h), new Point(x1, h - d), new Point(x1, h - L), true, true);
            if (includeShoulders)
            {
                c.LineTo(new Point(x1, s), true, true);
                c.ArcTo(new Point(x1 + s, 0), new Size(s, s), 0, false, SweepDirection.Clockwise, true, true);
            }
            else
            {
                c.LineTo(new Point(x1, closed ? 0 : s), true, true);
            }
        }
        g.Freeze();
        return g;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p)
    {
        _geometry ??= Build(includeShoulders: true, 0, closed: true);
        return _geometry.FillContains(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null;
    }

    private static Pen MakeHighlightPen()
    {
        // Fades in from the top so the bezel edge stays pure black.
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Theme.HighlightColor, 0.55),
                new GradientStop(Theme.HighlightColor, 1),
            },
        };
        brush.Freeze();
        var pen = new Pen(brush, 2);
        pen.Freeze();
        return pen;
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
