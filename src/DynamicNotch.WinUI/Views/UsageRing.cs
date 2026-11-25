using System.Globalization;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// A Claude usage gauge that sits next to the battery ring (closed notch and/or the open notch's header), drawn exactly
/// like <see cref="BatteryRing"/>: same diameter, stroke, round caps, track and number font. The arc fills clockwise from
/// 12 o'clock with the window's utilisation (a compositor spring), and its colour follows the usage page's bands: the
/// window's own calm colour (5-hour blue, weekly purple), amber from 60%, red from 85%, blended as it changes.
/// The centre shows a small label ("5h" / "W") so the two can be told apart; hovering (or <see cref="ShowNumber"/>)
/// swaps it for the % like the battery ring. The tooltip has the exact % and the reset time.
/// <para>
/// The ring element itself carries the shared-element flight between the closed and open notch, so showing and hiding
/// (a pop of scale and opacity) runs on an inner element, and the ring collapses from layout once it has popped out.
/// </para>
/// </summary>
public sealed class UsageRing : Grid
{
    public enum Kind { FiveHour, Weekly }

    private readonly Kind _kind;
    private readonly double _size, _thickness;
    private readonly Grid _body = new();
    private readonly Border _ringHost = new();
    private readonly TextBlock _label = new(), _number = new();
    private ShapeVisual? _shapes;
    private CompositionColorBrush? _arcBrush;
    private CompSpring? _fill, _numberReveal, _pop;
    private Color _color;
    private bool _hadValue, _showNumber, _hover, _shown = true;
    private double _percent = -1;
    private UsageWindow? _window;
    private string? _unavailable;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _collapse;

    public UsageRing(Kind kind, double size, double thickness)
    {
        _kind = kind;
        _size = size;
        _thickness = thickness;
        _color = CalmColor;
        Width = Height = size;
        Background = Theme.TransparentBrush; // the whole disc is hoverable

        foreach (var t in new[] { _label, _number })
        {
            t.FontFamily = Theme.FontBold;
            t.FontWeight = FontWeights.Bold;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.TextAlignment = TextAlignment.Center;
            t.IsHitTestVisible = false;
            t.TextLineBounds = TextLineBounds.Tight;
        }
        _label.Text = kind == Kind.FiveHour ? "5h" : "W";
        _label.FontSize = Theme.RingNumberFontSize * (size / Theme.NotchRingSize) * (kind == Kind.FiveHour ? 0.84 : 0.92);
        _label.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(CalmColor);
        _number.Foreground = Theme.PrimaryText;

        _body.Children.Add(_ringHost);
        _body.Children.Add(_label);
        _body.Children.Add(_number);
        Children.Add(_body);

        _collapse = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _collapse.IsRepeating = false;
        _collapse.Interval = TimeSpan.FromMilliseconds(260);
        _collapse.Tick += (_, _) => { _collapse.Stop(); if (!_shown) Visibility = Visibility.Collapsed; };

        Loaded += (_, _) => Build();
        _number.SizeChanged += (_, e) => Anim.VisualOf(_number).CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        _label.SizeChanged += (_, e) => Anim.VisualOf(_label).CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        PointerEntered += (_, _) => { _hover = true; ToolTipService.SetToolTip(this, TooltipText()); UpdateReveal(); };
        PointerExited += (_, _) => { _hover = false; UpdateReveal(); };
    }

    /// <summary>The 5-hour window is blue, the weekly one purple (the two ends of the usage page's calm gradient).</summary>
    private Color CalmColor => _kind == Kind.FiveHour ? Theme.UsageCalmA : Theme.UsageCalmB;

    private Color TargetColor => _percent >= Theme.UsageHotPercent ? Theme.UsageHotB
        : _percent >= Theme.UsageWarnPercent ? Theme.UsageWarnA : CalmColor;

    /// <summary>Shows the % instead of the label (the notch is hovered).</summary>
    public bool ShowNumber { get => _showNumber; set { if (_showNumber == value) return; _showNumber = value; UpdateReveal(); } }

    private void UpdateReveal() => _numberReveal?.SetTarget((_showNumber || _hover) && _percent >= 0 ? 1 : 0, Motion.Reveal);

    /// <summary>New data from the usage page's view model (null window: no data yet or unavailable).</summary>
    public void SetData(UsageWindow? window, string? unavailable)
    {
        _window = window;
        _unavailable = unavailable;
        double p = window?.Percent ?? -1;
        if (p == _percent && _shapes != null) return;
        _percent = p;
        Apply();
    }

    /// <summary>Pops in / out (the ring leaves layout once it has popped out).</summary>
    public void SetShown(bool shown, bool animate)
    {
        _shown = shown;
        if (shown)
        {
            _collapse.Stop();
            bool wasHidden = Visibility != Visibility.Visible;
            Visibility = Visibility.Visible;
            if (_pop != null)
            {
                if (!animate) _pop.Snap(1);
                else { if (wasHidden) _pop.Snap(0); _pop.SetTarget(1, Motion.Pop); }
            }
        }
        else if (Visibility == Visibility.Visible)
        {
            if (_pop == null || !animate) { _pop?.Snap(0); Visibility = Visibility.Collapsed; return; }
            _pop.SetTarget(0, Motion.FadeOut);
            _collapse.Stop();
            _collapse.Start();
        }
    }

