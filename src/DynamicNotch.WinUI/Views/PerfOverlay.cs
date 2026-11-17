using System.Diagnostics;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace DynamicNotch.Views;

/// <summary>
/// Hidden debug overlay (`--perf-overlay`): FPS, mean / worst frame time, process CPU and working set,
/// refreshed twice a second in a small topmost window at the bottom-left of the primary monitor.
/// Note: counting frames subscribes to CompositionTarget.Rendering, which keeps XAML rendering every
/// frame, so CPU shown while the overlay is on is higher than the real idle CPU.
/// </summary>
internal sealed class PerfOverlay : Window
{
    private static PerfOverlay? _instance;
    private readonly TextBlock _text = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _proc = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private double _lastT, _lastFrame, _worst, _sum;
    private int _frames;

    public static void Show()
    {
        if (_instance != null) return;
        _instance = new PerfOverlay();
    }

    private PerfOverlay()
    {
        Title = "Notch perf";
        Content = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD0, 0, 0, 0)), Padding = new Thickness(10, 6, 10, 6), Child = _text };
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        var hwnd = WindowNative.GetWindowHandle(this);
        Native.SetExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST, Native.WS_EX_APPWINDOW);
        var (bounds, scale) = Native.GetPrimaryMonitor();
        int w = (int)(250 * scale), h = (int)(64 * scale);
        AppWindow.MoveAndResize(new RectInt32(bounds.Left + (int)(12 * scale), bounds.Top + bounds.Height - h - (int)(60 * scale), w, h));
        AppWindow.Show(activateWindow: false);
        _lastCpu = _proc.TotalProcessorTime;
        CompositionTarget.Rendering += OnFrame;
        var t = DispatcherQueue.GetForCurrentThread().CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(500);
        t.Tick += (_, _) => Update();
        t.Start();
    }

    private void OnFrame(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_lastFrame > 0) { double d = now - _lastFrame; _sum += d; _worst = Math.Max(_worst, d); _frames++; }
        _lastFrame = now;
    }

    private void Update()
    {
        double t = _clock.Elapsed.TotalSeconds, dt = t - _lastT;
        _proc.Refresh();
        var cpu = _proc.TotalProcessorTime;
        double cpuPct = (cpu - _lastCpu).TotalSeconds / dt / Environment.ProcessorCount * 100;
        _text.Text = $"{_frames / dt,5:0} fps  {(_frames > 0 ? _sum / _frames : 0),5:0.0} ms avg  {_worst,5:0.0} max\n" +
                     $"CPU {cpuPct,5:0.0}%   WS {_proc.WorkingSet64 / 1048576.0,6:0.0} MB";
        _lastCpu = cpu;
        _lastT = t;
        _frames = 0;
        _sum = _worst = 0;
    }
}
