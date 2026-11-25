using System.IO;
using Windows.Media.Control;

namespace DynamicNotch.Services;

/// <summary>Immutable view of the selected media session.</summary>
public sealed record MediaSnapshot(
    string Title,
    string Artist,
    Windows.Graphics.Imaging.SoftwareBitmap? Artwork,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionSampledAt,
    double PlaybackRate,
    bool CanPrevious,
    bool CanNext,
    bool CanPlayPause,
    bool CanSeek,
    string SourceApp,
    int SessionCount)
{
    public static readonly MediaSnapshot Empty = new("", "", null, false, TimeSpan.Zero, TimeSpan.Zero,
        DateTimeOffset.Now, 1, false, false, false, false, "", 0);

    public bool HasMedia => !string.IsNullOrEmpty(Title) || SessionCount > 0;

    /// <summary>The album title, when the player reports one (lyrics matching).</summary>
    public string Album { get; init; } = "";

    /// <summary>Position extrapolated to "now" while playing (the OS only reports it sporadically).</summary>
    public TimeSpan PositionNow
    {
        get
        {
            if (!IsPlaying) return Clamp(Position);
            var p = Position + TimeSpan.FromTicks((long)((DateTimeOffset.Now - PositionSampledAt).Ticks * PlaybackRate));
            return Clamp(p);
        }
    }

    private TimeSpan Clamp(TimeSpan t) =>
        t < TimeSpan.Zero ? TimeSpan.Zero : (Duration > TimeSpan.Zero && t > Duration ? Duration : t);
}

