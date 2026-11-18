using System.Diagnostics;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace DynamicNotch.Services;

public enum WifiAvailability { Unknown, Ready, NoAdapter, AccessDenied, Unavailable }

/// <summary>One network in the Wi-Fi popup (de-duplicated by SSID, strongest signal kept).</summary>
public sealed record WifiNetworkItem(string Ssid, byte Bars, bool Secured, bool Connected, bool Saved, WiFiAvailableNetwork Network)
{
    public string Caption => Connected ? "Connected" : Saved ? "Saved" : Secured ? "Secured" : "Open";
}

public enum WifiConnectOutcome { Connected, NeedsPassword, WrongPassword, Failed }

/// <summary>
/// The Wi-Fi popup's data, through Windows.Devices.WiFi.WiFiAdapter: scan, list, connect (first without a
/// password, which works for open networks and saved profiles; then with one), disconnect. Local radio
/// only, no network requests. Scans happen only while the popup is open (on open and every
/// <see cref="Design.Motion.WifiRescanMs"/>), never in the background. Windows 11 hides network names from
/// desktop apps without location permission; that case is reported as <see cref="NamesHidden"/>.
/// </summary>
public sealed class WifiService
{
    private WiFiAdapter? _adapter;
    private bool _initialised;

    public WifiAvailability Availability { get; private set; }
    public string? ConnectedSsid { get; private set; }
    /// <summary>The last scan saw networks but Windows withheld their names (location permission is off).</summary>
    public bool NamesHidden { get; private set; }

    public async Task<WifiAvailability> InitialiseAsync()
    {
        if (_initialised) return Availability;
        _initialised = true;
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed) return Availability = WifiAvailability.AccessDenied;
            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            _adapter = adapters.FirstOrDefault();
            Availability = _adapter == null ? WifiAvailability.NoAdapter : WifiAvailability.Ready;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WiFiAdapter unavailable: {ex.Message}");
            Availability = WifiAvailability.Unavailable;
        }
        return Availability;
    }

    /// <summary>The last list read (shown at once when the popup opens; empty until the first read).</summary>
    public IReadOnlyList<WifiNetworkItem> Cached { get; private set; } = Array.Empty<WifiNetworkItem>();

    /// <summary>Page 4 came into view: initialise and read Windows' last report in the background (no scan).</summary>
    public async Task WarmAsync()
    {
        if (await InitialiseAsync() == WifiAvailability.Ready && Cached.Count == 0) await ReadAsync();
    }

    /// <summary>Scans, then returns the networks: connected first, then by signal; hidden networks without a name are skipped.</summary>
    public async Task<IReadOnlyList<WifiNetworkItem>> ScanAsync()
    {
        if (_adapter == null) return Array.Empty<WifiNetworkItem>();
        try { await _adapter.ScanAsync(); } catch { /* radio off or busy: use the last report */ }
        return await ReadAsync();
    }

    /// <summary>
    /// Reads the adapter's current report WITHOUT scanning (Windows keeps the last scan's results). The
    /// parsing and the saved-profile lookup (a synchronous OS call) run on a worker thread.
    /// </summary>
    public async Task<IReadOnlyList<WifiNetworkItem>> ReadAsync()
    {
        var adapter = _adapter;
        if (adapter == null) return Cached;
        var ssid = await ReadConnectedSsidAsync();
        var (list, hidden) = await Task.Run(() =>
        {
            var saved = SavedProfileNames();
            var raw = adapter.NetworkReport.AvailableNetworks;
            bool namesHidden = raw.Count > 0 && raw.All(n => string.IsNullOrEmpty(n.Ssid));
            IReadOnlyList<WifiNetworkItem> items = raw
                .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                .Select(n => new WifiNetworkItem(n.Ssid, n.SignalBars, IsSecured(n), n.Ssid == ssid, saved.Contains(n.Ssid), n))
                .OrderByDescending(n => n.Connected).ThenByDescending(n => n.Bars).ThenBy(n => n.Ssid, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return (items, namesHidden);
        });
        ConnectedSsid = ssid;
        NamesHidden = hidden;
        return Cached = list;
    }

    private static bool IsSecured(WiFiAvailableNetwork n) =>
        n.SecuritySettings.NetworkAuthenticationType is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None) ||
        n.SecuritySettings.NetworkEncryptionType is not NetworkEncryptionType.None;

    private async Task<string?> ReadConnectedSsidAsync()
    {
        try
        {
            if (_adapter == null) return null;
            var profile = await _adapter.NetworkAdapter.GetConnectedProfileAsync();
            if (profile == null || !profile.IsWlanConnectionProfile) return null;
            string? ssid = null;
            try { ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid(); } catch { }
            return string.IsNullOrEmpty(ssid) ? profile.ProfileName : ssid;
        }
        catch { return null; }
    }

    /// <summary>Wi-Fi profiles Windows has saved (their names are the SSIDs, which is how Windows names them).</summary>
    private static HashSet<string> SavedProfileNames()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var p in NetworkInformation.GetConnectionProfiles())
                if (p.IsWlanConnectionProfile && !string.IsNullOrEmpty(p.ProfileName)) set.Add(p.ProfileName);
        }
        catch { }
        return set;
    }

    /// <summary>Connects. Without a password first (open networks and saved profiles); a secured unknown network asks for one.</summary>
    public async Task<WifiConnectOutcome> ConnectAsync(WifiNetworkItem item, string? password = null, bool automatic = true)
    {
        if (_adapter == null) return WifiConnectOutcome.Failed;
        try
        {
            var kind = automatic ? WiFiReconnectionKind.Automatic : WiFiReconnectionKind.Manual;
            WiFiConnectionResult result = password == null
                ? await _adapter.ConnectAsync(item.Network, kind)
                : await _adapter.ConnectAsync(item.Network, kind, new PasswordCredential { Password = password });
            switch (result.ConnectionStatus)
            {
                case WiFiConnectionStatus.Success:
                    ConnectedSsid = item.Ssid;
                    return WifiConnectOutcome.Connected;
                case WiFiConnectionStatus.InvalidCredential:
                    return password == null ? WifiConnectOutcome.NeedsPassword : WifiConnectOutcome.WrongPassword;
                default:
                    // Unknown secured networks often fail generically without a key: ask for one.
                    return password == null && item.Secured && !item.Saved ? WifiConnectOutcome.NeedsPassword : WifiConnectOutcome.Failed;
            }
        }
        catch { return WifiConnectOutcome.Failed; }
    }

    public void Disconnect()
    {
        try { _adapter?.Disconnect(); ConnectedSsid = null; } catch { }
    }
}
