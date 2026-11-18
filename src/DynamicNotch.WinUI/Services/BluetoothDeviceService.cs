using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DynamicNotch.Services;

/// <summary>A device row in the Bluetooth popup.</summary>
public sealed record BtDeviceItem(string Id, string Name, DeviceKind Kind, bool Paired, bool Connected, bool IsLe, bool IsAudio)
{
    public string Caption => Connected ? "Connected" : Paired ? "Paired" : "Available";
    /// <summary>Connect / disconnect works in-app for classic audio devices; everything else opens Settings.</summary>
    public bool CanToggleInApp => Paired && IsAudio && !IsLe;
}

public enum BtActionOutcome { Done, Failed, OpenedSettings }

/// <summary>
/// The Bluetooth popup's data. Paired devices (Classic and LE) and, separately, nearby unpaired devices
/// come from DeviceWatchers that run ONLY while the popup is open, so connects / disconnects animate live
/// and nothing runs in the background. Measured: creating and starting the four watchers costs 60–80 ms
/// and the per-device event burst ~120 ms of UI work, so watchers start and stop on a worker thread,
/// their events are queued and applied in one batch every <see cref="BatchMs"/> (one
/// <see cref="Changed"/> per batch), and paired devices stay cached between opens (and are primed once
/// with a single FindAllAsync that asks for every property up front), so the popup shows them instantly.
/// Pairing uses Windows' pairing flow (DeviceInformation.Pairing).
/// Connect / disconnect: Windows has no public API for this; for Classic audio devices (headphones,
/// speakers, headsets) the audio services are switched with BluetoothSetServiceState, which is what
/// connects or drops them. Other device types (mice, keyboards, LE devices) open Bluetooth settings.
/// </summary>
public sealed class BluetoothDeviceService : IDisposable
{
    private const string PropConnected = "System.Devices.Aep.IsConnected";
    private const string PropPaired = "System.Devices.Aep.IsPaired";
    private const string PropCodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string PropCodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string PropLeAppearance = "System.Devices.Aep.Bluetooth.Le.Appearance";
    private const string PropAddress = "System.Devices.Aep.DeviceAddress";

    private static readonly string[] Props = { PropConnected, PropPaired, PropCodMajor, PropCodMinor, PropLeAppearance, PropAddress };
    /// <summary>Watcher events are applied together this often (the first batch after a start comes sooner).</summary>
    public const int BatchMs = 120, FirstBatchMs = 40;

    private enum Op { Upsert, Update, Remove, PairedEnumerated }
    private readonly record struct Pending(int Gen, Op Op, bool IsLe, DeviceInformation? Info, DeviceInformationUpdate? Update, string Id);

    private readonly object _gate = new();
    private List<DeviceWatcher> _watchers = new();                 // guarded by _gate
    private readonly ConcurrentQueue<Pending> _queue = new();
    private int _generation, _flushScheduled, _pairedDone;
    private bool _watching, _primed, _firstBatch;
    private readonly HashSet<string> _seenThisRun = new();
    private readonly Dictionary<string, (DeviceInformation info, BtDeviceItem item)> _devices = new();

    /// <summary>Raised on the UI thread after a batch of devices appeared, changed or disappeared.</summary>
    public event Action? Changed;

    /// <summary>The known devices (UI thread). Paired ones are kept between opens; nearby ones only while watching.</summary>
    public IEnumerable<BtDeviceItem> Devices => _devices.Values.Select(d => d.item);
    public bool HasCache => _devices.Count > 0;
    /// <summary>Both paired watchers finished their first pass since the last start (the paired list is complete).</summary>
    public bool PairedEnumerated => Volatile.Read(ref _pairedDone) >= 2;

