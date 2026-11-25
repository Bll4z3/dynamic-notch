using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DynamicNotch.Services;

/// <summary>Why a Spotify call didn't give a result (see <see cref="SpotifyService.Describe"/> for the wording).</summary>
public enum SpotifyError { None, NotConnected, MissingScopes, Offline, RateLimited, NoDevice, PremiumRequired, NotRegistered, Forbidden, NotFound, Failed }

/// <summary>A Spotify call's result: the value, or the error (never an exception).</summary>
public readonly record struct SpotifyResult<T>(T? Value, SpotifyError Error)
{
    public bool Ok => Error == SpotifyError.None;
    public static SpotifyResult<T> Fail(SpotifyError e) => new(default, e);
}

public sealed record SpotifyPlaylist(string Id, string Uri, string Name, string? ImageUrl, string? Owner);

public sealed record SpotifyTrack(string Id, string Uri, string Name, string Artists, string? ImageUrl, TimeSpan Duration)
{
    /// <summary>The first artist alone (Spotify writes either this or all of them to Windows' media session).</summary>
    public string PrimaryArtist { get; init; } = "";
}

/// <summary>Your playback queue: the song playing now and the songs after it (in order).</summary>
public sealed record SpotifyQueue(SpotifyTrack? Current, IReadOnlyList<SpotifyTrack> Items);

/// <summary>One page of a playlist's songs. <see cref="NextOffset"/> is null after the last page.</summary>
public sealed record SpotifyTrackPage(List<SpotifyTrack> Tracks, int Total, int? NextOffset);

/// <summary>The playlists, playlist songs, search, playback and queue calls behind the notch's Spotify panel.</summary>
public sealed partial class SpotifyService
{
    private (DateTimeOffset At, List<SpotifyPlaylist> List)? _myPlaylists;
    private readonly Dictionary<string, SpotifyPlaylist?> _playlistInfo = new(); // id → name / cover (null: not available)
    private static readonly TimeSpan MyPlaylistsFresh = TimeSpan.FromMinutes(3);

    /// <summary>A one-line diagnostic in %LocalAppData%\DynamicNotch\spotify.log (counts and status codes only: no tokens, names or ids).</summary>
    internal static void Log(string line)
    {
        try
        {
            var path = Path.Combine(SettingsService.DataFolder, "spotify.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:u}] {line}\n");
        }
        catch { }
    }

    // ---- Liked Songs ------------------------------------------------------------------------------------------
    /// <summary>Your Liked Songs, offered as a playlist (it isn't one in the Web API: songs from /me/tracks, played as your collection).</summary>
    public const string LikedSongsId = "liked", LikedSongsUri = "spotify:collection", LikedSongsCover = "dn:liked-songs";
    public static SpotifyPlaylist LikedSongs => new(LikedSongsId, LikedSongsUri, "Liked Songs", LikedSongsCover, null);
    private string? _userId;

    /// <summary>Your Spotify user id (GET /me), for playing Liked Songs as a context. Cached.</summary>
    private async Task<string?> UserIdAsync(CancellationToken ct)
    {
        if (_userId != null) return _userId;
        var r = await CallAsync(HttpMethod.Get, $"{Api}/me", null, ct);
        if (!r.Ok) return null;
        try { using var doc = JsonDocument.Parse(r.Body); _userId = Str(doc.RootElement, "id"); }
        catch (Exception ex) when (IsParseError(ex)) { }
        return _userId;
    }

    private SpotifyError Ready() => !IsConnected ? SpotifyError.NotConnected : !HasLibraryScopes ? SpotifyError.MissingScopes : SpotifyError.None;

