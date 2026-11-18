using System.Globalization;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// The content of the ONE activity pill (volume, brightness, Caps Lock, charging / battery, downloads,
/// updates): icon · title or track · value. The collapsed notch grows into it (the notch body is its
/// background); <paramref name="compact"/> is the slim twin shown at the top of the open notch, with its
/// own dark capsule and hairline border. A new kind cross-morphs (old content out quickly, new content
/// fades and rises in, staggered); an update of the same kind changes values in place. Every motion
/// is a compositor spring; numbers count with tabular digits.
/// </summary>
public sealed class ActivityPillView : Grid
{
    private readonly bool _compact;
    private readonly Grid _row = new(), _iconArea = new(), _lockGroup = new(), _sunGroup = new(), _valueArea = new();
    private readonly Border _capsule = new();
    private readonly Ellipse _glow = new();
    private readonly RadialGradientBrush _glowBrush = new();
    private readonly VectorIcon _icon = new(), _sunCore = new(), _sunRays = new(), _lockBody = new(), _lockShackle = new();
    private readonly StackPanel _center = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new(), _subtitle = new(), _value = new(), _upperA = new(), _lowerA = new(), _actionText = new();
    private readonly Border _actionPill = new();
    private readonly Grid _capsGlyph = new();
    private readonly LevelTrack _track;
    private readonly SolidColorBrush _accent = new(Theme.VolumeColor);
    private readonly CompositionPropertySet _p;
    private readonly CompSpring _shackle, _click, _upper, _rays, _glowLevel, _iconPop;
    private readonly List<CompSpring> _blocks = new();
    private readonly DispatcherQueueTimer _swap, _count;
    private ActivityKind? _kind;
    private Activity? _pendingSwap;
    private double _shownNumber = double.NaN, _countFrom, _countTo;
    private DateTime _countStart;
    private string _countSuffix = "";

