using System.Windows;
using System.Windows.Media;
using DynamicNotch.Design;

namespace DynamicNotch.Animation;

/// <summary>
/// Staggered entrances. Mark content blocks with <c>a:Stagger.Order="n"</c>; a <see cref="StaggerGroup"/>
/// built over a container fades and rises them in by order, <see cref="Motion.StaggerStepMs"/> apart,
/// and fades them all out together (faster) on close. Opacity + TranslateTransform only.
/// </summary>
public static class Stagger
{
    public static readonly DependencyProperty OrderProperty = DependencyProperty.RegisterAttached(
        "Order", typeof(int), typeof(Stagger), new PropertyMetadata(-1));

    public static int GetOrder(DependencyObject d) => (int)d.GetValue(OrderProperty);
    public static void SetOrder(DependencyObject d, int v) => d.SetValue(OrderProperty, v);
}

public sealed class StaggerGroup
{
    private sealed class Item
    {
        public required FrameworkElement Element;
        public required int Order;
        public required Spring Spring;
        public required TranslateTransform Translate;
    }

    private readonly List<Item> _items = new();
    private int _generation;

    public StaggerGroup(DependencyObject root)
    {
        Collect(root);
        _items.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    private void Collect(DependencyObject d)
    {
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is FrameworkElement fe && Stagger.GetOrder(fe) is int order and >= 0)
            {
                var t = new TranslateTransform();
                fe.RenderTransform = t; // stagger targets are plain wrappers, so they own their transform
                var item = new Item { Element = fe, Order = order, Spring = new Spring(Motion.FadeIn, 0), Translate = t };
                item.Spring.ValueChanged += v =>
                {
                    fe.Opacity = Math.Clamp(v, 0, 1);
                    BlurFx.Reveal(fe, v, Motion.EnterBlur);
                    t.Y = (1 - v) * Motion.StaggerRise;
                };
                item.Spring.Snap(0);
                _items.Add(item);
            }
            Collect(child);
        }
    }

    /// <summary>Fade/rise every block in, one order step after another.</summary>
    public void Show()
    {
        int gen = ++_generation;
        double elapsed = 0;
        int next = 0;
        int baseOrder = _items.Count > 0 ? _items[0].Order : 0;
        SpringAnimator.Instance.RunWhile(dt =>
        {
            if (gen != _generation) return false;
            elapsed += dt * 1000;
            while (next < _items.Count && (_items[next].Order - baseOrder) * Motion.StaggerStepMs <= elapsed)
            {
                _items[next].Spring.SetTarget(1, Motion.FadeIn);
                next++;
            }
            return next < _items.Count;
        });
    }

    /// <summary>Fade everything out together, quicker than it came in.</summary>
    public void Hide()
    {
        _generation++;
        foreach (var i in _items) i.Spring.SetTarget(0, Motion.FadeOut);
    }

    public bool IsShown => _items.Count > 0 && _items[^1].Spring.Target > 0;
}
