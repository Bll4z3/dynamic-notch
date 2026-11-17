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

    /// <summary>How often the bars change (fps). 30 by default; Low power mode lowers it.</summary>
    public static double FrameRate { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable("NOTCH_VIS_FPS"), out var dev) ? dev : Motion.VisualizerFps; // env: dev A/B only

    /// <summary>Re-applies <see cref="FrameRate"/> (or a style change) to a running loop.</summary>
    public void Restart()
    {
        if (!_playing || !IsLoaded) return;
        Start();
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

    private string _style = "bars";

    /// <summary>"bars" (springy bars), "wave" (dots riding a travelling wave) or "off" (hidden, nothing animates).</summary>
    public string Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            bool wasPlaying = _playing && IsLoaded;
            if (wasPlaying) Stop();
            _style = value;
            Visibility = value == "off" ? Visibility.Collapsed : Visibility.Visible;
            foreach (var b in _bars)
            {
                b.Height = value == "wave" ? BarWidth : BarHeight;
                var v = Anim.VisualOf(b, translation: true);
                v.CenterPoint = new Vector3((float)BarWidth / 2, (float)b.Height / 2, 0);
                v.Scale = new Vector3(1, value == "wave" ? 1 : (float)MinLevel, 1);
                v.StopAnimation("Translation.Y");
                v.Properties.InsertVector3("Translation", Vector3.Zero);
            }
            if (wasPlaying) Start();
        }
    }

    private void Start()
    {
        _loopStart = SpringModel.Now;
        if (!Anim.Enabled || FrameRate <= 0 || _style == "off") return; // "Animation effects" off (or the visualiser is off): the bars stay still
        var c = Anim.VisualOf(_bars[0]).Compositor;
        if (_style == "wave") { StartWave(c); return; }
        // Measured: smooth 60 Hz bars kept the compositor redrawing the (large, transparent) host every
        // frame: ~20% of a core while music played with the notch collapsed. The bars are decorative, so
        // they hold each value for 1 / FrameRate s (step easing): the compositor only has new frames to
        // draw at that rate.
        var step = c.CreateStepEasingFunction(1);
        step.IsFinalStepSingleFrame = false;
        int stride = Math.Max(1, (int)Math.Round(SampleRate / Math.Max(1, FrameRate)));
        for (int i = 0; i < BarCount; i++)
        {
            var a = c.CreateScalarKeyFrameAnimation();
            var loop = _loops[i];
            for (int k = 0; k < loop.Length; k += stride) a.InsertKeyFrame((float)k / (loop.Length - 1), loop[k], step);
            a.InsertKeyFrame(1, loop[^1], step);
            a.Duration = TimeSpan.FromSeconds(LoopSeconds);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            Anim.VisualOf(_bars[i]).StartAnimation("Scale.Y", a);
        }
    }

    /// <summary>Wave style: each dot moves up and down a sine that travels across them (stepped at <see cref="FrameRate"/>).</summary>
    private void StartWave(Compositor c)
    {
        var step = c.CreateStepEasingFunction(1);
        step.IsFinalStepSingleFrame = false;
        const double period = 1.1, amp = (BarHeight - BarWidth) / 2;
        int keys = Math.Max(8, (int)Math.Round(period * Math.Max(1, FrameRate)));
        for (int i = 0; i < BarCount; i++)
        {
            var a = c.CreateScalarKeyFrameAnimation();
            for (int k = 0; k <= keys; k++)
            {
                double t = (double)k / keys;
                a.InsertKeyFrame((float)t, (float)(amp * Math.Sin(2 * Math.PI * t - i * 0.9)), step);
            }
            a.Duration = TimeSpan.FromSeconds(period);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            Anim.VisualOf(_bars[i], translation: true).StartAnimation("Translation.Y", a);
        }
    }

    private void Stop()
    {
        if (_style == "wave")
        {
            foreach (var b in _bars)
            {
                var v = Anim.VisualOf(b, translation: true);
                v.StopAnimation("Translation.Y");
                Anim.SpringTo(v, "Translation.Y", 0, 0, Motion.FadeIn);
            }
            return;
        }
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
