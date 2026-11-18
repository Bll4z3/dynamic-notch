using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.Services;

/// <summary>
/// Dev timing markers for one interaction at a time (e.g. "click → popup visible → list populated").
/// <see cref="Begin"/> starts a trace; <see cref="Mark"/> records elapsed ms; the first rendered frame and
/// frame statistics (count, mean, p95, longest gap) are recorded while the trace runs. The frame callback
/// is attached only during a trace (it forces rendering) and detached in <see cref="End"/>.
/// Off unless <see cref="Enabled"/> (dev flags only); writes %LocalAppData%\DynamicNotch\perf.log.
/// </summary>
public static class PerfLog
{
    public static bool Enabled { get; set; }

    private static readonly Stopwatch Clock = new();
    private static readonly List<(string label, double ms)> Marks = new();
    private static readonly List<double> Gaps = new();
    private static readonly HashSet<string> Once = new();
    private static string _name = "";
    private static bool _active, _framed, _hooked;
    private static double _lastFrame;

    public static void Begin(string name)
    {
        if (!Enabled) return;
        if (_active) End();
        _name = name;
        Marks.Clear();
        Once.Clear();
        Gaps.Clear();
        _framed = false;
        _lastFrame = 0;
        _active = true;
        Clock.Restart();
        if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; }
    }

    /// <summary>Records a marker (only the first time this label is hit in the trace when <paramref name="once"/>).</summary>
    public static void Mark(string label, bool once = true)
    {
        if (!Enabled || !_active) return;
        if (once && !Once.Add(label)) return;
        Marks.Add((label, Clock.Elapsed.TotalMilliseconds));
    }

    /// <summary>Times a synchronous block inside the trace.</summary>
    public static T Time<T>(string label, Func<T> f)
    {
        if (!Enabled || !_active) return f();
        double t0 = Clock.Elapsed.TotalMilliseconds;
        var r = f();
        Marks.Add((label + $" [{Clock.Elapsed.TotalMilliseconds - t0:0.0} ms]", Clock.Elapsed.TotalMilliseconds));
        return r;
    }

    public static void Time(string label, Action f) => Time(label, () => { f(); return 0; });

    private static void OnFrame(object? sender, object e)
    {
        if (!_active) return;
        double now = Clock.Elapsed.TotalMilliseconds;
        if (!_framed) { _framed = true; Marks.Add(("first frame after start", now)); }
        else
        {
            double gap = now - _lastFrame;
            Gaps.Add(gap);
            if (gap > 34) Marks.Add(($"!! frame gap {gap:0.0} ms (ended here)", now));
        }
        _lastFrame = now;
    }

    /// <summary>Ends the trace and appends it to perf.log.</summary>
    public static void End()
    {
        if (!Enabled || !_active) return;
        _active = false;
        if (_hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; }
        var lines = new List<string> { $"[{DateTime.Now:HH:mm:ss}] {_name}" };
        foreach (var (label, ms) in Marks.OrderBy(m => m.ms)) lines.Add($"  {ms,8:0.0} ms  {label}");
        if (Gaps.Count > 0)
        {
            var sorted = Gaps.OrderBy(g => g).ToList();
            double p95 = sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(sorted.Count * 0.95) - 1)];
            int dropped = Gaps.Count(g => g > 25);
            lines.Add($"  frames: {Gaps.Count + 1}, mean {Gaps.Average():0.0} ms, p95 {p95:0.0} ms, max {sorted[^1]:0.0} ms, >25 ms: {dropped}");
        }
        Write(lines);
    }

    /// <summary>A one-line note in perf.log (startup time, phase markers).</summary>
    public static void Note(string text)
    {
        if (Enabled) Write(new List<string> { $"[{DateTime.Now:HH:mm:ss}] {text}" });
    }

    private static void Write(List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataFolder);
            File.AppendAllLines(Path.Combine(SettingsService.DataFolder, "perf.log"), lines);
        }
        catch { }
        Debug.WriteLine(string.Join("\n", lines));
    }
}
