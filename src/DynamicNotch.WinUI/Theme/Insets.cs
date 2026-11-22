using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace DynamicNotch.Design;

/// <summary>
/// Elements whose padding follows the corner radius (<see cref="Theme.InsetFor"/>): Settings cards and row
/// cards. They register once and are held weakly; <see cref="Refresh"/> runs after every radius change, so
/// a rounder card gets more inset straight away and text never crowds its curve.
/// </summary>
public static class Insets
{
    private static readonly List<WeakReference<FrameworkElement>> _elements = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Action> _apply = new();

    /// <summary>Calls <paramref name="apply"/> now and after every radius change, while <paramref name="el"/> lives.</summary>
    public static void Register(FrameworkElement el, Action apply)
    {
        _apply.AddOrUpdate(el, apply);
        _elements.Add(new WeakReference<FrameworkElement>(el));
        apply();
    }

    public static void Refresh()
    {
        for (int i = _elements.Count - 1; i >= 0; i--)
        {
            if (!_elements[i].TryGetTarget(out var el)) { _elements.RemoveAt(i); continue; }
            if (_apply.TryGetValue(el, out var a)) a();
        }
    }
}
