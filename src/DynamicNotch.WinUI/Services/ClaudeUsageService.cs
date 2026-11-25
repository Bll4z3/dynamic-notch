using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicNotch.Services;

/// <summary>One usage limit: how much of it is used (0-100) and when it resets.</summary>
public sealed record UsageWindow(string Key, string Label, double Percent, DateTimeOffset? ResetsAt);

/// <summary>Everything the usage page shows. Rows the response doesn't include are null / empty.</summary>
public sealed record UsageSnapshot(UsageWindow? FiveHour, UsageWindow? Weekly, IReadOnlyList<UsageWindow> Models, DateTimeOffset FetchedAt);

public enum UsageFailure { None, NotSignedIn, Expired, Unauthorized, RateLimited, Offline, Server, BadData }

public sealed record UsageResult(UsageSnapshot? Snapshot, UsageFailure Failure, string Reason, TimeSpan? RetryAfter = null)
{
    public static UsageResult Fail(UsageFailure f, string reason, TimeSpan? retryAfter = null) => new(null, f, reason, retryAfter);
}

/// <summary>
/// Reads the Claude plan usage from the UNOFFICIAL, undocumented endpoint Claude Code uses
/// (GET https://api.anthropic.com/api/oauth/usage). It can change or disappear at any time, so all
/// fetching and parsing lives here, every failure becomes a <see cref="UsageResult"/> with a short
/// reason (never an exception), and the parser checks each field instead of assuming the format.
///
/// Security: the OAuth access token is read from Claude Code's credentials file
/// (%USERPROFILE%\.claude\.credentials.json) into memory for each request only. It is never logged,
/// never written anywhere, never stored in settings, and only ever sent in the Authorization header to
/// api.anthropic.com (redirects are disabled, so it can't be forwarded to another host). This class
/// never writes Claude Code's file: when the token has expired (or is about to), it asks Claude Code to
/// renew it via <see cref="ClaudeCliRefresher"/>, which runs the CLI hidden, so Claude Code doesn't
/// have to be open.
/// </summary>
public sealed class ClaudeUsageService
{
    private const string Host = "api.anthropic.com";
    private static readonly Uri Endpoint = new($"https://{Host}/api/oauth/usage");
    private const string BetaHeader = "oauth-2025-04-20";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, // the token must never follow a redirect to another host
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseProxy = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public static string CredentialsPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    /// <summary>Renew the sign-in this long before it expires, so the numbers never go blank.</summary>
    private static readonly TimeSpan RenewEarly = TimeSpan.FromMinutes(5);

    public async Task<UsageResult> FetchAsync(CancellationToken ct)
    {
        var (token, failure, expires) = ReadToken();

        // Expired or about to: have Claude Code renew its own sign-in in the background, then re-read it.
        bool refreshed = false;
        if (failure?.Failure == UsageFailure.Expired || (expires is { } e && e - DateTimeOffset.UtcNow < RenewEarly))
        {
            refreshed = true;
            if (await ClaudeCliRefresher.TryRefreshAsync(ct).ConfigureAwait(false))
            {
                var again = ReadToken();
                if (again.token != null) (token, failure) = (again.token, null);
            }
        }
        if (token == null) return failure!;

        var result = await SendAsync(token, ct).ConfigureAwait(false);
        token = null;

        // The server can revoke a token before its stated expiry: renew once and retry.
        if (result.Failure == UsageFailure.Unauthorized && !refreshed &&
            await ClaudeCliRefresher.TryRefreshAsync(ct).ConfigureAwait(false))
        {
            var again = ReadToken();
            if (again.token != null) result = await SendAsync(again.token, ct).ConfigureAwait(false);
        }
        return result;
    }

    private static async Task<UsageResult> SendAsync(string? token, CancellationToken ct)
    {
        HttpResponseMessage response;
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            if (!string.Equals(request.RequestUri?.Host, Host, StringComparison.OrdinalIgnoreCase))
                return UsageResult.Fail(UsageFailure.BadData, "Blocked: unexpected host");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("DynamicNotch/2.0");
            response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UsageResult.Fail(UsageFailure.Offline, "Request timed out");
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return UsageResult.Fail(UsageFailure.Offline, "Can't reach Claude (offline?)");
        }
        catch (Exception)
        {
            return UsageResult.Fail(UsageFailure.Offline, "Request failed");
        }
        finally
        {
            token = null; // drop our reference as soon as the request is out
        }

