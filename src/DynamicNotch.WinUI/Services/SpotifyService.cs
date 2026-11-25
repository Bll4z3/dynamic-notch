using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DynamicNotch.Services;

/// <summary>
/// Your Spotify account, for the media player's Like button: the current track and whether it's in your Liked
/// Songs, and saving / removing it. Sign-in is OAuth 2.0 with PKCE (no client secret on this PC): your browser
/// opens Spotify's sign-in page, Spotify redirects to http://127.0.0.1:43821/callback, which this app listens
/// on just for that moment, and the code is exchanged for tokens. Only the refresh token is saved, encrypted
/// for your Windows account (DPAPI, the same protection as the Claude key); access tokens live in memory.
/// It needs your own Spotify app's Client ID (Settings ▸ General ▸ Spotify).
/// </summary>
public sealed class SpotifyService
{
    public const int CallbackPort = 43821;
    public static readonly string RedirectUri = $"http://127.0.0.1:{CallbackPort}/callback";
    private const string Scopes = "user-library-read user-library-modify user-read-currently-playing user-read-playback-state";
    private static readonly string TokenPath = Path.Combine(SettingsService.DataFolder, "spotify.token");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DynamicNotch.Spotify.v1");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly SettingsService _settings;
    private string? _accessToken;
    private DateTimeOffset _accessExpires;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public SpotifyService(SettingsService settings) => _settings = settings;

    /// <summary>Raised on the UI thread after connecting or disconnecting.</summary>
    public event Action? Changed;

    public string? ClientId => string.IsNullOrWhiteSpace(_settings.Current.SpotifyClientId) ? null : _settings.Current.SpotifyClientId!.Trim();
    public bool IsConnected => File.Exists(TokenPath);

