using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicNotch.Animation;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// Horizontal, right-aligned row of fixed-width slots (the connected-devices row). When items come
/// or go, the others slide to their new slots with a spring via RenderTransform ("FLIP"), so the
/// row reflows smoothly without animating any layout property. Leaving items keep their place while
/// they fade out but no longer take up a slot.
/// </summary>
public sealed class ReflowPanel : Panel
{
    public double SlotWidth { get; set; } = Theme.DeviceSlotWidth;

    private sealed class Slide
    {
        public readonly TranslateTransform Translate = new();
        public readonly Spring X = new(Motion.Reflow, 0) { Precision = 50 };
        public double? LastX;
    }

    private readonly ConditionalWeakTable<UIElement, Slide> _slides = new();

    private static bool IsLeaving(UIElement child)
    {
        if (child is FrameworkElement fe && VisualTreeHelper.GetChildrenCount(fe) > 0 &&
            VisualTreeHelper.GetChild(fe, 0) is DependencyObject root)
            return MotionBehaviors.GetLeaving(root);
        return false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double h = 0;
        int slots = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(SlotWidth, availableSize.Height));
            h = Math.Max(h, c.DesiredSize.Height);
            if (!IsLeaving(c)) slots++;
        }
        return new Size(slots * SlotWidth, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Right-aligned: the newest item sits on the far right.
        double x = finalSize.Width;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var c = InternalChildren[i];
            var slide = _slides.GetValue(c, el =>
            {
                var s = new Slide();
                el.RenderTransform = s.Translate;
                s.X.ValueChanged += v => s.Translate.X = v;
                return s;
            });

            double slotX;
            if (IsLeaving(c)) slotX = slide.LastX ?? x - SlotWidth; // stay put while fading out
            else { x -= SlotWidth; slotX = x; }

            c.Arrange(new Rect(slotX, 0, SlotWidth, finalSize.Height));
            if (slide.LastX is double last && Math.Abs(last - slotX) > 0.5)
            {
                // FLIP: start visually where it was, spring to the new slot.
                slide.X.Snap(slide.X.Value + last - slotX);
                slide.X.SetTarget(0, Motion.Reflow);
            }
            slide.LastX = slotX;
        }
        return finalSize;
    }
}
