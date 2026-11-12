using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using DynamicNotch.Design;

namespace DynamicNotch.Services;

/// <summary>
/// Detects VPN up/down from NetworkChange events. A VPN is any operational adapter that is a
/// PPP/RAS connection (built-in Windows VPN) or whose driver looks like a tunnel (WireGuard,
/// OpenVPN/TAP, Wintun, AnyConnect, Fortinet, GlobalProtect, ...).
/// </summary>
public sealed class VpnService : IDisposable
{
    private static readonly string[] Markers =
    {
        "vpn", "wireguard", "wintun", "tap-windows", "tap adapter", "openvpn", "anyconnect", "fortinet",
        "fortissl", "globalprotect", "pangp", "juniper", "pulse secure", "zscaler", "nordlynx", "tunnel",
        "sonicwall", "checkpoint", "f5 ", "expressvpn", "protonvpn", "mullvad", "tailscale", "zerotier",
    };

    private HashSet<string> _active = new();
    private DispatcherTimer? _settle;

    public event Action<PillRequest>? Pill;

    public void Start()
    {
        _active = Snapshot();
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _settle.Tick += (_, _) => { _settle.Stop(); Evaluate(); };
        NetworkChange.NetworkAddressChanged += OnChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailability;
    }

    private void OnChanged(object? s, EventArgs e) => Kick();
    private void OnAvailability(object? s, NetworkAvailabilityEventArgs e) => Kick();
    private void Kick() => Application.Current?.Dispatcher.InvokeAsync(() => { _settle?.Stop(); _settle?.Start(); });

    private void Evaluate()
    {
        var now = Snapshot();
        foreach (var name in now.Except(_active))
            Pill?.Invoke(new PillRequest(PillSource.Vpn, "VPN connected", name, Icons.Vpn));
        foreach (var name in _active.Except(now))
            Pill?.Invoke(new PillRequest(PillSource.Vpn, "VPN disconnected", name, Icons.Vpn, Badge: "off"));
        _active = now;
    }

    private static HashSet<string> Snapshot()
    {
        var set = new HashSet<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (IsVpn(nic)) set.Add(nic.Name);
            }
        }
        catch { }
        return set;
    }

    private static bool IsVpn(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Ppp) return true; // RAS / built-in VPN
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
        var text = (nic.Description + " " + nic.Name).ToLowerInvariant();
        // Exclude Hyper-V / WSL virtual switches and Teredo/ISATAP tunnels that are not VPNs.
        if (text.Contains("hyper-v") || text.Contains("teredo") || text.Contains("isatap") || text.Contains("6to4")) return false;
        return Markers.Any(text.Contains);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
    }
}