    private void Build()
    {
        if (_shapes != null) return;
        var c = ElementCompositionPreview.GetElementVisual(this).Compositor;
        float size = (float)_size, th = (float)_thickness, r = (size - th) / 2;
        var centre = new Vector2(size / 2);

        _shapes = c.CreateShapeVisual();
        _shapes.Size = new Vector2(size);
        _shapes.CenterPoint = new Vector3(centre, 0);
        _shapes.RotationAngleInDegrees = -90; // ellipse paths start at 3 o'clock; the fill starts at 12

        var trackGeo = c.CreateEllipseGeometry();
        trackGeo.Center = centre;
        trackGeo.Radius = new Vector2(r);
        var track = c.CreateSpriteShape(trackGeo);
        track.StrokeBrush = c.CreateColorBrush(Theme.TrackColor);
        track.StrokeThickness = th;
        _shapes.Shapes.Add(track);

        var arcGeo = c.CreateEllipseGeometry();
        arcGeo.Center = centre;
        arcGeo.Radius = new Vector2(r);
        _arcBrush = c.CreateColorBrush(_color);
        var arc = c.CreateSpriteShape(arcGeo);
        arc.StrokeBrush = _arcBrush;
        arc.StrokeThickness = th;
        arc.StrokeStartCap = arc.StrokeEndCap = CompositionStrokeCap.Round;
        _shapes.Shapes.Add(arc);
        _fill = new CompSpring(arcGeo, "TrimEnd", Motion.RingFill, 0, precision: 0.5);
        ElementCompositionPreview.SetElementChildVisual(_ringHost, _shapes);

        // Number in, label out (one spring, like the battery ring).
        var props = c.CreatePropertySet();
        _numberReveal = new CompSpring(props, "n", Motion.Reveal, 0);
        var nv = Anim.VisualOf(_number);
        nv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.n, 0, 1)", ("p", props)));
        nv.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.6 + 0.4 * Clamp(p.n, 0, 1.2), 0.6 + 0.4 * Clamp(p.n, 0, 1.2), 1)", ("p", props)));
        var lv = Anim.VisualOf(_label);
        lv.StartAnimation("Opacity", Anim.Expr(c, "1 - Clamp(p.n, 0, 1)", ("p", props)));
        lv.StartAnimation("Scale", Anim.Expr(c, "Vector3(1 - 0.3 * Clamp(p.n, 0, 1), 1 - 0.3 * Clamp(p.n, 0, 1), 1)", ("p", props)));

        // Showing / hiding: the body pops (scale + opacity) about its centre.
        var bv = Anim.VisualOf(_body);
        bv.CenterPoint = new Vector3(size / 2, size / 2, 0);
        var (pop, ps) = Motions.Value(c, Motion.Pop, _shown ? 1 : 0);
        _pop = pop;
        bv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", ps)));
        bv.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.4 + 0.6 * p.v, 0.4 + 0.6 * p.v, 1)", ("p", ps)));
        if (_shown && Anim.Enabled && Visibility == Visibility.Visible) { _pop.Snap(0); _pop.SetTarget(1, Motion.Pop); }

        Apply();
        UpdateReveal();
    }

    private void Apply()
    {
        _number.Text = _percent >= 0 ? Math.Round(Math.Clamp(_percent, 0, 100)).ToString(CultureInfo.CurrentCulture) : "";
        double fs = Theme.RingNumberFontSize * (_size / Theme.NotchRingSize);
        if (_number.Text.Length >= 3) fs *= 0.82;
        _number.FontSize = fs;
        if (_shapes == null || _arcBrush == null || _fill == null) return;
        _fill.SetTarget(_percent >= 0 ? Math.Clamp(_percent, 0, 100) / 100.0 : 0, Motion.RingFill);
        var target = _percent >= 0 ? TargetColor : CalmColor;
        if (!_hadValue || !Anim.Enabled)
        {
            _hadValue = _percent >= 0;
            _arcBrush.StopAnimation("Color");
            _arcBrush.Color = target;
        }
        else if (target != _color)
        {
            var a = _arcBrush.Compositor.CreateColorKeyFrameAnimation();
            a.InsertKeyFrame(1, target);
            a.Duration = TimeSpan.FromMilliseconds(Motion.RingColorMs);
            _arcBrush.StartAnimation("Color", a);
        }
        _color = target;
        // The label takes the arc's colour, softened, so the two rings read apart even at a glance.
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)_label.Foreground).Color = Blend(target, Theme.PrimaryTextColor, 0.25);
        UpdateReveal();
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private string TooltipText()
    {
        string name = _kind == Kind.FiveHour ? "5-hour session" : "Weekly";
        if (_window is not { } w) return $"Claude usage · {name}\n{_unavailable ?? "Loading…"}";
        var now = DateTimeOffset.Now;
        string clock = UsagePage.ClockText(w.ResetsAt, now);
        string reset = UsagePage.Countdown(w.ResetsAt, now) + (string.IsNullOrEmpty(clock) ? "" : $" ({clock})");
        return $"Claude usage · {name}: {w.Percent.ToString("0.#", CultureInfo.CurrentCulture)}%\n{reset}";
    }
}
