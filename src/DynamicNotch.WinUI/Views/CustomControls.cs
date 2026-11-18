using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using VirtualKey = Windows.System.VirtualKey;
using Colors = Microsoft.UI.Colors;

namespace DynamicNotch.Views;

/// <summary>
/// A labelled slider in the Settings style: the round <see cref="LevelTrack"/> filled with the accent, a
/// thumb that springs in on hover / drag / keyboard focus, a value bubble that follows the thumb while
/// adjusting, and keyboard control (← → ↑ ↓ step, Page Up / Down ×10, Home / End). Raises
/// <see cref="Changed"/> continuously while dragging (final = false) and once when the user lets go.
/// </summary>
internal sealed class ValueSlider : UserControl
{
    private readonly LevelTrack _track = new() { Interactive = true };
    private readonly Border _bubble = new();
    private readonly TextBlock _bubbleText = new(), _valueText = new();
    private readonly CompSpring _bubbleShow;
    private readonly double _min, _max, _step;
    private readonly Func<double, string> _format;
    private double _value;
    private bool _focused;

    public ValueSlider(string label, double min, double max, double step, Func<double, string> format, string? caption = null)
    {
        _min = min; _max = max; _step = step; _format = format;
        IsTabStop = true;
        AllowFocusOnInteraction = false; // a click must not take focus: the scroller would jump to bring it into view mid-drag
        UseSystemFocusVisuals = true;
        Tag = SettingsWindow.NoDragTag;
        var root = new Grid { MinHeight = 46 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label, Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
        if (caption != null) text.Children.Add(new TextBlock { Text = caption, Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = Theme.GapTopS1 });
        head.Children.Add(text);
        _valueText.Style = SettingsUi.StyleOf("TimeText");
        _valueText.Foreground = Theme.PrimaryText;
        _valueText.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(_valueText, 1);
        head.Children.Add(_valueText);
        root.Children.Add(head);

        var trackHost = new Grid { Margin = new Thickness(0, Theme.S1, 0, 0) };
        _track.FillBrush = Theme.AccentBrush;
        trackHost.Children.Add(_track);
        // Value bubble above the thumb.
        _bubble.Background = Theme.AccentBrush;
        _bubble.CornerRadius = new CornerRadius(9);
        _bubble.Padding = new Thickness(7, 1, 7, 2);
        _bubble.HorizontalAlignment = HorizontalAlignment.Left;
        _bubble.VerticalAlignment = VerticalAlignment.Top;
        _bubble.Margin = new Thickness(0, -20, 0, 0);
        _bubble.IsHitTestVisible = false;
        _bubbleText.FontFamily = Theme.FontSemiBold;
        _bubbleText.FontSize = Theme.FontCaption;
        _bubbleText.Foreground = Theme.OnAccentBrush;
        _bubble.Child = _bubbleText;
        trackHost.Children.Add(_bubble);
        Grid.SetRow(trackHost, 1);
        root.Children.Add(trackHost);
        Content = root;

        var bv = Anim.VisualOf(_bubble, translation: true);
        var (show, set) = Motions.Value(bv.Compositor, Motion.Pop, 0);
        _bubbleShow = show;
        bv.StartAnimation("Opacity", Anim.Expr(bv.Compositor, "Clamp(p.v, 0, 1)", ("p", set)));
        bv.StartAnimation("Scale", Anim.Expr(bv.Compositor, "Vector3(0.7 + 0.3 * Clamp(p.v, 0, 1.1), 0.7 + 0.3 * Clamp(p.v, 0, 1.1), 1)", ("p", set)));
        _bubble.SizeChanged += (_, e) => bv.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height, 0);

        _track.UserChanged += f => SetFromUser(_min + f * (_max - _min), final: false, fromDrag: true);
        _track.DragEnded += () => { Changed?.Invoke(_value, true); UpdateBubble(); };
        _track.HoverChanged += _ => UpdateBubble();
        _track.SizeChanged += (_, _) => PlaceBubble();
        KeyDown += OnKey;
        GotFocus += (_, _) => { _focused = FocusState == FocusState.Keyboard; _track.ShowThumb(_focused); UpdateBubble(); };
        LostFocus += (_, _) => { _focused = false; _track.ShowThumb(false); UpdateBubble(); };
    }

