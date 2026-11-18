using System.Management;

namespace DynamicNotch.Services;

/// <summary>
/// Brightness of the built-in laptop panel through WMI (root\wmi): WmiMonitorBrightness for the level,
/// WmiMonitorBrightnessEvent for changes (event-driven, no polling) and WmiSetBrightness to set it.
/// External monitors don't report through WMI: with no internal panel <see cref="IsSupported"/> stays
/// false and the brightness pill and slider simply don't appear. All WMI work runs off the UI thread.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private const string Scope = @"root\wmi";
    private ManagementEventWatcher? _watcher;
    private volatile bool _disposed;
    private int _pendingSet = -1;
    private int _setting; // 0 / 1: a set is running on the worker

    public bool IsSupported { get; private set; }
    public int Level { get; private set; }

    /// <summary>Raised on the UI thread once support is known (true/false).</summary>
    public event Action<bool>? SupportChanged;
    /// <summary>Raised on the UI thread when the panel brightness changes (0..100). Not at start-up.</summary>
    public event Action<int>? Changed;

    public void Start() => ThreadPool.QueueUserWorkItem(_ =>
    {
        int? level = Read();
        if (_disposed) return;
        bool supported = level != null;
        if (supported)
        {
            try
            {
                _watcher = new ManagementEventWatcher(new ManagementScope(Scope), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                _watcher.EventArrived += OnEvent;
                _watcher.Start();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Brightness events unavailable: {ex.Message}"); }
        }
        Ui.Post(() =>
        {
            IsSupported = supported;
            Level = level ?? 0;
            SupportChanged?.Invoke(supported);
        });
    });

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            int level = Convert.ToInt32(e.NewEvent["Brightness"]);
            Ui.Post(() =>
            {
                if (level == Level) return;
                Level = level;
                Changed?.Invoke(level);
            });
        }
        catch { }
    }

    /// <summary>Sets the panel brightness (0..100). Calls are coalesced: dragging never queues a backlog.</summary>
    public void Set(int level)
    {
        if (!IsSupported) return;
        Interlocked.Exchange(ref _pendingSet, Math.Clamp(level, 0, 100));
        if (Interlocked.CompareExchange(ref _setting, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                int next;
                while ((next = Interlocked.Exchange(ref _pendingSet, -1)) >= 0) Write(next);
            }
            finally { Interlocked.Exchange(ref _setting, 0); }
        });
    }

    private static int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (ManagementObject o in searcher.Get())
                using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    private static void Write(int level)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            foreach (ManagementObject o in searcher.Get())
                using (o) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)level });
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch { }
        _watcher = null;
    }
}
