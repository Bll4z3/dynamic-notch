using System.Windows;
using DynamicNotch.Design;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace DynamicNotch.Services;

public enum DeviceKind { Headphones, Speaker, Mouse, Keyboard, Controller, Phone, Watch, Generic }

/// <summary>A connected Bluetooth device as shown in the expanded notch.</summary>
public sealed record ConnectedDevice(string Id, string Name, DeviceKind Kind, int? Battery)
{
    public string Glyph => BluetoothService.GlyphFor(Kind);
}

/// <summary>
/// Watches paired Bluetooth Classic + LE association endpoints with DeviceWatcher.
/// - Keeps the list of connected devices (type from Class-of-Device / LE Appearance, name fallback).
/// - Battery from DEVPKEY_Bluetooth_Battery on the device's nodes (watched, so updates arrive as
///   events), falling back to the GATT Battery Service (read + notifications) for LE devices.
/// - Raises connect/disconnect pills and list events from the same transitions.
/// </summary>
public sealed class BluetoothService : IDisposable
{
    private const string PropConnected = "System.Devices.Aep.IsConnected";
    private const string PropContainer = "System.Devices.Aep.ContainerId";
    private const string PropCodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string PropCodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string PropLeAppearance = "System.Devices.Aep.Bluetooth.Le.Appearance";
    private const string PropBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // DEVPKEY_Bluetooth_Battery

    private sealed class DeviceState
    {
        public required string Id;
        public string Name = "";
        public bool IsLe;
        public bool Connected;
        public DeviceKind Kind = DeviceKind.Generic;
        public Guid? Container;
        public int? Battery;
        public DeviceWatcher? BatteryWatcher;
        public readonly Dictionary<string, int> NodeBattery = new();
        public BluetoothLEDevice? LeDevice;
        public GattCharacteristic? GattBattery;
        public CancellationTokenSource? GattCts;
    }

    private readonly Dictionary<string, DeviceState> _devices = new();
    private readonly List<DeviceWatcher> _watchers = new();
    private readonly object _gate = new();

    public event Action<PillRequest>? Pill;
    /// <summary>All list events are raised on the UI thread.</summary>
    public event Action<ConnectedDevice>? DeviceConnected;
    public event Action<string>? DeviceDisconnected;
    public event Action<string, int?>? BatteryChanged;

