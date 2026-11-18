using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>
/// A popup card that springs open from the element that opened it (scale + fade from its centre),
/// over a dimmed backdrop, inside the area its host gives it (it never leaves the notch). Deep black,
/// a 1px 8% white border, the small radius and a soft shadow. Header: icon tile · title + caption ·
/// optional on/off switch · close button. Body and footer are the owner's. Closes on Esc, a click on
/// the backdrop or the close button, with a quicker reverse spring.
/// The popup is built once and stays laid out while closed (fully transparent, so the compositor skips
/// it, and not hit-testable), so opening never realises or measures a visual tree: Open only retargets
/// one compositor spring. Everything that moves is transform / opacity on the compositor, and every
/// spring re-targets from where it is, so open ⇄ close can be interrupted at any point.
/// </summary>
public sealed class Popover : Grid
{
    private readonly Border _dim = new(), _shadowHost = new();
    private readonly Grid _card = new(), _header = new();
    private readonly VectorIcon _icon = new();
    private readonly TextBlock _title = new(), _caption = new();
    private readonly CompSpring _open;
    private SpriteVisual? _caster;

    public Popover(string icon, string title)
    {
        IsHitTestVisible = false; // closed: transparent and click-through, but laid out and ready
        _dim.Background = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
        _dim.CornerRadius = Theme.RadiusSmall;
        _dim.Tapped += (_, e) => { e.Handled = true; Close(); };
        Children.Add(_dim);
        Children.Add(_shadowHost);

        _card.Background = Theme.NotchBrush;
        _card.BorderBrush = Theme.HighlightBorderBrush;
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = Theme.RadiusSmall;
        _card.Padding = Theme.PopoverPadding;
        _card.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var h in new[] { GridLength.Auto, new GridLength(Theme.S2), new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _card.RowDefinitions.Add(new RowDefinition { Height = h });
        Children.Add(_card);

        // ---- Header ----
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var tile = new Border
        {
            Width = Theme.PopoverIconTile, Height = Theme.PopoverIconTile, CornerRadius = Theme.RadiusSmall, VerticalAlignment = VerticalAlignment.Center,
            Background = Theme.TileBrush, BorderBrush = Theme.TileBorderBrush, BorderThickness = new Thickness(1),
        };
        _icon.Data = icon;
        _icon.Size = Theme.IconSmall;
        _icon.Foreground = Theme.PrimaryText;
        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        tile.Child = _icon;
        _header.Children.Add(tile);
        var text = new StackPanel { Margin = Theme.GapLeftS2, VerticalAlignment = VerticalAlignment.Center };
        _title.Text = title;
        _title.Style = (Style)Application.Current.Resources["BaseText"];
        _title.FontFamily = Theme.FontBold;
        _title.FontWeight = FontWeights.Bold;
        _caption.Style = (Style)Application.Current.Resources["CaptionText"];
        _caption.Foreground = Theme.SecondaryText;
        text.Children.Add(_title);
        text.Children.Add(_caption);
        Grid.SetColumn(text, 1);
        _header.Children.Add(text);
        Switch = new PillSwitch { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS2, IsTabStop = false, Visibility = Visibility.Collapsed };
        Grid.SetColumn(Switch, 2);
        _header.Children.Add(Switch);
        var close = new IconButton(24, Theme.IconSmall - 2) { Icon = Icons.Close, Foreground = Theme.SecondaryText, Tooltip = "Close (Esc)", Margin = Theme.GapLeftS1, VerticalAlignment = VerticalAlignment.Center };
        close.Click += Close;
        Grid.SetColumn(close, 3);
        _header.Children.Add(close);
        _card.Children.Add(_header);

        Body = new Grid();
        Grid.SetRow(Body, 2);
        _card.Children.Add(Body);
        Footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S2, Margin = Theme.GapTopS2 };
        Grid.SetRow(Footer, 3);
        _card.Children.Add(Footer);

        // ---- Motion ----
        var cv = Anim.VisualOf(_card);
        var c = cv.Compositor;
        var (open, set) = Motions.Value(c, Motion.PopoverOpen, 0);
        _open = open;
        string s = $"({Anim.F(Motion.PopoverStartScale)} + (1 - {Anim.F(Motion.PopoverStartScale)}) * p.v)";
        cv.StartAnimation("Scale", Anim.Expr(c, $"Vector3({s}, {s}, 1)", ("p", set)));
        cv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v * 1.8, 0, 1)", ("p", set)));
        Anim.VisualOf(_dim).StartAnimation("Opacity", Anim.Expr(c, $"Clamp(p.v, 0, 1) * {Anim.F(Theme.PopoverDimOpacity)}", ("p", set)));
        // The shadow (a Win2D-rendered surface) is built off the start-up path: a moment after load, or on first open.
        _shadowSet = set;
        Loaded += (_, _) => DispatcherQueue.GetForCurrentThread().CreateTimer().Also(t => { t.IsRepeating = false; t.Interval = TimeSpan.FromSeconds(2.5); t.Tick += (_, _) => { t.Stop(); EnsureShadow(); }; t.Start(); });
        _card.SizeChanged += (_, e) =>
        {
            var size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            if (_caster != null) SoftShadow.Layout(_caster, size);
            _shadowHost.Width = e.NewSize.Width;
        };
        KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape && IsOpen) { Close(); e.Handled = true; } };
    }

    public PillSwitch Switch { get; }
    public Grid Body { get; }
    public StackPanel Footer { get; }
    public bool IsOpen { get; private set; }
    public string Caption { set => _caption.Text = value; }
    /// <summary>The card covers this much of the host from the right (the rest is dimmed backdrop).</summary>
    public double CardWidth { set => _card.Width = value; }

    public event Action? Opened, Closed;

    /// <summary>Springs open from <paramref name="from"/> (its centre becomes the scale origin).</summary>
    public void Open(FrameworkElement? from)
    {
        if (IsOpen) return;
        IsOpen = true;
        Services.PerfLog.Mark("Open() entered");
        IsHitTestVisible = true;
        EnsureShadow();
        var cv = Anim.VisualOf(_card);
        // Only re-aim the origin when the popup is (nearly) closed, so re-opening mid-close doesn't jump.
        if (from != null && _open.Value < 0.05)
        {
            try
            {
                var p = from.TransformToVisual(_card).TransformPoint(new Windows.Foundation.Point(from.ActualWidth / 2, from.ActualHeight / 2));
                cv.CenterPoint = new Vector3((float)p.X, (float)p.Y, 0);
            }
            catch { cv.CenterPoint = new Vector3((float)_card.ActualWidth / 2, 0, 0); }
        }
        _open.SetTarget(1, Motion.PopoverOpen);
        Services.PerfLog.Time("Opened handlers (sync part)", () => Opened?.Invoke());
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        IsHitTestVisible = false;
        _open.SetTarget(0, Motion.PopoverClose);
        Closed?.Invoke();
    }

    /// <summary>Plays the row entrance on the first <see cref="Motion.PopRowStaggerMax"/> rows, <see cref="Motion.PopRowStaggerMs"/> apart.</summary>
    public static void Stagger(IEnumerable<PopRow> rows, double startDelay = 0)
    {
        if (!Anim.Enabled) return;
        int k = 0;
        foreach (var r in rows)
        {
            r.Enter(startDelay + Math.Min(k, Motion.PopRowStaggerMax) * Motion.PopRowStaggerMs / 1000.0 * Motion.StaggerScale);
            k++;
        }
    }

    /// <summary>True when <paramref name="e"/> is inside the card (clicks elsewhere close it).</summary>
    public bool Contains(DependencyObject? d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d)) if (ReferenceEquals(d, _card)) return true;
        return false;
    }

    private CompositionPropertySet _shadowSet = null!;

    private void EnsureShadow()
    {
        if (_caster != null) return;
        BuildShadow(Anim.VisualOf(_card).Compositor, _shadowSet);
        if (_card.ActualWidth > 0) { SoftShadow.Layout(_caster!, new Vector2((float)_card.ActualWidth, (float)_card.ActualHeight)); _shadowHost.Width = _card.ActualWidth; }
    }

    private void BuildShadow(Compositor c, CompositionPropertySet set)
    {
        // Pre-rendered soft shadow (SoftShadow): the old drop shadow was masked by a live surface of the
        // card, re-rendered whenever anything in it changed (spinner frames, rows, hovers). Its host is
        // aligned with the card (it used to stretch across the page, casting from the wrong place).
        _shadowHost.HorizontalAlignment = HorizontalAlignment.Right;
        _shadowHost.IsHitTestVisible = false;
        _caster = SoftShadow.Create(c, Theme.CornerRadiusSmall, Theme.PopoverShadowBlur, 1, Theme.PopoverShadowOffsetY);
        _caster.StartAnimation("Opacity", Anim.Expr(c, $"Clamp(p.v, 0, 1) * {Anim.F(Theme.PopoverShadowOpacity)}", ("p", set)));
        ElementCompositionPreview.SetElementChildVisual(_shadowHost, _caster);
        // The shadow follows the card's open motion.
        var sv = ElementCompositionPreview.GetElementVisual(_shadowHost);
        sv.StartAnimation("Scale", Anim.Expr(c, "card.Scale", ("card", Anim.VisualOf(_card))));
        sv.StartAnimation("CenterPoint", Anim.Expr(c, "card.CenterPoint", ("card", Anim.VisualOf(_card))));
    }
}