    // ---- Playlists ------------------------------------------------------------------------------------------
    /// <summary>All your playlists (GET /me/playlists, every page; cached for a few minutes unless <paramref name="force"/>).</summary>
    public async Task<SpotifyResult<List<SpotifyPlaylist>>> MyPlaylistsAsync(bool force = false, CancellationToken ct = default)
    {
        if (Ready() is var e && e != SpotifyError.None) return SpotifyResult<List<SpotifyPlaylist>>.Fail(e);
        if (!force && _myPlaylists is { } c && DateTimeOffset.Now - c.At < MyPlaylistsFresh) return new(c.List, SpotifyError.None);
        var list = new List<SpotifyPlaylist>();
        string? url = $"{Api}/me/playlists?limit=50";
        for (int page = 0; url != null && page < 20; page++) // 1000 playlists is plenty
        {
            var r = await CallAsync(HttpMethod.Get, url, null, ct);
            if (!r.Ok) { Log($"me/playlists: {(int)r.Status} {r.Error}"); return SpotifyResult<List<SpotifyPlaylist>>.Fail(ErrorOf(r)); }
            url = null;
            try
            {
                using var doc = JsonDocument.Parse(r.Body);
                var root = doc.RootElement;
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var p in items.EnumerateArray())
                        if (ParsePlaylist(p) is { } pl) list.Add(pl);
                if (root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String) url = next.GetString();
            }
            catch (Exception ex) when (IsParseError(ex)) { return SpotifyResult<List<SpotifyPlaylist>>.Fail(SpotifyError.Failed); }
        }
        Log($"me/playlists: {list.Count} playlists");
        _myPlaylists = (DateTimeOffset.Now, list);
        foreach (var p in list) _playlistInfo[p.Id] = p;
        return new(list, SpotifyError.None);
    }

    /// <summary>
    /// Playlists you played recently, newest first: GET /me/player/recently-played, each track's context where
    /// type == "playlist", without duplicates. Names and covers come from your playlists, or GET /playlists/{id} for
    /// ones you follow; playlists Spotify won't describe (e.g. its own mixes in Development Mode) are left out.
    /// </summary>
    public async Task<SpotifyResult<List<SpotifyPlaylist>>> RecentPlaylistsAsync(int max = 12, CancellationToken ct = default)
    {
        if (Ready() is var e && e != SpotifyError.None) return SpotifyResult<List<SpotifyPlaylist>>.Fail(e);
        var r = await CallAsync(HttpMethod.Get, $"{Api}/me/player/recently-played?limit=50", null, ct);
        if (!r.Ok) { Log($"recently-played: {(int)r.Status} {r.Error}"); return SpotifyResult<List<SpotifyPlaylist>>.Fail(ErrorOf(r)); }
        var ids = new List<string>();
        var contextTypes = new Dictionary<string, int>();
        int rows = 0;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray())
                {
                    rows++;
                    string kind = it.TryGetProperty("context", out var c0) && c0.ValueKind == JsonValueKind.Object ? Str(c0, "type") ?? "?" : "none";
                    contextTypes[kind] = contextTypes.GetValueOrDefault(kind) + 1;
                    if (!it.TryGetProperty("context", out var ctx) || ctx.ValueKind != JsonValueKind.Object) continue;
                    if (Str(ctx, "type") != "playlist" || Str(ctx, "uri") is not { } uri) continue;
                    string id = uri.Split(':').Last();
                    if (id.Length > 0 && !ids.Contains(id)) ids.Add(id);
                }
        }
        catch (Exception ex) when (IsParseError(ex)) { return SpotifyResult<List<SpotifyPlaylist>>.Fail(SpotifyError.Failed); }

        // What's playing now counts as the most recent (its context is often the only one Spotify reports).
        if (await CurrentPlaylistIdAsync(ct) is { } current) { ids.Remove(current); ids.Insert(0, current); }
        Log($"recently-played: {rows} rows, contexts {string.Join(", ", contextTypes.Select(k => $"{k.Key}={k.Value}"))}, {ids.Count} distinct playlists");
        if (ids.Count > 0 && ids.Any(id => !_playlistInfo.ContainsKey(id))) await MyPlaylistsAsync(ct: ct); // names for your own ones
        var result = new List<SpotifyPlaylist>();
        foreach (var id in ids)
        {
            if (result.Count >= max) break;
            if (!_playlistInfo.TryGetValue(id, out var info))
            {
                info = await PlaylistInfoAsync(id, ct);
                if (LimitedUntil != null) break;
            }
            if (info != null) result.Add(info);
        }
        return new(result, SpotifyError.None);
    }

    /// <summary>The playlist the current playback comes from (GET /me/player context), or null.</summary>
    private async Task<string?> CurrentPlaylistIdAsync(CancellationToken ct) =>
        await CurrentContextAsync(ct) is ("playlist", { } uri) ? uri.Split(':').Last() : null;

    /// <summary>What the current playback comes from (GET /me/player context): its type and URI, or null.</summary>
    private async Task<(string Type, string Uri)?> CurrentContextAsync(CancellationToken ct)
    {
        var r = await CallAsync(HttpMethod.Get, $"{Api}/me/player", null, ct);
        if (!r.Ok || r.Status != HttpStatusCode.OK || r.Body.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            if (!doc.RootElement.TryGetProperty("context", out var ctx) || ctx.ValueKind != JsonValueKind.Object) return null;
            return Str(ctx, "type") is { } type && Str(ctx, "uri") is { } uri ? (type, uri) : null;
        }
        catch (Exception ex) when (IsParseError(ex)) { return null; }
    }

    /// <summary>Whether Spotify is playing right now and what (GET /me/player), or null when it can't tell.</summary>
    public async Task<(bool IsPlaying, string? Track)?> PlaybackStateAsync(CancellationToken ct = default)
    {
        if (Ready() != SpotifyError.None) return null;
        var r = await CallAsync(HttpMethod.Get, $"{Api}/me/player", null, ct);
        if (!r.Ok || r.Status != HttpStatusCode.OK || r.Body.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            var root = doc.RootElement;
            bool playing = root.TryGetProperty("is_playing", out var p) && p.ValueKind == JsonValueKind.True;
            string? name = root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object ? Str(item, "name") : null;
            return (playing, name);
        }
        catch (Exception ex) when (IsParseError(ex)) { return null; }
    }

    // ---- Playing a queued song ---------------------------------------------------------------------------------
    /// <summary>
    /// Plays a song from the queue. The Web API has no "play this queued song" call, so: the first one is just Next;
    /// otherwise, when playback comes from a playlist / album / Liked Songs, it jumps there inside that context (the
    /// rest carries on after it, songs you queued yourself stay queued) and checks that the right song started. If the
    /// song isn't in that context (you queued it yourself), it skips forward to it instead, like Spotify does (the songs
    /// in between leave the queue). <paramref name="position"/> is the song's place in the queue (0 = next).
    /// </summary>
    public async Task<SpotifyError> PlayQueuedAsync(SpotifyTrack target, int position, CancellationToken ct = default)
    {
        if (Ready() is var e && e != SpotifyError.None) return e;
        if (position > 0 && await CurrentContextAsync(ct) is ({ } type, { } uri) && type is "playlist" or "album" or "collection")
        {
            var body = JsonSerializer.Serialize(new { context_uri = uri, offset = new { uri = target.Uri } });
            var played = await PlayAsync(body, ct);
            Log($"queue jump in {type}: {played}");
            if (played is SpotifyError.NoDevice or SpotifyError.RateLimited or SpotifyError.Offline or SpotifyError.PremiumRequired or SpotifyError.NotConnected) return played;
            if (played == SpotifyError.None)
            {
                // Spotify may start the context from the top when the song isn't in it: make sure the right one plays.
                await Task.Delay(700, ct);
                if (await CurrentTrackAsync(ct) is { } now && now.Id == target.Id) return SpotifyError.None;
                Log("queue jump: a different song started; playing the song itself");
                return await PlayTrackAsync(target.Uri, ct);
            }
            // Not in that context (rejected): fall through to skipping.
        }
        int skips = Math.Min(position + 1, 25);
        for (int i = 0; i < skips; i++)
        {
            var r = await CallAsync(HttpMethod.Post, $"{Api}/me/player/next", null, ct);
            var err = ErrorOf(r, player: true);
            if (err != SpotifyError.None) { Log($"queue skip {i + 1}/{skips}: {err}"); return err; }
        }
        Log($"queue skip: {skips} skips");
        return SpotifyError.None;
    }

    /// <summary>A playlist's name and cover (GET /playlists/{id}, metadata only). Cached, including "not available".</summary>
    private async Task<SpotifyPlaylist?> PlaylistInfoAsync(string id, CancellationToken ct)
    {
        var r = await CallAsync(HttpMethod.Get, $"{Api}/playlists/{Uri.EscapeDataString(id)}?fields=id,uri,name,images,owner(display_name)", null, ct);
        SpotifyPlaylist? info = null;
        if (r.Ok)
        {
            try { using var doc = JsonDocument.Parse(r.Body); info = ParsePlaylist(doc.RootElement); }
            catch (Exception ex) when (IsParseError(ex)) { }
        }
        if (r.Ok || (int)r.Status is 403 or 404) _playlistInfo[id] = info; // don't cache network errors
        Log($"playlist info: {(int)r.Status} {r.Error}{(info == null ? " (left out)" : "")}");
        return info;
    }

    /// <summary>
    /// One page of a playlist's songs: GET /playlists/{id}/items (Feb 2026: rows carry "item", formerly "track"). Only for
    /// playlists you own or collaborate on: others answer 403 (<see cref="SpotifyError.Forbidden"/>). Local files and
    /// podcast episodes are skipped.
    /// </summary>
    public async Task<SpotifyResult<SpotifyTrackPage>> PlaylistTracksAsync(string playlistId, int offset, CancellationToken ct = default)
    {
        if (Ready() is var e && e != SpotifyError.None) return SpotifyResult<SpotifyTrackPage>.Fail(e);
        const int limit = 50;
        string url = playlistId == LikedSongsId
            ? $"{Api}/me/tracks?limit={limit}&offset={offset}" // Liked Songs (rows carry "track")
            : $"{Api}/playlists/{Uri.EscapeDataString(playlistId)}/items?limit={limit}&offset={offset}";
        var r = await CallAsync(HttpMethod.Get, url, null, ct);
        if (!r.Ok) { Log($"playlist items: {(int)r.Status} {r.Error}"); return SpotifyResult<SpotifyTrackPage>.Fail(ErrorOf(r)); }
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            var root = doc.RootElement;
            var tracks = new List<SpotifyTrack>();
            int rows = 0;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var row in items.EnumerateArray())
                {
                    rows++;
                    if (row.TryGetProperty("is_local", out var local) && local.ValueKind == JsonValueKind.True) continue;
                    // "item" since February 2026; "track" before (it may linger as a boolean, so only an object counts).
                    JsonElement t = default;
                    bool found = (row.TryGetProperty("item", out t) && t.ValueKind == JsonValueKind.Object)
                                 || (row.TryGetProperty("track", out t) && t.ValueKind == JsonValueKind.Object);
                    if (found && ParseTrack(t) is { } track) tracks.Add(track);
                }
            int total = root.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number && tot.TryGetInt32(out var n) ? n : offset + rows;
            Log($"playlist items: offset {offset}, {rows} rows, {tracks.Count} playable");
            bool more = root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String && rows > 0;
            return new(new SpotifyTrackPage(tracks, total, more ? offset + rows : null), SpotifyError.None);
        }
        catch (Exception ex) when (IsParseError(ex)) { return SpotifyResult<SpotifyTrackPage>.Fail(SpotifyError.Failed); }
    }

    // ---- Queue ----------------------------------------------------------------------------------------------
    /// <summary>The last queue fetched (by the queue view or the Next prediction), and when.</summary>
    public SpotifyQueue? LastQueue { get; private set; }
    public DateTimeOffset LastQueueAt { get; private set; }

    /// <summary>Raised (UI thread) with every freshly fetched queue: one request serves the queue view and the Next prediction.</summary>
    public event Action<SpotifyQueue>? QueueUpdated;

    /// <summary>
    /// What's playing and what plays next (GET /me/player/queue: currently_playing + queue, which Spotify fills with your
    /// added songs, then the rest of the playlist / album / autoplay). View-only: the Web API can't reorder, remove or
    /// jump to a queued song. Episodes and local files are left out.
    /// </summary>
    public async Task<SpotifyResult<SpotifyQueue>> GetQueueAsync(CancellationToken ct = default)
    {
        if (Ready() is var e && e != SpotifyError.None) return SpotifyResult<SpotifyQueue>.Fail(e);
        var r = await CallAsync(HttpMethod.Get, $"{Api}/me/player/queue", null, ct);
        if (r.Ok && (r.Status == HttpStatusCode.NoContent || r.Body.Length == 0)) return SpotifyResult<SpotifyQueue>.Fail(SpotifyError.NoDevice); // nothing playing anywhere
        if (!r.Ok) { Log($"queue: {(int)r.Status} {r.Error}"); return SpotifyResult<SpotifyQueue>.Fail(ErrorOf(r, player: true)); }
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            var root = doc.RootElement;
            var current = root.TryGetProperty("currently_playing", out var c) && c.ValueKind == JsonValueKind.Object ? ParseTrack(c) : null;
            var items = new List<SpotifyTrack>();
            if (root.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Array)
                foreach (var t in q.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.Object && ParseTrack(t) is { } track) items.Add(track);
            var queue = new SpotifyQueue(current, items);
            LastQueue = queue;
            LastQueueAt = DateTimeOffset.Now;
            Log($"queue: {items.Count} upcoming");
            QueueUpdated?.Invoke(queue);
            return new(queue, SpotifyError.None);
        }
        catch (Exception ex) when (IsParseError(ex)) { return SpotifyResult<SpotifyQueue>.Fail(SpotifyError.Failed); }
    }

    /// <summary>The song playing now and the next one in your queue (for the Next prediction).</summary>
    public async Task<SpotifyResult<(SpotifyTrack? Current, SpotifyTrack? Next)>> UpNextAsync(CancellationToken ct = default)
    {
        var r = await GetQueueAsync(ct);
        return r.Ok ? new((r.Value!.Current, r.Value.Items.FirstOrDefault()), SpotifyError.None) : SpotifyResult<(SpotifyTrack?, SpotifyTrack?)>.Fail(r.Error);
    }

    // ---- Search ---------------------------------------------------------------------------------------------
    /// <summary>Songs matching <paramref name="query"/>: GET /search?type=track (Development Mode: at most 10).</summary>
    public async Task<SpotifyResult<List<SpotifyTrack>>> SearchTracksAsync(string query, CancellationToken ct = default)
    {
        if (Ready() is var e && e != SpotifyError.None) return SpotifyResult<List<SpotifyTrack>>.Fail(e);
        var r = await CallAsync(HttpMethod.Get, $"{Api}/search?type=track&limit=10&q={Uri.EscapeDataString(query)}", null, ct);
        if (!r.Ok) { Log($"search: {(int)r.Status} {r.Error}"); return SpotifyResult<List<SpotifyTrack>>.Fail(ErrorOf(r)); }
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            var list = new List<SpotifyTrack>();
            if (doc.RootElement.TryGetProperty("tracks", out var tracks) && tracks.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var t in items.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.Object && ParseTrack(t) is { } track) list.Add(track);
            return new(list, SpotifyError.None);
        }
        catch (Exception ex) when (IsParseError(ex)) { return SpotifyResult<List<SpotifyTrack>>.Fail(SpotifyError.Failed); }
    }

    // ---- Playback ---------------------------------------------------------------------------------------------
    /// <summary>Plays <paramref name="trackUri"/> inside the playlist, so the playlist carries on after it (PUT /me/player/play).</summary>
    public async Task<SpotifyError> PlayInContextAsync(string contextUri, string? trackUri, CancellationToken ct = default)
    {
        if (contextUri == LikedSongsUri)
        {
            // Liked Songs plays as your collection (spotify:user:{id}:collection), so it carries on after the song.
            if (await UserIdAsync(ct) is { } me) contextUri = $"spotify:user:{me}:collection";
            else if (trackUri != null) return await PlayTrackAsync(trackUri, ct);
            else return SpotifyError.Failed;
        }
        var body = trackUri != null
            ? JsonSerializer.Serialize(new { context_uri = contextUri, offset = new { uri = trackUri } })
            : JsonSerializer.Serialize(new { context_uri = contextUri });
        return await PlayAsync(body, ct);
    }

    /// <summary>Plays a single song (PUT /me/player/play with its URI).</summary>
    public Task<SpotifyError> PlayTrackAsync(string trackUri, CancellationToken ct = default) =>
        PlayAsync(JsonSerializer.Serialize(new { uris = new[] { trackUri } }), ct);

    /// <summary>
    /// Starts playback. With no active device (404), the call is repeated on a device Spotify knows about (the desktop
    /// app counts even while idle); only when there's none at all does it answer <see cref="SpotifyError.NoDevice"/>.
    /// </summary>
    private async Task<SpotifyError> PlayAsync(string body, CancellationToken ct)
    {
        if (Ready() is var e && e != SpotifyError.None) return e;
        var r = await CallAsync(HttpMethod.Put, $"{Api}/me/player/play", body, ct);
        var err = ErrorOf(r, player: true);
        if (err != SpotifyError.NoDevice) return err;
        if (await AnyDeviceAsync(ct) is not { } device) return SpotifyError.NoDevice;
        r = await CallAsync(HttpMethod.Put, $"{Api}/me/player/play?device_id={Uri.EscapeDataString(device)}", body, ct);
        return ErrorOf(r, player: true);
    }

    /// <summary>Adds a song to the end of the queue (POST /me/player/queue).</summary>
    public async Task<SpotifyError> QueueAsync(string trackUri, CancellationToken ct = default)
    {
        if (Ready() is var e && e != SpotifyError.None) return e;
        var r = await CallAsync(HttpMethod.Post, $"{Api}/me/player/queue?uri={Uri.EscapeDataString(trackUri)}", null, ct);
        var err = ErrorOf(r, player: true);
        if (err != SpotifyError.NoDevice) return err;
        if (await AnyDeviceAsync(ct) is not { } device) return SpotifyError.NoDevice;
        r = await CallAsync(HttpMethod.Post, $"{Api}/me/player/queue?uri={Uri.EscapeDataString(trackUri)}&device_id={Uri.EscapeDataString(device)}", null, ct);
        return ErrorOf(r, player: true);
    }

    /// <summary>The active device, or this computer's Spotify, or any other available one (GET /me/player/devices).</summary>
    private async Task<string?> AnyDeviceAsync(CancellationToken ct)
    {
        var r = await CallAsync(HttpMethod.Get, $"{Api}/me/player/devices", null, ct);
        if (!r.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return null;
            var list = devices.EnumerateArray()
                .Where(d => Str(d, "id") != null && !(d.TryGetProperty("is_restricted", out var x) && x.ValueKind == JsonValueKind.True))
                .Select(d => (Id: Str(d, "id")!, Active: d.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True,
                              Computer: string.Equals(Str(d, "type"), "Computer", StringComparison.OrdinalIgnoreCase),
                              Name: Str(d, "name") ?? ""))
                .ToList();
            var here = Environment.MachineName;
            return list.OrderByDescending(d => d.Active)
                .ThenByDescending(d => d.Computer && d.Name.Equals(here, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(d => d.Computer)
                .Select(d => d.Id).FirstOrDefault();
        }
        catch (Exception ex) when (IsParseError(ex)) { return null; }
    }

    // ---- Parsing ----------------------------------------------------------------------------------------------
    private static SpotifyPlaylist? ParsePlaylist(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || Str(p, "id") is not { } id) return null;
        string uri = Str(p, "uri") ?? $"spotify:playlist:{id}";
        string name = Str(p, "name") is { Length: > 0 } n ? n : "Untitled playlist";
        string? owner = p.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object ? Str(o, "display_name") : null;
        return new SpotifyPlaylist(id, uri, name, PickImage(p, 120), owner);
    }

    private static SpotifyTrack? ParseTrack(JsonElement t)
    {
        if (Str(t, "type") is { } type && type != "track") return null; // podcast episodes
        if (Str(t, "uri") is not { } uri || !uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
        string id = Str(t, "id") ?? uri["spotify:track:".Length..];
        string artists = t.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
            ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name")).Where(x => !string.IsNullOrEmpty(x)))
            : "";
        string? image = t.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object ? PickImage(album, 96) : null;
        var duration = t.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
        string primary = t.TryGetProperty("artists", out var a2) && a2.ValueKind == JsonValueKind.Array && a2.GetArrayLength() > 0 ? Str(a2[0], "name") ?? "" : "";
        return new SpotifyTrack(id, uri, Str(t, "name") ?? "Unknown title", artists, image, duration) { PrimaryArtist = primary };
    }

    /// <summary>The smallest image at least <paramref name="minWidth"/> px wide (else the largest): small downloads for small tiles.</summary>
    private static string? PickImage(JsonElement owner, int minWidth)
    {
        if (!owner.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        string? best = null, largest = null;
        int bestW = int.MaxValue, largestW = -1;
        foreach (var img in images.EnumerateArray())
        {
            if (Str(img, "url") is not { } url) continue;
            int w = img.TryGetProperty("width", out var wv) && wv.ValueKind == JsonValueKind.Number && wv.TryGetInt32(out var wi) ? wi : 0;
            if (w == 0) { largest ??= url; continue; } // unknown size (single user-uploaded covers)
            if (w > largestW) { largestW = w; largest = url; }
            if (w >= minWidth && w < bestW) { bestW = w; best = url; }
        }
        return best ?? largest;
    }

    /// <summary>Malformed or unexpected JSON (a null where a number was expected…): treated as a failed call, never a crash.</summary>
    private static bool IsParseError(Exception ex) => ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
