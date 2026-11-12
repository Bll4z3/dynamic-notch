using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DynamicNotch.Views;

/// <summary>
/// Animated audio bars for the collapsed notch. Same motion as the WPF version (each bar is a wobbly
/// spring re-targeted to a random height every 0.11 s), but the whole sequence is simulated once and
/// handed to the compositor as a looping keyframe animation, so playing costs the app no CPU at all.
/// When paused, each bar springs back down from wherever its loop is right now.
/// </summary>
public sealed class Visualizer : StackPanel
{
    private const int BarCount = 4;
    private const double BarWidth = 3, Gap = 2.5, MinLevel = 0.18, BarHeight = 14;
    private const double RetargetEvery = 0.11, LoopSeconds = 4.4, SampleRate = 60;

    private readonly Border[] _bars = new Border[BarCount];
    private readonly float[][] _loops = new float[BarCount][];
    private bool _playing;
    private double _loopStart;

    public Visualizer()
    {
        Orientation = Orientation.Horizontal;
        Spacing = Gap;
        Height = BarHeight;
        IsHitTestVisible = false;
        var rng = new Random(7);
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i] = new Border
            {
                Width = BarWidth,
                Height = BarHeight,
                CornerRadius = new CornerRadius(Math.Min(Theme.CornerRadius, BarWidth / 2)),
                Background = Theme.PrimaryText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Children.Add(_bars[i]);
            _loops[i] = Simulate(rng);
        }
        Loaded += (_, _) =>
        {
            foreach (var b in _bars)
            {
                var v = Anim.VisualOf(b);
                v.CenterPoint = new Vector3((float)BarWidth / 2, (float)BarHeight / 2, 0);
                v.Scale = new Vector3(1, (float)MinLevel, 1);
            }
            if (_playing) Start();
        };
    }

    public bool IsPlaying
    {
        get => _playing;
        set
        {
            if (_playing == value) return;
            _playing = value;
            if (!IsLoaded) return;
            if (value) Start(); else Stop();
        }
    }

    /// <summary>One loop of a bar: the WPF spring re-targeted every 0.11 s, sampled at 60 Hz, ending where it began.</summary>
    private static float[] Simulate(Random rng)
    {
        int n = (int)(LoopSeconds * SampleRate);
        var samples = new float[n + 1];
        double dt = 1 / SampleRate, sinceRetarget = RetargetEvery;
        var sim = new VirtualSpring(Motion.Wobble, MinLevel);
        for (int i = 0; i <= n; i++)
        {
            double t = i * dt;
            if (sinceRetarget >= RetargetEvery && t < LoopSeconds - 0.3)
            {
                sinceRetarget = 0;
                sim.SetTarget(MinLevel + rng.NextDouble() * (1 - MinLevel), t);
            }
            else if (t >= LoopSeconds - 0.3 && sim.Target != MinLevel) sim.SetTarget(MinLevel, t); // settle back to the start
            sinceRetarget += dt;
            samples[i] = (float)Math.Clamp(sim.ValueAt(t), 0.05, 1.1);
        }
        samples[n] = (float)MinLevel;
        return samples;
    }

    private void Start()
    {
        _loopStart = SpringModel.Now;
        if (!Anim.Enabled) return; // "Animation effects" off: the bars stay still
        var c = Anim.VisualOf(_bars[0]).Compositor;
        var linear = c.CreateLinearEasingFunction();
        for (int i = 0; i < BarCount; i++)
        {
            var a = c.CreateScalarKeyFrameAnimation();
            var loop = _loops[i];
            for (int k = 0; k < loop.Length; k++) a.InsertKeyFrame((float)k / (loop.Length - 1), loop[k], linear);
            a.Duration = TimeSpan.FromSeconds(LoopSeconds);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            Anim.VisualOf(_bars[i]).StartAnimation("Scale.Y", a);
        }
    }

    private void Stop()
    {
        // Where each loop is right now (deterministic), then a soft spring down to rest.
        double phase = ((SpringModel.Now - _loopStart) % LoopSeconds) / LoopSeconds;
        for (int i = 0; i < BarCount; i++)
        {
            var loop = _loops[i];
            double from = loop[(int)Math.Round(phase * (loop.Length - 1))];
            var v = Anim.VisualOf(_bars[i]);
            v.StopAnimation("Scale.Y");
            Anim.SpringTo(v, "Scale.Y", from, MinLevel, Motion.FadeIn);
        }
    }

    /// <summary>The spring maths on a virtual clock (for pre-simulating the loop).</summary>
    private sealed class VirtualSpring
    {
        private readonly SpringPreset _p;
        private double _x0, _v0, _t0;
        public double Target { get; private set; }
        public VirtualSpring(SpringPreset p, double initial) { _p = p; Target = initial; }

        public double ValueAt(double t) => Target + SpringModel.Solve(_x0, _v0, Math.Max(0, t - _t0), _p).x;

        public void SetTarget(double target, double t)
        {
            var (x, v) = SpringModel.Solve(_x0, _v0, Math.Max(0, t - _t0), _p);
            double value = Target + x;
            Target = target;
            _x0 = value - target;
            _v0 = v;
            _t0 = t;
        }
    }
}
