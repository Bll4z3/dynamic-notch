using System.Numerics;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace DynamicNotch.Animation;

/// <summary>
/// The WPF app's motion behaviours (press squash, hover fade, pop in, leave, staggered entrances),
/// played by the compositor: each is a <see cref="CompSpring"/> on a small property set that the
/// element's visual follows through an expression.
/// </summary>
public static class Motions
{
    /// <summary>Spring-driven 0..1 value on its own property set ("v").</summary>
    public static (CompSpring spring, CompositionPropertySet set) Value(Compositor c, SpringPreset preset, double initial, double precision = 1)
    {
        var set = c.CreatePropertySet();
        return (new CompSpring(set, "v", preset, initial, precision), set);
    }

    /// <summary>Keeps the visual's centre point at the element's centre (for scale animations).</summary>
    public static void CentreOrigin(FrameworkElement el)
    {
        var v = Anim.VisualOf(el);
        void Set() => v.CenterPoint = new Vector3((float)el.ActualWidth / 2, (float)el.ActualHeight / 2, 0);
        el.SizeChanged += (_, _) => Set();
        Set();
    }

    /// <summary>Button press squash: shrinks to ~0.92 on press, springs back on release.</summary>
    public static void PressScale(FrameworkElement el, Func<bool>? enabled = null)
    {
        var visual = Anim.VisualOf(el);
        var (spring, set) = Value(visual.Compositor, Motion.Press, 1);
        CentreOrigin(el);
        visual.StartAnimation("Scale", Anim.Expr(visual.Compositor, "Vector3(p.v, p.v, 1)", ("p", set)));
        el.PointerPressed += (_, _) => { if (enabled?.Invoke() != false) spring.SetTarget(Motion.ButtonPressScale, Motion.Press); };
        el.PointerReleased += (_, _) => spring.SetTarget(1, Motion.Press);
        el.PointerExited += (_, _) => spring.SetTarget(1, Motion.Press);
        el.PointerCaptureLost += (_, _) => spring.SetTarget(1, Motion.Press);
    }

    /// <summary>A hover fill whose opacity springs 0 ⇄ 1 while <paramref name="source"/> is under the pointer.</summary>
    public static void HoverFade(UIElement source, UIElement fill, Func<bool>? enabled = null)
    {
        fill.IsHitTestVisible = false;
        var visual = Anim.VisualOf(fill);
        var (spring, set) = Value(visual.Compositor, Motion.Hover, 0);
        visual.StartAnimation("Opacity", Anim.Expr(visual.Compositor, "Clamp(p.v, 0, 1)", ("p", set)));
        source.PointerEntered += (_, _) => { if (enabled?.Invoke() != false) spring.SetTarget(1, Motion.Hover); };
        source.PointerExited += (_, _) => spring.SetTarget(0, Motion.Hover);
    }

    /// <summary>Pops an element in from slightly above, small and transparent (list items appearing).</summary>
    public static CompSpring PopIn(FrameworkElement el, double delaySeconds = 0)
    {
        var visual = Anim.VisualOf(el, translation: true);
        var c = visual.Compositor;
        var (spring, set) = Value(c, Motion.Pop, 0);
        CentreOrigin(el);
        visual.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
        visual.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.88 + 0.12 * p.v, 0.88 + 0.12 * p.v, 1)", ("p", set)));
        var t = Anim.Expr(c, "Vector3(0, (1 - p.v) * -S, 0)", ("p", set));
        t.SetScalarParameter("S", (float)Theme.S2);
        visual.StartAnimation("Translation", t);
        spring.SetTarget(1, Motion.Pop, delaySeconds);
        return spring;
    }

    /// <summary>Fades and shrinks an element out, quickly (a list item leaving).</summary>
    public static void Leave(FrameworkElement el)
    {
        el.IsHitTestVisible = false;
        var visual = Anim.VisualOf(el);
        var c = visual.Compositor;
        var (spring, set) = Value(c, Motion.FadeOut, 1);
        CentreOrigin(el);
        visual.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
        visual.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.8 + 0.2 * p.v, 0.8 + 0.2 * p.v, 1)", ("p", set)));
        spring.SetTarget(0, Motion.FadeOut);
    }
}

/// <summary>
/// Staggered entrances: blocks fade and rise in by order, <see cref="Motion.StaggerStepMs"/> apart, and
/// fade out together (faster) on close. The stagger is a start delay baked into each block's spring,
/// so the whole entrance is scheduled in one go on the compositor.
/// </summary>
public sealed class StaggerGroup
{
    private readonly List<(int order, CompSpring spring)> _items = new();

    /// <summary>Adds a block (a plain wrapper element: its visual's Opacity and Translation belong to the stagger).</summary>
    public void Add(FrameworkElement el, int order)
    {
        var visual = Anim.VisualOf(el, translation: true);
        var c = visual.Compositor;
        var (spring, set) = Motions.Value(c, Motion.FadeIn, 0);
        visual.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
        var t = Anim.Expr(c, "Vector3(0, (1 - p.v) * R, 0)", ("p", set));
        t.SetScalarParameter("R", (float)Motion.StaggerRise);
        visual.StartAnimation("Translation", t);
        _items.Add((order, spring));
        _items.Sort((a, b) => a.order.CompareTo(b.order));
    }

    public void Show()
    {
        if (_items.Count == 0) return;
        int first = _items[0].order;
        foreach (var (order, spring) in _items)
            spring.SetTarget(1, Motion.FadeIn, (order - first) * Motion.StaggerStepMs / 1000.0);
    }

    public void Hide()
    {
        foreach (var (_, spring) in _items) spring.SetTarget(0, Motion.FadeOut);
    }
}
