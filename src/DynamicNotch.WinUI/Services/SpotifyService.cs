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
/// Your Spotify account (Web API): the media player's Like button, and the notch's playlists, playlist view, search
/// and "play from the notch" (SpotifyService.Library.cs). Sign-in is OAuth 2.0 Authorization Code with PKCE (no
/// client secret on this PC): your browser opens Spotify's sign-in page, Spotify redirects to
/// http://127.0.0.1:43821/callback, which this app listens on just for that moment, and the code is exchanged for
/// tokens. Only the refresh token (and the scopes it was granted) is saved, encrypted for your Windows account
/// (DPAPI, the same protection as the Claude key); access tokens live in memory and are renewed automatically.
/// It needs your own Spotify app's Client ID (Settings ▸ General ▸ Spotify).
/// <para>
/// Development Mode (since February 2026): the app owner needs Premium, at most 5 users (each added under User
/// Management in the dashboard), saved-track calls moved to /me/library (URIs), playlist songs come from
/// /playlists/{id}/items and only for playlists you own or collaborate on (403 otherwise), and search returns at most
/// 10 results.
/// </para>
/// </summary>
public sealed partial class SpotifyService
{
    public const int CallbackPort = 43821;
    public static readonly string RedirectUri = $"http://127.0.0.1:{CallbackPort}/callback";

    /// <summary>What the Like button needs (granted by every sign-in so far).</summary>
    private static readonly string[] LikeScopes = { "user-library-read", "user-library-modify", "user-read-currently-playing", "user-read-playback-state" };
    /// <summary>What the notch's playlists, search and playback need (sign-ins before 2.12 didn't ask for these).</summary>
    public static readonly string[] LibraryScopes = { "user-read-recently-played", "playlist-read-private", "playlist-read-collaborative", "user-read-playback-state", "user-modify-playback-state" };
    private static readonly string Scopes = string.Join(' ', LikeScopes.Concat(LibraryScopes).Distinct());

    private static readonly string TokenPath = Path.Combine(SettingsService.DataFolder, "spotify.token");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DynamicNotch.Spotify.v1");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string Api = "https://api.spotify.com/v1";

    private readonly SettingsService _settings;
    private string? _accessToken;
    private DateTimeOffset _accessExpires;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private HashSet<string>? _granted;

    public SpotifyService(SettingsService settings) => _settings = settings;

    /// <summary>Raised on the UI thread after connecting or disconnecting.</summary>
    public event Action? Changed;

    public string? ClientId => string.IsNullOrWhiteSpace(_settings.Current.SpotifyClientId) ? null : _settings.Current.SpotifyClientId!.Trim();
    public bool IsConnected => File.Exists(TokenPath);

    /// <summary>
    /// The sign-in includes the playlist / playback scopes. A sign-in from an older version (Like button only) is still
    /// connected, but needs a reconnect for the notch's playlists, search and playback.
    /// </summary>
    public bool HasLibraryScopes => IsConnected && GrantedScopes() is { } g && LibraryScopes.All(g.Contains);