/// <summary>A small indeterminate spinner (an arc rotating on the compositor; still when animations are off).</summary>
public sealed class Spinner : Grid
{
    public Spinner(double size = 14, Brush? brush = null)
    {
        Width = Height = size;
        IsHitTestVisible = false;
        var icon = new VectorIcon { Data = "M21 12 a9 9 0 1 1 -6.22 -8.56", Size = size, Foreground = brush ?? Theme.SecondaryText };
        Children.Add(icon);
        _v = Anim.VisualOf(icon);
        _v.CenterPoint = new Vector3((float)size / 2, (float)size / 2, 0);
        // A looping animation keeps the compositor (and XAML) waking every frame, so it runs only while the
        // spinner is in the tree AND visible (it used to start on Loaded even while collapsed).
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Update());
    }

    private readonly Visual _v;
    private bool _spinning;

    private void Update()
    {
        bool on = IsLoaded && Visibility == Visibility.Visible && Anim.Enabled;
        if (on == _spinning) return;
        if (!on) { Stop(); return; }
        _spinning = true;
        var a = _v.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, 0);
        a.InsertKeyFrame(1, 360, _v.Compositor.CreateLinearEasingFunction());
        a.Duration = TimeSpan.FromSeconds(Motion.SpinnerPeriod);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        _v.StartAnimation("RotationAngleInDegrees", a);
    }

    private void Stop()
    {
        _spinning = false;
        _v.StopAnimation("RotationAngleInDegrees");
    }
}