    // ---- Warm cache --------------------------------------------------------------------------------------
    /// <summary>Once per app run: one background FindAllAsync (all properties requested up front) fills the paired cache.</summary>
    public void Prime()
    {
        if (_primed) return;
        _primed = true;
        Task.Run(async () =>
        {
            foreach (var (aqs, isLe) in new[] { (BluetoothDevice.GetDeviceSelectorFromPairingState(true), false), (BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true) })
            {
                try
                {
                    foreach (var info in await DeviceInformation.FindAllAsync(aqs, Props, DeviceInformationKind.AssociationEndpoint))
                        Enqueue(new Pending(-1, Op.Upsert, isLe, info, null, info.Id));
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Bluetooth prime failed: {ex.Message}"); }
            }
        });
    }

    // ---- Watching (popup open only) ---------------------------------------------------------------------
    /// <summary>Starts the four watchers on a worker thread (returns at once).</summary>
    public void StartWatching()
    {
        if (_watching) return;
        _watching = true;
        _firstBatch = true;
        _seenThisRun.Clear();
        Volatile.Write(ref _pairedDone, 0);
        int gen = Interlocked.Increment(ref _generation);
        Task.Run(() =>
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _generation) != gen) return; // closed again before we got here
                Watch(gen, BluetoothDevice.GetDeviceSelectorFromPairingState(true), false, paired: true);
                Watch(gen, BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true, paired: true);
                Watch(gen, BluetoothDevice.GetDeviceSelectorFromPairingState(false), false, paired: false);   // nearby Classic
                Watch(gen, BluetoothLEDevice.GetDeviceSelectorFromPairingState(false), true, paired: false);  // nearby LE (named ones only)
            }
        });
    }

    /// <summary>Stops the watchers on a worker thread, drops queued events and nearby devices; paired ones stay cached.</summary>
    public void StopWatching()
    {
        if (!_watching) return;
        _watching = false;
        Interlocked.Increment(ref _generation);
        Task.Run(() =>
        {
            List<DeviceWatcher> old;
            lock (_gate) { old = _watchers; _watchers = new(); }
            foreach (var w in old)
            {
                try { if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch { }
            }
        });
        foreach (var id in _devices.Where(d => !d.Value.item.Paired).Select(d => d.Key).ToList()) _devices.Remove(id);
    }

    private void Watch(int gen, string aqs, bool isLe, bool paired)
    {
        try
        {
            var w = DeviceInformation.CreateWatcher(aqs, Props, DeviceInformationKind.AssociationEndpoint);
            w.Added += (_, info) => Enqueue(new Pending(gen, Op.Upsert, isLe, info, null, info.Id));
            w.Updated += (_, u) => Enqueue(new Pending(gen, Op.Update, isLe, null, u, u.Id));
            w.Removed += (_, u) => Enqueue(new Pending(gen, Op.Remove, isLe, null, null, u.Id));
            if (paired) w.EnumerationCompleted += (_, _) => Enqueue(new Pending(gen, Op.PairedEnumerated, isLe, null, null, ""));
            w.Start();
            _watchers.Add(w);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Bluetooth watcher failed: {ex.Message}"); }
    }

    /// <summary>Any thread: queue an event and make sure one flush is scheduled.</summary>
    private void Enqueue(Pending p)
    {
        _queue.Enqueue(p);
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1) return;
        int delay = _firstBatch ? FirstBatchMs : BatchMs;
        Task.Delay(delay).ContinueWith(_ => Ui.Post(Flush));
    }

    /// <summary>UI thread: apply everything queued, then raise <see cref="Changed"/> once.</summary>
    private void Flush()
    {
        Volatile.Write(ref _flushScheduled, 0);
        _firstBatch = false;
        int gen = Volatile.Read(ref _generation);
        bool changed = false;
        while (_queue.TryDequeue(out var p))
        {
            if (p.Gen != -1 && (p.Gen != gen || !_watching)) continue; // from a run that has been stopped
            switch (p.Op)
            {
                case Op.Upsert when p.Info != null:
                    if (p.Gen == -1 && _devices.ContainsKey(p.Id)) break; // a watcher already reported it (fresher)
                    if (p.Gen != -1) _seenThisRun.Add(p.Id);
                    changed |= Upsert(p.Info, p.IsLe);
                    break;
                case Op.Update when p.Update != null:
                    if (!_devices.TryGetValue(p.Id, out var d)) break;
                    _seenThisRun.Add(p.Id);
                    d.info.Update(p.Update);
                    changed |= Upsert(d.info, p.IsLe);
                    break;
                case Op.Remove:
                    changed |= _devices.Remove(p.Id);
                    break;
                case Op.PairedEnumerated:
                    if (Interlocked.Increment(ref _pairedDone) == 2)
                    {
                        // The paired list is complete: forget cached devices that were unpaired meanwhile.
                        foreach (var id in _devices.Where(x => x.Value.item.Paired && !_seenThisRun.Contains(x.Key)).Select(x => x.Key).ToList())
                            _devices.Remove(id);
                        changed = true; // listeners stop showing the "looking for devices" state
                    }
                    break;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private bool Upsert(DeviceInformation info, bool isLe)
    {
        if (string.IsNullOrWhiteSpace(info.Name)) return false; // anonymous LE beacons
        bool paired = info.Pairing?.IsPaired == true || Get(info, PropPaired) is true;
        bool connected = Get(info, PropConnected) is true;
        var kind = BluetoothService.Classify(info.Name, Get(info, PropCodMajor), Get(info, PropCodMinor), Get(info, PropLeAppearance));
        bool audio = kind is DeviceKind.Headphones or DeviceKind.Speaker;
        var item = new BtDeviceItem(info.Id, info.Name, kind, paired, connected, isLe, audio);
        bool same = _devices.TryGetValue(info.Id, out var old) && old.item == item;
        _devices[info.Id] = (info, item);
        return !same;
    }

    private static object? Get(DeviceInformation info, string key) => info.Properties.TryGetValue(key, out var v) ? v : null;

    // ---- Pair ------------------------------------------------------------------------------------------
    public async Task<BtActionOutcome> PairAsync(string id)
    {
        if (!_devices.TryGetValue(id, out var d)) return BtActionOutcome.Failed;
        try
        {
            var result = await d.info.Pairing.PairAsync();
            return result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired ? BtActionOutcome.Done : BtActionOutcome.Failed;
        }
        catch { return BtActionOutcome.Failed; }
    }

    // ---- Connect / disconnect ----------------------------------------------------------------------------
    /// <summary>Connects or disconnects a paired Classic audio device; other kinds open Bluetooth settings.</summary>
    public async Task<BtActionOutcome> SetConnectedAsync(string id, bool connect)
    {
        if (!_devices.TryGetValue(id, out var d) || !d.item.CanToggleInApp)
        {
            QuickActionsService.Open("ms-settings:bluetooth");
            return BtActionOutcome.OpenedSettings;
        }
        ulong address = 0;
        try
        {
            using var dev = await BluetoothDevice.FromIdAsync(id);
            address = dev?.BluetoothAddress ?? 0;
        }
        catch { }
        if (address == 0) return BtActionOutcome.Failed;
        bool ok = await Task.Run(() => SetAudioServices(address, connect));
        return ok ? BtActionOutcome.Done : BtActionOutcome.Failed;
    }

    /// <summary>
    /// Switches the Classic audio services (A2DP sink, AVRCP, Hands-Free, Headset) on or off for the device.
    /// Off drops the audio link (disconnect); on (after a reset) makes Windows connect it again.
    /// Returns true when at least one service changed.
    /// </summary>
    private static bool SetAudioServices(ulong address, bool enable)
    {
        var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr find = BluetoothFindFirstRadio(ref p, out IntPtr radio);
        if (find == IntPtr.Zero) return false;
        try
        {
            var info = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(), Address = address, szName = "" };
            if (BluetoothGetDeviceInfo(radio, ref info) != 0) return false;
            bool any = false;
            foreach (var svc in AudioServices)
            {
                var g = svc;
                if (enable) BluetoothSetServiceState(radio, ref info, ref g, BLUETOOTH_SERVICE_DISABLE); // reset, so enabling reconnects
                if (BluetoothSetServiceState(radio, ref info, ref g, enable ? BLUETOOTH_SERVICE_ENABLE : BLUETOOTH_SERVICE_DISABLE) == 0) any = true;
            }
            return any;
        }
        finally
        {
            CloseHandle(radio);
            BluetoothFindRadioClose(find);
        }
    }

    private static readonly Guid[] AudioServices =
    {
        new("0000110B-0000-1000-8000-00805F9B34FB"), // A2DP audio sink
        new("0000110E-0000-1000-8000-00805F9B34FB"), // AVRCP remote control
        new("0000111E-0000-1000-8000-00805F9B34FB"), // Hands-Free
        new("00001108-0000-1000-8000-00805F9B34FB"), // Headset
    };

    public void Dispose() => StopWatching();

    // ---- Win32 Bluetooth (bthprops.cpl) -------------------------------------------------------------------
    private const uint BLUETOOTH_SERVICE_DISABLE = 0, BLUETOOTH_SERVICE_ENABLE = 1;

    [StructLayout(LayoutKind.Sequential)] private struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }
    [StructLayout(LayoutKind.Sequential)] private struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected, fRemembered, fAuthenticated;
        public SYSTEMTIME stLastSeen, stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr radio);
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern bool BluetoothFindRadioClose(IntPtr find);
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern uint BluetoothGetDeviceInfo(IntPtr radio, ref BLUETOOTH_DEVICE_INFO info);
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern uint BluetoothSetServiceState(IntPtr radio, ref BLUETOOTH_DEVICE_INFO info, ref Guid service, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
}