        using (response)
        {
            int code = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.OK) return Parse(body, DateTimeOffset.Now);
            if (code is 401 or 403) return UsageResult.Fail(UsageFailure.Unauthorized, "Sign-in expired. Open Claude Code to sign in again");
            if (code == 429) return UsageResult.Fail(UsageFailure.RateLimited, "Rate limited. Retrying later", RetryAfter(response));
            if (code >= 500) return UsageResult.Fail(UsageFailure.Server, "Claude's servers are busy", RetryAfter(response));
            if (code is >= 300 and < 400) return UsageResult.Fail(UsageFailure.Server, "Endpoint moved (unofficial API changed?)");
            return UsageResult.Fail(UsageFailure.Server, $"Unexpected response (HTTP {code})");
        }
    }

    /// <summary>The access token from Claude Code's credentials file, or a failure explaining why there isn't one.</summary>
    private static (string? token, UsageResult? failure, DateTimeOffset? expires) ReadToken()
    {
        try
        {
            if (!File.Exists(CredentialsPath))
                return (null, UsageResult.Fail(UsageFailure.NotSignedIn, "Claude Code isn't signed in on this PC"), null);
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object ||
                !oauth.TryGetProperty("accessToken", out var tok) || tok.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tok.GetString()))
                return (null, UsageResult.Fail(UsageFailure.NotSignedIn, "No Claude sign-in found in Claude Code's credentials"), null);
            DateTimeOffset? expires = null;
            if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out long ms))
            {
                expires = ms > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.FromUnixTimeSeconds(ms);
                if (expires < DateTimeOffset.UtcNow)
                    return (null, UsageResult.Fail(UsageFailure.Expired, "Sign-in expired. Open Claude Code to sign in again"), expires);
            }
            return (tok.GetString(), null, expires);
        }
        catch (Exception)
        {
            // Claude Code may be mid-write while renewing; the next poll will read it fine.
            return (null, UsageResult.Fail(UsageFailure.NotSignedIn, "Couldn't read Claude Code's credentials"), null);
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage r)
    {
        var ra = r.Headers.RetryAfter;
        if (ra?.Delta is { } d) return d;
        if (ra?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    // ---- Parsing (defensive: every field is checked; unknown extras are ignored) -------------------

    /// <summary>
    /// Expected shape (unofficial): { "five_hour": { "utilization": 37.0, "resets_at": "2026-…Z" },
    /// "seven_day": { … }, "seven_day_sonnet": { … } | null, "seven_day_opus": … }. Utilization is a
    /// percentage (0-100). Resets may be an ISO time or a Unix time (s or ms). A null or missing window
    /// is simply not shown; at least one of five_hour / seven_day must parse.
    /// </summary>
    internal static UsageResult Parse(string json, DateTimeOffset now)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (Exception) { return UsageResult.Fail(UsageFailure.BadData, "Unexpected response format"); }
        if (root == null) return UsageResult.Fail(UsageFailure.BadData, "Unexpected response format");

        var five = Window(root, "five_hour", "5-hour session");
        var week = Window(root, "seven_day", "Weekly");
        var models = new List<UsageWindow>();

        // Per-model weekly limits (seen in a real response, Nov 2026): "limits": [{ "kind": "weekly_scoped",
        // "percent": 0, "resets_at": "…", "scope": { "model": { "display_name": "Fable" } } }, …]. The
        // "limits" entries for "session" / "weekly_all" also stand in if five_hour / seven_day ever go away.
        if (root["limits"] is JsonArray limits)
        {
            foreach (var node in limits)
            {
                if (node is not JsonObject l || l["kind"] is not JsonValue kv || !kv.TryGetValue(out string? kind)) continue;
                if (Number(l["percent"]) is not { } pct || double.IsNaN(pct)) continue;
                var resets = Time(l["resets_at"]);
                if (kind == "session") five ??= new UsageWindow("limits.session", "5-hour session", Math.Clamp(pct, 0, 100), resets);
                else if (kind == "weekly_all") week ??= new UsageWindow("limits.weekly_all", "Weekly", Math.Clamp(pct, 0, 100), resets);
                else if (kind == "weekly_scoped")
                {
                    string? name = null;
                    if (l["scope"] is JsonObject sc && sc["model"] is JsonObject m && m["display_name"] is JsonValue dn) dn.TryGetValue(out name);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    models.Add(new UsageWindow("limits.weekly_scoped." + name, "Weekly · " + name.Trim(), Math.Clamp(pct, 0, 100), resets));
                }
            }
        }

        foreach (var (key, _) in root)
        {
            if (!key.StartsWith("seven_day_", StringComparison.Ordinal)) continue;
            string model = key["seven_day_".Length..];
            if (!(model.Contains("sonnet", StringComparison.OrdinalIgnoreCase) || model.Contains("opus", StringComparison.OrdinalIgnoreCase))) continue;
            if (Window(root, key, "Weekly · " + Title(model)) is { } w && !models.Any(x => x.Label.Equals(w.Label, StringComparison.OrdinalIgnoreCase))) models.Add(w);
        }
        if (five == null && week == null) return UsageResult.Fail(UsageFailure.BadData, "Unexpected response format");
        return new UsageResult(new UsageSnapshot(five, week, models.Take(2).ToList(), now), UsageFailure.None, "");
    }

    private static UsageWindow? Window(JsonObject root, string key, string label)
    {
        if (root[key] is not JsonObject o) return null;
        double? pct = Number(o["utilization"]);
        if (pct is not { } p || double.IsNaN(p) || double.IsInfinity(p)) return null;
        return new UsageWindow(key, label, Math.Clamp(p, 0, 100), Time(o["resets_at"]));
    }

    private static double? Number(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    private static DateTimeOffset? Time(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out string? s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) return t;
        if (v.TryGetValue(out double d) && d > 0)
            return d > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)d) : DateTimeOffset.FromUnixTimeSeconds((long)d);
        return null;
    }

    private static string Title(string s) =>
        string.Join(' ', s.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
