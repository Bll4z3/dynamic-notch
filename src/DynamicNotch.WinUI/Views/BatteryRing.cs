using System.Globalization;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// Thin donut battery gauge used for the notch battery AND each connected device.
/// - Fills clockwise from 12 o'clock in proportion to <see cref="Percent"/> (a compositor spring on the
///   arc's trim), green / amber / red by level with a blended colour change.
/// - Centre shows <see cref="Glyph"/> (device icon) or a bolt while charging; when <see cref="ShowNumber"/>
///   is true a bold % fades and scales in instead.
/// - Percent &lt; 0 means "no battery data": only the glyph is drawn, no ring.
/// </summary>
public sealed class BatteryRing : Grid
{
    private double _size = Theme.NotchRingSize, _thickness = Theme.NotchRingThickness;
    private readonly Border _ringHost = new();
    private readonly VectorIcon _glyph = new();
    private readonly TextBlock _number = new();
    private readonly SolidColorBrush _boltBrush = new(Theme.BatteryGreen);
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _halo = new();
    private bool _haloOn;

    private ShapeVisual? _shapes;
    private CompositionColorBrush? _arcBrush;
    private CompSpring? _fill, _numberReveal;
    private Color _color = Theme.BatteryGreen;
    private bool _hadValue;

    private double _percent = -1;
    private bool _charging, _showNumber;
    private string? _deviceGlyph;

