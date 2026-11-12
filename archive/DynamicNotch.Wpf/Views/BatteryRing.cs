using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DynamicNotch.Animation;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// Thin donut battery gauge used for the notch battery AND each connected device.
/// - Fills clockwise from 12 o'clock in proportion to <see cref="Percent"/>.
/// - Green / amber / red by level (thresholds in Theme), colour changes blend with a spring.
/// - Centre shows <see cref="Glyph"/> (device icon) or a bolt while charging; when
///   <see cref="ShowNumber"/> is true a bold % fades and scales in instead.
/// - Percent &lt; 0 means "no battery data": only the glyph is drawn, no ring.
/// </summary>
public sealed class BatteryRing : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(BatteryRing), new PropertyMetadata(-1.0, (d, _) => ((BatteryRing)d).OnPercentChanged()));

    public static readonly DependencyProperty IsChargingProperty = DependencyProperty.Register(
        nameof(IsCharging), typeof(bool), typeof(BatteryRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowNumberProperty = DependencyProperty.Register(
        nameof(ShowNumber), typeof(bool), typeof(BatteryRing), new PropertyMetadata(false, (d, e) =>
            ((BatteryRing)d)._number.SetTarget((bool)e.NewValue ? 1 : 0, Motion.Reveal)));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(BatteryRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingSizeProperty = DependencyProperty.Register(
        nameof(RingSize), typeof(double), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Theme.NotchRingSize, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Theme.NotchRingThickness, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>When true, hovering the ring itself toggles <see cref="ShowNumber"/>.</summary>
    public static readonly DependencyProperty NumberOnHoverProperty = DependencyProperty.Register(
        nameof(NumberOnHover), typeof(bool), typeof(BatteryRing), new PropertyMetadata(false));

    private readonly Spring _fill = new(Motion.RingFill, 0) { Precision = 0.5 };
    private readonly Spring _number = new(Motion.Reveal, 0);
    private readonly Spring _colorBlend = new(Motion.RingFill, 1);
    private Color _fromColor = Theme.BatteryGreen, _toColor = Theme.BatteryGreen;
    private bool _hadValue;

    public BatteryRing()
    {
        _fill.ValueChanged += _ => InvalidateVisual();
        _number.ValueChanged += _ => InvalidateVisual();
        _colorBlend.ValueChanged += _ => InvalidateVisual();
    }

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public bool IsCharging { get => (bool)GetValue(IsChargingProperty); set => SetValue(IsChargingProperty, value); }
    public bool ShowNumber { get => (bool)GetValue(ShowNumberProperty); set => SetValue(ShowNumberProperty, value); }
    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double RingSize { get => (double)GetValue(RingSizeProperty); set => SetValue(RingSizeProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool NumberOnHover { get => (bool)GetValue(NumberOnHoverProperty); set => SetValue(NumberOnHoverProperty, value); }

    private bool HasValue => Percent >= 0;

    private void OnPercentChanged()
    {
        if (!HasValue) { InvalidateVisual(); return; }
        _fill.SetTarget(Math.Clamp(Percent, 0, 100) / 100.0, Motion.RingFill);

        var target = Theme.BatteryColor(Percent);
        if (!_hadValue)
        {
            // First reading: start in the right colour (the fill still springs in from empty).
            _hadValue = true;
            _fromColor = _toColor = target;
            _colorBlend.Snap(1);
        }
        else if (target != _toColor)
        {
            _fromColor = CurrentColor();
            _toColor = target;
            _colorBlend.Snap(0);
            _colorBlend.SetTarget(1, Motion.RingFill);
        }
        InvalidateVisual();
    }

    private Color CurrentColor()
    {
        double t = Math.Clamp(_colorBlend.Value, 0, 1);
        byte L(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromRgb(L(_fromColor.R, _toColor.R), L(_fromColor.G, _toColor.G), L(_fromColor.B, _toColor.B));
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e) { if (NumberOnHover) ShowNumber = true; }
    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e) { if (NumberOnHover) ShowNumber = false; }

    protected override Size MeasureOverride(Size availableSize) => new(RingSize, RingSize);

    protected override void OnRender(DrawingContext dc)
    {
        double size = RingSize, th = Thickness;
        var c = new Point(size / 2, size / 2);
        double r = (size - th) / 2;

        // Transparent disc so the whole ring is hoverable.
        dc.DrawEllipse(Brushes.Transparent, null, c, size / 2, size / 2);

        double n = HasValue ? Math.Clamp(_number.Value, 0, 1.2) : 0;

        if (HasValue)
        {
            var track = new Pen(Theme.TrackBrush, th);
            dc.DrawEllipse(null, track, c, r, r);

            double f = Math.Clamp(_fill.Value, 0, 1);
            var arcPen = new Pen(new SolidColorBrush(CurrentColor()), th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (f >= 0.999) dc.DrawEllipse(null, arcPen, c, r, r);
            else if (f > 0.002) dc.DrawGeometry(null, arcPen, Arc(c, r, f));
        }

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Centre glyph: device icon, or a bolt while charging. Fades out as the number comes in.
        string? glyph = !string.IsNullOrEmpty(Glyph) ? Glyph : IsCharging && HasValue ? Icons.Bolt : null;
        if (glyph != null && n < 1)
        {
            double gSize = !string.IsNullOrEmpty(Glyph) ? size * 0.5 : size * 0.52;
            var brush = IsCharging && string.IsNullOrEmpty(Glyph) ? new SolidColorBrush(CurrentColor()) : Theme.PrimaryText;
            dc.PushOpacity(1 - Math.Clamp(n, 0, 1));
            Icons.Draw(dc, glyph, c, gSize, brush);
            dc.Pop();
        }

        // Percentage: bold, tabular, springs in from 60% scale.
        if (n > 0.01)
        {
            string text = Math.Round(Math.Clamp(Percent, 0, 100)).ToString(CultureInfo.CurrentCulture);
            double fs = Theme.RingNumberFontSize * (size / Theme.NotchRingSize);
            if (text.Length >= 3) fs *= 0.82;
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), fs, Theme.PrimaryText, dip);
            double s = 0.6 + 0.4 * n;
            dc.PushOpacity(Math.Clamp(n, 0, 1));
            dc.PushTransform(new ScaleTransform(s, s, c.X, c.Y));
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
            dc.Pop();
            dc.Pop();
        }
    }

    private static Geometry Arc(Point c, double r, double fraction)
    {
        double angle = fraction * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
