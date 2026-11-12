using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;

namespace DynamicNotch.Views;

/// <summary>
/// Page indicator that morphs from dots into page icons on hover. The pages (and their icons) come
/// from the <see cref="PageRegistry"/>, so new pages appear automatically. Each dot shrinks and fades
/// while its icon grows (0.5 → 1) and fades in at the same spot, staggered per item; the dark pill
/// behind them springs wider from the centre. The control's own size never changes (it's sized for
/// the icon state), and every frame only changes transforms, opacity and brush colours.
/// The active page is accent-tinted with a soft highlight that follows the page spring.
/// </summary>
public sealed class PageNavigator : Grid
{
    private sealed class Slot
    {
        public required Border Dot;
        public required FrameworkElement Icon;
        public required Border Hit;
        public required SolidColorBrush Tint;
        public readonly TranslateTransform DotMove = new(), IconMove = new();
        public readonly ScaleTransform DotScale = new(), IconScale = new();
        public readonly Spring Morph = new(Motion.NavMorph, 0);
        public readonly Spring Hover = new(Motion.Hover, 1);
    }

    private readonly List<Slot> _slots = new();
    private readonly Rectangle _pillCentre = new();
    private readonly Ellipse _capLeft = new(), _capRight = new();
    private readonly ScaleTransform _centreScale = new();
    private readonly TranslateTransform _leftMove = new(), _rightMove = new();
    private readonly Ellipse _highlight = new();
    private readonly TranslateTransform _highlightMove = new();
    private readonly Spring _pill = new(Motion.NavMorph, 0);
    private readonly DispatcherTimer _leaveTimer;
    private double _position;
    private int _generation;