    public BatteryRing()
    {
        Width = Height = _size;
        Background = Theme.TransparentBrush; // the whole disc is hoverable

        _glyph.HorizontalAlignment = HorizontalAlignment.Center;
        _glyph.VerticalAlignment = VerticalAlignment.Center;

        _number.FontFamily = Theme.FontBold;
        _number.FontWeight = FontWeights.Bold;
        _number.Foreground = Theme.PrimaryText;
        _number.HorizontalAlignment = HorizontalAlignment.Center;
        _number.VerticalAlignment = VerticalAlignment.Center;
        _number.TextAlignment = TextAlignment.Center;
        _number.IsHitTestVisible = false;

        // Charging halo (the notch's own battery only): a soft green glow that breathes on the compositor.
        _halo.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0xC0, Theme.ChargingColor.R, Theme.ChargingColor.G, Theme.ChargingColor.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0, Theme.ChargingColor.R, Theme.ChargingColor.G, Theme.ChargingColor.B), Offset = 1 },
            },
        };
        _halo.IsHitTestVisible = false;
        _halo.Visibility = Visibility.Collapsed;
        Children.Add(_halo);
        Children.Add(_ringHost);
        Children.Add(_glyph);
        Children.Add(_number);

        Loaded += (_, _) => Build();
        _number.SizeChanged += (_, e) => Anim.VisualOf(_number).CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        _glyph.SizeChanged += (_, e) => Anim.VisualOf(_glyph).CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        PointerEntered += (_, _) => { if (NumberOnHover) ShowNumber = true; };
        PointerExited += (_, _) => { if (NumberOnHover) ShowNumber = false; };
    }

    /// <summary>Diameter in DIPs (set before the ring is loaded).</summary>
    public double RingSize { get => _size; set { _size = value; Width = Height = value; } }
    /// <summary>Stroke thickness in DIPs (set before the ring is loaded).</summary>
    public double Thickness { get => _thickness; set => _thickness = value; }

    public double Percent { get => _percent; set { if (value == _percent) return; _percent = value; OnPercentChanged(); } }
    public bool IsCharging { get => _charging; set { if (value == _charging) return; _charging = value; ApplyColor(Motion.ChargeColorMs); } }
    public string? Glyph { get => _deviceGlyph; set { _deviceGlyph = value; UpdateGlyph(); } }
    /// <summary>When true, hovering the ring itself toggles <see cref="ShowNumber"/>.</summary>
    public bool NumberOnHover { get; set; }

    public bool ShowNumber
    {
        get => _showNumber;
        set
        {
            if (value == _showNumber) return;
            _showNumber = value;
            _numberReveal?.SetTarget(value && HasValue ? 1 : 0, Motion.Reveal);
        }
    }

    private bool HasValue => _percent >= 0;

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

        // Number in, glyph out: one spring drives both (expressions run in the compositor).
        var props = c.CreatePropertySet();
        _numberReveal = new CompSpring(props, "n", Motion.Reveal, 0);
        var nv = Anim.VisualOf(_number);
        nv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.n, 0, 1)", ("p", props)));
        nv.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.6 + 0.4 * Clamp(p.n, 0, 1.2), 0.6 + 0.4 * Clamp(p.n, 0, 1.2), 1)", ("p", props)));
        Anim.VisualOf(_glyph).StartAnimation("Opacity", Anim.Expr(c, "1 - Clamp(p.n, 0, 1)", ("p", props)));

        OnPercentChanged();
        if (_showNumber && HasValue) _numberReveal.SetTarget(1, Motion.Reveal);
    }

    private void OnPercentChanged()
    {
        _number.Text = HasValue ? Math.Round(Math.Clamp(_percent, 0, 100)).ToString(CultureInfo.CurrentCulture) : "";
        double fs = Theme.RingNumberFontSize * (_size / Theme.NotchRingSize);
        if (_number.Text.Length >= 3) fs *= 0.82;
        _number.FontSize = fs;
        if (_shapes == null) { UpdateGlyph(); return; }

        _shapes.IsVisible = HasValue;
        if (!HasValue) { UpdateGlyph(); return; }
        _fill!.SetTarget(Math.Clamp(_percent, 0, 100) / 100.0, Motion.RingFill);

        ApplyColor(Motion.RingColorMs);
    }

    /// <summary>Level colour, or green while charging for the notch's own battery (<see cref="GreenWhileCharging"/>).</summary>
    private Color TargetColor => GreenWhileCharging && _charging ? Theme.ChargingColor : Theme.BatteryColor(_percent);

    private void ApplyColor(int ms)
    {
        if (_arcBrush == null || !HasValue) { UpdateGlyph(); return; }
        var target = TargetColor;
        if (!_hadValue || !Anim.Enabled)
        {
            // First reading: start in the right colour (the fill still springs in from empty).
            _hadValue = true;
            _arcBrush.StopAnimation("Color");
            _arcBrush.Color = target;
        }
        else if (target != _color)
        {
            var a = _arcBrush.Compositor.CreateColorKeyFrameAnimation();
            a.InsertKeyFrame(1, target);
            a.Duration = TimeSpan.FromMilliseconds(ms);
            _arcBrush.StartAnimation("Color", a);
        }
        _color = target;
        UpdateGlyph();
    }

    /// <summary>
    /// The notch's own battery (collapsed ring and the expanded header): ring and bolt are always green while
    /// the charger is connected, whatever the level, and cross-fade back to the level colours when unplugged.
    /// Device rings leave this off.
    /// </summary>
    public bool GreenWhileCharging { get; set; }

    private void UpdateGlyph()
    {
        bool device = !string.IsNullOrEmpty(_deviceGlyph);
        string? data = device ? _deviceGlyph : _charging && HasValue ? Icons.Bolt : null;
        _boltBrush.Color = _color;
        _glyph.Size = _size * (device ? 0.5 : 0.52);
        _glyph.Foreground = device ? Theme.PrimaryText : _boltBrush;
        _glyph.Data = data;
        UpdateHalo(!device && _charging && HasValue && ChargeHalo);
    }

    /// <summary>Show the breathing charging halo while charging (the collapsed notch's ring).</summary>
    public bool ChargeHalo { get; set; }

    private void UpdateHalo(bool on)
    {
        if (on == _haloOn) return;
        _haloOn = on;
        double d = _size * Theme.ChargeHaloScale;
        _halo.Width = _halo.Height = d;
        _halo.Margin = new Thickness(-(d - _size) / 2);
        _halo.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var v = Anim.VisualOf(_halo);
        v.CenterPoint = new Vector3((float)d / 2, (float)d / 2, 0);
        if (!on) { v.StopAnimation("Opacity"); v.StopAnimation("Scale"); return; }
        if (!Anim.Enabled) { v.Opacity = (float)Theme.ChargeHaloOpacity; return; }
        var c = v.Compositor;
        var op = c.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0, 0.15f);
        op.InsertKeyFrame(0.5f, (float)Theme.ChargeHaloOpacity);
        op.InsertKeyFrame(1, 0.15f);
        op.Duration = TimeSpan.FromSeconds(Motion.ChargePulsePeriod);
        op.IterationBehavior = AnimationIterationBehavior.Forever;
        var sc = c.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0, new Vector3(0.75f, 0.75f, 1));
        sc.InsertKeyFrame(0.5f, new Vector3(1, 1, 1));
        sc.InsertKeyFrame(1, new Vector3(0.75f, 0.75f, 1));
        sc.Duration = op.Duration;
        sc.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Opacity", op);
        v.StartAnimation("Scale", sc);
    }
}
