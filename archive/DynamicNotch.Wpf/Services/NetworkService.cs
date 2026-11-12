using System.Windows;
using System.Windows.Threading;
using DynamicNotch.Design;
using Windows.Networking.Connectivity;

namespace DynamicNotch.Services;

/// <summary>Wi-Fi connected / disconnected / network changed, via NetworkInformation.NetworkStatusChanged.</summary>
public sealed class NetworkService : IDisposable
{
    private string? _lastSsid;      // null = not on Wi-Fi
    private bool _started;
    private DispatcherTimer? _settle;

    public event Action<PillRequest>? Pill;

    public void Start()
    {
        _lastSsid = ReadWifiSsid();
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _settle.Tick += (_, _) => { _settle.Stop(); Evaluate(); };
        NetworkInformation.NetworkStatusChanged += OnStatusChanged;
        _started = true;
    }

    // The OS fires this several times per transition; wait for things to settle.
    private void OnStatusChanged(object? sender) =>
        Application.Current?.Dispatcher.InvokeAsync(() => { _settle?.Stop(); _settle?.Start(); });

    private void Evaluate()
    {
        var ssid = ReadWifiSsid();
        if (ssid == _lastSsid) return;
        var previous = _lastSsid;
        _lastSsid = ssid;

        if (ssid != null && previous == null)
            Pill?.Invoke(new PillRequest(PillSource.Network, "Wi-Fi connected", ssid, Icons.Wifi));
        else if (ssid == null && previous != null)
            Pill?.Invoke(new PillRequest(PillSource.Network, "Wi-Fi disconnected", previous, Icons.Wifi, Badge: "off"));
        else if (ssid != null)
            Pill?.Invoke(new PillRequest(PillSource.Network, "Network changed", ssid, Icons.Wifi));
    }

    /// <summary>Returns the connected Wi-Fi SSID, or null if the active connection isn't Wi-Fi.</summary>
    private static string? ReadWifiSsid()
    {
        try
        {
            // Prefer the internet profile; fall back to any connected WLAN profile.
            var profiles = new List<ConnectionProfile>();
            var inet = NetworkInformation.GetInternetConnectionProfile();
            if (inet != null) profiles.Add(inet);
            profiles.AddRange(NetworkInformation.GetConnectionProfiles());

            foreach (var p in profiles)
            {
                if (!p.IsWlanConnectionProfile) continue;
                var level = p.GetNetworkConnectivityLevel();
                if (level == NetworkConnectivityLevel.None) continue;
                string? ssid = null;
                // Windows 11 24H2+ may hide the SSID without location consent; ProfileName is usually the SSID.
                try { ssid = p.WlanConnectionProfileDetails?.GetConnectedSsid(); } catch { }
                if (string.IsNullOrEmpty(ssid)) ssid = p.ProfileName;
                return string.IsNullOrEmpty(ssid) ? "Wi-Fi" : ssid;
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        if (_started) NetworkInformation.NetworkStatusChanged -= OnStatusChanged;
    }
}
