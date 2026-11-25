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
    private sealed record Override(DateTimeOffset Until, bool? Playing, MediaPrediction? Track, bool Restart, string FromTitle);

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
            else if (SameTitle(os.Title, o.FromTitle))
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

            Log("refresh start");
            var props = await _session.TryGetMediaPropertiesAsync();
            var playback = _session.GetPlaybackInfo();
            Log($"refresh props: '{props?.Title}' {playback.PlaybackStatus}");
            var timeline = _session.GetTimelineProperties();
            var controls = playback.Controls;

            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            if (string.IsNullOrEmpty(artist)) artist = props?.AlbumArtist ?? "";

            string key = $"{_session.SourceAppUserModelId}|{title}|{artist}";
            // The player now reports the song the notch already predicted: keep the predicted cover (same bitmap, so
            // nothing re-animates) instead of decoding the player's thumbnail a second later.
            if (key != _artKey && _ovr?.Track is { Art: { } predictedArt } t && SameTitle(title, t.Title))
            {
                _artKey = key;
                _art = predictedArt;
            }
            if (key != _artKey)
            {
                _artKey = key;
                _art = await LoadArtworkAsync(props);
                Log($"refresh art loaded ({(_art != null ? "yes" : "none")})");
            }

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
            pick = _manager.GetCurrentSession() ?? sessions.FirstOrDefault();
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

    private void Attach(GlobalSystemMediaTransportControlsSession s)
    {
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
        NoteTrack(s);
        Snapshot = s;
        Changed?.Invoke(s);
        Log($"published '{s.Title}' playing={s.IsPlaying}");
    }

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