/// <summary>
/// One row in a quick-action popup (~44 px, small radius, hover fill): icon · bold name + caption · a
/// status slot on the right (spinner, check, a pill, a battery ring…), plus an optional area that expands
/// underneath (the Wi-Fi password). <see cref="Shake"/> plays the failure shake.
/// </summary>
public sealed class PopRow : Grid
{
    private readonly Border _fill = new(), _active = new();
    private readonly VectorIcon _icon = new();
    private readonly TextBlock _name = new(), _caption = new();
    private readonly CompSpring _shake;

    public PopRow()
    {
        Background = Theme.TransparentBrush;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.PopRowHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _active.Background = Theme.AccentSubtleBrush; // the connected row: a faint accent tint
        _active.CornerRadius = Theme.RadiusSmall;
        _active.Visibility = Visibility.Collapsed;
        SetRowSpan(_active, 2);
        Children.Add(_active);
        _fill.Background = Theme.CardHoverBrush;
        _fill.CornerRadius = Theme.RadiusSmall;
        SetRowSpan(_fill, 2);
        Children.Add(_fill);
        // The hover spring is created the first time the pointer enters (rows are built in bursts; most are never hovered).
        _fill.Opacity = 0;
        PointerEntered += OnFirstHover;

        var line = new Grid { Margin = new Thickness(Theme.S2, 0, Theme.S2, 0) };
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        _icon.Size = Theme.IconMedium - 2;
        _icon.Foreground = Theme.PrimaryText;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(_icon);
        var text = new StackPanel { Margin = Theme.GapLeftS3, VerticalAlignment = VerticalAlignment.Center };
        _name.Style = (Style)Application.Current.Resources["BaseText"];
        _name.FontFamily = Theme.FontBold;
        _name.FontWeight = FontWeights.Bold;
        _caption.Style = (Style)Application.Current.Resources["CaptionText"];
        _caption.Foreground = Theme.SecondaryText;
        text.Children.Add(_name);
        text.Children.Add(_caption);
        Grid.SetColumn(text, 1);
        line.Children.Add(text);
        Status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S2, VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS2 };
        Grid.SetColumn(Status, 2);
        line.Children.Add(Status);
        Children.Add(line);
        Expander = new Grid { Margin = new Thickness(Theme.S2, 0, Theme.S2, Theme.S2), Visibility = Visibility.Collapsed };
        SetRow(Expander, 1);
        Children.Add(Expander);

