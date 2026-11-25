using DynamicNotch.Design;
using Windows.Devices.Enumeration;

namespace DynamicNotch.Services;

/// <summary>
/// Pills for hardware coming and going that Windows tells apps about:
/// <list type="bullet">
/// <item>USB drives (and other removable storage) plugged in / removed: a DeviceWatcher on the removable storage
/// selector (event-driven; the devices present at start-up are only remembered, never announced).</item>
/// <item>Displays connected / disconnected: the monitor count when Windows broadcasts a display change (settled for a
/// moment, since a change arrives as several broadcasts).</item>
/// </list>
/// </summary>
public sealed class DeviceWatchService : IDisposable
{
    private DeviceWatcher? _usb;
    private readonly Dictionary<string, string> _drives = new();
    private bool _usbReady;
    private int _monitors;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _displaySettle;

    public event Action<PillRequest>? Pill;

    public void Start()
    {
        try
        {
            _usb = DeviceInformation.CreateWatcher(Windows.Devices.Portable.StorageDevice.GetDeviceSelector());
            _usb.Added += (_, d) => Ui.Post(() => OnDriveAdded(d));
            _usb.Removed += (_, u) => Ui.Post(() => OnDriveRemoved(u));
            _usb.EnumerationCompleted += (_, _) => Ui.Post(() => _usbReady = true);
            _usb.Start();
        }
        catch (Exception ex) { App.LogError(ex); }

        _monitors = Interop.Native.AllMonitors().Count;
        _displaySettle = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _displaySettle.IsRepeating = false;
        _displaySettle.Interval = TimeSpan.FromMilliseconds(1500);
        _displaySettle.Tick += (_, _) => { _displaySettle.Stop(); CheckDisplays(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDriveAdded(DeviceInformation d)
    {
        string name = string.IsNullOrWhiteSpace(d.Name) ? "USB drive" : d.Name;
        _drives[d.Id] = name;
        if (_usbReady) Pill?.Invoke(new PillRequest(PillSource.System, "USB drive connected", name, Icons.Usb, Anim: PillAnim.UsbIn));
    }

    private void OnDriveRemoved(DeviceInformationUpdate u)
    {
        if (!_drives.Remove(u.Id, out var name) || !_usbReady) return;
        Pill?.Invoke(new PillRequest(PillSource.System, "USB drive removed", name, Icons.Usb, Badge: "off", Anim: PillAnim.UsbOut));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Ui.Post(() => { _displaySettle?.Stop(); _displaySettle?.Start(); });

    private void CheckDisplays()
    {
        int n = Interop.Native.AllMonitors().Count;
        if (n == _monitors || n == 0) return;
        bool added = n > _monitors;
        _monitors = n;
        string count = n == 1 ? "1 display" : $"{n} displays";
        Pill?.Invoke(added
            ? new PillRequest(PillSource.System, "Display connected", count, Icons.Monitor, Anim: PillAnim.DisplayIn)
            : new PillRequest(PillSource.System, "Display disconnected", count, Icons.Monitor, Badge: "off", Anim: PillAnim.DisplayOut));
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        try { _usb?.Stop(); } catch { }
    }
}
