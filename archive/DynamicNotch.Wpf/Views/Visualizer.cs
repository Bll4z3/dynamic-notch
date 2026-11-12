using System.Windows;
using System.Windows.Media;
using DynamicNotch.Animation;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// Animated audio bars for the collapsed notch. Each bar is a wobbly spring re-targeted to a new
/// random height a few times a second while playing; when paused the bars settle and the render
/// loop stops entirely.
/// </summary>
public sealed class Visualizer : FrameworkElement
{
    private const int BarCount = 4;
    private const double BarWidth = 3, Gap = 2.5, MinLevel = 0.18;

    private readonly Spring[] _bars = new Spring[BarCount];
    private readonly Random _rng = new();
    private bool _running;
    private double _sinceRetarget;
    private double _sinceRedraw;
    private int _generation;

    public Visualizer()
    {
        Width = BarCount * BarWidth + (BarCount - 1) * Gap;
        Height = 14;
        IsHitTestVisible = false;
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i] = new Spring(Motion.Wobble, MinLevel);
            _bars[i].ValueChanged += _ => { if (!_running) InvalidateVisual(); }; // while playing, Tick redraws at a capped rate
        }
    }

    public Brush Fill { get; set; } = Theme.PrimaryText;

    public bool IsPlaying
    {
        get => _running;
        set
        {
            if (_running == value) return;
            _running = value;
            if (value)
            {
                int gen = ++_generation;
                SpringAnimator.Instance.RunWhile(dt => gen == _generation && Tick(dt));
            }
            else
            {
                foreach (var b in _bars) b.SetTarget(MinLevel, Motion.FadeIn);
            }
        }
    }

    private bool Tick(double dt)
    {
        if (!_running) return false;
        _sinceRedraw += dt;
        if (_sinceRedraw >= 1.0 / Motion.VisualizerFps) { _sinceRedraw = 0; InvalidateVisual(); }
        _sinceRetarget += dt;
        if (_sinceRetarget > 0.11)
        {
            _sinceRetarget = 0;
            for (int i = 0; i < BarCount; i++)
            {
                double target = MinLevel + _rng.NextDouble() * (1 - MinLevel);
                _bars[i].SetTarget(target, Motion.Wobble);
            }
        }
        return _running;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double h = ActualHeight;
        double radius = Math.Min(Theme.CornerRadius, BarWidth / 2);
        for (int i = 0; i < BarCount; i++)
        {
            double level = Math.Clamp(_bars[i].Value, 0.05, 1.1);
            double bh = Math.Max(BarWidth, level * h);
            double x = i * (BarWidth + Gap);
            dc.DrawRoundedRectangle(Fill, null, new Rect(x, (h - bh) / 2, BarWidth, bh), radius, radius);
        }
    }
}