    /// <summary>(value, final): final is true when the user let go / pressed a key.</summary>
    public event Action<double, bool>? Changed;

    public double Value
    {
        get => _value;
        set => SetValue(value, animate: true);
    }

    public void SetValue(double v, bool animate)
    {
        v = Snap(v);
        _value = v;
        _track.SetValue((_max - _min) <= 0 ? 0 : (v - _min) / (_max - _min), animate);
        _valueText.Text = _bubbleText.Text = _format(v);
        PlaceBubble();
    }

    private double Snap(double v) => Math.Clamp(_step > 0 ? _min + Math.Round((v - _min) / _step) * _step : v, _min, _max);

    private void SetFromUser(double v, bool final, bool fromDrag)
    {
        v = Snap(v);
        bool changed = Math.Abs(v - _value) > 1e-9;
        _value = v;
        if (!fromDrag) _track.SetValue((v - _min) / (_max - _min), true);
        _valueText.Text = _bubbleText.Text = _format(v);
        PlaceBubble();
        UpdateBubble();
        if (changed || final) Changed?.Invoke(v, final);
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        double big = _step * 10;
        double? next = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Down => _value - _step,
            VirtualKey.Right or VirtualKey.Up => _value + _step,
            VirtualKey.PageDown => _value - big,
            VirtualKey.PageUp => _value + big,
            VirtualKey.Home => _min,
            VirtualKey.End => _max,
            _ => null,
        };
        if (next is not { } n) return;
        SetFromUser(n, final: true, fromDrag: false);
        e.Handled = true;
    }

    private void UpdateBubble() => _bubbleShow.SetTarget(_track.IsDragging || _focused ? 1 : 0, _track.IsDragging || _focused ? Motion.Pop : Motion.FadeOut);

    private void PlaceBubble()
    {
        double w = _track.ActualWidth;
        if (w <= 0) return;
        double f = (_max - _min) <= 0 ? 0 : (_value - _min) / (_max - _min);
        _bubble.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = _bubble.DesiredSize.Width;
        Anim.VisualOf(_bubble, translation: true).Properties.InsertVector3("Translation", new Vector3((float)Math.Clamp(f * w - bw / 2, -4, w - bw + 4), 0, 0));
    }
}

/// <summary>A segmented pill (equal segments) whose accent-tinted highlight springs to the chosen segment.</summary>
internal sealed class Segmented : Grid
{
    private readonly (string id, string label)[] _options;
    private readonly Border _highlight = new();
    private readonly List<TextBlock> _labels = new();
    private readonly CompositionPropertySet _p;
    private readonly CompSpring _pos;
    private int _index;

    public Segmented(params (string id, string label)[] options)
    {
        _options = options;
        Height = 30;
        Tag = SettingsWindow.NoDragTag;
        Background = Theme.InputBrush;
        CornerRadius = new CornerRadius(15);
        BorderBrush = Theme.CardBorderBrush;
        BorderThickness = new Thickness(1);
        for (int i = 0; i < options.Length; i++) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _highlight.Background = Theme.AccentSubtleBrush;
        _highlight.BorderBrush = Theme.AccentBrush;
        _highlight.BorderThickness = new Thickness(1);
        _highlight.CornerRadius = new CornerRadius(13);
        _highlight.Margin = new Thickness(1);
        Children.Add(_highlight);
        for (int i = 0; i < options.Length; i++)
        {
            int idx = i;
            var cell = new Grid { Background = Theme.TransparentBrush };
            var t = new TextBlock { Text = options[i].label, FontFamily = Theme.FontMedium, FontWeight = FontWeights.Medium, FontSize = Theme.FontCaption + 1, Foreground = Theme.SecondaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
            _labels.Add(t);
            cell.Children.Add(t);
            cell.Tapped += (_, e) => { e.Handled = true; Select(idx, true); Selected?.Invoke(_options[idx].id); };
            SetColumn(cell, i);
            Children.Add(cell);
        }
        var hv = Anim.VisualOf(_highlight, translation: true);
        _p = hv.Compositor.CreatePropertySet();
        _p.InsertScalar("w", 0);
        _pos = new CompSpring(_p, "i", Motion.TabHighlight, 0, precision: 0.05);
        hv.StartAnimation("Translation", Anim.Expr(hv.Compositor, "Vector3(p.i * p.w, 0, 0)", ("p", _p)));
        SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width / Math.Max(1, _options.Length);
            _highlight.Width = Math.Max(0, w - 2);
            _highlight.HorizontalAlignment = HorizontalAlignment.Left;
            _p.InsertScalar("w", (float)w);
        };
        IsTabStop = false;
    }

