using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// The animated, coloured icon of a system pill (inside the pill's icon tile). Each kind plays one short motion as the
/// pill appears (about 300–800 ms), then rests:
/// <list type="bullet">
/// <item>Wi-Fi connected (blue): the bars fill in one by one from the dot up, then a quick settle pulse and a ripple.
/// Disconnected (red-grey): the bars fade out from the top and the icon sinks a little.</item>
/// <item>VPN connected (teal): the shield spins and grows in, the check draws itself across, then the shield "clicks"
/// (a squeeze and release) with a ripple. Disconnected (grey): the check wipes away and the shield dims.</item>
/// <item>Bluetooth device connected (blue): the device pops in and sends out two waves. Disconnected: it dims and shrinks.</item>
/// <item>Audio output switched (purple): the device slides in with waves. USB drive (orange): plugs in from below with a
/// ripple, or slides out and greys when removed. Display (cyan): pops in with a ripple, or dims when unplugged.</item>
/// </list>
/// Every motion is a compositor keyframe animation started with a delay (so it plays once the pill's content has
/// revealed), so it runs at the display's refresh rate with no work on the UI thread. With animations off, the final
/// state shows at once.
/// </summary>
public sealed class PillIconView : Grid
{
    private readonly double _iconSize;
    private readonly List<Visual> _animated = new();

