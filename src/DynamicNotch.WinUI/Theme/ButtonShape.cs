using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace DynamicNotch.Design;

/// <summary>
/// The button roundness token (Settings ▸ Customisation ▸ Shape ▸ Button roundness): 1 = a full pill
/// (radius = half the height, the original look), 0 = a softly squared button. Buttons register once and
/// are held weakly, so lists that rebuild their buttons (popovers) never leak.
/// </summary>
public static class ButtonShape
{
    /// <summary>Smallest radius a button keeps at 0% roundness.</summary>
    public const double MinRadius = 4;

    public static double Roundness { get; private set; } = 1;

    private static readonly List<WeakReference<FrameworkElement>> _elements = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Action> _apply = new();

    /// <summary>Corner radius for a button of this height under the current roundness.</summary>
    public static double RadiusFor(double height)
    {
        double full = Math.Max(0, height / 2);
        return Math.Min(full, MinRadius + (full - MinRadius) * Roundness);
    }

    /// <summary>Calls <paramref name="apply"/> now and whenever the roundness changes, while <paramref name="el"/> lives.</summary>
    public static void Register(FrameworkElement el, Action apply)
    {
        _apply.AddOrUpdate(el, apply);
        _elements.Add(new WeakReference<FrameworkElement>(el));
        apply();
    }

    public static void SetRoundness(double r)
    {
        r = Math.Clamp(r, 0, 1);
        if (r == Roundness) return;
        Roundness = r;
        for (int i = _elements.Count - 1; i >= 0; i--)
        {
            if (!_elements[i].TryGetTarget(out var el)) { _elements.RemoveAt(i); continue; }
            if (_apply.TryGetValue(el, out var a)) a();
        }
    }
}