/// <summary>
/// Wraps GlobalSystemMediaTransportControlsSessionManager. Entirely event-driven: it listens to
/// session, media-property, playback and timeline change events and never polls the OS.
/// </summary>
public sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _userPickedSourceId;
    private string _artKey = "";
    private Windows.Graphics.Imaging.SoftwareBitmap? _art;
    private int _refreshQueued;

    public MediaSnapshot Snapshot { get; private set; } = MediaSnapshot.Empty;

    /// <summary>Dev (--media-trace): times every command, OS event, refresh step and publish in media.log.</summary>
    public static bool Trace { get; set; }
    private static readonly System.Diagnostics.Stopwatch TraceClock = System.Diagnostics.Stopwatch.StartNew();
    internal static void Log(string line)
    {
        if (!Trace) return;
        try { File.AppendAllText(Path.Combine(SettingsService.DataFolder, "media.log"), $"{TraceClock.ElapsedMilliseconds,8} ms  {line}\n"); } catch { }
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<MediaSnapshot>? Changed;

    private bool _running;

    public async Task StartAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (!_running) return; // stopped while starting
            _manager.SessionsChanged += OnSessionsChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            QueueRefresh(reselect: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Media unavailable: {ex.Message}");
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager s, SessionsChangedEventArgs e) => QueueRefresh(reselect: true);
    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager s, CurrentSessionChangedEventArgs e) => QueueRefresh(reselect: true);

    /// <summary>Stop listening to media sessions (Music page switched off) and clear what's shown.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        if (_manager != null)
        {
            try { _manager.SessionsChanged -= OnSessionsChanged; _manager.CurrentSessionChanged -= OnCurrentSessionChanged; } catch { }
        }
        Detach();
        _art = null;
        _artKey = "";
        Publish(MediaSnapshot.Empty);
    }

    // ---- Commands (optimistic) -----------------------------------------------------------------
    // Some players report changes through the media session seconds late (Spotify: 2-3 s after play / pause / skip,
    // measured with --media-test), although the music itself changes at once. So each command shows its result
    // straight away and the player's own reports are reconciled with it (see Reconcile): a report that still shows the
    // old state is overridden for a few seconds, one that confirms the prediction ends the override, and one that shows
    // something else wins (the prediction was wrong).

    /// <summary>A song the notch expects to play next (or before): what to show until the player reports it.</summary>
    public sealed record MediaPrediction(string Title, string Artist, Windows.Graphics.Imaging.SoftwareBitmap? Art, TimeSpan Duration, string ForTitle);

    /// <summary>The next song in the queue for the current one (set from Spotify's queue; ignored once the song changes).</summary>
    public MediaPrediction? UpNext { get; set; }

    private MediaPrediction? _previousTrack; // the song before this one (Previous at the start of a song goes back to it)
    private Override? _ovr;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _ovrTimer;
    private static readonly TimeSpan OverrideFor = TimeSpan.FromSeconds(4);

    /// <summary>What a command promised: a play state, a song, or a restart (position 0), until the player catches up.</summary>
    private sealed record Override(DateTimeOffset Until, bool? Playing, MediaPrediction? Track, bool Restart, string FromTitle)
    {
        /// <summary>Songs the player may report on the way to <see cref="Track"/> (skipped through): not a wrong prediction.</summary>
        public IReadOnlySet<string>? Passing { get; init; }
    }

    /// <summary>
    /// Something else is about to play this song (the queue view played it): show it now, and hold it through the player's
    /// late reports, including the songs it reports while skipping to it (<paramref name="passing"/>).
    /// </summary>
    public void ExpectTrack(MediaPrediction p, IEnumerable<string> passing)
    {
        var s = Snapshot;
        if (!s.HasMedia) return;
        var set = new HashSet<string>(passing.Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        StartOverride(new Override(DateTimeOffset.Now + TimeSpan.FromSeconds(6), true, p, true, s.Title) { Passing = set });
        PublishOptimistic(AsTrack(s, p));
    }

    public async void PlayPause()
    {
        Log("cmd PlayPause");
        if (_session == null) return;
        var s = Snapshot;
        if (s.HasMedia && s.CanPlayPause)
        {
            bool target = !s.IsPlaying;
            StartOverride(new Override(DateTimeOffset.Now + OverrideFor, target, _ovr?.Track, _ovr?.Restart ?? false, _ovr?.FromTitle ?? s.Title));
            PublishOptimistic(s with { IsPlaying = target, Position = s.PositionNow, PositionSampledAt = DateTimeOffset.Now });
        }
        try { await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        Log("cmd Next");
        if (_session == null) return;
        var s = Snapshot;
        if (s.CanNext && IsSpotify(s) && UpNext is { } next && SameTitle(next.ForTitle, s.Title))
        {
            StartOverride(new Override(DateTimeOffset.Now + OverrideFor, true, next, true, s.Title));
            PublishOptimistic(AsTrack(s, next));
        }
        try { await _session.TrySkipNextAsync(); } catch { }
    }

    public async void Previous()
    {
        Log("cmd Previous");
        if (_session == null) return;
        var s = Snapshot;
        if (s.CanPrevious && IsSpotify(s))
        {
            if (s.PositionNow > TimeSpan.FromSeconds(3))
            {
                // Spotify restarts the song when it's more than a few seconds in.
                StartOverride(new Override(DateTimeOffset.Now + OverrideFor, _ovr?.Playing, null, true, s.Title));
                PublishOptimistic(s with { Position = TimeSpan.Zero, PositionSampledAt = DateTimeOffset.Now });
            }
            else if (_previousTrack is { } prev)
            {
                StartOverride(new Override(DateTimeOffset.Now + OverrideFor, true, prev, true, s.Title));
                PublishOptimistic(AsTrack(s, prev));
            }
        }
        try { await _session.TrySkipPreviousAsync(); } catch { }
    }

    private static bool IsSpotify(MediaSnapshot s) => s.SourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase);

    private static bool SameTitle(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static MediaSnapshot AsTrack(MediaSnapshot s, MediaPrediction p) => s with
    {
        Title = p.Title, Artist = p.Artist, Artwork = p.Art, Album = "", IsPlaying = true,
        Position = TimeSpan.Zero, PositionSampledAt = DateTimeOffset.Now, Duration = p.Duration > TimeSpan.Zero ? p.Duration : s.Duration,
    };

    private void StartOverride(Override o)
    {
        _ovr = o;
        // When it runs out, ask the player once more (its late report may have come and gone meanwhile).
        _ovrTimer ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_ovrTimer == null) return;
        _ovrTimer.IsRepeating = false;
        _ovrTimer.Interval = OverrideFor + TimeSpan.FromMilliseconds(100);
        _ovrTimer.Tick -= OnOverrideEnded;
        _ovrTimer.Tick += OnOverrideEnded;
        _ovrTimer.Stop();
        _ovrTimer.Start();
    }

    private void OnOverrideEnded(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object e) { t.Stop(); QueueRefresh(reselect: false); }

    private void PublishOptimistic(MediaSnapshot s)
    {
        Log($"optimistic '{s.Title}' playing={s.IsPlaying}");
        NoteTrack(s);
        Snapshot = s;
        Changed?.Invoke(s);
    }

    /// <summary>The player's report, adjusted for a command it hasn't caught up with yet.</summary>
    private MediaSnapshot Reconcile(MediaSnapshot os)
    {
        if (_ovr is not { } o) return os;
        if (DateTimeOffset.Now >= o.Until) { _ovr = null; return os; }
        var shown = Snapshot;
        if (o.Track is { } t)
        {
            if (SameTitle(os.Title, t.Title)) { _ovr = o.Playing is { } p && os.IsPlaying != p ? o with { Track = null, Restart = false } : null; }
            else if (SameTitle(os.Title, o.FromTitle) || o.Passing?.Contains(os.Title.Trim()) == true)
                return os with { Title = shown.Title, Artist = shown.Artist, Artwork = shown.Artwork, Album = shown.Album, Duration = shown.Duration,
                                 IsPlaying = o.Playing ?? os.IsPlaying, Position = shown.Position, PositionSampledAt = shown.PositionSampledAt, PlaybackRate = shown.PlaybackRate };
            else { _ovr = null; return os; } // a different song than predicted: the player is right
        }
        if (_ovr is not { } o2) return os;
        if (o2.Restart)
        {
            if (!SameTitle(os.Title, o2.FromTitle) && o2.Track == null) { _ovr = null; return os; }
            if (os.Position < TimeSpan.FromSeconds(3) && o2.Playing is null) { _ovr = null; return os; }
            if (os.Position >= TimeSpan.FromSeconds(3)) os = os with { Position = shown.Position, PositionSampledAt = shown.PositionSampledAt };
        }
        if (o2.Playing is { } playing)
        {
            if (os.IsPlaying == playing) { if (!o2.Restart || os.Position < TimeSpan.FromSeconds(3)) _ovr = null; }
            else os = os with { IsPlaying = playing, Position = shown.Position, PositionSampledAt = shown.PositionSampledAt };
        }
        return os;
    }

    /// <summary>Remembers the previous song whenever the title changes (for Previous at the start of a song).</summary>
    private void NoteTrack(MediaSnapshot s)
    {
        var cur = Snapshot;
        if (string.IsNullOrWhiteSpace(s.Title) || string.IsNullOrWhiteSpace(cur.Title) || SameTitle(s.Title, cur.Title)) return;
        if (_previousTrack is { } p && SameTitle(p.Title, s.Title)) _previousTrack = null; // went back to it: forget it
        else _previousTrack = new MediaPrediction(cur.Title, cur.Artist, cur.Artwork, cur.Duration, s.Title);
    }

    public async void Seek(TimeSpan position) => await SeekAsync(position);

    /// <summary>
    /// Asks the player to jump to <paramref name="position"/>. True when the player accepted (it then reports the new
    /// position itself; the snapshot is updated optimistically meanwhile so nothing snaps back).
    /// </summary>
    public async Task<bool> SeekAsync(TimeSpan position)
    {
        try
        {
            if (_session == null) return false;
            // The timeline's start isn't always zero: positions are relative to it.
            var start = _session.GetTimelineProperties().StartTime;
            bool ok = await _session.TryChangePlaybackPositionAsync((position + start).Ticks);
            if (!ok) return false;
            Snapshot = Snapshot with { Position = position, PositionSampledAt = DateTimeOffset.Now };
            Changed?.Invoke(Snapshot);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Switch to the next active player (when several are running).</summary>
    public void CycleSession()
    {
        if (_manager == null) return;
        var sessions = _manager.GetSessions();
        if (sessions.Count < 2) return;
        int idx = 0;
        for (int i = 0; i < sessions.Count; i++)
            if (sessions[i].SourceAppUserModelId == _session?.SourceAppUserModelId) { idx = i; break; }
        _userPickedSourceId = sessions[(idx + 1) % sessions.Count].SourceAppUserModelId;
        QueueRefresh(reselect: true);
    }

    // ---- Internals -------------------------------------------------------------------------
    private void QueueRefresh(bool reselect)
    {
        // Coalesce bursts of OS events into one refresh on the UI thread.
        if (reselect) _reselectPending = true;
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        Ui.Post(async () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            await RefreshAsync();
        });
    }

    private volatile bool _reselectPending;

    private async Task RefreshAsync()
    {
        if (_manager == null || !_running) return;
        try
        {
            if (_reselectPending)
            {
                _reselectPending = false;
                SelectSession();
            }

            int count = _manager.GetSessions().Count;
            if (_session == null)
            {
                Publish(MediaSnapshot.Empty with { SessionCount = count });
                return;
            }

            // Refreshes can overlap (an OS event arrives while another refresh is still decoding a cover). Only the newest
            // one may publish: an older one finishing later would put back a stale state (e.g. "paused" from the moment
            // the song changed, which then stuck because Spotify sends nothing more while it plays).
            int version = ++_refreshVersion;
            var session = _session;
            Log("refresh start");
            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion || !ReferenceEquals(session, _session)) { Log("refresh superseded"); return; }

            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            if (string.IsNullOrEmpty(artist)) artist = props?.AlbumArtist ?? "";
            string key = $"{session.SourceAppUserModelId}|{title}|{artist}";
            await UpdateArtworkAsync(key, title, props);
            if (version != _refreshVersion || !ReferenceEquals(session, _session)) { Log("refresh superseded"); return; }

            // Read the playback state and timeline now, after the awaits, so they're current when published.
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var controls = playback.Controls;
            Log($"refresh props: '{title}' {playback.PlaybackStatus}");
            var duration = timeline.EndTime - timeline.StartTime;
            Publish(Reconcile(new MediaSnapshot(
                title, artist, _art,
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                timeline.Position - timeline.StartTime,
                duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
                timeline.LastUpdatedTime.Year > 2000 ? timeline.LastUpdatedTime : DateTimeOffset.Now,
                playback.PlaybackRate ?? 1.0,
                controls.IsPreviousEnabled, controls.IsNextEnabled,
                controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                controls.IsPlaybackPositionEnabled && duration > TimeSpan.Zero,
                FriendlyAppName(_session.SourceAppUserModelId),
                count) { Album = props?.AlbumTitle ?? "" }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Media refresh failed: {ex.Message}");
        }
    }

    private void SelectSession()
    {
        var sessions = _manager!.GetSessions();
        GlobalSystemMediaTransportControlsSession? pick = null;
        if (_userPickedSourceId != null)
            pick = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _userPickedSourceId);
        if (pick == null)
        {
            _userPickedSourceId = null;
            // Windows' "current" session isn't always the one playing (a paused browser tab can hold it while Spotify
            // plays): prefer one that's actually playing.
            pick = _manager.GetCurrentSession();
            if (pick == null || !IsPlaying(pick)) pick = sessions.FirstOrDefault(IsPlaying) ?? pick ?? sessions.FirstOrDefault();
        }
        if (ReferenceEquals(pick, _session) ||
            (pick != null && _session != null && pick.SourceAppUserModelId == _session.SourceAppUserModelId))
        {
            if (pick != null && !ReferenceEquals(pick, _session)) { Detach(); Attach(pick); }
            return;
        }
        Detach();
        if (pick != null) Attach(pick);
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession s)
    {
        LogState($"listening to {FriendlyAppName(s.SourceAppUserModelId)}");
        _session = s;
        s.MediaPropertiesChanged += OnSessionChanged;
        s.PlaybackInfoChanged += OnSessionChanged;
        s.TimelinePropertiesChanged += OnSessionChanged;
    }

    private void Detach()
    {
        if (_session == null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
        }
        catch { }
        _session = null;
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        Log($"event {args.GetType().Name}");
        QueueRefresh(reselect: false);
    }

    private void Publish(MediaSnapshot s)
    {
        if (!_running && !ReferenceEquals(s, MediaSnapshot.Empty)) return; // a refresh that finished after Stop()
        var before = Snapshot;
        if (SameState(before, s)) return; // nothing new (e.g. a re-check): don't rebuild the player for it
        if (before.IsPlaying != s.IsPlaying || before.SourceApp != s.SourceApp)
            LogState($"{(before.SourceApp != s.SourceApp ? $"session {(before.SourceApp.Length == 0 ? "-" : before.SourceApp)} -> {(s.SourceApp.Length == 0 ? "-" : s.SourceApp)}, " : "")}'{s.Title}' {(s.IsPlaying ? "playing" : "paused")}{(_ovr != null ? " (held for a command)" : "")}");
        NoteTrack(s);
        Snapshot = s;
        Changed?.Invoke(s);
        Log($"published '{s.Title}' playing={s.IsPlaying}");
    }

    // ---- Artwork --------------------------------------------------------------------------------------------
    // Spotify (and some browsers) update the session's thumbnail a moment after the title, so the first read after a
    // song change can still return the previous song's cover. Each song's cover is decoded once (overlapping refreshes
    // share the same load, and nothing is marked loaded until it is), compared with the previous song's cover, and
    // when it's that stale cover (or missing) the song shows the placeholder instead, while the thumbnail is read again
    // shortly (and on the session's next change) until the real one arrives. A cover the notch already has for the
    // song (the predicted next song, from Spotify's queue) is used straight away.
    private int _refreshVersion;
    private Task<Windows.Graphics.Imaging.SoftwareBitmap?>? _artLoad;
    private string? _artLoadKey;
    private bool _artStale;                 // the shown cover for _artKey isn't the real one yet (stale or missing)
    private ulong _prevArtPrint;            // the previous song's cover (to recognise a stale thumbnail)
    private int _artRetries;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _artRetry;
    private static readonly int[] ArtRetryMs = { 300, 700, 1300, 2500 };
    private Windows.Graphics.Imaging.SoftwareBitmap? _artCandidate; // what the thumbnail returned (maybe the same album's cover)

    private async Task UpdateArtworkAsync(string key, string title, GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        if (key != _artKey)
        {
            // A new song: forget the old cover now (never show it with the new title).
            if (_art != null) _prevArtPrint = Fingerprint(_art);
            _artKey = key;
            _artRetries = 0;
            var known = _ovr?.Track is { Art: { } a } t && SameTitle(title, t.Title) ? a
                      : UpNext is { Art: { } b } u && SameTitle(title, u.Title) ? b : null;
            if (known != null) { _art = known; _artStale = false; LogState($"cover for '{title}' from the queue prediction"); return; }
            _art = null;
            _artCandidate = null;
            _artStale = true;
        }
        if (!_artStale) return;
        if (_artLoadKey != key || _artLoad == null) { _artLoadKey = key; _artLoad = LoadArtworkAsync(props); }
        var load = _artLoad;
        var art = await load;
        if (ReferenceEquals(load, _artLoad)) _artLoad = null; // next attempt reads the thumbnail again
        if (key != _artKey || !_artStale) return;
        if (art != null && (_prevArtPrint == 0 || Fingerprint(art) != _prevArtPrint))
        {
            _art = art;
            _artStale = false;
            Log($"cover for '{title}' loaded");
            return;
        }
        if (art != null) _artCandidate = art;
        LogState(art == null ? $"cover for '{title}' not available yet: retrying" : $"cover for '{title}' is still the previous song's: retrying");
        ScheduleArtRetry();
    }

    private void ScheduleArtRetry()
    {
        if (_artRetries >= ArtRetryMs.Length)
        {
            // It never changed: two songs from the same album share their cover, so that one is right after all.
            _artStale = false;
            if (_artCandidate != null) { _art = _artCandidate; LogState("cover unchanged after retries: same album, keeping it"); QueueRefresh(reselect: false); }
            return;
        }
        _artRetry ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_artRetry == null) return;
        _artRetry.IsRepeating = false;
        _artRetry.Interval = TimeSpan.FromMilliseconds(ArtRetryMs[_artRetries++]);
        _artRetry.Tick -= OnArtRetry;
        _artRetry.Tick += OnArtRetry;
        _artRetry.Stop();
        _artRetry.Start();
    }

    private void OnArtRetry(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object e)
    {
        t.Stop();
        if (!_artStale) return;
        if (_artRetries >= ArtRetryMs.Length) { ScheduleArtRetry(); return; } // last wait over: settle
        QueueRefresh(reselect: false);
    }

    /// <summary>The song on screen still has no real cover (Spotify's Web API can supply one).</summary>
    public bool NeedsArtwork => Snapshot.HasMedia && (_artStale || _art == null);

    /// <summary>A cover for the playing song from elsewhere (Spotify's Web API), used while the session's is missing / stale.</summary>
    public void SupplyArtwork(string title, Windows.Graphics.Imaging.SoftwareBitmap art)
    {
        if (!NeedsArtwork || !SameTitle(Snapshot.Title, title)) return;
        _art = art;
        _artStale = false;
        LogState($"cover for '{title}' from Spotify's Web API");
        Snapshot = Snapshot with { Artwork = art };
        Changed?.Invoke(Snapshot);
    }

    /// <summary>A cheap fingerprint of a cover (sampled pixels), to tell a stale thumbnail from the new one.</summary>
    private static ulong Fingerprint(Windows.Graphics.Imaging.SoftwareBitmap bmp)
    {
        try
        {
            var bytes = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < bytes.Length; i += 37) { h ^= bytes[i]; h *= 1099511628211UL; }
            return h ^ (ulong)bmp.PixelWidth << 32 ^ (ulong)bmp.PixelHeight;
        }
        catch { return 0; }
    }

    // ---- Playback-state safety net and logging -----------------------------------------------------------------
    /// <summary>Re-reads the sessions and the playing state (the media player is on screen: every few seconds, and when the notch opens).</summary>
    public void Recheck() { if (_running) QueueRefresh(reselect: _userPickedSourceId == null); }

    /// <summary>
    /// Spotify's Web API says the song is (not) playing. If the session says otherwise and no command is in flight, the
    /// session is read again and, while it keeps disagreeing (for up to 20 s), the Web API's answer is shown.
    /// </summary>
    public void ConfirmPlaying(string title, bool playing)
    {
        var s = Snapshot;
        if (_ovr != null || !s.HasMedia || !SameTitle(s.Title, title) || s.IsPlaying == playing) return;
        LogState($"Spotify Web API says '{title}' is {(playing ? "playing" : "paused")}, the session said {(s.IsPlaying ? "playing" : "paused")}: correcting");
        StartOverride(new Override(DateTimeOffset.Now + TimeSpan.FromSeconds(20), playing, null, false, s.Title));
        PublishOptimistic(s with { IsPlaying = playing, Position = s.PositionNow, PositionSampledAt = DateTimeOffset.Now });
        QueueRefresh(reselect: false);
    }

    /// <summary>Playback-state changes, session switches and corrections, always logged (media.log, kept small).</summary>
    internal static void LogState(string line)
    {
        try
        {
            var path = Path.Combine(SettingsService.DataFolder, "media.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}\n");
        }
        catch { }
    }

    /// <summary>Same song, cover, state and controls, and the position where the shown one would be by now (within 1 s).</summary>
    private static bool SameState(MediaSnapshot a, MediaSnapshot b) =>
        a.Title == b.Title && a.Artist == b.Artist && a.Album == b.Album && ReferenceEquals(a.Artwork, b.Artwork) && a.IsPlaying == b.IsPlaying
        && a.Duration == b.Duration && a.CanNext == b.CanNext && a.CanPrevious == b.CanPrevious && a.CanPlayPause == b.CanPlayPause
        && a.CanSeek == b.CanSeek && a.SourceApp == b.SourceApp && a.SessionCount == b.SessionCount && a.PlaybackRate == b.PlaybackRate
        && Math.Abs((a.PositionNow - b.PositionNow).TotalSeconds) < 1;

    private static async Task<Windows.Graphics.Imaging.SoftwareBitmap?> LoadArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        if (props?.Thumbnail == null) return null;
        try { return await ImageLoader.LoadAsync(props.Thumbnail, 200); }
        catch { return null; }
    }

    private static string FriendlyAppName(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var s = aumid;
        int bang = s.LastIndexOf('!');
        if (bang >= 0) s = s[..bang]; // packaged app: use the package family name
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        if (s.Contains('_')) s = s.Split('_')[0];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s;
    }

    public void Dispose() => Detach();
}
