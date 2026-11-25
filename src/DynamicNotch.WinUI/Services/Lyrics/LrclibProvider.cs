using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DynamicNotch.Services.Lyrics;

/// <summary>
/// LRCLIB (https://lrclib.net): a free, open lyrics database with line-synced (LRC), word-synced (Lyricsfile) and plain
/// lyrics, no key needed. The exact signature (/api/get: title, artist, album, duration ±2 s) is tried first, then a
/// search whose best match must agree on duration (±2 s preferred, never more than 4 s) and closely on title and artist.
/// Requests identify the app in the User-Agent, as LRCLIB asks; 429 / 503 answers honour Retry-After.
/// </summary>
public sealed class LrclibProvider : ILyricsProvider
{
    public const string Credit = "Lyrics from LRCLIB";
    private static readonly HttpClient Http = CreateClient();
    private static DateTimeOffset _blockedUntil;

    public string Id => "lrclib";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            BaseAddress = new Uri("https://lrclib.net/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        // App name and version only (LRCLIB also suggests a homepage; add one here if you want to).
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"DynamicNotch/{UpdateService.CurrentVersionText}");
        c.DefaultRequestHeaders.UserAgent.ParseAdd("(Windows)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    public async Task<LyricsRaw?> FetchAsync(LyricsQuery q, CancellationToken ct)
    {
        if (DateTimeOffset.Now < _blockedUntil) throw new LyricsUnavailableException("LRCLIB asked to wait") { RetryAfter = _blockedUntil - DateTimeOffset.Now };

        // 1. The exact signature (needs the album; duration narrows it to the right version).
        if (q.Album.Length > 0 && q.DurationSeconds > 0)
        {
            var url = $"api/get?track_name={E(q.Title)}&artist_name={E(q.Artist)}&album_name={E(q.Album)}&duration={Math.Round(q.DurationSeconds)}";
            var hit = await GetOneAsync(url, ct);
            if (hit != null && Acceptable(hit, q, strict: false)) return hit;
        }

        // 2. Search by title + artist, then by the first artist only, then free text.
        var tried = new HashSet<string>();
        foreach (var url in new[]
                 {
                     $"api/search?track_name={E(q.Title)}&artist_name={E(q.Artist)}",
                     $"api/search?track_name={E(q.Title)}&artist_name={E(TitleCleaner.PrimaryArtist(q.Artist))}",
                     $"api/search?q={E(TitleCleaner.PrimaryArtist(q.Artist) + " " + q.Title)}",
                 })
        {
            if (!tried.Add(url)) continue;
            var list = await SearchAsync(url, ct);
            var best = Best(list, q);
            if (best != null) return best;
        }
        return null;
    }

    private static string E(string s) => Uri.EscapeDataString(s);

    private static async Task<LyricsRaw?> GetOneAsync(string url, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(url, ct);
        return doc?.RootElement.ValueKind == JsonValueKind.Object ? Read(doc.RootElement) : null;
    }

    private static async Task<List<LyricsRaw>> SearchAsync(string url, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(url, ct);
        var list = new List<LyricsRaw>();
        if (doc?.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var el in doc.RootElement.EnumerateArray())
                if (Read(el) is { } r) list.Add(r);
        return list;
    }

    /// <summary>The JSON answer; null on "not found". Network trouble, timeouts and rate limits throw.</summary>
    private static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new LyricsUnavailableException("LRCLIB timed out"); }
        catch (HttpRequestException ex) { throw new LyricsUnavailableException("LRCLIB unreachable", ex); }
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(resp.StatusCode == HttpStatusCode.TooManyRequests ? 30 : 2);
                wait += TimeSpan.FromMilliseconds(wait.TotalMilliseconds * 0.3 * Random.Shared.NextDouble()); // jitter, as LRCLIB asks
                _blockedUntil = DateTimeOffset.Now + wait;
                throw new LyricsUnavailableException($"LRCLIB busy ({(int)resp.StatusCode})") { RetryAfter = wait };
            }
            if (!resp.IsSuccessStatusCode) throw new LyricsUnavailableException($"LRCLIB error {(int)resp.StatusCode}");
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.Contains("json", StringComparison.OrdinalIgnoreCase)) return null; // plain-text errors (bad query)
            var text = await resp.Content.ReadAsStringAsync(ct);
            try { return JsonDocument.Parse(text); } catch (JsonException) { return null; }
        }
    }

    private static LyricsRaw? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool B(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        double D(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var r = new LyricsRaw
        {
            Provider = "lrclib",
            Credit = Credit,
            Id = e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
            TrackName = S("trackName"),
            ArtistName = S("artistName"),
            AlbumName = S("albumName"),
            Duration = D("duration"),
            Instrumental = B("instrumental"),
            HasWordSync = B("hasWordSync"),
            Synced = NullIfEmpty(S("syncedLyrics")),
            Plain = NullIfEmpty(S("plainLyrics")),
            Lyricsfile = NullIfEmpty(S("lyricsfile")),
        };
        return r;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static bool HasAnything(LyricsRaw r) => r.Instrumental || r.Synced != null || r.Plain != null || r.HasWordSync;

    /// <summary>Close enough to be the same recording.</summary>
    private static bool Acceptable(LyricsRaw r, LyricsQuery q, bool strict)
    {
        if (!HasAnything(r)) return false;
        if (q.DurationSeconds > 0 && r.Duration > 0 && Math.Abs(r.Duration - q.DurationSeconds) > 4) return false;
        double title = TitleCleaner.Similarity(TitleCleaner.Normalize(TitleCleaner.CleanTitle(r.TrackName, r.ArtistName)), TitleCleaner.Normalize(q.Title));
        double artist = q.Artist.Length == 0 ? 1 : TitleCleaner.ArtistSimilarity(q.Artist, r.ArtistName);
        return strict ? title >= 0.6 && artist >= 0.45 : title >= 0.45 && artist >= 0.3;
    }

    /// <summary>The best search result: duration first (±2 s), then title, then artist; synced and word-synced preferred.</summary>
    private static LyricsRaw? Best(List<LyricsRaw> list, LyricsQuery q)
    {
        LyricsRaw? best = null;
        double bestScore = double.MinValue;
        foreach (var r in list)
        {
            if (!Acceptable(r, q, strict: true)) continue;
            double title = TitleCleaner.Similarity(TitleCleaner.Normalize(TitleCleaner.CleanTitle(r.TrackName, r.ArtistName)), TitleCleaner.Normalize(q.Title));
            double artist = q.Artist.Length == 0 ? 0.5 : TitleCleaner.ArtistSimilarity(q.Artist, r.ArtistName);
            double dur = q.DurationSeconds <= 0 || r.Duration <= 0 ? 0.4 : Math.Clamp(1 - Math.Max(0, Math.Abs(r.Duration - q.DurationSeconds) - 2) / 2, 0, 1);
            double album = q.Album.Length > 0 && TitleCleaner.Similarity(TitleCleaner.Normalize(r.AlbumName), TitleCleaner.Normalize(q.Album)) > 0.7 ? 0.3 : 0;
            double score = 3 * title + 2 * artist + 2.5 * dur + album
                         + (r.HasWordSync ? 0.5 : 0) + (r.Synced != null ? 0.8 : 0) + (r.Plain != null ? 0.1 : 0);
            if (score > bestScore) { bestScore = score; best = r; }
        }
        return best;
    }
}
