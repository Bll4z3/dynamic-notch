using System.Text;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// Page indicator that morphs from dots into page icons on hover (same design as the WPF navigator).
/// Pages and icons come from the <see cref="PageRegistry"/>. Each dot shrinks and fades while its icon
/// grows (0.5 → 1) and fades in at the same spot, staggered per item; the dark pill behind them
/// springs wider from the centre; the active page is accent-tinted with a soft highlight that follows
/// the page spring. All of it is compositor expressions over the page spring ("Page" on the notch's
/// property set) and one spring per item, so a page slide or a morph costs the app nothing per frame.
/// </summary>
public sealed class PageNavigator : Grid
{
    private sealed class Slot
    {
        public required Border Dot, Hit;
        public required Grid Icon;
        public required CompSpring Morph, Hover;
    }

    private readonly CompositionPropertySet _page;
    private readonly Compositor _c;
    private readonly CompositionPropertySet _s;
    private readonly List<Slot> _slots = new();
    private readonly CompSpring _pill;
    private readonly DispatcherQueueTimer _leaveTimer;

    /// <param name="page">Property set holding "Page" (the fractional page spring).</param>
    public PageNavigator(CompositionPropertySet page)
    {
        _page = page;
        _c = page.Compositor;
        _s = _c.CreatePropertySet();
        _pill = new CompSpring(_s, "Pill", Motion.NavMorph, 0);
        Background = Theme.TransparentBrush; // the whole (padded) box is the hover target
        Height = Theme.FooterHeight;

        _leaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _leaveTimer.IsRepeating = false;
        _leaveTimer.Interval = TimeSpan.FromMilliseconds(Motion.NavLeaveDelayMs);
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); MorphTo(0); };
        PointerEntered += (_, _) => { _leaveTimer.Stop(); MorphTo(1); };
        PointerExited += (_, _) => { _leaveTimer.Stop(); _leaveTimer.Start(); };
    }

    /// <summary>Raised with the enabled-page index when an icon is clicked.</summary>
    public event Action<int>? PageClicked;

    private static double SlotCentre(int i, int n) => (i - (n - 1) / 2.0) * Theme.NavSlot;

    /// <summary>Rebuild for the enabled pages (in order). Hidden when there's only one.</summary>
    public void SetPages(IReadOnlyList<PageDefinition> pages)
    {
        Children.Clear();
        _slots.Clear();
        int n = pages.Count;
        Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (n == 0) return;
        double iconsWidth = n * Theme.NavSlot + 2 * Theme.NavPillPadding;
        Width = iconsWidth + 2 * Theme.NavHitPadding;

        // ---- Expression text shared by every part ----
        string D = Anim.F(Theme.NavDot), E = Anim.F(Theme.NavDotActiveExtra), G = Anim.F(Theme.NavDotGap);
        string Active(int i) => $"Max(0, 1 - Abs(N.Page - {i}))";
        string W(int i) => $"({D} + {E} * {Active(i)})";
        var total = new StringBuilder("(");
        for (int j = 0; j < n; j++) total.Append(j > 0 ? " + " : "").Append(W(j));
        total.Append($" + {Anim.F((n - 1) * Theme.NavDotGap)})");
        string Total = total.ToString();

        // ---- Pill: centre stretches with ScaleX, the round caps slide outwards (ends stay semicircles) ----
        string p = "Clamp(S.Pill, 0, 1.1)";
        string pillWidth = $"Max({Anim.F(Theme.NavPillHeight)}, ({Total} + {Anim.F(2 * Theme.NavPillPadding)}) + ({Anim.F(iconsWidth)} - ({Total} + {Anim.F(2 * Theme.NavPillPadding)})) * {p})";
        string centre = $"Max(0, {pillWidth} - {Anim.F(Theme.NavPillHeight)})";
        string pillOpacity = $"0.55 + 0.45 * Clamp(S.Pill, 0, 1)";
        double centreRectWidth = Math.Max(1, iconsWidth - Theme.NavPillHeight);

        var capL = Ellipse(Theme.NavPillHeight, Theme.NavPillColor);
        var capR = Ellipse(Theme.NavPillHeight, Theme.NavPillColor);
        var mid = new Rectangle { Width = centreRectWidth, Height = Theme.NavPillHeight, Fill = new SolidColorBrush(Theme.NavPillColor), IsHitTestVisible = false };
        Children.Add(mid);
        Bind(capL, "Translation", $"Vector3(-{centre} / 2, 0, 0)", translation: true);
        Bind(capR, "Translation", $"Vector3({centre} / 2, 0, 0)", translation: true);
        Motions.CentreOrigin(mid);
        Bind(mid, "Scale", $"Vector3({centre} / {Anim.F(centreRectWidth)}, 1, 1)");
        foreach (var el in new UIElement[] { capL, capR, mid }) Bind(el, "Opacity", pillOpacity);

        // Highlight behind the active icon, following the page spring.
        var highlight = Ellipse(Theme.NavHighlightSize, Theme.NavHighlightColor);
        Bind(highlight, "Translation", $"Vector3((N.Page - {Anim.F((n - 1) / 2.0)}) * {Anim.F(Theme.NavSlot)}, 0, 0)", translation: true);
        Bind(highlight, "Opacity", "Clamp(S.Pill, 0, 1)");

        // ---- Items ----
        for (int i = 0; i < n; i++)
        {
            var page = pages[i];
            int index = i;
            var morph = new CompSpring(_s, $"M{i}", Motion.NavMorph, 0);
            var hover = new CompSpring(_s, $"H{i}", Motion.Hover, 1);

            var dot = new Border
            {
                Width = Theme.NavDot + Theme.NavDotActiveExtra, Height = Theme.NavDot,
                CornerRadius = new CornerRadius(Theme.NavDot / 2), // a pill: half its height
                Background = Theme.PrimaryText, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            // Dimmed icon, plus an accent copy on top that shows by how "current" the page is.
            var icon = new Grid { Width = Theme.NavIconSize, Height = Theme.NavIconSize, IsHitTestVisible = false,
                                  HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            string glyph = page.UseClaudeLogo ? Icons.Sparkle : page.Icon; // the logo asset loader arrives with the Ask page
            var dim = new VectorIcon { Data = glyph, Size = Theme.NavIconSize, Foreground = new SolidColorBrush(Theme.NavDimColor) };
            var accent = new VectorIcon { Data = glyph, Size = Theme.NavIconSize, Foreground = new SolidColorBrush(Theme.NavAccentColor) };
            icon.Children.Add(dim);
            icon.Children.Add(accent);
            var hit = new Border
            {
                Width = Theme.NavSlot, Height = Theme.NavPillHeight, Background = Theme.TransparentBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new TranslateTransform { X = SlotCentre(i, n) }, IsHitTestVisible = false,
            };
            ToolTipService.SetToolTip(hit, page.Name);
            hit.PointerEntered += (_, _) => hover.SetTarget(Motion.NavIconHoverScale, Motion.Hover);
            hit.PointerExited += (_, _) => hover.SetTarget(1, Motion.Hover);
            hit.Tapped += (_, e) => { PageClicked?.Invoke(index); e.Handled = true; };

            var before = new StringBuilder("0");
            for (int j = 0; j < i; j++) before.Append(" + ").Append(W(j));
            string dotCentre = $"(-{Total} / 2 + {before} + {Anim.F(i * Theme.NavDotGap)} + {W(i)} / 2)";
            string cm = $"Clamp(S.M{i}, 0, 1)";
            string cx = $"({dotCentre} + ({Anim.F(SlotCentre(i, n))} - {dotCentre}) * {cm})";

            // Dot: shrinks and fades out in place.
            Motions.CentreOrigin(dot);
            Bind(dot, "Translation", $"Vector3({cx}, 0, 0)", translation: true);
            Bind(dot, "Scale", $"Vector3({W(i)} / {Anim.F(Theme.NavDot + Theme.NavDotActiveExtra)} * (1 - 0.6 * {cm}), 1 - 0.6 * {cm}, 1)");
            Bind(dot, "Opacity", $"(0.3 + 0.7 * {Active(i)}) * (1 - {cm})");
            // Icon: grows 0.5 → 1 and fades in at the same spot; hover adds a little scale.
            string s = Anim.F(Motion.NavIconStartScale);
            Motions.CentreOrigin(icon);
            Bind(icon, "Translation", $"Vector3({cx}, 0, 0)", translation: true);
            Bind(icon, "Scale", $"Vector3(({s} + (1 - {s}) * Clamp(S.M{i}, 0, 1.15)) * S.H{i}, ({s} + (1 - {s}) * Clamp(S.M{i}, 0, 1.15)) * S.H{i}, 1)");
            Bind(icon, "Opacity", cm);
            Bind(accent, "Opacity", Active(i));

            Children.Add(dot);
            Children.Add(icon);
            _slots.Add(new Slot { Dot = dot, Hit = hit, Icon = icon, Morph = morph, Hover = hover });
        }
        foreach (var sl in _slots) Children.Add(sl.Hit); // hit targets on top
    }

    /// <summary>Dev: show the icon state without a hover (`--nav-icons`).</summary>
    public void ShowIcons() => MorphTo(1);

    /// <summary>Dots ⇄ icons, staggered per item; re-targets from wherever each item is.</summary>
    private void MorphTo(double target)
    {
        _pill.SetTarget(target, Motion.NavMorph);
        var order = target > 0 ? _slots : Enumerable.Reverse(_slots).ToList();
        for (int k = 0; k < order.Count; k++)
        {
            order[k].Morph.SetTarget(target, Motion.NavMorph, k * Motion.NavStaggerMs / 1000.0);
            order[k].Hit.IsHitTestVisible = target > 0.5;
        }
    }

    private Ellipse Ellipse(double size, Color color) => new Ellipse
    {
        Width = size, Height = size, Fill = new SolidColorBrush(color), IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    }.Also(Children.Add);

    private void Bind(UIElement el, string property, string expression, bool translation = false)
    {
        var e = _c.CreateExpressionAnimation(expression);
        e.SetReferenceParameter("N", _page);
        e.SetReferenceParameter("S", _s);
        Anim.VisualOf(el, translation).StartAnimation(property, e);
    }
}

internal static class FluentExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