    // ---- Sign in ------------------------------------------------------------------------------------------
    /// <summary>Opens Spotify's sign-in page and waits (up to 3 minutes) for the redirect. Returns null on success, else why not.</summary>
    public async Task<string?> ConnectAsync(CancellationToken ct = default)
    {
        if (ClientId is not { } clientId) return "Enter your Spotify app's Client ID first.";
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, CallbackPort);
            listener.Start();
        }
        catch { return $"Port {CallbackPort} is busy. Close whatever is using it and try again."; }
        try
        {
            string url = "https://accounts.spotify.com/authorize?response_type=code" +
                         $"&client_id={Uri.EscapeDataString(clientId)}&scope={Uri.EscapeDataString(Scopes)}" +
                         $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&code_challenge_method=S256&code_challenge={challenge}&state={state}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream();
                var buffer = new byte[8192];
                int n = await stream.ReadAsync(buffer, timeout.Token);
                string request = Encoding.ASCII.GetString(buffer, 0, n);
                string line = request.Split("\r\n")[0];          // GET /callback?code=…&state=… HTTP/1.1
                var parts = line.Split(' ');
                if (parts.Length < 2 || !parts[1].StartsWith("/callback", StringComparison.Ordinal)) { await Respond(stream, 404, "Not found"); continue; }
                var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://127.0.0.1" + parts[1]).Query);
                if (query["state"] != state) { await Respond(stream, 400, "This sign-in link doesn't match. Start again from Dynamic Notch."); return "The sign-in didn't match. Try again."; }
                if (query["error"] is { } err) { await Respond(stream, 200, "Spotify wasn't connected. You can close this tab."); return err == "access_denied" ? "You didn't allow access." : $"Spotify said: {err}"; }
                if (query["code"] is not { } code) { await Respond(stream, 400, "No code from Spotify."); return "Spotify didn't send a code."; }
                var error = await ExchangeAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri, ["client_id"] = clientId, ["code_verifier"] = verifier,
                }, timeout.Token);
                await Respond(stream, 200, error == null ? "Spotify is connected to Dynamic Notch. You can close this tab." : "Spotify couldn't be connected. Check the Client ID and redirect URI, then try again.");
                if (error == null) Ui.Post(() => Changed?.Invoke());
                return error;
            }
        }
        catch (OperationCanceledException) { return "Timed out waiting for Spotify."; }
        catch (Exception ex) { return ex.Message; }
        finally { listener.Stop(); }
    }

    private static async Task Respond(NetworkStream stream, int status, string message)
    {
        string body = $"<!doctype html><meta charset=utf-8><title>Dynamic Notch</title><body style=\"background:#000;color:#f5f5f7;font:16px system-ui;display:grid;place-items:center;height:90vh\"><p>{WebUtility.HtmlEncode(message)}</p>";
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
    }

    public void Disconnect()
    {
        _accessToken = null;
        try { if (File.Exists(TokenPath)) File.Delete(TokenPath); } catch { }
        Changed?.Invoke();
    }

    /// <summary>Exchanges a code / refresh token for tokens; saves the refresh token. Null on success.</summary>
    private async Task<string?> ExchangeAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var res = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct);
        string json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            try { using var e = JsonDocument.Parse(json); return e.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : $"Spotify returned {(int)res.StatusCode}"; }
            catch { return $"Spotify returned {(int)res.StatusCode}"; }
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _accessToken = root.GetProperty("access_token").GetString();
        _accessExpires = DateTimeOffset.Now.AddSeconds(root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() - 60 : 3000);
        if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } refresh) SaveRefresh(refresh);
        return null;
    }

    private async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTimeOffset.Now < _accessExpires) return _accessToken;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken != null && DateTimeOffset.Now < _accessExpires) return _accessToken;
            if (ClientId is not { } clientId || LoadRefresh() is not { } refresh) return null;
            var error = await ExchangeAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId }, ct);
            if (error != null && error.Contains("revoked", StringComparison.OrdinalIgnoreCase)) Ui.Post(Disconnect);
            return error == null ? _accessToken : null;
        }
        finally { _tokenLock.Release(); }
    }

    // ---- Web API --------------------------------------------------------------------------------------------
    /// <summary>The track Spotify is playing on your account right now (its id and name), or null.</summary>
    public async Task<(string Id, string Name)?> CurrentTrackAsync(CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing", ct);
        if (res == null || res.StatusCode != HttpStatusCode.OK) return null;
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
            if (!item.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } trackId) return null;
            string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            return (trackId, name);
        }
        catch { return null; }
    }

    /// <summary>Whether the track is in your Liked Songs (null when it couldn't be checked).</summary>
    public async Task<bool?> IsLikedAsync(string trackId, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, $"https://api.spotify.com/v1/me/tracks/contains?ids={Uri.EscapeDataString(trackId)}", ct);
        if (res == null || !res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 && doc.RootElement[0].GetBoolean();
    }

    /// <summary>Adds the track to / removes it from your Liked Songs. True when Spotify accepted it.</summary>
    public async Task<bool> SetLikedAsync(string trackId, bool liked, CancellationToken ct = default)
    {
        using var res = await SendAsync(liked ? HttpMethod.Put : HttpMethod.Delete, $"https://api.spotify.com/v1/me/tracks?ids={Uri.EscapeDataString(trackId)}", ct);
        return res != null && res.IsSuccessStatusCode;
    }

    /// <summary>
    /// Set when Spotify answers 429 Too Many Requests: until then no Web API call is made at all (calling anyway only
    /// extends the block, which for a development-mode app can last hours). Null when not limited.
    /// </summary>
    public DateTimeOffset? LimitedUntil => _limitedUntil > DateTimeOffset.Now ? _limitedUntil : null;
    private DateTimeOffset _limitedUntil;

    /// <summary>Raised (on the UI thread) when Spotify starts rate-limiting this app.</summary>
    public event Action? RateLimited;

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        if (LimitedUntil != null) return null; // respect Spotify's Retry-After
        try
        {
            var token = await AccessTokenAsync(ct);
            if (token == null) return null;
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (method != HttpMethod.Get) req.Content = new StringContent("", Encoding.UTF8, "application/json");
            var res = await Http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized) { _accessToken = null; res.Dispose(); return null; }
            if ((int)res.StatusCode == 429)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                _limitedUntil = DateTimeOffset.Now + wait;
                App.LogError(new HttpRequestException($"Spotify rate limit (429) on {new Uri(url).AbsolutePath}: retry after {wait.TotalSeconds:0} s"));
                res.Dispose();
                Ui.Post(() => RateLimited?.Invoke());
                return null;
            }
            return res;
        }
        catch { return null; }
    }

    // ---- Refresh token, encrypted for this Windows user (DPAPI) -----------------------------------------------
    private static void SaveRefresh(string token)
    {
        var blob = Dpapi(Encoding.UTF8.GetBytes(token), protect: true);
        if (blob == null) return;
        Directory.CreateDirectory(SettingsService.DataFolder);
        File.WriteAllBytes(TokenPath, blob);
    }

    private static string? LoadRefresh()
    {
        try { return File.Exists(TokenPath) && Dpapi(File.ReadAllBytes(TokenPath), protect: false) is { } p ? Encoding.UTF8.GetString(p) : null; }
        catch { return null; }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [StructLayout(LayoutKind.Sequential)] private struct DATA_BLOB { public int cbData; public IntPtr pbData; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? desc, ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB pDataOut);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr desc, ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB pDataOut);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    private static byte[]? Dpapi(byte[] data, bool protect)
    {
        var inH = GCHandle.Alloc(data, GCHandleType.Pinned);
        var enH = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = inH.AddrOfPinnedObject() };
            var entropy = new DATA_BLOB { cbData = Entropy.Length, pbData = enH.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "Dynamic Notch Spotify", ref entropy, IntPtr.Zero, IntPtr.Zero, 0x1, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0x1, out output);
            if (!ok) return null;
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { inH.Free(); enH.Free(); }
    }
}