    public PillIconView(double iconSize)
    {
        _iconSize = iconSize;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    // ---- Colours (chosen to sit well on the black notch) ----
    public static readonly Color WifiBlue = Color.FromArgb(255, 0x40, 0x9C, 0xFF);
    public static readonly Color VpnTeal = Color.FromArgb(255, 0x2D, 0xD4, 0xBF);
    public static readonly Color DeviceBlue = Color.FromArgb(255, 0x0A, 0x84, 0xFF);
    public static readonly Color AudioPurple = Color.FromArgb(255, 0xBF, 0x5A, 0xF2);
    public static readonly Color UsbOrange = Color.FromArgb(255, 0xFF, 0x9F, 0x0A);
    public static readonly Color DisplayCyan = Color.FromArgb(255, 0x64, 0xD2, 0xFF);
    public static readonly Color OffRed = Color.FromArgb(255, 0xE5, 0x6B, 0x6B);
    public static readonly Color OffGrey = Color.FromArgb(255, 0x8E, 0x8E, 0x93);

    /// <summary>The icon colour for a kind (the tile is tinted with it too), or null for the plain look.</summary>
    public static Color? ColorFor(PillAnim a) => a switch
    {
        PillAnim.WifiOn => WifiBlue,
        PillAnim.WifiOff => OffRed,
        PillAnim.VpnOn => VpnTeal,
        PillAnim.DeviceOn => DeviceBlue,
        PillAnim.AudioSwitch => AudioPurple,
        PillAnim.UsbIn => UsbOrange,
        PillAnim.DisplayIn => DisplayCyan,
        PillAnim.VpnOff or PillAnim.DeviceOff or PillAnim.UsbOut or PillAnim.DisplayOut => OffGrey,
        _ => null,
    };

    /// <summary>Removes the icon (the pill shows its plain glyph or an app logo instead).</summary>
    public void Clear()
    {
        foreach (var v in _animated) StopAll(v);
        _animated.Clear();
        Children.Clear();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Builds the icon for <paramref name="anim"/> and plays its motion after <paramref name="delay"/> seconds.</summary>
    public void Play(PillAnim anim, string? glyph, double delay)
    {
        Clear();
        if (anim == PillAnim.None || ColorFor(anim) is not { } color) return;
        Visibility = Visibility.Visible;
        Width = Height = _iconSize + 12;
        var brush = new SolidColorBrush(color);
        int d = (int)Math.Round(delay * 1000);
        switch (anim)
        {
            case PillAnim.WifiOn: WifiOn(brush, color, d); break;
            case PillAnim.WifiOff: WifiOff(brush, d); break;
            case PillAnim.VpnOn: VpnOn(brush, color, d); break;
            case PillAnim.VpnOff: VpnOff(brush, d); break;
            case PillAnim.DeviceOn: Waves(Icon(glyph ?? Icons.Bluetooth, brush), color, d, slide: false); break;
            case PillAnim.AudioSwitch: Waves(Icon(glyph ?? Icons.Speaker, brush), color, d, slide: true); break;
            case PillAnim.DeviceOff or PillAnim.DisplayOut: Dim(Icon(glyph ?? (anim == PillAnim.DisplayOut ? Icons.Monitor : Icons.Bluetooth), brush), d, sink: 0); break;
            case PillAnim.UsbIn: UsbIn(Icon(Icons.Usb, brush), color, d); break;
            case PillAnim.UsbOut: Dim(Icon(Icons.Usb, brush), d, sink: 6); break;
            case PillAnim.DisplayIn: PopIn(Icon(Icons.Monitor, brush), color, d); break;
        }
    }

    // =================================================================================================================
    // Kinds
    // =================================================================================================================
    private void WifiOn(Brush brush, Color color, int d)
    {
        var group = Group();
        var parts = new[] { Icons.WifiDot, Icons.WifiArc1, Icons.WifiArc2, Icons.WifiArc3 };
        float s = (float)(_iconSize / 24);
        for (int i = 0; i < parts.Length; i++)
        {
            var icon = Icon(parts[i], brush, group);
            var v = Anim.VisualOf(icon);
            v.CenterPoint = new Vector3(12 * s, 20 * s, 0); // each bar grows out of the dot
            Scalar(v, "Opacity", d + i * 95, 200, (0, 0.16f), (1, 1));
            Vec(v, "Scale", d + i * 95, 240, (0, new Vector3(0.72f, 0.72f, 1)), (0.7f, new Vector3(1.04f, 1.04f, 1)), (1, Vector3.One));
        }
        int settle = d + parts.Length * 95 + 60;
        Pulse(Anim.VisualOf(group), settle, 1.12f);
        Ripple(color, settle);
    }

    private void WifiOff(Brush brush, int d)
    {
        var group = Group();
        var parts = new[] { Icons.WifiDot, Icons.WifiArc1, Icons.WifiArc2, Icons.WifiArc3 };
        for (int i = 0; i < parts.Length; i++)
        {
            var v = Anim.VisualOf(Icon(parts[i], brush, group));
            Scalar(v, "Opacity", d + (parts.Length - 1 - i) * 85, 240, (0, 1), (1, i == 0 ? 0.55f : 0.14f)); // largest bar first
        }
        Vec(Anim.VisualOf(group, translation: true), "Translation", d, 480, (0, Vector3.Zero), (1, new Vector3(0, 2, 0)));
    }

    private void VpnOn(Brush brush, Color color, int d)
    {
        var group = Group();
        var shield = Icon(Icons.VpnShield, brush, group);
        var check = Icon(Icons.VpnCheck, brush, group);
        var sv = Anim.VisualOf(shield);
        Centre(sv);
        Scalar(sv, "RotationAngleInDegrees", d, 520, (0, -100), (0.75f, 6), (1, 0));
        Vec(sv, "Scale", d, 520, (0, new Vector3(0.5f, 0.5f, 1)), (0.7f, new Vector3(1.06f, 1.06f, 1)), (1, Vector3.One));
        Scalar(sv, "Opacity", d, 260, (0, 0), (1, 1));
        // The check draws itself from left to right (a wipe, on the compositor).
        var cv = Anim.VisualOf(check);
        var clip = cv.Compositor.CreateInsetClip((float)(_iconSize * 0.3), 0, (float)(_iconSize * 0.7), 0);
        cv.Clip = clip;
        Scalar(clip, "RightInset", d + 400, 220, (0, (float)(_iconSize * 0.7)), (1, 0));
        // Then the "lock click": a quick squeeze and release, with a ripple.
        var gv = Anim.VisualOf(group);
        Centre(gv);
        int click = d + 640;
        Vec(gv, "Scale", click, 280, (0, Vector3.One), (0.35f, new Vector3(0.86f, 0.86f, 1)), (0.72f, new Vector3(1.08f, 1.08f, 1)), (1, Vector3.One));
        Ripple(color, click + 60);
    }

    private void VpnOff(Brush brush, int d)
    {
        var group = Group();
        var shield = Icon(Icons.VpnShield, brush, group);
        var check = Icon(Icons.VpnCheck, brush, group);
        var cv = Anim.VisualOf(check);
        var clip = cv.Compositor.CreateInsetClip(0, 0, 0, 0);
        cv.Clip = clip;
        Scalar(clip, "LeftInset", d, 260, (0, 0), (1, (float)(_iconSize * 0.7)));
        var sv = Anim.VisualOf(shield);
        Centre(sv);
        Vec(sv, "Scale", d + 120, 420, (0, Vector3.One), (1, new Vector3(0.9f, 0.9f, 1)));
        Scalar(sv, "Opacity", d + 120, 420, (0, 1), (1, 0.6f));
    }

    /// <summary>The device pops (or slides) in and sends out two waves.</summary>
    private void Waves(VectorIcon icon, Color color, int d, bool slide)
    {
        var v = Anim.VisualOf(icon, translation: slide);
        Centre(v);
        if (slide)
        {
            Vec(v, "Translation", d, 420, (0, new Vector3(-9, 0, 0)), (0.7f, new Vector3(1, 0, 0)), (1, Vector3.Zero));
            Scalar(v, "Opacity", d, 260, (0, 0), (1, 1));
        }
        else
        {
            Vec(v, "Scale", d, 420, (0, new Vector3(0.6f, 0.6f, 1)), (0.6f, new Vector3(1.12f, 1.12f, 1)), (1, Vector3.One));
            Scalar(v, "Opacity", d, 200, (0, 0), (1, 1));
        }
        Ripple(color, d + 160);
        Ripple(color, d + 420);
    }

    private void UsbIn(VectorIcon icon, Color color, int d)
    {
        var v = Anim.VisualOf(icon, translation: true);
        Vec(v, "Translation", d, 460, (0, new Vector3(0, 9, 0)), (0.65f, new Vector3(0, -1.5f, 0)), (1, Vector3.Zero));
        Scalar(v, "Opacity", d, 220, (0, 0), (1, 1));
        Ripple(color, d + 360);
    }

    private void PopIn(VectorIcon icon, Color color, int d)
    {
        var v = Anim.VisualOf(icon);
        Centre(v);
        Vec(v, "Scale", d, 460, (0, new Vector3(0.55f, 0.55f, 1)), (0.65f, new Vector3(1.1f, 1.1f, 1)), (1, Vector3.One));
        Scalar(v, "Opacity", d, 220, (0, 0), (1, 1));
        Ripple(color, d + 260);
    }

    /// <summary>Something went away: the icon dims and shrinks (and sinks a little for USB).</summary>
    private void Dim(VectorIcon icon, int d, float sink)
    {
        var v = Anim.VisualOf(icon, translation: sink > 0);
        Centre(v);
        Vec(v, "Scale", d, 420, (0, Vector3.One), (1, new Vector3(0.88f, 0.88f, 1)));
        Scalar(v, "Opacity", d, 420, (0, 1), (1, 0.55f));
        if (sink > 0) Vec(v, "Translation", d, 420, (0, Vector3.Zero), (1, new Vector3(0, sink, 0)));
    }

    // =================================================================================================================
    // Pieces
    // =================================================================================================================
    private Grid Group()
    {
        var g = new Grid { Width = _iconSize, Height = _iconSize, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Children.Add(g);
        return g;
    }

    private VectorIcon Icon(string data, Brush brush, Panel? parent = null)
    {
        var icon = new VectorIcon { Data = data, Size = _iconSize, Foreground = brush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        (parent ?? this).Children.Add(icon);
        return icon;
    }

    private void Centre(Visual v) => v.CenterPoint = new Vector3((float)_iconSize / 2, (float)_iconSize / 2, 0);

    /// <summary>A soft ring that spreads out from the icon and fades.</summary>
    private void Ripple(Color color, int delayMs)
    {
        double d = _iconSize + 4;
        var ring = new Ellipse
        {
            Width = d, Height = d, StrokeThickness = 1.5,
            Stroke = new SolidColorBrush(Color.FromArgb(0xC0, color.R, color.G, color.B)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        Children.Insert(0, ring);
        var v = Anim.VisualOf(ring);
        v.CenterPoint = new Vector3((float)d / 2, (float)d / 2, 0);
        v.Opacity = 0;
        Vec(v, "Scale", delayMs, 620, (0, new Vector3(0.55f, 0.55f, 1)), (1, new Vector3(1.45f, 1.45f, 1)));
        Scalar(v, "Opacity", delayMs, 620, (0, 0.85f), (1, 0));
    }

    private void Pulse(Visual v, int delayMs, float peak)
    {
        Centre(v);
        Vec(v, "Scale", delayMs, 320, (0, Vector3.One), (0.4f, new Vector3(peak, peak, 1)), (1, Vector3.One));
    }

    // ---- Keyframe helpers (start values apply during the delay; animations off → the final value) ----
    private CompositionEasingFunction Ease(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.22f, 0.9f), new Vector2(0.32f, 1f));

    private void Scalar(CompositionObject target, string property, int delayMs, int durationMs, params (float at, float value)[] keys)
    {
        if (!Anim.Enabled) { Anim.SetScalar(target, property, keys[^1].value); return; }
        var c = target.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        foreach (var (at, value) in keys) a.InsertKeyFrame(at, value, Ease(c));
        a.Duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs / Math.Max(0.5, Motion.Speed)));
        a.DelayTime = TimeSpan.FromMilliseconds(Math.Max(0, delayMs));
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        target.StartAnimation(property, a);
        if (target is Visual v && !_animated.Contains(v)) _animated.Add(v);
    }

    private void Vec(Visual target, string property, int delayMs, int durationMs, params (float at, Vector3 value)[] keys)
    {
        if (!Anim.Enabled)
        {
            if (property == "Scale") target.Scale = keys[^1].value;
            else if (property == "Translation") target.Properties.InsertVector3("Translation", keys[^1].value);
            return;
        }
        var c = target.Compositor;
        var a = c.CreateVector3KeyFrameAnimation();
        foreach (var (at, value) in keys) a.InsertKeyFrame(at, value, Ease(c));
        a.Duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs / Math.Max(0.5, Motion.Speed)));
        a.DelayTime = TimeSpan.FromMilliseconds(Math.Max(0, delayMs));
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        target.StartAnimation(property, a);
        if (!_animated.Contains(target)) _animated.Add(target);
    }

    private static void StopAll(Visual v)
    {
        foreach (var p in new[] { "Opacity", "Scale", "Translation", "RotationAngleInDegrees" })
            try { v.StopAnimation(p); } catch { }
    }
}
