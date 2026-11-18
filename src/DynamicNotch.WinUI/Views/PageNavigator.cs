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

        // ---- Shared values, each a short chained expression on the property set (one long inline
        //      expression per part grows with the page count and the compositor rejects it past ~5 pages) ----
        string D = Anim.F(Theme.NavDot), E = Anim.F(Theme.NavDotActiveExtra);
        string Active(int i) => $"S.A{i}";
        string W(int i) => $"S.W{i}";
        for (int j = 0; j < n; j++)
        {
            Chain($"A{j}", $"Max(0, 1 - Abs(N.Page - {j}))");
            Chain($"W{j}", $"{D} + {E} * S.A{j}");
            Chain($"B{j}", j == 0 ? "0" : $"S.B{j - 1} + S.W{j - 1}"); // widths of the dots before this one
        }
        Chain("Tot", $"S.B{n - 1} + S.W{n - 1} + {Anim.F((n - 1) * Theme.NavDotGap)}");
        string Total = "S.Tot";

        // ---- Pill: centre stretches with ScaleX, the round caps slide outwards (ends stay semicircles) ----
        string p = "Clamp(S.Pill, 0, 1.1)";
        Chain("PillW", $"Max({Anim.F(Theme.NavPillHeight)}, (S.Tot + {Anim.F(2 * Theme.NavPillPadding)}) + ({Anim.F(iconsWidth)} - (S.Tot + {Anim.F(2 * Theme.NavPillPadding)})) * {p})");
        Chain("Cen", $"Max(0, S.PillW - {Anim.F(Theme.NavPillHeight)})");
        string centre = "S.Cen";
        string pillOpacity = $"0.55 + 0.45 * Clamp(S.Pill, 0, 1)";
        double centreRectWidth = Math.Max(1, iconsWidth - Theme.NavPillHeight);

        // ONE rounded rectangle (radius = height / 2) whose width follows the pill expression. (It used to be two
        // round caps plus a middle bar, each at ~55% opacity: where they overlapped the opacity doubled, which
        // showed as dark circles at both ends on the tinted music-page background.)
        _ = centre; _ = centreRectWidth;
        var pillHost = new Border { IsHitTestVisible = false };
        Children.Add(pillHost);
        var pillGeo = _c.CreateRoundedRectangleGeometry();
        pillGeo.CornerRadius = new System.Numerics.Vector2((float)Theme.NavPillHeight / 2);
        Chain("HostW", $"{Anim.F(iconsWidth + 2 * Theme.NavHitPadding)}");
        var size = _c.CreateExpressionAnimation($"Vector2(S.PillW, {Anim.F(Theme.NavPillHeight)})");
        size.SetReferenceParameter("S", _s);
        pillGeo.StartAnimation("Size", size);
        var offset = _c.CreateExpressionAnimation($"Vector2((S.HostW - S.PillW) / 2, {Anim.F((Theme.FooterHeight - Theme.NavPillHeight) / 2)})");
        offset.SetReferenceParameter("S", _s);
        pillGeo.StartAnimation("Offset", offset);
        var pillShape = _c.CreateSpriteShape(pillGeo);
        pillShape.FillBrush = _c.CreateColorBrush(Theme.NavPillColor);
        var pillVisual = _c.CreateShapeVisual();
        pillVisual.Size = new System.Numerics.Vector2((float)(iconsWidth + 2 * Theme.NavHitPadding), (float)Theme.FooterHeight);
        pillVisual.Shapes.Add(pillShape);
        var po = _c.CreateExpressionAnimation(pillOpacity);
        po.SetReferenceParameter("S", _s);
        po.SetReferenceParameter("N", _page);
        pillVisual.StartAnimation("Opacity", po);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(pillHost, pillVisual);

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
            var accent = new VectorIcon { Data = glyph, Size = Theme.NavIconSize, Foreground = Theme.AccentBrush }; // the current page in the accent (live)
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

            Chain($"C{i}", $"-{Total} / 2 + S.B{i} + {Anim.F(i * Theme.NavDotGap)} + {W(i)} / 2");
            string dotCentre = $"S.C{i}";
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

    /// <summary>A derived scalar on the navigator's property set, driven by an expression.</summary>
    private void Chain(string name, string expression)
    {
        _s.InsertScalar(name, 0);
        var e = _c.CreateExpressionAnimation(expression);
        e.SetReferenceParameter("N", _page);
        e.SetReferenceParameter("S", _s);
        _s.StartAnimation(name, e);
    }

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
