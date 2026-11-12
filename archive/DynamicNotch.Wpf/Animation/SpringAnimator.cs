using System.Diagnostics;
using System.Windows.Media;
using DynamicNotch.Interop;

namespace DynamicNotch.Animation;

/// <summary>
/// Drives every active <see cref="Spring"/> from CompositionTarget.Rendering. The render hook is
/// attached only while something is moving, so the app draws nothing (≈0% CPU) when idle.
/// </summary>
public sealed class SpringAnimator
{
    public static SpringAnimator Instance { get; } = new();

    private readonly List<Spring> _active = new();
    private readonly List<Action<double>> _frameCallbacks = new();
    private readonly Stopwatch _clock = new();
    private TimeSpan _last;
    private bool _hooked;

    /// <summary>Mirrors the Windows "Animation effects" setting (Settings ▸ Accessibility ▸ Visual effects).</summary>
    public static bool AnimationsEnabled { get; private set; } = true;

    public static void RefreshSystemSetting() => AnimationsEnabled = Native.ClientAreaAnimationsEnabled();

    /// <summary>Fires every frame while any spring is active (for derived drawing like the notch shape).</summary>
    public event Action? Frame;

    internal void Track(Spring s)
    {
        if (!_active.Contains(s)) _active.Add(s);
        Hook();
    }

    /// <summary>Register a per-frame callback that runs while it returns true (keeps the loop alive).</summary>
    public void RunWhile(Func<double, bool> tick)
    {
        Action<double>? cb = null;
        cb = dt => { if (!tick(dt)) _frameCallbacks.Remove(cb!); };
        _frameCallbacks.Add(cb);
        Hook();
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        _clock.Restart();
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long cbStart = Profile ? Stopwatch.GetTimestamp() : 0;
        // RenderingEventArgs.RenderingTime is vsync-aligned; fall back to the stopwatch.
        var now = e is RenderingEventArgs r ? r.RenderingTime : _clock.Elapsed;
        if (_last == TimeSpan.Zero || now <= _last) { _last = now; return; }
        double dt = Math.Min((now - _last).TotalSeconds, 1.0 / 20.0); // clamp after hitches
        _last = now;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (!_active[i].Step(dt)) _active.RemoveAt(i);
        }
        for (int i = _frameCallbacks.Count - 1; i >= 0; i--)
        {
            if (i < _frameCallbacks.Count) _frameCallbacks[i](dt);
        }

        Frame?.Invoke();

        if (Profile) { _intervals.Add(dt * 1000); _callbackMs.Add((Stopwatch.GetTimestamp() - cbStart) * 1000.0 / Stopwatch.Frequency); }

        if (_active.Count == 0 && _frameCallbacks.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
            if (Profile) WriteProfile();
        }
    }

    // ---- Opt-in frame profiler: set DYNAMICNOTCH_PROFILE=1 -------------------------------------
    // Each burst of animation appends frame-interval stats to %LocalAppData%\DynamicNotch\frames.log.
    private static readonly bool Profile = Environment.GetEnvironmentVariable("DYNAMICNOTCH_PROFILE") == "1";
    private readonly List<double> _intervals = new();
    private readonly List<double> _callbackMs = new();

    private void WriteProfile()
    {
        if (_intervals.Count < 5) { _intervals.Clear(); _callbackMs.Clear(); return; }
        var s = _intervals.OrderBy(x => x).ToList();
        double median = s[s.Count / 2];
        int dropped = _intervals.Count(x => x > median * 1.5);
        string line = $"[{DateTime.Now:HH:mm:ss}] frames={s.Count} median={median:F1}ms p95={s[(int)(s.Count * 0.95)]:F1}ms max={s[^1]:F1}ms long(>1.5x median)={dropped} renderTier={RenderCapability.Tier >> 16} ourCode avg={(_callbackMs.Count > 0 ? _callbackMs.Average() : 0):F2}ms max={(_callbackMs.Count > 0 ? _callbackMs.Max() : 0):F2}ms";
        _callbackMs.Clear();
        _intervals.Clear();
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicNotch");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "frames.log"), line + Environment.NewLine);
        }
        catch { }
    }
}
