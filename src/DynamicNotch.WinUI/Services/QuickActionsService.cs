using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Radios;
using Windows.System.Power;

namespace DynamicNotch.Services;

/// <summary>
/// What the quick-action tiles control. Wi-Fi and Bluetooth go through Windows.Devices.Radios (the same
/// switches as Windows' own quick settings); the microphone through Core Audio (see AudioService).
/// Windows has no public way for an app to flip airplane mode, night light or energy saver, so those
/// tiles show what they can (energy saver's state) and open the matching Settings page. Cast opens
/// Windows' own Cast panel (Win+K). Everything is event-driven: radio and power state changes call back.
/// </summary>
public sealed class QuickActionsService : IDisposable
{
    private Radio? _wifi, _bluetooth;
    private bool _started;

    /// <summary>Raised on the UI thread when a radio or energy saver changes.</summary>
    public event Action? Changed;

    public bool HasWifi => _wifi != null;
    public bool HasBluetooth => _bluetooth != null;
    public bool WifiOn => _wifi?.State == RadioState.On;
    public bool BluetoothOn => _bluetooth?.State == RadioState.On;
    public bool EnergySaverOn
    {
        get { try { return PowerManager.EnergySaverStatus == EnergySaverStatus.On; } catch { return false; } }
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
            {
                foreach (var r in await Radio.GetRadiosAsync())
                {
                    if (r.Kind == RadioKind.WiFi && _wifi == null) _wifi = r;
                    else if (r.Kind == RadioKind.Bluetooth && _bluetooth == null) _bluetooth = r;
                }
                if (_wifi != null) _wifi.StateChanged += OnRadioChanged;
                if (_bluetooth != null) _bluetooth.StateChanged += OnRadioChanged;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Radios unavailable: {ex.Message}"); }
        try { PowerManager.EnergySaverStatusChanged += OnPowerChanged; } catch { }
        Changed?.Invoke();
    }

    private void OnRadioChanged(Radio sender, object args) => Ui.Post(() => Changed?.Invoke());
    private void OnPowerChanged(object? sender, object e) => Ui.Post(() => Changed?.Invoke());

    public Task SetWifiAsync(bool on) => SetAsync(_wifi, on, "ms-settings:network-wifi");
    public Task SetBluetoothAsync(bool on) => SetAsync(_bluetooth, on, "ms-settings:bluetooth");

    private async Task SetAsync(Radio? radio, bool on, string fallback)
    {
        if (radio == null) { Open(fallback); return; }
        try
        {
            var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            if (result != RadioAccessStatus.Allowed) Open(fallback);
        }
        catch { Open(fallback); }
        Changed?.Invoke();
    }

    public static void Open(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Windows' own Cast panel (Win+K), opened without the notch taking focus.</summary>
    public static void OpenCast() => PressWinKey(0x4B /* K */);

    private static void PressWinKey(byte key)
    {
        const byte VK_LWIN = 0x5B;
        const uint KEYUP = 0x0002;
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public void Dispose()
    {
        if (_wifi != null) _wifi.StateChanged -= OnRadioChanged;
        if (_bluetooth != null) _bluetooth.StateChanged -= OnRadioChanged;
        try { PowerManager.EnergySaverStatusChanged -= OnPowerChanged; } catch { }
    }
}
