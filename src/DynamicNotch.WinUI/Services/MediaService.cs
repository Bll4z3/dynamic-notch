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

    // ---- Commands ----------------------------------------------------------------------------
    public async void PlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }

    public async void Seek(TimeSpan position)
    {
        try
        {
            if (_session == null) return;
            await _session.TryChangePlaybackPositionAsync(position.Ticks);
            // Optimistically update so the bar doesn't snap back while the player catches up.
            Snapshot = Snapshot with { Position = position, PositionSampledAt = DateTimeOffset.Now };
            Changed?.Invoke(Snapshot);
        }
        catch { }
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

            var props = await _session.TryGetMediaPropertiesAsync();
            var playback = _session.GetPlaybackInfo();
            var timeline = _session.GetTimelineProperties();
            var controls = playback.Controls;

            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            if (string.IsNullOrEmpty(artist)) artist = props?.AlbumArtist ?? "";

            string key = $"{_session.SourceAppUserModelId}|{title}|{artist}";
            if (key != _artKey)
            {
                _artKey = key;
                _art = await LoadArtworkAsync(props);
            }

            var duration = timeline.EndTime - timeline.StartTime;
            Publish(new MediaSnapshot(
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
                count));
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

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => QueueRefresh(reselect: false);

    private void Publish(MediaSnapshot s)
    {
        if (!_running && !ReferenceEquals(s, MediaSnapshot.Empty)) return; // a refresh that finished after Stop()
        Snapshot = s;
        Changed?.Invoke(s);
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
