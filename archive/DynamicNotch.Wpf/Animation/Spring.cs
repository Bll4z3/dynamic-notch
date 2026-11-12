using DynamicNotch.Design;

namespace DynamicNotch.Animation;

/// <summary>
/// A damped harmonic oscillator advanced with the exact (closed-form) solution for the real frame
/// time, so motion is identical at 60, 120 or 144 Hz and never goes unstable after a hitch.
/// Retargeting keeps the current value AND velocity, so an interrupted animation curves smoothly
/// toward its new goal instead of snapping.
/// </summary>
public sealed class Spring
{
    private const double RestVelocity = 0.01;
    private const double RestDistance = 0.001;

    private double _target;

    public Spring(SpringPreset preset, double initial = 0)
    {
        Preset = preset;
        Value = _target = initial;
    }

    public SpringPreset Preset { get; set; }
    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target => _target;

    /// <summary>Relative precision used to decide when the spring is at rest (scaled to its range).</summary>
    public double Precision { get; set; } = 1;

    public bool IsSettled { get; private set; } = true;

    /// <summary>Raised on every frame the value changes (UI thread).</summary>
    public event Action<double>? ValueChanged;

    public void SetTarget(double target, SpringPreset? preset = null)
    {
        if (preset is { } p) Preset = p;
        if (target == _target && IsSettled) return;
        _target = target;
        IsSettled = false;
        SpringAnimator.Instance.Track(this);
    }

    /// <summary>Jump without animating (used when animations are disabled in Windows).</summary>
    public void Snap(double value)
    {
        _target = Value = value;
        Velocity = 0;
        IsSettled = true;
        ValueChanged?.Invoke(Value);
    }

    /// <summary>Kick the spring (e.g. a bounce) without changing the target.</summary>
    public void Impulse(double velocity)
    {
        Velocity += velocity;
        IsSettled = false;
        SpringAnimator.Instance.Track(this);
    }

    /// <summary>Advance the simulation by the real frame time. Returns true while still moving.</summary>
    internal bool Step(double dt)
    {
        if (IsSettled) return false;
        if (!SpringAnimator.AnimationsEnabled)
        {
            Snap(_target);
            return false;
        }

        (double x, double v) = Solve(Value - _target, Velocity, dt, Preset);
        Value = _target + x;
        Velocity = v;

        if (Math.Abs(Velocity) < RestVelocity * Precision && Math.Abs(Value - _target) < RestDistance * Precision)
        {
            Value = _target;
            Velocity = 0;
            IsSettled = true;
        }
        ValueChanged?.Invoke(Value);
        return !IsSettled;
    }

    /// <summary>Closed-form displacement/velocity of a damped spring after <paramref name="t"/> seconds.</summary>
    private static (double x, double v) Solve(double x0, double v0, double t, SpringPreset p)
    {
        double m = p.Mass, k = p.Stiffness, c = p.Damping;
        double w0 = Math.Sqrt(k / m);
        double zeta = c / (2 * Math.Sqrt(k * m));

        if (zeta < 0.999)
        {
            double wd = w0 * Math.Sqrt(1 - zeta * zeta);
            double e = Math.Exp(-zeta * w0 * t);
            double a = x0, b = (v0 + zeta * w0 * x0) / wd;
            double cos = Math.Cos(wd * t), sin = Math.Sin(wd * t);
            return (e * (a * cos + b * sin),
                    e * (cos * (b * wd - zeta * w0 * a) - sin * (a * wd + zeta * w0 * b)));
        }
        if (zeta <= 1.001)
        {
            double e = Math.Exp(-w0 * t);
            double cc = v0 + w0 * x0;
            return (e * (x0 + cc * t), e * (v0 - w0 * cc * t));
        }
        {
            double s = Math.Sqrt(zeta * zeta - 1);
            double r1 = -w0 * (zeta - s), r2 = -w0 * (zeta + s);
            double c2 = (v0 - r1 * x0) / (r2 - r1), c1 = x0 - c2;
            double e1 = Math.Exp(r1 * t), e2 = Math.Exp(r2 * t);
            return (c1 * e1 + c2 * e2, r1 * c1 * e1 + r2 * c2 * e2);
        }
    }
}
