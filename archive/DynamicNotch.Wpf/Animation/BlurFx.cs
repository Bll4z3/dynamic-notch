using System.Windows;
using System.Windows.Media.Effects;

namespace DynamicNotch.Animation;

/// <summary>
/// Spring-driven blur for transitions (GPU pixel shader). Content blurs in as it appears, blurs out
/// as it leaves, and pages get a light motion blur while they slide. Below a small radius the effect
/// is removed entirely, so settled content is pixel-sharp and costs nothing to draw.
/// </summary>
public static class BlurFx
{
    private const double Off = 0.3;

    private static readonly DependencyProperty BlurProperty = DependencyProperty.RegisterAttached(
        "Blur", typeof(BlurEffect), typeof(BlurFx));

    /// <summary>Set the element's blur radius in DIPs (0 or "Animation effects" off = no effect at all).</summary>
    public static void Set(UIElement el, double radius)
    {
        if (radius < Off || !SpringAnimator.AnimationsEnabled)
        {
            if (el.Effect is BlurEffect) el.Effect = null;
            return;
        }
        if (el.GetValue(BlurProperty) is not BlurEffect blur)
        {
            blur = new BlurEffect { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            el.SetValue(BlurProperty, blur);
        }
        blur.Radius = radius;
        if (!ReferenceEquals(el.Effect, blur)) el.Effect = blur;
    }

    /// <summary>Blur for a 0→1 reveal value: full <paramref name="max"/> at 0, none at 1.</summary>
    public static void Reveal(UIElement el, double value, double max) =>
        Set(el, max * (1 - Math.Clamp(value, 0, 1)));
}