    public event Action<string>? Selected;

    public string Value
    {
        get => _options[_index].id;
        set { int i = Array.FindIndex(_options, o => o.id == value); if (i >= 0) Select(i, IsLoaded); }
    }

    private void Select(int i, bool animate)
    {
        _index = i;
        if (animate) _pos.SetTarget(i, Motion.TabHighlight); else _pos.Snap(i);
        for (int k = 0; k < _labels.Count; k++)
        {
            _labels[k].Foreground = k == i ? Theme.PrimaryText : Theme.SecondaryText;
            _labels[k].FontFamily = k == i ? Theme.FontSemiBold : Theme.FontMedium;
        }
    }
}

/// <summary>
/// Accent swatches: one round chip per preset plus a "Custom" chip. The selected chip gets a ring in its
/// colour that expands in with a spring; clicking plays a ripple out of the chip.
/// </summary>
internal sealed class SwatchGrid : Grid
{
    private const double Chip = 26, RingGap = 4;
    private readonly List<(string id, Grid host, Ellipse ring, CompSpring spring)> _chips = new();
    private readonly Ellipse _customFill = new();

    public SwatchGrid()
    {
        Tag = SettingsWindow.NoDragTag;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S2 };
        foreach (var p in AccentPresets.All) panel.Children.Add(Make(p.Id, p.Name, new SolidColorBrush(p.Color)));
        var rainbow = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        foreach (var (o, c) in new[] { (0.0, 0xFF3B30), (0.25, 0xFFCC00), (0.5, 0x34C759), (0.75, 0x0A84FF), (1.0, 0xBF5AF2) })
            rainbow.GradientStops.Add(new GradientStop { Offset = o, Color = Color.FromArgb(255, (byte)(c >> 16), (byte)(c >> 8), (byte)c) });
        panel.Children.Add(Make("custom", "Custom colour", rainbow));
        Children.Add(panel);
    }

    public event Action<string>? Picked;

    private FrameworkElement Make(string id, string name, Brush fill)
    {
        double outer = Chip + 2 * RingGap + 4;
        var host = new Grid { Width = outer, Height = outer, Background = Theme.TransparentBrush };
        var ring = new Ellipse { Width = outer, Height = outer, StrokeThickness = 2, Stroke = fill, IsHitTestVisible = false };
        var dot = new Ellipse { Width = Chip, Height = Chip, Fill = fill, IsHitTestVisible = false, Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 };
        var ripple = new Ellipse { Width = Chip, Height = Chip, Fill = fill, IsHitTestVisible = false, Opacity = 0 };
        host.Children.Add(ripple);
        host.Children.Add(ring);
        host.Children.Add(dot);
        ToolTipService.SetToolTip(host, name);
        var rv = Anim.VisualOf(ring);
        rv.CenterPoint = new Vector3((float)outer / 2, (float)outer / 2, 0);
        var (spring, set) = Motions.Value(rv.Compositor, Motion.Pop, 0);
        rv.StartAnimation("Opacity", Anim.Expr(rv.Compositor, "Clamp(p.v, 0, 1)", ("p", set)));
        rv.StartAnimation("Scale", Anim.Expr(rv.Compositor, "Vector3(0.6 + 0.4 * p.v, 0.6 + 0.4 * p.v, 1)", ("p", set)));
        Motions.PressScale(host);
        host.Tapped += (_, e) =>
        {
            e.Handled = true;
            Ripple(ripple);
            Picked?.Invoke(id);
        };
        _chips.Add((id, host, ring, spring));
        return host;
    }

    private static void Ripple(Ellipse e)
    {
        if (!Anim.Enabled) return;
        var v = Anim.VisualOf(e);
        v.CenterPoint = new Vector3((float)Chip / 2, (float)Chip / 2, 0);
        var c = v.Compositor;
        var s = c.CreateVector3KeyFrameAnimation();
        s.InsertKeyFrame(0, new Vector3(1, 1, 1));
        s.InsertKeyFrame(1, new Vector3(2.4f, 2.4f, 1), c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.3f, 1)));
        s.Duration = TimeSpan.FromMilliseconds(520);
        var o = c.CreateScalarKeyFrameAnimation();
        o.InsertKeyFrame(0, 0.45f);
        o.InsertKeyFrame(1, 0);
        o.Duration = s.Duration;
        v.StartAnimation("Scale", s);
        v.StartAnimation("Opacity", o);
    }

    public void Select(string id, bool animate)
    {
        foreach (var (cid, _, _, spring) in _chips)
        {
            double t = cid == id ? 1 : 0;
            if (animate) spring.SetTarget(t, Motion.Pop); else spring.Snap(t);
        }
    }
}