    private HashSet<string>? GrantedScopes()
    {
        if (_granted != null) return _granted;
        var stored = LoadStored();
        _granted = stored?.Scope is { Length: > 0 } s ? new HashSet<string>(s.Split(' ', StringSplitOptions.RemoveEmptyEntries)) : null;
        return _granted;
    }

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
            // show_dialog: a reconnect for new scopes always shows the consent page (and lets you switch accounts).
            string url = "https://accounts.spotify.com/authorize?response_type=code&show_dialog=true" +
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
                string? error;
                try
                {
                    error = await ExchangeAsync(new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri, ["client_id"] = clientId, ["code_verifier"] = verifier,
                    }, timeout.Token);
                }
                catch (HttpRequestException) { error = "Couldn't reach Spotify. Check your internet connection."; }
                await Respond(stream, 200, error == null ? "Spotify is connected to Dynamic Notch. You can close this tab." : "Spotify couldn't be connected. Check the Client ID and redirect URI, then try again.");
                if (error == null) { _myPlaylists = null; Ui.Post(() => Changed?.Invoke()); }
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
        _granted = null;
        _myPlaylists = null;
        _playlistInfo.Clear();
        try { if (File.Exists(TokenPath)) File.Delete(TokenPath); } catch { }
        Changed?.Invoke();
    }

    /// <summary>Exchanges a code / refresh token for tokens; saves the refresh token and scopes. Null on success.</summary>
    private async Task<string?> ExchangeAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var res = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct);
        string json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            try
            {
                using var e = JsonDocument.Parse(json);
                var root = e.RootElement;
                string? code = root.TryGetProperty("error", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                string? desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
                if (code == "invalid_grant") return "revoked: " + (desc ?? "the sign-in is no longer valid");
                return desc ?? $"Spotify returned {(int)res.StatusCode}";
            }
            catch { return $"Spotify returned {(int)res.StatusCode}"; }
        }
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        _accessToken = r.GetProperty("access_token").GetString();
        _accessExpires = DateTimeOffset.Now.AddSeconds(r.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() - 60 : 3000);
        string? scope = r.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : null;
        string? refresh = r.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } t ? t : null;
        var stored = LoadStored();
        refresh ??= stored?.Refresh; // a refresh grant may not rotate the token
        scope ??= stored?.Scope;
        if (refresh != null) { SaveStored(new StoredToken(refresh, scope)); _granted = null; }
        return null;
    }

    private async Task<string?> AccessTokenAsync(CancellationToken ct, bool force = false)
    {
        if (!force && _accessToken != null && DateTimeOffset.Now < _accessExpires) return _accessToken;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!force && _accessToken != null && DateTimeOffset.Now < _accessExpires) return _accessToken;
            if (ClientId is not { } clientId || LoadStored()?.Refresh is not { } refresh) return null;
            var error = await ExchangeAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId }, ct);
            if (error != null && error.StartsWith("revoked", StringComparison.Ordinal)) Ui.Post(Disconnect);
            return error == null ? _accessToken : null;
        }
        finally { _tokenLock.Release(); }
    }

    // ---- Web API core -------------------------------------------------------------------------------------
    /// <summary>
    /// Set when Spotify answers 429 Too Many Requests: until then no Web API call is made at all (calling anyway only
    /// extends the block, which for a development-mode app can last hours). Null when not limited.
    /// </summary>
    public DateTimeOffset? LimitedUntil => _limitedUntil > DateTimeOffset.Now ? _limitedUntil : null;
    private DateTimeOffset _limitedUntil;

    /// <summary>Raised (on the UI thread) when Spotify starts rate-limiting this app.</summary>
    public event Action? RateLimited;

    /// <summary>One Web API answer: the status and body, or why there wasn't one.</summary>
    private readonly record struct ApiReply(HttpStatusCode Status, string Body, SpotifyError Error)
    {
        public bool Ok => Error == SpotifyError.None && (int)Status is >= 200 and < 300;
    }

    /// <summary>
    /// Sends one request with the current access token. An expired token (401) is renewed and the request sent once
    /// more; 429 starts the Retry-After block; no network, a timeout or a refused sign-in become an error, never an
    /// exception (only cancellation through <paramref name="ct"/> throws).
    /// </summary>
    private async Task<ApiReply> CallAsync(HttpMethod method, string url, string? json, CancellationToken ct)
    {
        if (!IsConnected) return new(0, "", SpotifyError.NotConnected);
        if (LimitedUntil != null) return new(0, "", SpotifyError.RateLimited);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var token = await AccessTokenAsync(ct, force: attempt > 0);
                if (token == null) return new(0, "", IsConnected ? SpotifyError.Failed : SpotifyError.NotConnected);
                using var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                else if (method != HttpMethod.Get) req.Content = new StringContent("", Encoding.UTF8, "application/json");
                using var res = await Http.SendAsync(req, ct);
                if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { _accessToken = null; continue; }
                if ((int)res.StatusCode == 429)
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                    _limitedUntil = DateTimeOffset.Now + wait;
                    App.LogError(new HttpRequestException($"Spotify rate limit (429) on {new Uri(url).AbsolutePath}: retry after {wait.TotalSeconds:0} s"));
                    Ui.Post(() => RateLimited?.Invoke());
                    return new(res.StatusCode, "", SpotifyError.RateLimited);
                }
                string body = res.Content == null ? "" : await res.Content.ReadAsStringAsync(ct);
                return new(res.StatusCode, body, SpotifyError.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return new(0, "", SpotifyError.Offline); } // HttpClient timeout
            catch (HttpRequestException) { return new(0, "", SpotifyError.Offline); }
            catch (Exception ex) { App.LogError(ex); return new(0, "", SpotifyError.Failed); }
        }
        return new(HttpStatusCode.Unauthorized, "", SpotifyError.NotConnected);
    }

    /// <summary>Turns an unsuccessful reply into an error kind (player calls also know "no device" and "Premium").</summary>
    private static SpotifyError ErrorOf(ApiReply r, bool player = false)
    {
        if (r.Error != SpotifyError.None) return r.Error;
        int s = (int)r.Status;
        if (s is >= 200 and < 300) return SpotifyError.None;
        string reason = ReasonOf(r.Body);
        if (s == 404 && player) return SpotifyError.NoDevice;
        if (reason.Contains("NO_ACTIVE_DEVICE", StringComparison.Ordinal)) return SpotifyError.NoDevice;
        if (s == 403 && (reason.Contains("PREMIUM", StringComparison.OrdinalIgnoreCase) || r.Body.Contains("PREMIUM_REQUIRED", StringComparison.Ordinal))) return SpotifyError.PremiumRequired;
        if (s == 403 && r.Body.Contains("not registered", StringComparison.OrdinalIgnoreCase)) return SpotifyError.NotRegistered;
        if (s == 403) return SpotifyError.Forbidden;
        if (s == 404) return SpotifyError.NotFound;
        if (s == 401) return SpotifyError.NotConnected;
        return SpotifyError.Failed;
    }

    private static string ReasonOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
                return (e.TryGetProperty("reason", out var r) ? r.GetString() : null) ?? (e.TryGetProperty("message", out var m) ? m.GetString() : null) ?? "";
        }
        catch { }
        return "";
    }

    /// <summary>What to tell you for an error (short: it shows in the notch).</summary>
    public string Describe(SpotifyError e) => e switch
    {
        SpotifyError.NotConnected => "Connect Spotify in Settings ▸ General",
        SpotifyError.MissingScopes => "Reconnect Spotify in Settings to use playlists",
        SpotifyError.Offline => "Can't reach Spotify. Check your connection",
        SpotifyError.RateLimited => LimitedUntil is { } t ? $"Spotify is busy. Try again at {t.LocalDateTime:t}" : "Spotify is busy. Try again shortly",
        SpotifyError.NoDevice => "Open Spotify to play",
        SpotifyError.PremiumRequired => "Playing from the notch needs Spotify Premium",
        SpotifyError.NotRegistered => "Add your account under User Management in your Spotify app's dashboard",
        SpotifyError.Forbidden => "Spotify didn't allow that",
        SpotifyError.NotFound => "Spotify couldn't find that",
        _ => "Something went wrong with Spotify",
    };

    // ---- Like button ------------------------------------------------------------------------------------------
    /// <summary>The track Spotify is playing on your account right now (its id and name), or null.</summary>
    public async Task<(string Id, string Name)?> CurrentTrackAsync(CancellationToken ct = default)
    {
        var r = await CallAsync(HttpMethod.Get, $"{Api}/me/player/currently-playing", null, ct);
        if (r.Error != SpotifyError.None || r.Status != HttpStatusCode.OK) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            if (!doc.RootElement.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
            if (!item.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } trackId) return null;
            string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            return (trackId, name);
        }
        catch { return null; }
    }

    /// <summary>Whether the track is in your Liked Songs (null when it couldn't be checked). GET /me/library/contains (Feb 2026: URIs).</summary>
    public async Task<bool?> IsLikedAsync(string trackId, CancellationToken ct = default)
    {
        var r = await CallAsync(HttpMethod.Get, $"{Api}/me/library/contains?uris={Uri.EscapeDataString("spotify:track:" + trackId)}", null, ct);
        if (!r.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 && doc.RootElement[0].GetBoolean();
        }
        catch { return null; }
    }

    /// <summary>Adds the track to / removes it from your Liked Songs (PUT / DELETE /me/library). True when Spotify accepted it.</summary>
    public async Task<bool> SetLikedAsync(string trackId, bool liked, CancellationToken ct = default)
    {
        var r = await CallAsync(liked ? HttpMethod.Put : HttpMethod.Delete, $"{Api}/me/library?uris={Uri.EscapeDataString("spotify:track:" + trackId)}", null, ct);
        return r.Ok;
    }

    // ---- Refresh token + scopes, encrypted for this Windows user (DPAPI) ---------------------------------------
    private sealed record StoredToken(string Refresh, string? Scope);

    private static void SaveStored(StoredToken token)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string?> { ["refresh"] = token.Refresh, ["scope"] = token.Scope });
        var blob = Dpapi(Encoding.UTF8.GetBytes(json), protect: true);
        if (blob == null) return;
        Directory.CreateDirectory(SettingsService.DataFolder);
        File.WriteAllBytes(TokenPath, blob);
    }

    /// <summary>The saved sign-in. Files from before 2.12 hold just the refresh token (scopes unknown).</summary>
    private static StoredToken? LoadStored()
    {
        try
        {
            if (!File.Exists(TokenPath) || Dpapi(File.ReadAllBytes(TokenPath), protect: false) is not { } plain) return null;
            string text = Encoding.UTF8.GetString(plain);
            if (!text.StartsWith("{", StringComparison.Ordinal)) return new StoredToken(text, null);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string? refresh = root.TryGetProperty("refresh", out var r) ? r.GetString() : null;
            string? scope = root.TryGetProperty("scope", out var s) ? s.GetString() : null;
            return refresh is { Length: > 0 } ? new StoredToken(refresh, scope) : null;
        }
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
