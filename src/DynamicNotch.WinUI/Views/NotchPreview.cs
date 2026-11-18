using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// The Customisation tab's live preview: a miniature of the real notch, made from the same tokens and
/// controls (accent brushes, radius tokens, LevelTrack, PillSwitch, BatteryRing, the motion springs), in
/// three modes: Collapsed, Expanded (a music-page sample at half size) and Pill (a volume pill).
/// The body is a compositor rounded rectangle whose width, height, corner radius and content scale are
/// springs, so a mode switch, a radius change and a Look all morph. Accent changes ripple across it,
/// a motion change replays expand / collapse with the new feel, sounds pulse a row of equaliser bars, and
/// the border highlight / shadow / glow follow their sliders. Everything is transform / opacity / clip.
/// </summary>
internal sealed class NotchPreview : Grid
{
    public const double PreviewHeight = 150;
    private const double ExpandedScale = 0.46;
    private readonly ThemeService _theme;
    private readonly Grid _stage = new(), _content = new();
    private readonly Grid _collapsed = new(), _expanded = new(), _pill = new();
    private readonly Ellipse _ripple = new(), _glow = new();
    private readonly StackPanel _eq = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
    private readonly Segmented _modes;
    private readonly CompositionPropertySet _p;
    private readonly CompSpring _w, _h, _r, _s, _fadeC, _fadeE, _fadeP;
    private readonly CompositionRoundedRectangleGeometry _body;
    private readonly CompositionSpriteShape _outline;
    private readonly DropShadow _shadow;
    private readonly CompositionVisualSurface _surface;
    private readonly SpriteVisual _caster;
    private readonly CompositionRadialGradientBrush _glowBrush;
    private string _mode = "expanded";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _demo;

