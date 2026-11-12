using System.Numerics;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace DynamicNotch.Animation;

/// <summary>Small helpers for compositor-driven motion.</summary>
public static class Anim
{
    /// <summary>Mirrors the Windows "Animation effects" setting. Off = every spring snaps.</summary>
    public static bool Enabled { get; private set; } = true;

    public static void RefreshSystemSetting() => Enabled = Native.ClientAreaAnimationsEnabled();

    private static LinearEasingFunction? _linear;

    /// <summary>The model's current trajectory as linear keyframes (the compositor plays it on its own thread).</summary>
    internal static ScalarKeyFrameAnimation Bake(Compositor c, SpringModel model, double duration, double keysPerSecond)
    {
        _linear ??= c.CreateLinearEasingFunction();
        var a = c.CreateScalarKeyFrameAnimation();
        double start = SpringModel.Now;
        int n = Math.Clamp((int)Math.Ceiling(duration * keysPerSecond), 2, 400);
        for (int i = 0; i <= n; i++)
        {
            float p = (float)i / n;
            double v = i == n ? model.Target : model.ValueAt(start + duration * p);
            a.InsertKeyFrame(p, (float)v, _linear);
        }
        a.Duration = TimeSpan.FromSeconds(Math.Max(duration, 0.001));
        return a;
    }

    /// <summary>Sets a scalar property on any composition object (property sets, visuals, geometries, brushes).</summary>
    public static void SetScalar(CompositionObject target, string property, double value)
    {
        if (target is CompositionPropertySet set) { set.InsertScalar(property, (float)value); return; }
        var a = target.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1, (float)value);
        a.Duration = TimeSpan.FromMilliseconds(1);
        target.StartAnimation(property, a);
    }

    /// <summary>Fire-and-forget spring on one scalar property, from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static void SpringTo(CompositionObject target, string property, double from, double to, SpringPreset preset, double precision = 1)
    {
        var m = new SpringModel(preset, from) { Precision = precision };
        m.SetTarget(to);
        double d = m.SettleDuration();
        if (!Enabled || d <= 0.001) { target.StopAnimation(property); SetScalar(target, property, to); return; }
        target.StartAnimation(property, Bake(target.Compositor, m, d, 60));
    }

    /// <summary>An expression bound to named references (each value is a CompositionObject).</summary>
    public static ExpressionAnimation Expr(Compositor c, string expression, params (string name, CompositionObject obj)[] refs)
    {
        var e = c.CreateExpressionAnimation(expression);
        foreach (var (name, obj) in refs) e.SetReferenceParameter(name, obj);
        return e;
    }

    /// <summary>The element's compositor visual, with Translation enabled so it can move without layout.</summary>
    public static Visual VisualOf(UIElement element, bool translation = false)
    {
        if (translation) ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return ElementCompositionPreview.GetElementVisual(element);
    }

    public static Vector3 V3(double x, double y, double z = 0) => new((float)x, (float)y, (float)z);

    /// <summary>A number for an expression string (invariant culture).</summary>
    public static string F(double v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Expression text for Clamp((x - a) / (b - a), 0, 1).</summary>
    public static string Ramp(double a, double b, string x) => $"Clamp(({x} - {F(a)}) / {F(b - a)}, 0, 1)";

    /// <summary>Expression text for a smoothstep of <paramref name="x"/> between a and b (same curve as the WPF app).</summary>
    public static string SmoothStep(double a, double b, string x)
    {
        string t = Ramp(a, b, x);
        return $"({t} * {t} * (3 - 2 * {t}))";
    }
}