    public void Start()
    {
        var full = new[] { PropConnected, PropContainer, PropCodMajor, PropCodMinor, PropLeAppearance };
        var basic = new[] { PropConnected, PropContainer, PropCodMajor };
        StartWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true), false, full, basic);
        StartWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true, full, basic);
    }

    private void StartWatcher(string selector, bool isLe, string[] props, string[] fallbackProps)
    {
        // Older builds may not know every Aep.Bluetooth property; retry with the basic set.
        if (TryStart(selector, isLe, props)) return;
        if (!TryStart(selector, isLe, fallbackProps))
            System.Diagnostics.Debug.WriteLine("Bluetooth watcher unavailable");
    }

    private bool TryStart(string selector, bool isLe, string[] props)
    {
        try
        {
            var w = DeviceInformation.CreateWatcher(selector, props, DeviceInformationKind.AssociationEndpoint);
            w.Added += (_, info) => OnAdded(info, isLe);
            w.Updated += (_, update) => OnUpdated(update);
            w.Removed += (_, update) => OnRemoved(update.Id);
            w.Start();
            _watchers.Add(w);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bluetooth watcher failed: {ex.Message}");
            return false;
        }
    }

    // ---- Watcher events (background threads) ---------------------------------------------------
    private void OnAdded(DeviceInformation info, bool isLe)
    {
        DeviceState st;
        lock (_gate)
        {
            st = new DeviceState
            {
                Id = info.Id,
                Name = info.Name,
                IsLe = isLe,
                Connected = info.Properties.TryGetValue(PropConnected, out var c) && c is true,
                Container = info.Properties.TryGetValue(PropContainer, out var g) && g is Guid guid ? guid : null,
            };
            st.Kind = Classify(info.Name,
                Get(info.Properties, PropCodMajor), Get(info.Properties, PropCodMinor), Get(info.Properties, PropLeAppearance));
            _devices[info.Id] = st;
        }
        // Devices that are already connected at start-up appear in the row silently (no pill).
        if (st.Connected) OnConnected(st, announce: false);
    }

    private void OnUpdated(DeviceInformationUpdate update)
    {
        if (!update.Properties.TryGetValue(PropConnected, out var c) || c is not bool connected) return;
        DeviceState? st;
        lock (_gate)
        {
            if (!_devices.TryGetValue(update.Id, out st) || st.Connected == connected) return;
            st.Connected = connected;
        }
        if (connected) OnConnected(st, announce: true);
        else OnDisconnected(st, announce: true);
    }

    private void OnRemoved(string id)
    {
        DeviceState? st;
        lock (_gate)
        {
            if (!_devices.Remove(id, out st)) return;
        }
        if (st.Connected) OnDisconnected(st, announce: false);
    }

    private void OnConnected(DeviceState st, bool announce)
    {
        Ui(() => DeviceConnected?.Invoke(new ConnectedDevice(st.Id, st.Name, st.Kind, st.Battery)));
        StartBatteryTracking(st);
        if (announce) _ = AnnounceConnectedAsync(st);
    }

    private void OnDisconnected(DeviceState st, bool announce)
    {
        StopBatteryTracking(st);
        Ui(() => DeviceDisconnected?.Invoke(st.Id));
        if (announce)
            Ui(() => Pill?.Invoke(new PillRequest(PillSource.Device, st.Name, "Disconnected", GlyphFor(st.Kind), Badge: "off")));
    }

    private async Task AnnounceConnectedAsync(DeviceState st)
    {
        // Give the battery a moment to arrive so the pill can show it.
        for (int i = 0; i < 8 && st.Battery == null; i++) await Task.Delay(150);
        var badge = st.Battery is int b ? $"{b}%" : null;
        Ui(() => Pill?.Invoke(new PillRequest(PillSource.Device, st.Name, "Connected", GlyphFor(st.Kind), Badge: badge)));
    }

    // ---- Battery: DEVPKEY via a per-device watcher, GATT Battery Service fallback --------------
    private void StartBatteryTracking(DeviceState st)
    {
        if (st.Container is Guid id && st.BatteryWatcher == null)
        {
            try
            {
                var aqs = $"System.Devices.ContainerId:=\"{{{id}}}\"";
                var w = DeviceInformation.CreateWatcher(aqs, new[] { PropBattery }, DeviceInformationKind.Device);
                w.Added += (_, info) => OnBatteryNode(st, info.Id, info.Properties);
                w.Updated += (_, u) => OnBatteryNode(st, u.Id, u.Properties);
                w.Removed += (_, u) => { lock (_gate) st.NodeBattery.Remove(u.Id); };
                st.BatteryWatcher = w;
                w.Start();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Battery watcher failed: {ex.Message}"); }
        }

        if (st.IsLe)
        {
            st.GattCts = new CancellationTokenSource();
            _ = TryGattBatteryAsync(st, st.GattCts.Token);
        }
    }

    private void OnBatteryNode(DeviceState st, string nodeId, IReadOnlyDictionary<string, object> props)
    {
        if (!props.TryGetValue(PropBattery, out var v) || v is not byte b || b > 100) return;
        lock (_gate) st.NodeBattery[nodeId] = b;
        SetBattery(st, st.NodeBattery.Values.Min());
    }

    private async Task TryGattBatteryAsync(DeviceState st, CancellationToken ct)
    {
        try
        {
            // Prefer DEVPKEY if the driver publishes it; only go to GATT when it doesn't.
            await Task.Delay(2000, ct);
            if (st.Battery != null || !st.Connected) return;

            st.LeDevice = await BluetoothLEDevice.FromIdAsync(st.Id);
            if (st.LeDevice == null || ct.IsCancellationRequested) return;
            var services = await st.LeDevice.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) return;
            var chars = await services.Services[0].GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel);
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return;

            var ch = chars.Characteristics[0];
            st.GattBattery = ch;
            var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status == GattCommunicationStatus.Success) SetBattery(st, ReadByte(read.Value));

            if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                ch.ValueChanged += (_, args) => SetBattery(st, ReadByte(args.CharacteristicValue));
                await ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GATT battery unavailable for {st.Name}: {ex.Message}"); }
    }

    private static int? ReadByte(IBuffer buffer)
    {
        if (buffer.Length < 1) return null;
        using var reader = DataReader.FromBuffer(buffer);
        int v = reader.ReadByte();
        return v <= 100 ? v : null;
    }

    private void SetBattery(DeviceState st, int? value)
    {
        if (value == null || st.Battery == value) return;
        st.Battery = value;
        Ui(() => BatteryChanged?.Invoke(st.Id, value));
    }

    private void StopBatteryTracking(DeviceState st)
    {
        try
        {
            if (st.BatteryWatcher is { } w &&
                w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop();
        }
        catch { }
        st.BatteryWatcher = null;
        st.GattCts?.Cancel();
        st.GattCts = null;
        st.GattBattery = null;
        st.LeDevice?.Dispose();
        st.LeDevice = null;
        lock (_gate) st.NodeBattery.Clear();
        st.Battery = null;
    }

    // ---- Classification ----------------------------------------------------------------------
    private static object? Get(IReadOnlyDictionary<string, object> p, string key) => p.TryGetValue(key, out var v) ? v : null;

    private static int? AsInt(object? o) => o switch
    {
        byte b => b, ushort us => us, uint u => (int)u, int i => i, short s => s, _ => null,
    };

    /// <summary>Device type from Bluetooth Class-of-Device (Classic) or Appearance (LE), then the name.</summary>
    internal static DeviceKind Classify(string name, object? codMajor, object? codMinor, object? leAppearance)
    {
        int? major = AsInt(codMajor), minor = AsInt(codMinor), appearance = AsInt(leAppearance);

        if (major is int mj)
        {
            switch (mj)
            {
                case 2: return DeviceKind.Phone;
                case 4: // Audio/Video
                    return minor is 5 or 7 or 8 or 10 ? DeviceKind.Speaker : DeviceKind.Headphones;
                case 5: // Peripheral: bits 4-5 keyboard/pointer, bits 0-3 sub-type
                    if (minor is int mn)
                    {
                        if ((mn & 0x0F) is 1 or 2) return DeviceKind.Controller;
                        int kb = (mn >> 4) & 0x3;
                        if (kb is 1 or 3) return DeviceKind.Keyboard;
                        if (kb == 2) return DeviceKind.Mouse;
                    }
                    break;
                case 7: return DeviceKind.Watch; // Wearable
            }
        }

        if (appearance is int ap && ap > 0)
        {
            int category = ap >> 6, sub = ap & 0x3F;
            switch (category)
            {
                case 0x001: return DeviceKind.Phone;
                case 0x003: return DeviceKind.Watch;
                case 0x00F: // HID
                    return sub switch { 1 => DeviceKind.Keyboard, 2 => DeviceKind.Mouse, 3 or 4 => DeviceKind.Controller, _ => DeviceKind.Generic };
                case 0x021: return DeviceKind.Speaker;     // Audio sink
                case 0x025: return DeviceKind.Headphones;  // Wearable audio (earbuds, headset, headphones)
            }
        }

        var n = (name ?? "").ToLowerInvariant();
        bool Has(params string[] words) => words.Any(n.Contains);
        if (Has("mouse", "mx master", "mx anywhere")) return DeviceKind.Mouse;
        if (Has("keyboard", "keys", "mx keys")) return DeviceKind.Keyboard;
        if (Has("controller", "xbox", "dualsense", "dualshock", "gamepad", "joy-con", "pro controller")) return DeviceKind.Controller;
        if (Has("watch", "band", "fitbit", "garmin")) return DeviceKind.Watch;
        if (Has("speaker", "soundbar", "soundlink", "flip", "charge", "boom", "sonos", "homepod")) return DeviceKind.Speaker;
        if (Has("bud", "airpod", "headphone", "headset", "earbud", "wh-", "wf-", "beats", "jabra", "pods", "soundcore", "bose qc")) return DeviceKind.Headphones;
        if (Has("iphone", "galaxy", "pixel", "phone", "oneplus")) return DeviceKind.Phone;
        return DeviceKind.Generic;
    }

    public static string GlyphFor(DeviceKind k) => k switch
    {
        DeviceKind.Headphones => Icons.Headphone,
        DeviceKind.Speaker => Icons.Speaker,
        DeviceKind.Mouse => Icons.Mouse,
        DeviceKind.Keyboard => Icons.Keyboard,
        DeviceKind.Controller => Icons.Controller,
        DeviceKind.Phone => Icons.Phone,
        DeviceKind.Watch => Icons.Watch,
        _ => Icons.Bluetooth,
    };

    private static void Ui(Action a) => Application.Current?.Dispatcher.InvokeAsync(a);

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try { if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch { }
        }
        lock (_gate)
        {
            foreach (var st in _devices.Values.ToList()) StopBatteryTracking(st);
        }
    }
}
