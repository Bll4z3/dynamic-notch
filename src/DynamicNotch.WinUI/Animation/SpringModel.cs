using System.Diagnostics;
using DynamicNotch.Design;

namespace DynamicNotch.Animation;

/// <summary>
/// The app's damped spring as a pure function of time (same closed-form maths and presets as the WPF
/// app's Spring). Nothing ticks: the value at any moment is computed on demand, so the app can ask
/// "where is the notch now?" or "when does it cross 35%?" without a render loop. <see cref="CompSpring"/>
/// turns a trajectory into a compositor keyframe animation, so the screen and this model agree.
/// Retargeting keeps the current value AND velocity, so interruptions curve smoothly.
/// </summary>
public sealed class SpringModel
{
    private const double RestVelocity = 0.01;
    private const double RestDistance = 0.001;
    /// <summary>Longest trajectory we ever bake (a spring this slow is a tuning mistake).</summary>
    public const double MaxDuration = 4;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double Now => Clock.Elapsed.TotalSeconds;

    private double _x0, _v0, _t0; // displacement from target, velocity, start time of this segment

    public SpringModel(SpringPreset preset, double initial = 0)
    {
        Preset = preset;
        Target = initial;
    }

    public SpringPreset Preset { get; private set; }
    public double Target { get; private set; }
    /// <summary>Relative precision used to decide when the spring is at rest (scaled to its range).</summary>
    public double Precision { get; init; } = 1;

    public double Value => ValueAt(Now);
    public double Velocity => StateAt(Now).v;

    /// <summary>Seconds since this segment started (negative while a delay is still running).</summary>
    public double Elapsed => Now - _t0;

    /// <summary>Seconds from now until the spring is at rest.</summary>
    public double RemainingDuration => Math.Max(0, _t0 + SettleDuration() - Now);

    public double ValueAt(double time) => StateAt(time).x;

    public (double x, double v) StateAt(double time)
    {
        double t = time - _t0;
        if (t <= 0) return (Target + _x0, _v0);
        if (_x0 == 0 && _v0 == 0) return (Target, 0);
        var (x, v) = Solve(_x0, _v0, t, Preset);
        return (Target + x, v);
    }

    public bool IsSettled => IsSettledAt(Now);

    public bool IsSettledAt(double time)
    {
        var (x, v) = StateAt(time);
        return Math.Abs(v) < RestVelocity * Precision && Math.Abs(x - Target) < RestDistance * Precision;
    }

    /// <summary>
    /// Retarget from the current value and velocity. Returns false if nothing changes. With a
    /// <paramref name="delay"/> (seconds) the value holds where it is until the delay has passed
    /// (used for staggered entrances, which start from rest).
    /// </summary>
    public bool SetTarget(double target, SpringPreset? preset = null, double delay = 0)
    {
        double now = Now;
        bool settled = IsSettledAt(now);
        if (target == Target && settled && preset == null) return false;
        var (x, v) = settled ? (Target, 0.0) : StateAt(now);
        if (preset is { } p) Preset = p;
        Target = target;
        _x0 = x - target;
        _v0 = delay > 0 ? 0 : v;
        _t0 = now + Math.Max(0, delay);
        return true;
    }

    /// <summary>Jump without animating.</summary>
    public void Snap(double value)
    {
        Target = value;
        _x0 = _v0 = 0;
        _t0 = Now;
    }

    /// <summary>Kick the spring (a bounce) without changing the target.</summary>
    public void Impulse(double velocity)
    {
        double now = Now;
        var (x, v) = StateAt(now);
        _x0 = x - Target;
        _v0 = v + velocity;
        _t0 = now;
    }

    /// <summary>Seconds from the start of the current segment until the spring is at rest.</summary>
    public double SettleDuration()
    {
        if (_x0 == 0 && _v0 == 0) return 0;
        const double step = 1.0 / 240;
        for (double t = step; t < MaxDuration; t += step)
            if (IsSettledAt(_t0 + t)) return t;
        return MaxDuration;
    }

    /// <summary>Absolute time when <paramref name="condition"/> first holds on the current trajectory, or null.</summary>
    public double? TimeWhen(Func<double, bool> condition, double from)
    {
        double end = _t0 + SettleDuration();
        const double step = 1.0 / 240;
        for (double t = from; t <= end + step; t += step)
            if (condition(ValueAt(t))) return t;
        return condition(Target) ? end : null;
    }

    /// <summary>Largest value on the trajectory from <paramref name="from"/> until rest (for overshoot-safe bounds).</summary>
    public double MaxFrom(double from)
    {
        double end = _t0 + SettleDuration(), max = Math.Max(ValueAt(from), Target);
        for (double t = from; t <= end; t += 1.0 / 120) max = Math.Max(max, ValueAt(t));
        return max;
    }

    /// <summary>Closed-form displacement/velocity of a damped spring after <paramref name="t"/> seconds.</summary>
    internal static (double x, double v) Solve(double x0, double v0, double t, SpringPreset p)
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