/// <summary>
/// Custom colour: a saturation / value plane over the hue, a hue strip, a hex box and a row of recent
/// colours. Raises <see cref="ColorChanged"/> while dragging (final = false) and on release / Enter.
/// </summary>
internal sealed class ColorPicker : StackPanel
{
    private const double PlaneW = 220, PlaneH = 110, HueH = 12;
    private readonly SolidColorBrush _hueBase = new(Colors.Red);
    private readonly Grid _plane = new(), _hue = new();
    private readonly Ellipse _planeThumb = new(), _hueThumb = new();
    private readonly TextBox _hex = new();
    private readonly StackPanel _recent = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Border _swatch = new();
    private double _h, _s = 1, _v = 1;
    private bool _dragPlane, _dragHue;

    public ColorPicker()
    {
        Spacing = Theme.S2;
        Tag = SettingsWindow.NoDragTag;
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S3 };
        // ---- SV plane ----
        _plane.Width = PlaneW; _plane.Height = PlaneH;
        _plane.CornerRadius = Theme.RadiusSmall;
        _plane.Children.Add(new Border { Background = _hueBase });
        var white = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        white.GradientStops.Add(new GradientStop { Offset = 0, Color = Colors.White });
        white.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(0, 255, 255, 255) });
        _plane.Children.Add(new Border { Background = white });
        var black = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        black.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0, 0, 0, 0) });
        black.GradientStops.Add(new GradientStop { Offset = 1, Color = Colors.Black });
        _plane.Children.Add(new Border { Background = black });
        _planeThumb.Width = _planeThumb.Height = 14;
        _planeThumb.Stroke = new SolidColorBrush(Colors.White);
        _planeThumb.StrokeThickness = 2;
        _planeThumb.HorizontalAlignment = HorizontalAlignment.Left;
        _planeThumb.VerticalAlignment = VerticalAlignment.Top;
        _planeThumb.IsHitTestVisible = false;
        _plane.Children.Add(_planeThumb);
        _plane.PointerPressed += (_, e) => { _dragPlane = _plane.CapturePointer(e.Pointer); PlaneAt(e); e.Handled = true; };
        _plane.PointerMoved += (_, e) => { if (_dragPlane) PlaneAt(e); };
        _plane.PointerReleased += (_, e) => { if (_dragPlane) { _dragPlane = false; _plane.ReleasePointerCapture(e.Pointer); Raise(true); } };
        _plane.ProtectedCursorSet(InputSystemCursorShape.Cross);
        top.Children.Add(_plane);

        // ---- Swatch + hex ----
        var side = new StackPanel { Spacing = Theme.S2, VerticalAlignment = VerticalAlignment.Top };
        _swatch.Width = 64; _swatch.Height = 40; _swatch.CornerRadius = Theme.RadiusSmall;
        _swatch.BorderBrush = Theme.CardBorderBrush; _swatch.BorderThickness = new Thickness(1);
        side.Children.Add(_swatch);
        _hex.Width = 92; _hex.FontFamily = Theme.MonoFont; _hex.FontSize = Theme.FontBody; _hex.MaxLength = 7;
        _hex.PlaceholderText = "#RRGGBB";
        InputStyle.Apply(_hex);
        _hex.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { FromHex(); e.Handled = true; } };
        _hex.LostFocus += (_, _) => FromHex();
        side.Children.Add(_hex);
        top.Children.Add(side);
        Children.Add(top);

        // ---- Hue strip ----
        _hue.Width = PlaneW; _hue.Height = HueH; _hue.HorizontalAlignment = HorizontalAlignment.Left;
        _hue.CornerRadius = new CornerRadius(HueH / 2);
        var hueBrush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        for (int i = 0; i <= 6; i++) hueBrush.GradientStops.Add(new GradientStop { Offset = i / 6.0, Color = ColorUtil.FromHsv(i * 60, 1, 1) });
        _hue.Children.Add(new Border { Background = hueBrush });
        _hueThumb.Width = _hueThumb.Height = HueH + 4;
        _hueThumb.Stroke = new SolidColorBrush(Colors.White); _hueThumb.StrokeThickness = 2;
        _hueThumb.HorizontalAlignment = HorizontalAlignment.Left; _hueThumb.VerticalAlignment = VerticalAlignment.Center;
        _hueThumb.Margin = new Thickness(0, -2, 0, -2);
        _hueThumb.IsHitTestVisible = false;
        _hue.Children.Add(_hueThumb);
        _hue.PointerPressed += (_, e) => { _dragHue = _hue.CapturePointer(e.Pointer); HueAt(e); e.Handled = true; };
        _hue.PointerMoved += (_, e) => { if (_dragHue) HueAt(e); };
        _hue.PointerReleased += (_, e) => { if (_dragHue) { _dragHue = false; _hue.ReleasePointerCapture(e.Pointer); Raise(true); } };
        Children.Add(_hue);

        var recentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S2 };
        recentRow.Children.Add(new TextBlock { Text = "Recent", Style = SettingsUi.StyleOf("CaptionText"), VerticalAlignment = VerticalAlignment.Center });
        recentRow.Children.Add(_recent);
        Children.Add(recentRow);
    }

    public event Action<Color, bool>? ColorChanged;

    public void SetColor(Color c)
    {
        (_h, _s, _v) = ColorUtil.ToHsv(c);
        Sync(updateHex: true);
    }

    public void SetRecent(IEnumerable<string> hexes)
    {
        _recent.Children.Clear();
        foreach (var hex in hexes)
        {
            if (!ColorUtil.TryParse(hex, out var c)) continue;
            var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(c), BorderBrush = Theme.CardBorderBrush, BorderThickness = new Thickness(1) };
            ToolTipService.SetToolTip(dot, hex);
            dot.Tapped += (_, e) => { e.Handled = true; SetColor(c); Raise(true); };
            _recent.Children.Add(dot);
        }
        if (_recent.Children.Count == 0) _recent.Children.Add(new TextBlock { Text = "None yet", Style = SettingsUi.StyleOf("CaptionText") });
    }

    private void PlaneAt(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_plane).Position;
        _s = Math.Clamp(p.X / PlaneW, 0, 1);
        _v = 1 - Math.Clamp(p.Y / PlaneH, 0, 1);
        Sync(updateHex: true);
        Raise(false);
    }

    private void HueAt(PointerRoutedEventArgs e)
    {
        _h = Math.Clamp(e.GetCurrentPoint(_hue).Position.X / PlaneW, 0, 1) * 359.9;
        Sync(updateHex: true);
        Raise(false);
    }

    private void FromHex()
    {
        if (!ColorUtil.TryParse(_hex.Text, out var c)) { Sync(updateHex: true); return; }
        SetColor(c);
        Raise(true);
    }

    private Color Current => ColorUtil.FromHsv(_h, _s, _v);

    private void Sync(bool updateHex)
    {
        _hueBase.Color = ColorUtil.FromHsv(_h, 1, 1);
        _swatch.Background = new SolidColorBrush(Current);
        _planeThumb.Margin = new Thickness(_s * PlaneW - 7, (1 - _v) * PlaneH - 7, 0, 0);
        _hueThumb.Margin = new Thickness(_h / 360 * PlaneW - (HueH + 4) / 2, -2, 0, -2);
        if (updateHex) _hex.Text = ColorUtil.ToHex(Current);
    }

    private void Raise(bool final) => ColorChanged?.Invoke(Current, final);
}

internal static class CursorExtensions
{
    /// <summary>Sets a system cursor on any element (ProtectedCursor is protected, so via reflection-free subclass trick: UIElement exposes it to derived types only).</summary>
    public static void ProtectedCursorSet(this UIElement el, InputSystemCursorShape shape)
    {
        try
        {
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(el, InputSystemCursor.Create(shape));
        }
        catch { }
    }
}
