using Microsoft.UI.Composition;
using Windows.UI;

namespace DynamicNotch.Design;

/// <summary>
/// Keeps compositor colours (brushes, gradient stops, shadows) in step with the accent palette, the way the
/// shared XAML brushes in <see cref="Theme"/> are. Bindings run only when the accent changes (a pick, or the
/// frames of a cross-fade), so an idle notch does no work at all.
/// Targets are held strongly: a gradient stop that lives only inside its brush's native collection has no
/// other managed reference, so a weak reference let the GC drop the binding (the colour then stayed at
/// whatever it was first painted). The bound objects are few and live as long as their window.
/// </summary>
public static class AccentBinding
{
    private static readonly List<(CompositionObject Target, Action<CompositionObject, AccentPalette> Apply)> _items = new();
    private static bool _hooked;

    /// <summary>A colour brush that always shows <paramref name="pick"/> of the live palette.</summary>
    public static CompositionColorBrush Brush(Compositor c, Func<AccentPalette, Color> pick)
    {
        var b = c.CreateColorBrush(pick(Theme.Palette));
        Track(b, (o, p) => ((CompositionColorBrush)o).Color = pick(p));
        return b;
    }

    /// <summary>A gradient stop that always shows <paramref name="pick"/> of the live palette.</summary>
    public static CompositionColorGradientStop Stop(Compositor c, float offset, Func<AccentPalette, Color> pick)
    {
        var s = c.CreateColorGradientStop(offset, pick(Theme.Palette));
        Track(s, (o, p) => ((CompositionColorGradientStop)o).Color = pick(p));
        return s;
    }

    /// <summary>Runs <paramref name="apply"/> now and on every accent change while <paramref name="target"/> lives.</summary>
    public static void Track<T>(T target, Action<T, AccentPalette> apply) where T : CompositionObject
    {
        if (!_hooked) { _hooked = true; Theme.AccentChanged += Refresh; }
        apply(target, Theme.Palette);
        _items.Add((target, (o, p) => apply((T)o, p)));
    }

    private static void Refresh()
    {
        var p = Theme.Palette;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            try { _items[i].Apply(_items[i].Target, p); }
            catch (ObjectDisposedException) { _items.RemoveAt(i); }
        }
    }
}
