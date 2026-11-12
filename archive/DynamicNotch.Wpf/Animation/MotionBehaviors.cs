using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DynamicNotch.Design;

namespace DynamicNotch.Animation;

/// <summary>
/// Attached behaviours that give any element spring-based interaction motion. They only ever
/// animate GPU-friendly properties (RenderTransform and Opacity), never layout.
/// </summary>
public static class MotionBehaviors
{
    // ---------------------------------------------------------------------------------------
    // PressScale: shrink to ~0.92 on press, spring back on release.
    // ---------------------------------------------------------------------------------------
    public static readonly DependencyProperty PressScaleProperty = DependencyProperty.RegisterAttached(
        "PressScale", typeof(bool), typeof(MotionBehaviors), new PropertyMetadata(false, OnPressScaleChanged));

    public static bool GetPressScale(DependencyObject d) => (bool)d.GetValue(PressScaleProperty);
    public static void SetPressScale(DependencyObject d, bool v) => d.SetValue(PressScaleProperty, v);

    private static void OnPressScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.NewValue is not true) return;
        var scale = EnsureScaleTranslate(el).scale;
        var spring = new Spring(Motion.Press, 1);
        spring.ValueChanged += v => { scale.ScaleX = v; scale.ScaleY = v; };

        el.PreviewMouseLeftButtonDown += (_, _) => spring.SetTarget(Motion.ButtonPressScale, Motion.Press);
        el.PreviewMouseLeftButtonUp += (_, _) => spring.SetTarget(1, Motion.Press);
        el.MouseLeave += (_, _) => spring.SetTarget(1, Motion.Press);
    }

    // ---------------------------------------------------------------------------------------
    // HoverFade: put on a hover-fill element inside a template; its opacity springs 0 ⇄ 1 while the
    // templated parent (or the nearest parent) is under the mouse.
    // ---------------------------------------------------------------------------------------
    public static readonly DependencyProperty HoverFadeProperty = DependencyProperty.RegisterAttached(
        "HoverFade", typeof(bool), typeof(MotionBehaviors), new PropertyMetadata(false, OnHoverFadeChanged));

    public static bool GetHoverFade(DependencyObject d) => (bool)d.GetValue(HoverFadeProperty);
    public static void SetHoverFade(DependencyObject d, bool v) => d.SetValue(HoverFadeProperty, v);

    private static void OnHoverFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not true) return;
        el.Opacity = 0;
        el.IsHitTestVisible = false;
        var spring = new Spring(Motion.Hover, 0);
        spring.ValueChanged += v => el.Opacity = Math.Clamp(v, 0, 1);
        bool wired = false;
        el.Loaded += (_, _) =>
        {
            if (wired) return;
            var source = el.TemplatedParent as UIElement ?? VisualTreeHelper.GetParent(el) as UIElement;
            if (source == null) return;
            wired = true;
            source.MouseEnter += (_, _) => { if (source.IsEnabled) spring.SetTarget(1, Motion.Hover); };
            source.MouseLeave += (_, _) => spring.SetTarget(0, Motion.Hover);
            source.IsEnabledChanged += (_, a) => { if (a.NewValue is false) spring.SetTarget(0, Motion.Hover); };
        };
    }

    // ---------------------------------------------------------------------------------------
    // AnimateIn: on Loaded, pop in from slightly above, small and transparent.
    // ---------------------------------------------------------------------------------------
    public static readonly DependencyProperty AnimateInProperty = DependencyProperty.RegisterAttached(
        "AnimateIn", typeof(bool), typeof(MotionBehaviors), new PropertyMetadata(false, OnAnimateInChanged));

    public static bool GetAnimateIn(DependencyObject d) => (bool)d.GetValue(AnimateInProperty);
    public static void SetAnimateIn(DependencyObject d, bool v) => d.SetValue(AnimateInProperty, v);

    private static readonly DependencyProperty AnimatedInProperty = DependencyProperty.RegisterAttached(
        "AnimatedIn", typeof(bool), typeof(MotionBehaviors));

    private static void OnAnimateInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not true) return;
        el.Loaded += (_, _) =>
        {
            if ((bool)el.GetValue(AnimatedInProperty)) return;
            el.SetValue(AnimatedInProperty, true);
            var (scale, translate) = EnsureScaleTranslate(el);
            var p = new Spring(Motion.Pop, 0);
            p.ValueChanged += v =>
            {
                el.Opacity = Math.Clamp(v, 0, 1);
                BlurFx.Reveal(el, v, Motion.ItemBlur);
                double s = 0.88 + 0.12 * v;
                scale.ScaleX = scale.ScaleY = s;
                translate.Y = (1 - v) * -Theme.S2;
            };
            p.Snap(0);
            int delay = GetAnimateInDelay(el);
            if (delay <= 0) { p.SetTarget(1); return; }
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            t.Tick += (_, _) => { t.Stop(); p.SetTarget(1); };
            t.Start();
        };
    }

    /// <summary>Optional delay (ms) before an AnimateIn element springs in (e.g. the answer after the question).</summary>
    public static readonly DependencyProperty AnimateInDelayProperty = DependencyProperty.RegisterAttached(
        "AnimateInDelay", typeof(int), typeof(MotionBehaviors), new PropertyMetadata(0));
    public static int GetAnimateInDelay(DependencyObject d) => (int)d.GetValue(AnimateInDelayProperty);
    public static void SetAnimateInDelay(DependencyObject d, int v) => d.SetValue(AnimateInDelayProperty, v);

    // ---------------------------------------------------------------------------------------
    // Pulse: a slow opacity breathing while the element is visible ("Claude is thinking…").
    // ---------------------------------------------------------------------------------------
    public static readonly DependencyProperty PulseProperty = DependencyProperty.RegisterAttached(
        "Pulse", typeof(bool), typeof(MotionBehaviors), new PropertyMetadata(false, OnPulseChanged));
    public static bool GetPulse(DependencyObject d) => (bool)d.GetValue(PulseProperty);
    public static void SetPulse(DependencyObject d, bool v) => d.SetValue(PulseProperty, v);

    private static void OnPulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.NewValue is not true) return;
        void Run()
        {
            if (!el.IsVisible || !SpringAnimator.AnimationsEnabled) return;
            double t = 0, since = 0;
            SpringAnimator.Instance.RunWhile(dt =>
            {
                if (!el.IsVisible) { el.Opacity = 1; return false; }
                t += dt; since += dt;
                if (since >= 1.0 / Theme.GlowPulseFps)
                {
                    since = 0;
                    el.Opacity = 0.45 + 0.55 * (0.5 + 0.5 * Math.Cos(2 * Math.PI * t / Motion.ThinkingPulsePeriod));
                }
                return true;
            });
        }
        el.IsVisibleChanged += (_, a) => { if (a.NewValue is true) Run(); };
        Run();
    }

    // ---------------------------------------------------------------------------------------
    // Leaving: bind to a view-model flag; when true, the element fades and shrinks out (quickly).
    // ---------------------------------------------------------------------------------------
    public static readonly DependencyProperty LeavingProperty = DependencyProperty.RegisterAttached(
        "Leaving", typeof(bool), typeof(MotionBehaviors), new PropertyMetadata(false, OnLeavingChanged));

    public static bool GetLeaving(DependencyObject d) => (bool)d.GetValue(LeavingProperty);
    public static void SetLeaving(DependencyObject d, bool v) => d.SetValue(LeavingProperty, v);

    private static void OnLeavingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not true) return;
        var (scale, _) = EnsureScaleTranslate(el);
        el.IsHitTestVisible = false;
        var p = new Spring(Motion.FadeOut, 1);
        p.ValueChanged += v =>
        {
            el.Opacity = Math.Clamp(v, 0, 1);
            BlurFx.Reveal(el, v, Motion.ItemBlur);
            scale.ScaleX = scale.ScaleY = 0.8 + 0.2 * v;
        };
        p.SetTarget(0);
        // Let a reflowing parent slide the siblings over right away.
        (VisualTreeHelper.GetParent(el) is UIElement p1 ? VisualTreeHelper.GetParent(p1) as UIElement : null)?.InvalidateMeasure();
        (VisualTreeHelper.GetParent(el) as UIElement)?.InvalidateMeasure();
    }

    // ---------------------------------------------------------------------------------------
    internal static (ScaleTransform scale, TranslateTransform translate) EnsureScaleTranslate(UIElement el)
    {
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        if (el.RenderTransform is TransformGroup g && g.Children.Count == 2 &&
            g.Children[0] is ScaleTransform s0 && g.Children[1] is TranslateTransform t0)
            return (s0, t0);
        var s = new ScaleTransform(1, 1);
        var t = new TranslateTransform();
        el.RenderTransform = new TransformGroup { Children = { s, t } };
        return (s, t);
    }
}