    public NotchPreview(ThemeService theme, SoundService sounds)
    {
        _theme = theme;
        Height = PreviewHeight;
        CornerRadius = Theme.Radius;
        Background = new SolidColorBrush(Color.FromArgb(255, 0x0B, 0x0B, 0x0E));
        BorderBrush = Theme.CardBorderBrush;
        BorderThickness = new Thickness(1);

        // "Screen" behind the notch: a faint wallpaper-like gradient so the black notch reads.
        var wall = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        wall.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 0x1B, 0x1D, 0x2A) });
        wall.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(255, 0x12, 0x14, 0x1C) });
        Children.Add(new Border { Background = wall, CornerRadius = Theme.Radius, IsHitTestVisible = false });

        _stage.IsHitTestVisible = false;
        Children.Add(_stage);
        _content.IsHitTestVisible = false;
        _content.HorizontalAlignment = HorizontalAlignment.Center;
        _content.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_content);

        // ---- Compositor body ----
        var sv = ElementCompositionPreview.GetElementVisual(_stage);
        var c = sv.Compositor;
        _p = c.CreatePropertySet();
        _p.InsertScalar("HW", 0);
        _w = new CompSpring(_p, "W", Motion.Notch, Theme.ExpandedWidth * ExpandedScale, precision: 50);
        _h = new CompSpring(_p, "H", Motion.Notch, Theme.ExpandedHeight * ExpandedScale, precision: 50);
        _r = new CompSpring(_p, "R", Motion.Notch, Theme.CornerRadius, precision: 20);
        _s = new CompSpring(_p, "S", Motion.Notch, ExpandedScale);
        _body = c.CreateRoundedRectangleGeometry();
        Bind(_body, "Size", "Vector2(p.W, p.H + p.R * p.S)");
        Bind(_body, "Offset", "Vector2((p.HW - p.W) / 2, -p.R * p.S)");
        Bind(_body, "CornerRadius", "Vector2(Min(p.R * p.S, p.H / 2), Min(p.R * p.S, p.H / 2))");
        var shapes = c.CreateShapeVisual();

        var fill = c.CreateSpriteShape(_body);
        fill.FillBrush = c.CreateColorBrush(Theme.NotchColor);
        shapes.Shapes.Add(fill);
        _outline = c.CreateSpriteShape(_body);
        _outline.StrokeThickness = 1.5f;
        _outline.StrokeBrush = c.CreateColorBrush(Theme.HighlightColor);
        shapes.Shapes.Add(_outline);
        // Shadow: a sprite whose drop-shadow mask is a live surface of the shapes (as the real notch does;
        // a LayerVisual shadow mis-positions inside XAML).
        var root = c.CreateContainerVisual();
        root.RelativeSizeAdjustment = Vector2.One;
        _surface = c.CreateVisualSurface();
        _surface.SourceVisual = shapes;
        _shadow = c.CreateDropShadow();
        _shadow.BlurRadius = 14;
        _shadow.Offset = new Vector3(0, 4, 0);
        _shadow.Color = Color.FromArgb(255, 0, 0, 0);
        _shadow.Opacity = (float)Theme.ShadowOpacity;
        _shadow.Mask = c.CreateSurfaceBrush(_surface);
        _caster = c.CreateSpriteVisual();
        _caster.Shadow = _shadow;
        root.Children.InsertAtTop(_caster);
        root.Children.InsertAtTop(shapes);
        ElementCompositionPreview.SetElementChildVisual(_stage, root);
        _stage.SizeChanged += (_, e) =>
        {
            var size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            _caster.Size = size;
            _surface.SourceSize = size;
            shapes.Size = size;
            _p.InsertScalar("HW", size.X);
        };
        // Content is clipped to the body.
        var cv = Anim.VisualOf(_content);
        cv.Clip = c.CreateGeometricClip(_body);
        _content.SizeChanged += (_, e) => { };

        // ---- Glow (expanded, behind the art) ----
        _glowBrush = c.CreateRadialGradientBrush();
        _glowBrush.ColorStops.Add(c.CreateColorGradientStop(0, Theme.AccentColor));
        _glowBrush.ColorStops.Add(c.CreateColorGradientStop(1, Color.FromArgb(0, Theme.AccentColor.R, Theme.AccentColor.G, Theme.AccentColor.B)));

        BuildCollapsed();
        BuildExpanded();
        BuildPill();
        foreach (var g in new[] { _collapsed, _expanded, _pill }) _content.Children.Add(g);
        _fadeC = Fade(_collapsed, 0);
        _fadeE = Fade(_expanded, 1);
        _fadeP = Fade(_pill, 0);

        // Accent ripple over the whole preview.
        _ripple.Width = _ripple.Height = 60;
        _ripple.Fill = Theme.AccentBrush;
        _ripple.Opacity = 0;
        _ripple.IsHitTestVisible = false;
        _ripple.HorizontalAlignment = HorizontalAlignment.Center;
        _ripple.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_ripple);

        // Equaliser bars (bottom-right) while a sound plays.
        for (int i = 0; i < 5; i++) _eq.Children.Add(new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = Theme.AccentBrush, VerticalAlignment = VerticalAlignment.Bottom });
        _eq.HorizontalAlignment = HorizontalAlignment.Right;
        _eq.VerticalAlignment = VerticalAlignment.Bottom;
        _eq.Margin = new Thickness(0, 0, Theme.S4, Theme.S3);
        _eq.Opacity = 0;
        Children.Add(_eq);

        _modes = new Segmented(("collapsed", "Collapsed"), ("expanded", "Expanded"), ("pill", "Pill")) { Width = 240 };
        _modes.Value = _mode;
        _modes.Selected += m => SetMode(m, animate: true);



        Loaded += (_, _) => { _content.Width = ActualWidth; ApplySurface(); };
        SizeChanged += (_, e) => _content.Width = e.NewSize.Width;
        Theme.AccentChanged += OnAccent;
        Unloaded += (_, _) => Theme.AccentChanged -= OnAccent;
        theme.Changed += OnTheme;
        theme.Demo += OnDemo;
        sounds.Played += (_, ms) => PulseEq(ms);
        SetMode(_mode, animate: false);
    }

    private void Bind(CompositionObject target, string property, string expression)
    {
        var e = _p.Compositor.CreateExpressionAnimation(expression);
        e.SetReferenceParameter("p", _p);
        target.StartAnimation(property, e);
    }

    private CompSpring Fade(UIElement el, double initial)
    {
        var v = Anim.VisualOf(el, translation: true);
        var set = v.Compositor.CreatePropertySet();
        var spring = new CompSpring(set, "v", Motion.FadeIn, initial);
        v.StartAnimation("Opacity", Anim.Expr(v.Compositor, "Clamp(f.v, 0, 1)", ("f", set)));
        v.StartAnimation("Translation", Anim.Expr(v.Compositor, "Vector3(0, (1 - Clamp(f.v, 0, 1)) * -6, 0)", ("f", set)));
        return spring;
    }

    // ---- Modes ---------------------------------------------------------------------------------------------
    private (double w, double h, double s) Size(string mode) => mode switch
    {
        "collapsed" => (Theme.CollapsedWidth, Theme.CollapsedHeight, 1),
        "pill" => (Theme.ActivityWidthSlider, Theme.ActivityHeight, 1),
        _ => (Theme.ExpandedWidth * ExpandedScale, Theme.MusicExpandedHeight * ExpandedScale, ExpandedScale),
    };

    /// <summary>The Collapsed / Expanded / Pill switch (placed by the tab, in its title row).</summary>
    public Segmented ModeSwitch => _modes;

    public void SetMode(string mode, bool animate)
    {
        _mode = mode;
        var (w, h, s) = Size(mode);
        if (animate) { _w.SetTarget(w, Motion.Notch); _h.SetTarget(h, Motion.Notch); _s.SetTarget(s, Motion.Notch); }
        else { _w.Snap(w); _h.Snap(h); _s.Snap(s); }
        foreach (var (spring, m) in new[] { (_fadeC, "collapsed"), (_fadeE, "expanded"), (_fadeP, "pill") })
        {
            double t = m == mode ? 1 : 0;
            if (!animate) spring.Snap(t);
            else spring.SetTarget(t, t > 0 ? Motion.FadeIn : Motion.FadeOut, t > 0 ? 0.12 * Motion.StaggerScale : 0);
        }
        _collapsed.IsHitTestVisible = _expanded.IsHitTestVisible = _pill.IsHitTestVisible = false;
    }

    // ---- Reactions -----------------------------------------------------------------------------------------
    private void OnTheme(ThemeService.Part parts)
    {
        if (parts.HasFlag(ThemeService.Part.Radius)) _r.SetTarget(Theme.CornerRadius, Motion.Notch);
        if (parts.HasFlag(ThemeService.Part.Surface) || parts.HasFlag(ThemeService.Part.Glow)) ApplySurface();
        if (parts.HasFlag(ThemeService.Part.Motion)) ReplayMotion();
    }

    private void OnDemo(ThemeService.Part part)
    {
        if (part == ThemeService.Part.Accent) Ripple();
        if (part == ThemeService.Part.Motion) ReplayMotion();
        if (part == ThemeService.Part.Glow && _mode != "expanded") { _modes.Value = "expanded"; SetMode("expanded", true); }
        if (part == ThemeService.Part.Collapsed && _mode != "collapsed") { _modes.Value = "collapsed"; SetMode("collapsed", true); }
    }

    private void OnAccent()
    {
        var a = Theme.AccentColor;
        {
            _glowBrush.ColorStops[0].Color = a;
            _glowBrush.ColorStops[1].Color = Color.FromArgb(0, a.R, a.G, a.B);
        }
    }

    private void ApplySurface()
    {
        ((CompositionColorBrush)_outline.StrokeBrush).Color = Theme.HighlightColor;
        _shadow.Opacity = (float)Math.Min(1, Theme.ShadowOpacity * 1.2);
        var s = _theme.Current;
        _glow.Opacity = s.GlowEnabled ? Math.Min(1, 0.55 * s.GlowIntensity) : 0;
    }

    /// <summary>Plays expand ⇄ collapse with the current motion feel and speed, then returns to the chosen mode.</summary>
    private void ReplayMotion()
    {
        string back = _mode;
        string other = back == "collapsed" ? "expanded" : "collapsed";
        var (w, h, s) = Size(other);
        _w.SetTarget(w, Motion.Notch); _h.SetTarget(h, Motion.Notch); _s.SetTarget(s, Motion.Notch);
        _demo ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _demo.Stop();
        _demo.IsRepeating = false;
        _demo.Interval = TimeSpan.FromMilliseconds(Math.Max(350, _w.Model.SettleDuration() * 1000 * 0.8));
        _demo.Tick += Back;
        _demo.Start();
        void Back(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object o)
        {
            t.Stop();
            t.Tick -= Back;
            SetMode(back, animate: true);
        }
    }

    private void Ripple()
    {
        if (!Anim.Enabled) return;
        var v = Anim.VisualOf(_ripple);
        v.CenterPoint = new Vector3(30, 30, 0);
        var c = v.Compositor;
        float scale = (float)(Math.Max(ActualWidth, ActualHeight) / 60 * 2.2);
        var s = c.CreateVector3KeyFrameAnimation();
        s.InsertKeyFrame(0, new Vector3(0.2f, 0.2f, 1));
        s.InsertKeyFrame(1, new Vector3(scale, scale, 1), c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.7f), new Vector2(0.3f, 1)));
        s.Duration = TimeSpan.FromMilliseconds(650);
        var o = c.CreateScalarKeyFrameAnimation();
        o.InsertKeyFrame(0, 0.32f);
        o.InsertKeyFrame(1, 0);
        o.Duration = s.Duration;
        v.StartAnimation("Scale", s);
        v.StartAnimation("Opacity", o);
    }

    private void PulseEq(int ms)
    {
        if (!Anim.Enabled) return;
        var c = Anim.VisualOf(_eq).Compositor;
        var dur = TimeSpan.FromMilliseconds(Math.Max(260, ms + 120));
        var o = c.CreateScalarKeyFrameAnimation();
        o.InsertKeyFrame(0, 1);
        o.InsertKeyFrame(0.8f, 1);
        o.InsertKeyFrame(1, 0);
        o.Duration = dur;
        Anim.VisualOf(_eq).StartAnimation("Opacity", o);
        var rng = new Random(ms);
        for (int i = 0; i < _eq.Children.Count; i++)
        {
            var b = (FrameworkElement)_eq.Children[i];
            var v = Anim.VisualOf(b);
            v.CenterPoint = new Vector3(1.5f, 16, 0);
            var a = c.CreateVector3KeyFrameAnimation();
            int keys = 6;
            for (int k = 0; k <= keys; k++)
            {
                float y = k == 0 || k == keys ? 0.2f : (float)(0.3 + 0.7 * rng.NextDouble());
                a.InsertKeyFrame((float)k / keys, new Vector3(1, y, 1));
            }
            a.Duration = dur;
            v.StartAnimation("Scale", a);
        }
    }

    // ---- Content (built from the real controls and tokens) ---------------------------------------------------
    private void BuildCollapsed()
    {
        _collapsed.Width = Theme.CollapsedWidth;
        _collapsed.Height = Theme.CollapsedHeight;
        _collapsed.HorizontalAlignment = HorizontalAlignment.Center;
        _collapsed.VerticalAlignment = VerticalAlignment.Top;
        var ring = new BatteryRing { RingSize = Theme.NotchRingSize, Thickness = Theme.NotchRingThickness, Percent = 76, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = Theme.CollapsedPadding };
        _collapsed.Children.Add(ring);
        var time = new TextBlock { Text = ClockService.FormatTime(new DateTime(2026, 1, 1, 9, 41, 0)), Style = SettingsUi.StyleOf("ClockTimeText"), HorizontalAlignment = HorizontalAlignment.Center };
        _collapsed.Children.Add(time);
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = Theme.CollapsedPadding };
        foreach (var hgt in new[] { 6.0, 12, 8, 10 }) bars.Children.Add(new Border { Width = 3, Height = hgt, CornerRadius = new CornerRadius(1.5), Background = Theme.PrimaryText, VerticalAlignment = VerticalAlignment.Center });
        _collapsed.Children.Add(bars);
    }

    private void BuildExpanded()
    {
        // Designed at the real size, drawn at half size.
        var full = new Grid { Width = Theme.ExpandedWidth, Height = Theme.MusicExpandedHeight, Padding = Theme.ExpandedPadding };
        full.RenderTransform = new ScaleTransform { ScaleX = ExpandedScale, ScaleY = ExpandedScale };
        _expanded.Width = Theme.ExpandedWidth * ExpandedScale;
        _expanded.Height = Theme.MusicExpandedHeight * ExpandedScale;
        _expanded.HorizontalAlignment = HorizontalAlignment.Center;
        _expanded.VerticalAlignment = VerticalAlignment.Top;
        _expanded.Children.Add(full);
        full.RowDefinitions.Add(new RowDefinition { Height = Theme.HeaderRow });
        full.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header: clock · page icons (active one in the accent) · battery.
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = "9:41", Style = SettingsUi.StyleOf("HeaderTimeText"), VerticalAlignment = VerticalAlignment.Center });
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var navPill = new Border { Background = new SolidColorBrush(Theme.NavPillColor), CornerRadius = new CornerRadius(Theme.NavPillHeight / 2), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Center, Child = nav };
        foreach (var (icon, active) in new[] { (Icons.Music, true), (Icons.Clipboard, false), (Icons.Sparkle, false), (Icons.Timer, false), (Icons.Dashboard, false) })
            nav.Children.Add(new VectorIcon { Data = icon, Size = Theme.NavIconSize, Foreground = active ? Theme.AccentBrush : new SolidColorBrush(Theme.NavDimColor) });
        header.Children.Add(navPill);
        header.Children.Add(new BatteryRing { RingSize = Theme.HeaderRingSize, Thickness = Theme.HeaderRingThickness, Percent = 76, ShowNumber = true, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        full.Children.Add(header);

        // Body: glow · art · title / artist · seek · controls + a switch and a tile in their accent states.
        var body = new Grid { Margin = Theme.GapTopS3 };
        Grid.SetRow(body, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _glow.Width = 420; _glow.Height = 300;
        _glow.HorizontalAlignment = HorizontalAlignment.Left; _glow.VerticalAlignment = VerticalAlignment.Center;
        _glow.Margin = new Thickness(-130, -80, 0, -80);
        var glowBrush = new RadialGradientBrush();
        glowBrush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0xB0, 0xFF, 0x37, 0x5F) });
        glowBrush.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(0, 0xFF, 0x37, 0x5F) });
        _glow.Fill = glowBrush;
        Theme.AccentChanged += () =>
        {
            var a = Theme.AccentColor;
            glowBrush.GradientStops[0].Color = Color.FromArgb(0xB0, a.R, a.G, a.B);
            glowBrush.GradientStops[1].Color = Color.FromArgb(0, a.R, a.G, a.B);
        };
        body.Children.Add(_glow);
        var art = new Border { Width = Theme.AlbumArtSize, Height = Theme.AlbumArtSize, CornerRadius = Theme.Radius, VerticalAlignment = VerticalAlignment.Top };
        var artBrush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        artBrush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 0x3A, 0x2A, 0x5C) });
        artBrush.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(255, 0x12, 0x3C, 0x58) });
        art.Background = artBrush;
        art.Child = new VectorIcon { Data = Icons.Music, Size = Theme.IconHero * 1.6, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(art);
        var right = new StackPanel { Margin = new Thickness(Theme.S5, Theme.S1, 0, 0), Spacing = Theme.S1 };
        Grid.SetColumn(right, 1);
        right.Children.Add(new TextBlock { Text = "Midnight Drive", FontFamily = Theme.FontBold, FontWeight = FontWeights.Bold, FontSize = 22, Foreground = Theme.PrimaryText });
        right.Children.Add(new TextBlock { Text = "The Night Shift", FontFamily = Theme.Font, FontSize = 16, Foreground = Theme.SecondaryText });
        var seek = new LevelTrack(Theme.SeekThickness) { Margin = new Thickness(0, Theme.S4, 0, 0) };
        seek.FillBrush = Theme.AccentBrush;
        seek.SetValue(0.42, animate: false);
        right.Children.Add(seek);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S5, HorizontalAlignment = HorizontalAlignment.Center, Margin = Theme.GapTopS2 };
        controls.Children.Add(new VectorIcon { Data = Icons.Prev, Size = 26, Foreground = Theme.PrimaryText, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(new Border { Width = Theme.PlayButtonSize, Height = Theme.PlayButtonSize, CornerRadius = new CornerRadius(Theme.PlayButtonSize / 2), Background = Theme.PrimaryText, Child = new VectorIcon { Data = Icons.Pause, Size = 24, Foreground = Theme.PillButtonTextBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        controls.Children.Add(new VectorIcon { Data = Icons.Next, Size = 26, Foreground = Theme.PrimaryText, VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(controls);
        var states = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S3, Margin = Theme.GapTopS3, HorizontalAlignment = HorizontalAlignment.Center };
        states.Children.Add(new PillSwitch { IsOn = true, IsHitTestVisible = false, IsTabStop = false });
        var tile = new Border { Width = 70, Height = 34, CornerRadius = Theme.RadiusSmall, Background = Theme.AccentBrush, Child = new VectorIcon { Data = Icons.Bluetooth, Size = 18, Foreground = Theme.OnAccentBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        states.Children.Add(tile);
        states.Children.Add(new Border { Width = 70, Height = 34, CornerRadius = Theme.RadiusSmall, Background = Theme.TileBrush, BorderBrush = Theme.TileBorderBrush, BorderThickness = new Thickness(1), Child = new VectorIcon { Data = Icons.Wifi, Size = 18, Foreground = Theme.PrimaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        right.Children.Add(states);
        body.Children.Add(right);
        full.Children.Add(body);
    }

    private void BuildPill()
    {
        _pill.Width = Theme.ActivityWidthSlider;
        _pill.Height = Theme.ActivityHeight;
        _pill.HorizontalAlignment = HorizontalAlignment.Center;
        _pill.VerticalAlignment = VerticalAlignment.Top;
        _pill.Padding = Theme.ActivityPadding;
        _pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _pill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Theme.ActivityValueWidth) });
        _pill.Children.Add(new VectorIcon { Data = Icons.VolumeMid, Size = Theme.ActivityIconSize, Foreground = Theme.PrimaryText, VerticalAlignment = VerticalAlignment.Center });
        var track = new LevelTrack { Margin = new Thickness(Theme.S3, 0, Theme.S2, 0) };
        track.FillBrush = Theme.AccentBrush;
        track.SetValue(0.6, animate: false);
        Grid.SetColumn(track, 1);
        _pill.Children.Add(track);
        var value = new TextBlock { Text = "60", Style = SettingsUi.StyleOf("TimeText"), Foreground = Theme.PrimaryText, FontSize = Theme.FontActivityValue, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(value, 2);
        _pill.Children.Add(value);
    }
}