    public PageNavigator()
    {
        Background = Brushes.Transparent; // the whole (padded) box is the hover target
        Height = Theme.FooterHeight;
        Cursor = Cursors.Hand;
        Focusable = false;

        foreach (var cap in new[] { _capLeft, _capRight })
        {
            cap.Width = cap.Height = Theme.NavPillHeight;
            cap.Fill = Theme.NavPillBrush;
            cap.IsHitTestVisible = false;
            Children.Add(cap);
        }
        _capLeft.RenderTransform = _leftMove;
        _capRight.RenderTransform = _rightMove;
        _pillCentre.Height = Theme.NavPillHeight;
        _pillCentre.Fill = Theme.NavPillBrush;
        _pillCentre.IsHitTestVisible = false;
        _pillCentre.RenderTransformOrigin = new Point(0.5, 0.5);
        _pillCentre.RenderTransform = _centreScale;
        Children.Add(_pillCentre);

        _highlight.Width = _highlight.Height = Theme.NavHighlightSize;
        _highlight.Fill = Theme.NavHighlightBrush;
        _highlight.IsHitTestVisible = false;
        _highlight.RenderTransform = _highlightMove;
        Children.Add(_highlight);

        _pill.ValueChanged += _ => Apply();
        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.NavLeaveDelayMs) };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); MorphTo(0); };
        MouseEnter += (_, _) => { _leaveTimer.Stop(); MorphTo(1); };
        MouseLeave += (_, _) => { _leaveTimer.Stop(); _leaveTimer.Start(); };
    }

    /// <summary>Raised with the enabled-page index when an icon (or dot) is clicked.</summary>
    public event Action<int>? PageClicked;

    /// <summary>Current (fractional) page from the page spring.</summary>
    public double Position
    {
        get => _position;
        set { if (Math.Abs(value - _position) < 0.0005) return; _position = value; Apply(); }
    }

    /// <summary>Rebuild for the enabled pages (in order). Hidden when there's only one.</summary>
    public void SetPages(IReadOnlyList<PageDefinition> pages)
    {
        foreach (var s in _slots) { Children.Remove(s.Dot); Children.Remove(s.Icon); Children.Remove(s.Hit); }
        _slots.Clear();
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            int index = i;
            var tint = new SolidColorBrush(Theme.NavDimColor);
            FrameworkElement icon = page.UseClaudeLogo
                ? new ClaudeLogo { Size = Theme.NavIconSize, Foreground = tint }
                : new VectorIcon { Data = page.Icon, Size = Theme.NavIconSize, Foreground = tint };
            icon.IsHitTestVisible = false;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            var slot = new Slot
            {
                Dot = new Border
                {
                    Width = Theme.NavDot + Theme.NavDotActiveExtra, Height = Theme.NavDot,
                    CornerRadius = new CornerRadius(Theme.NavDot / 2), // a pill: half its height
                    Background = Theme.PrimaryText, IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                },
                Icon = icon,
                Hit = new Border { Width = Theme.NavSlot, Height = Theme.NavPillHeight, Background = Brushes.Transparent, ToolTip = page.Name },
                Tint = tint,
            };
            slot.Dot.RenderTransform = new TransformGroup { Children = { slot.DotScale, slot.DotMove } };
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = new TransformGroup { Children = { slot.IconScale, slot.IconMove } };
            slot.Hit.RenderTransform = new TranslateTransform(SlotCentre(i, pages.Count), 0);
            slot.Hit.MouseEnter += (_, _) => slot.Hover.SetTarget(Motion.NavIconHoverScale, Motion.Hover);
            slot.Hit.MouseLeave += (_, _) => slot.Hover.SetTarget(1, Motion.Hover);
            slot.Hit.MouseLeftButtonUp += (_, e) => { PageClicked?.Invoke(index); e.Handled = true; };
            slot.Morph.ValueChanged += _ => Apply();
            slot.Hover.ValueChanged += _ => Apply();
            Children.Add(slot.Dot);
            Children.Add(slot.Icon);
            Children.Add(slot.Hit);
            _slots.Add(slot);
        }
        Width = IconsWidth + 2 * Theme.NavHitPadding;
        _pillCentre.Width = Math.Max(0, IconsWidth - Theme.NavPillHeight);
        Visibility = pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Apply();
    }

    private double IconsWidth => _slots.Count * Theme.NavSlot + 2 * Theme.NavPillPadding;
    private static double SlotCentre(int i, int n) => (i - (n - 1) / 2.0) * Theme.NavSlot;

    /// <summary>Dots ⇄ icons, staggered per item; re-targets from wherever each item is.</summary>
    private void MorphTo(double target)
    {
        int gen = ++_generation;
        _pill.SetTarget(target, Motion.NavMorph);
        double elapsed = 0;
        int next = 0;
        var order = target > 0 ? _slots : Enumerable.Reverse(_slots).ToList();
        SpringAnimator.Instance.RunWhile(dt =>
        {
            if (gen != _generation) return false;
            elapsed += dt * 1000;
            while (next < order.Count && next * Motion.NavStaggerMs <= elapsed)
                order[next++].Morph.SetTarget(target, Motion.NavMorph);
            return next < order.Count;
        });
    }

    private void Apply()
    {
        int n = _slots.Count;
        if (n == 0) return;

        // Dot layout (dots mode): the active dot is a wider capsule, all centred as a row.
        var widths = new double[n];
        double total = 0;
        for (int i = 0; i < n; i++)
        {
            double active = Math.Max(0, 1 - Math.Abs(_position - i));
            widths[i] = Theme.NavDot + Theme.NavDotActiveExtra * active;
            total += widths[i];
        }
        total += (n - 1) * Theme.NavDotGap;
        double x = -total / 2;

        for (int i = 0; i < n; i++)
        {
            var s = _slots[i];
            double m = Math.Clamp(s.Morph.Value, 0, 1.15);
            double active = Math.Max(0, 1 - Math.Abs(_position - i));
            double dotCentre = x + widths[i] / 2;
            x += widths[i] + Theme.NavDotGap;
            double iconCentre = SlotCentre(i, n);
            double cx = dotCentre + (iconCentre - dotCentre) * Math.Clamp(m, 0, 1);

            // Dot: shrinks and fades out in place.
            double dotScale = 1 - 0.6 * Math.Clamp(m, 0, 1);
            s.DotScale.ScaleX = widths[i] / (Theme.NavDot + Theme.NavDotActiveExtra) * dotScale;
            s.DotScale.ScaleY = dotScale;
            s.DotMove.X = cx;
            s.Dot.Opacity = (0.3 + 0.7 * active) * (1 - Math.Clamp(m, 0, 1));

            // Icon: grows 0.5 → 1 and fades in at the same spot; hover adds a little scale.
            double iconScale = (Motion.NavIconStartScale + (1 - Motion.NavIconStartScale) * m) * s.Hover.Value;
            s.IconScale.ScaleX = s.IconScale.ScaleY = iconScale;
            s.IconMove.X = cx;
            s.Icon.Opacity = Math.Clamp(m, 0, 1);
            s.Tint.Color = Theme.Lerp(Theme.NavDimColor, Theme.NavAccentColor, active);
            s.Hit.IsHitTestVisible = m > 0.5;
        }

        // Pill: centre stretches with ScaleX, the round caps slide outwards (ends stay semicircles).
        double p = Math.Clamp(_pill.Value, 0, 1.1);
        double dotsPill = total + 2 * Theme.NavPillPadding;
        double width = Math.Max(Theme.NavPillHeight, dotsPill + (IconsWidth - dotsPill) * p);
        double centre = Math.Max(0, width - Theme.NavPillHeight);
        _centreScale.ScaleX = _pillCentre.Width > 0 ? centre / _pillCentre.Width : 0;
        _leftMove.X = -centre / 2;
        _rightMove.X = centre / 2;
        double pillOpacity = 0.55 + 0.45 * Math.Clamp(p, 0, 1);
        _pillCentre.Opacity = _capLeft.Opacity = _capRight.Opacity = pillOpacity;

        // Highlight behind the active icon, following the page spring.
        _highlightMove.X = (_position - (n - 1) / 2.0) * Theme.NavSlot;
        _highlight.Opacity = Math.Clamp(p, 0, 1);
    }
}