        // One property set drives both the failure shake (x) and the entrance (fade + rise), so neither
        // ever replaces the other's animation and an entrance costs one spring retarget, not new animations.
        var v = Anim.VisualOf(this, translation: true);
        var c = v.Compositor;
        var set = c.CreatePropertySet();
        _shake = new CompSpring(set, "s", Motion.Shake, 0, precision: 20);
        _enter = new CompSpring(set, "e", Motion.PopRowEnter, 1);
        var t = Anim.Expr(c, "Vector3(p.s, (1 - p.e) * R, 0)", ("p", set));
        t.SetScalarParameter("R", (float)Motion.PopRowRise);
        v.StartAnimation("Translation", t);
        v.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.e, 0, 1)", ("p", set)));
    }

    private readonly CompSpring _enter;

    /// <summary>Fades and rises the row into place after <paramref name="delay"/> seconds.</summary>
    public void Enter(double delay = 0)
    {
        if (!Anim.Enabled) return;
        _enter.Snap(0);
        _enter.SetTarget(1, Motion.PopRowEnter, delay);
    }

    public StackPanel Status { get; }
    public Grid Expander { get; }
    public string Icon { set => _icon.Data = value; }
    public string Title { set => _name.Text = value; }
    public string Caption { set => _caption.Text = value; }
    public Brush CaptionBrush { set => _caption.Foreground = value; }
    public bool IsActive { set => _active.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    /// <summary>A row action failed somewhere (for the error sound).</summary>
    public static event Action? AnyFailed;

    public void Shake()
    {
        AnyFailed?.Invoke();
        if (!Anim.Enabled) return;
        _shake.Snap(0);
        _shake.Impulse(Motion.ShakeImpulse);
    }

    /// <summary>Replaces the status slot with one element (or nothing).</summary>
    public void SetStatus(params UIElement[] items)
    {
        Status.Children.Clear();
        foreach (var i in items) Status.Children.Add(i);
    }

    /// <summary>A check that springs in (success).</summary>
    public static UIElement Check()
    {
        var check = new VectorIcon { Data = Icons.Check, Size = Theme.IconSmall, Foreground = Theme.SuccessBrush, VerticalAlignment = VerticalAlignment.Center };
        var v = Anim.VisualOf(check);
        v.CenterPoint = new Vector3((float)Theme.IconSmall / 2, (float)Theme.IconSmall / 2, 0);
        Anim.SpringTo(v, "Scale.X", 0.3, 1, Motion.Pop);
        Anim.SpringTo(v, "Scale.Y", 0.3, 1, Motion.Pop);
        return check;
    }

    /// <summary>A small pill button for a row action ("Disconnect", "Pair", "Connect").</summary>
    public static PillButton Action(string label, bool primary = false) => new(null, label, quiet: !primary) { Height = Theme.HubSmallPillHeight, VerticalAlignment = VerticalAlignment.Center };

    private void OnFirstHover(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        PointerEntered -= OnFirstHover;
        _fill.Opacity = 1;
        Motions.HoverFade(this, _fill).SetTarget(1, Motion.Hover); // already over it: fade in now
    }

    /// <summary>A light "Pair"-style chip for rows built in bulk: a rounded label, no springs (it only needs a tap).</summary>
    public static FrameworkElement Chip(string label, Action click)
    {
        var chip = new Border
        {
            Height = Theme.HubSmallPillHeight, CornerRadius = new CornerRadius(Theme.HubSmallPillHeight / 2), Padding = new Thickness(12, 0, 12, 0),
            Background = Theme.PillButtonBrush, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = label, FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold, FontSize = Theme.FontCaption, Foreground = Theme.PillButtonTextBrush, VerticalAlignment = VerticalAlignment.Center },
        };
        chip.Tapped += (_, e) => { e.Handled = true; click(); };
        return chip;
    }
}

/// <summary>
/// Placeholder rows for a popup's first-ever open (nothing cached yet): an icon dot and two bars per row,
/// pulsing in opacity only, on the compositor (stopped while hidden).
/// </summary>
public sealed class SkeletonRows : StackPanel
{
    public SkeletonRows(int count = 4)
    {
        Spacing = 2;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        for (int i = 0; i < count; i++)
        {
            var row = new Grid { Height = Theme.PopRowHeight, Padding = new Thickness(Theme.S2, 0, Theme.S2, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Ellipse { Width = Theme.IconMedium, Height = Theme.IconMedium, Fill = Theme.CardHoverBrush, VerticalAlignment = VerticalAlignment.Center });
            var bars = new StackPanel { Margin = Theme.GapLeftS3, VerticalAlignment = VerticalAlignment.Center, Spacing = 6 };
            bars.Children.Add(new Border { Height = 9, Width = 110 - i * 14, CornerRadius = new CornerRadius(4.5), Background = Theme.CardHoverBrush, HorizontalAlignment = HorizontalAlignment.Left });
            bars.Children.Add(new Border { Height = 7, Width = 64, CornerRadius = new CornerRadius(3.5), Background = Theme.CardHoverBrush, HorizontalAlignment = HorizontalAlignment.Left });
            Grid.SetColumn(bars, 1);
            row.Children.Add(bars);
            Children.Add(row);
        }
    }

    public void Show(bool on)
    {
        var v = Anim.VisualOf(this);
        if (on == (Visibility == Visibility.Visible)) return;
        Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on || !Anim.Enabled) { v.StopAnimation("Opacity"); v.Opacity = 1; return; }
        var a = v.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, 0.45f);
        a.InsertKeyFrame(0.5f, 1f);
        a.InsertKeyFrame(1, 0.45f);
        a.Duration = TimeSpan.FromSeconds(Motion.SkeletonPulsePeriod);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Opacity", a);
    }
}
