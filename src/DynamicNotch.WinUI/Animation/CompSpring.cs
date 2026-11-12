using DynamicNotch.Design;
using Microsoft.UI.Composition;

namespace DynamicNotch.Animation;

/// <summary>
/// A <see cref="SpringModel"/> played by the compositor. Every retarget bakes the spring's exact
/// trajectory (from the current value and velocity) into a keyframe animation on one scalar property
/// (usually on a <see cref="CompositionPropertySet"/> that expressions read). The compositor then
/// produces every frame on its own thread, so motion holds the refresh rate even when the UI thread
/// is busy, and the app pays nothing per frame. Because the model is the same maths, <see cref="Value"/>
/// always tells the app where the animation is right now.
/// </summary>
public sealed class CompSpring
{
    /// <summary>Keyframes per second of trajectory (linear in between: indistinguishable from the curve).</summary>
    private const double KeysPerSecond = 60;

    private readonly CompositionObject _target;
    private readonly string _property;
    private readonly SpringModel _model;
    public CompSpring(CompositionObject target, string property, SpringPreset preset, double initial = 0, double precision = 1)
    {
        _target = target;
        _property = property;
        _model = new SpringModel(preset, initial) { Precision = precision };
        if (target is CompositionPropertySet set) set.InsertScalar(property, (float)initial);
        else Anim.SetScalar(target, property, initial);
    }

    public SpringModel Model => _model;
    public double Value => _model.Value;
    public double Target => _model.Target;
    public bool IsSettled => _model.IsSettled;

    /// <summary>Raised after every retarget / snap / impulse (the app re-plans threshold timers from here).</summary>
    public event Action? Changed;

    public void SetTarget(double target, SpringPreset? preset = null, double delay = 0)
    {
        if (!_model.SetTarget(target, preset, delay)) return;
        Play();
    }

    public void Snap(double value)
    {
        _model.Snap(value);
        _target.StopAnimation(_property);
        Anim.SetScalar(_target, _property, value);
        Changed?.Invoke();
    }

    public void Impulse(double velocity)
    {
        _model.Impulse(velocity);
        Play();
    }

    private void Play()
    {
        if (!Anim.Enabled)
        {
            Snap(_model.Target);
            return;
        }
        double duration = _model.RemainingDuration;
        if (duration <= 0.001)
        {
            Snap(_model.Target);
            return;
        }
        _target.StartAnimation(_property, Anim.Bake(_target.Compositor, _model, duration, KeysPerSecond));
        Changed?.Invoke();
    }
}