    public ActivityPillView(bool compact)
    {
        _compact = compact;
        Height = compact ? Theme.ActivityCompactHeight : Theme.ActivityHeight;
        VerticalAlignment = VerticalAlignment.Top;
        Background = Theme.TransparentBrush;

        if (compact)
        {
            _capsule.Background = Theme.CardBrush;
            _capsule.BorderBrush = Theme.HighlightBorderBrush;
            _capsule.BorderThickness = new Thickness(1);
            _capsule.CornerRadius = new CornerRadius(Theme.ActivityCompactHeight / 2);
            Children.Add(_capsule);
        }

        _row.Margin = compact ? Theme.ActivityCompactPadding : Theme.ActivityPadding;
        foreach (var w in new[] { GridLength.Auto, Theme.GapS3, new GridLength(1, GridUnitType.Star), Theme.GapS3, GridLength.Auto })
            _row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        Children.Add(_row);

        // ---- Icon area: glow · generic icon · sun (core + rays) · padlock (body + shackle) ----
        double box = compact ? Theme.ActivityCompactIconSize + 4 : Theme.ActivityIconBox;
        double iconSize = compact ? Theme.ActivityCompactIconSize : Theme.ActivityIconSize;
        _iconArea.Width = _iconArea.Height = box;
        _iconArea.VerticalAlignment = VerticalAlignment.Center;
        _glowBrush.GradientStops.Add(new GradientStop { Color = Theme.CapsOnColor, Offset = 0 });
        _glowBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, Theme.CapsOnColor.R, Theme.CapsOnColor.G, Theme.CapsOnColor.B), Offset = 1 });
        double glow = compact ? Theme.ActivityGlowSize * 0.7 : Theme.ActivityGlowSize;
        _glow.Width = _glow.Height = glow;
        _glow.Margin = new Thickness(-(glow - box) / 2);
        _glow.Fill = _glowBrush;
        _glow.IsHitTestVisible = false;
        _iconArea.Children.Add(_glow);
        foreach (var i in new[] { _icon, _sunCore, _sunRays, _lockBody, _lockShackle })
        {
            i.Size = iconSize;
            i.Foreground = _accent;
            i.HorizontalAlignment = HorizontalAlignment.Center;
            i.VerticalAlignment = VerticalAlignment.Center;
        }
        _sunCore.Data = Icons.SunCore;
        _sunRays.Data = Icons.SunRays;
        _lockBody.Data = Icons.LockBody;
        _lockShackle.Data = Icons.LockShackle;
        _sunGroup.Children.Add(_sunRays);
        _sunGroup.Children.Add(_sunCore);
        _lockGroup.Children.Add(_lockShackle);
        _lockGroup.Children.Add(_lockBody);
        _iconArea.Children.Add(_icon);
        _iconArea.Children.Add(_sunGroup);
        _iconArea.Children.Add(_lockGroup);
        _row.Children.Add(_iconArea);

        // ---- Centre: title (+ subtitle) and / or the level track ----
        _title.FontFamily = Theme.FontBold;
        _title.FontWeight = FontWeights.Bold;
        _title.FontSize = compact ? Theme.FontCaption + 1 : Theme.FontActivityTitle;
        _title.Foreground = Theme.PrimaryText;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.TextWrapping = TextWrapping.NoWrap;
        _title.IsHitTestVisible = false;
        _subtitle.Style = (Style)Application.Current.Resources["CaptionText"];
        _subtitle.Foreground = Theme.SecondaryText;
        _track = new LevelTrack();
        _center.Children.Add(_title);
        _center.Children.Add(_subtitle);
        _center.Children.Add(_track);
        SetColumn(_center, 2);
        _row.Children.Add(_center);

        // ---- Value: number (tabular) · Caps "A / a" glyph · action pill ----
        _valueArea.VerticalAlignment = VerticalAlignment.Center;
        _valueArea.MinWidth = compact ? 0 : Theme.ActivityValueWidth;
        _value.FontFamily = Theme.FontBold;
        _value.FontWeight = FontWeights.Bold;
        _value.FontSize = compact ? Theme.FontCaption + 1 : Theme.FontActivityValue;
        _value.HorizontalAlignment = HorizontalAlignment.Right;
        _value.VerticalAlignment = VerticalAlignment.Center;
        _value.IsHitTestVisible = false;
        Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
        Typography.SetNumeralStyle(_value, FontNumeralStyle.Lining);
        _valueArea.Children.Add(_value);
        foreach (var (t, text) in new[] { (_upperA, "A"), (_lowerA, "a") })
        {
            t.Text = text;
            t.FontFamily = Theme.FontBold;
            t.FontWeight = FontWeights.Bold;
            t.FontSize = compact ? Theme.FontActivityGlyph - 4 : Theme.FontActivityGlyph;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.IsHitTestVisible = false;
            _capsGlyph.Children.Add(t);
        }
        _capsGlyph.Width = compact ? 18 : 24;
        _capsGlyph.HorizontalAlignment = HorizontalAlignment.Right;
        _valueArea.Children.Add(_capsGlyph);
        _actionPill.Height = compact ? 20 : 24;
        _actionPill.CornerRadius = new CornerRadius(_actionPill.Height / 2);
        _actionPill.Background = Theme.PillButtonBrush;
        _actionPill.Padding = new Thickness(10, 0, 10, 0);
        _actionPill.HorizontalAlignment = HorizontalAlignment.Right;
        _actionText.FontFamily = Theme.FontSemiBold;
        _actionText.FontWeight = FontWeights.SemiBold;
        _actionText.FontSize = Theme.FontCaption;
        _actionText.Foreground = Theme.PillButtonTextBrush;
        _actionText.VerticalAlignment = VerticalAlignment.Center;
        _actionPill.Child = _actionText;
        _actionPill.IsHitTestVisible = false;
        _valueArea.Children.Add(_actionPill);
        SetColumn(_valueArea, 4);
        _row.Children.Add(_valueArea);

        // ---- Motion ----
        var c = Anim.VisualOf(this).Compositor;
        _p = c.CreatePropertySet();
        _shackle = new CompSpring(_p, "o", Motion.LockShackle, 0);
        _click = new CompSpring(_p, "k", Motion.LockClick, 1, precision: 0.5);
        _upper = new CompSpring(_p, "u", Motion.Pop, 0);
        _rays = new CompSpring(_p, "r", Motion.ActivityFill, 1);
        _glowLevel = new CompSpring(_p, "g", Motion.GlowDim, 0);
        _iconPop = new CompSpring(_p, "b", Motion.Pop, 1);
        var iv = Anim.VisualOf(_icon);
        iv.CenterPoint = new Vector3((float)iconSize / 2, (float)iconSize / 2, 0);
        iv.StartAnimation("Scale", Anim.Expr(c, "Vector3(p.b, p.b, 1)", ("p", _p)));

        // Padlock: the shackle lifts and swings open around its right leg; the body "clicks" (scale pulse) on lock.
        double s = iconSize / 24.0;
        var sv = Anim.VisualOf(_lockShackle, translation: true);
        sv.CenterPoint = new Vector3((float)(17 * s), (float)(11 * s), 0);
        sv.StartAnimation("RotationAngleInDegrees", Anim.Expr(c, "-24 * p.o", ("p", _p)));
        var lift = Anim.Expr(c, "Vector3(0, -L * p.o, 0)", ("p", _p));
        lift.SetScalarParameter("L", (float)(3 * s));
        sv.StartAnimation("Translation", lift);
        var lv = Anim.VisualOf(_lockGroup);
        lv.CenterPoint = new Vector3((float)box / 2, (float)box / 2, 0);
        lv.StartAnimation("Scale", Anim.Expr(c, "Vector3(p.k, p.k, 1)", ("p", _p)));

        // "A" ⇄ "a": cross-scale and fade (the glyph morphs between cases).
        Motions.CentreOrigin(_upperA);
        Motions.CentreOrigin(_lowerA);
        var uv = Anim.VisualOf(_upperA, translation: true);
        uv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.u, 0, 1)", ("p", _p)));
        uv.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.6 + 0.4 * p.u, 0.6 + 0.4 * p.u, 1)", ("p", _p)));
        uv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (1 - p.u) * 3, 0)", ("p", _p)));
        var dv = Anim.VisualOf(_lowerA, translation: true);
        dv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(1 - p.u, 0, 1)", ("p", _p)));
        dv.StartAnimation("Scale", Anim.Expr(c, "Vector3(1.25 - 0.25 * (1 - p.u), 1.25 - 0.25 * (1 - p.u), 1)", ("p", _p)));

        // Sun rays grow with the brightness level.
        var rv = Anim.VisualOf(_sunRays);
        rv.CenterPoint = new Vector3((float)iconSize / 2, (float)iconSize / 2, 0);
        rv.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.55 + 0.45 * p.r, 0.55 + 0.45 * p.r, 1)", ("p", _p)));
        rv.StartAnimation("Opacity", Anim.Expr(c, "0.45 + 0.55 * Clamp(p.r, 0, 1)", ("p", _p)));

        // Glow behind the icon.
        var gv = Anim.VisualOf(_glow);
        gv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.g, 0, 1)", ("p", _p)));
        gv.CenterPoint = new Vector3((float)glow / 2, (float)glow / 2, 0);

        // Staggered blocks: icon, centre, value.
        foreach (var el in new FrameworkElement[] { _iconArea, _center, _valueArea })
        {
            var v = Anim.VisualOf(el, translation: true);
            var (spring, set) = Motions.Value(c, Motion.FadeIn, 0);
            v.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
            var rise = Anim.Expr(c, "Vector3(0, (1 - p.v) * R, 0)", ("p", set));
            rise.SetScalarParameter("R", (float)Motion.ActivityRise);
            v.StartAnimation("Translation", rise);
            _blocks.Add(spring);
        }

        _swap = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _swap.IsRepeating = false;
        _swap.Interval = TimeSpan.FromMilliseconds(90);
        _swap.Tick += (_, _) => { _swap.Stop(); if (_pendingSwap is { } a) { _pendingSwap = null; Build(a); Enter(); } };
        _count = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _count.Interval = TimeSpan.FromMilliseconds(16);
        _count.Tick += (_, _) => StepCount();

        _track.UserChanged += v => Current?.OnSet?.Invoke(v);
    }

    public Activity? Current { get; private set; }
    /// <summary>The level track (its hover keeps the pill open; dragging sets the real value).</summary>
    public LevelTrack Track => _track;

    /// <summary>Pill width for an activity kind (the notch springs to it).</summary>
    public static double WidthFor(ActivityKind k) => k switch
    {
        ActivityKind.Volume or ActivityKind.Brightness => Theme.ActivityWidthSlider,
        ActivityKind.CapsLock => Theme.ActivityWidthCaps,
        ActivityKind.Download => Theme.ActivityWidthDownload,
        ActivityKind.Update => Theme.ActivityWidthUpdate,
        _ => Theme.ActivityWidthBattery,
    };

    /// <summary>
    /// Shows <paramref name="a"/>: in place when it's the same kind as now, otherwise a quick cross-fade
    /// to the new layout (<paramref name="entering"/> = the pill itself is just appearing: no fade-out first).
    /// </summary>
    public void Apply(Activity a, bool sameKind, bool entering = false)
    {
        var previous = Current;
        Current = a;
        if (sameKind && _kind == a.Kind && previous != null) { Update(a, previous); return; }
        if (entering || _kind == null || !Anim.Enabled)
        {
            _swap.Stop();
            _pendingSwap = null;
            Build(a);
            if (entering || _kind == null) Enter();
            return;
        }
        // Cross-morph: current content leaves quickly, then the new layout fades / rises in.
        foreach (var b in _blocks) b.SetTarget(0, Motion.FadeOut);
        _pendingSwap = a;
        _swap.Stop();
        _swap.Start();
    }

    /// <summary>Staggered entrance of the three blocks.</summary>
    public void Enter()
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            _blocks[i].Snap(0);
            _blocks[i].SetTarget(1, Motion.FadeIn, i * Motion.ActivityStaggerMs / 1000.0);
        }
    }

    // ---- Layout per kind -------------------------------------------------------------------------------
    private void Build(Activity a)
    {
        _kind = a.Kind;
        _shownNumber = double.NaN;
        _count.Stop();
        bool slider = a.Kind is ActivityKind.Volume or ActivityKind.Brightness;
        bool caps = a.Kind == ActivityKind.CapsLock;

        _icon.Visibility = a.Kind is ActivityKind.Brightness or ActivityKind.CapsLock ? Visibility.Collapsed : Visibility.Visible;
        _sunGroup.Visibility = a.Kind == ActivityKind.Brightness ? Visibility.Visible : Visibility.Collapsed;
        _lockGroup.Visibility = caps ? Visibility.Visible : Visibility.Collapsed;
        _capsGlyph.Visibility = caps ? Visibility.Visible : Visibility.Collapsed;
        _actionPill.Visibility = a.Kind == ActivityKind.Update && !_compact ? Visibility.Visible : Visibility.Collapsed;
        _value.Visibility = caps || a.Kind == ActivityKind.Update ? Visibility.Collapsed : Visibility.Visible;
        _title.Visibility = slider ? Visibility.Collapsed : Visibility.Visible;
        _track.Visibility = slider || a.Kind == ActivityKind.Download && !a.On ? Visibility.Visible : Visibility.Collapsed;
        _track.Interactive = slider && a.OnSet != null;
        _track.Margin = slider ? new Thickness(0) : new Thickness(0, 2, 0, -4);

        // Caps / brightness springs start from the state being shown (no animation from a stale state).
        if (caps)
        {
            _shackle.Snap(a.On ? 1 : 0);
            _upper.Snap(a.On ? 0 : 1);
        }
        if (a.Kind == ActivityKind.Brightness) _rays.Snap(a.Level ?? 1);
        _iconPop.Snap(1); // only the battery kinds pop their bolt (in Update)
        Update(a, null);
    }

    /// <summary>Values only (same layout): track, number, icon state, colours, glow.</summary>
    private void Update(Activity a, Activity? previous)
    {
        bool animate = previous != null;
        Color accent = Theme.VolumeColor;
        double glow = 0;
        switch (a.Kind)
        {
            case ActivityKind.Volume:
            {
                double level = a.Level ?? 0;
                bool muted = a.On || level <= 0.001;
                _icon.Data = muted ? Icons.VolumeMute : level < 0.34 ? Icons.VolumeLow : level < 0.67 ? Icons.VolumeMid : Icons.VolumeHigh;
                accent = muted ? Theme.VolumeMutedColor : Theme.VolumeColor;
                _track.FillBrush = Theme.AccentTrackBrush;
                _track.Dimmed = a.On;
                _track.Indeterminate = false;
                _track.SetValue(level, animate);
                CountTo(level * 100, "", animate);
                _value.Foreground = muted ? Theme.TertiaryText : Theme.PrimaryText;
                break;
            }
            case ActivityKind.Brightness:
            {
                double level = a.Level ?? 0;
                accent = Theme.BrightnessColor;
                _track.FillBrush = Theme.AccentTrackBrush;
                _track.Dimmed = false;
                _track.Indeterminate = false;
                _track.SetValue(level, animate);
                _rays.SetTarget(level, Motion.ActivityFill);
                CountTo(level * 100, "", animate);
                _value.Foreground = Theme.PrimaryText;
                break;
            }
            case ActivityKind.CapsLock:
            {
                accent = a.On ? Theme.CapsOnColor : Theme.SecondaryTextColor;
                glow = a.On ? Theme.ActivityGlowOpacity : 0;
                _title.Text = a.On ? "Caps Lock on" : "Caps Lock off";
                _subtitle.Visibility = Visibility.Collapsed;
                _shackle.SetTarget(a.On ? 0 : 1, Motion.LockShackle); // on = locked (closed)
                _upper.SetTarget(a.On ? 1 : 0, Motion.Pop);
                if (a.On && (previous == null || !previous.On)) { _click.Snap(1); _click.Impulse(Motion.LockClickImpulse); }
                _upperA.Foreground = _lowerA.Foreground = a.On ? new SolidColorBrush(Theme.CapsOnColor) : Theme.SecondaryText;
                break;
            }
            case ActivityKind.Charging:
            case ActivityKind.LowBattery:
            {
                bool low = a.Kind == ActivityKind.LowBattery;
                bool charging = a.On;
                // Plugged in: green bolt + green glow. Unplugged: the SAME bolt, neutral white, faint white glow.
                accent = low ? Theme.BatteryRed : charging ? Theme.ChargingColor : Theme.VolumeColor;
                glow = low ? 0 : charging ? Theme.ActivityGlowOpacity : Theme.UnpluggedGlowOpacity;
                _icon.Data = low ? Icons.BatteryLow : Icons.Bolt;
                if (previous == null || previous.On != a.On) { _iconPop.Snap(Motion.BoltPopFrom); _iconPop.SetTarget(1, Motion.Pop); }
                _title.Text = a.Title;
                SetSubtitle(a.Subtitle);
                CountTo((a.Level ?? 0) * 100, "%", animate);
                _value.Foreground = low ? new SolidColorBrush(Theme.BatteryRed) : charging ? new SolidColorBrush(Theme.ChargingColor) : Theme.PrimaryText;
                break;
            }
            case ActivityKind.Download:
            {
                accent = a.Failed ? Theme.SecondaryTextColor : a.On ? Theme.ChargingColor : Theme.DownloadColor;
                _icon.Data = a.Failed ? Icons.Alert : a.On ? Icons.CheckCircle : Icons.Download;
                _title.Text = a.Title;
                SetSubtitle(a.On || _compact ? a.Subtitle : null);
                _track.FillBrush = Theme.AccentTrackBrush;
                _track.Dimmed = false;
                _track.Indeterminate = !a.On && a.Level == null;
                if (a.Level is { } f) _track.SetValue(f, animate);
                _track.Visibility = a.On ? Visibility.Collapsed : Visibility.Visible;
                _count.Stop();
                _value.Text = a.Value;
                _value.Foreground = Theme.SecondaryText;
                break;
            }
            case ActivityKind.Update:
            {
                accent = Theme.UpdateColor;
                _icon.Data = Icons.UpdateArrow;
                _title.Text = a.Title;
                SetSubtitle(a.Subtitle);
                _actionText.Text = string.IsNullOrEmpty(a.Value) ? "Update" : a.Value;
                break;
            }
        }
        _accent.Color = accent;
        SetGlow(accent, glow, pulse: a.Kind == ActivityKind.Charging && a.On);
    }

    private void SetSubtitle(string? text)
    {
        _subtitle.Text = text ?? "";
        _subtitle.Visibility = string.IsNullOrEmpty(text) || _compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetGlow(Color color, double level, bool pulse)
    {
        _glowBrush.GradientStops[0].Color = Color.FromArgb(0xD0, color.R, color.G, color.B);
        _glowBrush.GradientStops[1].Color = Color.FromArgb(0, color.R, color.G, color.B);
        _glowLevel.SetTarget(level, Motion.GlowDim);
        var gv = Anim.VisualOf(_glow);
        if (pulse && Anim.Enabled)
        {
            var a = gv.Compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(0, new Vector3(0.85f, 0.85f, 1));
            a.InsertKeyFrame(0.5f, new Vector3(1.1f, 1.1f, 1));
            a.InsertKeyFrame(1, new Vector3(0.85f, 0.85f, 1));
            a.Duration = TimeSpan.FromSeconds(Motion.ChargePulsePeriod);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            gv.StartAnimation("Scale", a);
        }
        else
        {
            gv.StopAnimation("Scale");
            gv.Scale = Vector3.One;
        }
    }

    // ---- Counting numbers (only while a change is animating; the timer stops itself) ----------------------
    private void CountTo(double value, string suffix, bool animate)
    {
        value = Math.Round(Math.Clamp(value, 0, 100));
        _countSuffix = suffix;
        if (!animate || !Anim.Enabled || double.IsNaN(_shownNumber))
        {
            _count.Stop();
            _shownNumber = value;
            _value.Text = Format(value);
            return;
        }
        _countFrom = _shownNumber;
        _countTo = value;
        _countStart = DateTime.UtcNow;
        if (!_count.IsRunning) _count.Start();
    }

    private void StepCount()
    {
        double t = Math.Clamp((DateTime.UtcNow - _countStart).TotalMilliseconds / Motion.CountUpMs, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        _shownNumber = Math.Round(_countFrom + (_countTo - _countFrom) * eased);
        _value.Text = Format(_shownNumber);
        if (t >= 1) _count.Stop();
    }

    private string Format(double v) => v.ToString("0", CultureInfo.CurrentCulture) + _countSuffix;
}
