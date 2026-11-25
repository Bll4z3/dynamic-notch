using DynamicNotch.Services;
using Microsoft.UI.Dispatching;

namespace DynamicNotch.ViewModels;

/// <summary>
/// The media player's Like button: shown while Spotify is the current player. When a song starts it asks your
/// Spotify account (Web API) whether the playing track is in your Liked Songs, so the heart starts filled or
/// empty; a click saves / removes it. The heart flips at once and goes back if Spotify refuses. Without a
/// connected account a click asks you to connect it in Settings.
/// </summary>
public sealed class SpotifyLikeViewModel : ObservableObject
{
    private readonly MusicViewModel _music;
    private readonly SpotifyService _spotify;
    private readonly DispatcherQueueTimer _check;
    private string? _trackId;
    private int _version;
    private bool _busy, _wasVisible;

    public SpotifyLikeViewModel(MusicViewModel music, SpotifyService spotify)
    {
        _music = music;
        _spotify = spotify;
        _check = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _check.IsRepeating = false;
        _check.Interval = TimeSpan.FromMilliseconds(900); // Spotify's API catches up with a new song a moment later
        _check.Tick += async (_, _) => { _check.Stop(); await RefreshAsync(); };
        // Only a real track change resets the state. (The music view model raises a catch-all change on every
        // media snapshot, including Spotify's periodic timeline updates: reacting to those restarted the check
        // and discarded every lookup in flight, so the heart never learned the real state.)
        music.TrackChanged += OnTrackChanged;
        music.PropertyChanged += (_, _) =>
        {
            bool visible = IsVisible;
            if (visible == _wasVisible) return;
            _wasVisible = visible;
            OnPropertyChanged(nameof(IsVisible));
            OnTrackChanged();
        };
        spotify.Changed += () => { OnPropertyChanged(nameof(IsConnected)); OnTrackChanged(); };
        spotify.RateLimited += () => OnPropertyChanged(nameof(LimitedUntil));
        _wasVisible = IsVisible;
        OnTrackChanged();
    }

    /// <summary>Raised when a click needs a Spotify connection first (the app opens Settings).</summary>
    public event Action? ConnectRequested;

    /// <summary>Spotify is the player on the media page.</summary>
    public bool IsVisible => _music.HasMedia && _music.SourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
    public bool IsConnected => _spotify.IsConnected;

    /// <summary>While Spotify is rate-limiting this app (429): likes can't be read or saved until then.</summary>
    public DateTimeOffset? LimitedUntil => _spotify.LimitedUntil;

    private DateTimeOffset _lastChecked;

    private bool _liked;
    public bool IsLiked { get => _liked; private set => Set(ref _liked, value); }

    /// <summary>True when the last IsLiked change came from a click (the view plays the pop); false for a sync.</summary>
    public bool ChangedByUser { get; private set; }

    /// <summary>True while the state came from your account (not a guess).</summary>
    public bool IsKnown { get; private set; }

    private void SetLiked(bool liked, bool byUser)
    {
        ChangedByUser = byUser;
        IsLiked = liked;
    }

    /// <summary>A new song (or player / connection): forget the old track and look the new one up shortly.</summary>
    private void OnTrackChanged()
    {
        _version++;
        _trackId = null; // never like / unlike the previous song by mistake
        IsKnown = false;
        _check.Stop();
        if (!IsVisible || !_spotify.IsConnected) { SetLiked(false, byUser: false); return; }
        _check.Start();
    }

    /// <summary>Re-checks the playing track (also when the media page comes into view: likes made in Spotify itself).</summary>
    public void Refresh()
    {
        if (_busy || !IsVisible || !_spotify.IsConnected || LimitedUntil != null) return;
        // At most once a minute for the same song (opening the notch often must not add up to a rate limit).
        if (_trackId != null && IsKnown && DateTimeOffset.Now - _lastChecked < TimeSpan.FromSeconds(60)) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        int v = ++_version;
        var id = await CurrentTrackIdAsync(v);
        if (v != _version) return;
        _trackId = id;
        if (id == null) { IsKnown = false; return; }
        var liked = await _spotify.IsLikedAsync(id);
        if (v != _version || liked == null) return;
        IsKnown = true;
        _lastChecked = DateTimeOffset.Now;
        SetLiked(liked.Value, byUser: false);
    }

    /// <summary>The track Spotify reports, once its name matches the song on screen (its API can lag a skip by a second or so).</summary>
    private async Task<string?> CurrentTrackIdAsync(int v)
    {
        (string Id, string Name)? track = null;
        for (int attempt = 0; attempt < 4 && v == _version; attempt++)
        {
            if (attempt > 0) await Task.Delay(700);
            track = await _spotify.CurrentTrackAsync();
            if (track is { } t && TitleMatches(t.Name)) return t.Id;
        }
        return track?.Id; // still different (e.g. a local file or a podcast): use what Spotify says
    }

    private bool TitleMatches(string name) =>
        _music.Title is "" or "Unknown title" || string.Equals(name.Trim(), _music.Title.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Like / unlike the playing song: the heart flips at once, and goes back if Spotify refuses.</summary>
    public async Task ToggleAsync()
    {
        if (!_spotify.IsConnected) { ConnectRequested?.Invoke(); return; }
        if (LimitedUntil != null) { OnPropertyChanged(nameof(LimitedUntil)); return; } // can't save now: don't fake a fill
        if (_busy) return; // one request at a time (a double click must not like then unlike)
        _busy = true;
        int v = ++_version; // results of a lookup started before the click are stale now
        _check.Stop();
        try
        {
            if (_trackId == null)
            {
                _trackId = await CurrentTrackIdAsync(v);
                if (_trackId == null || v != _version) return;
            }
            string id = _trackId;
            if (!IsKnown && await _spotify.IsLikedAsync(id) is { } actual)
            {
                // Toggle from the real state, not a guess.
                if (v != _version) return;
                IsKnown = true;
                SetLiked(actual, byUser: false);
            }
            bool target = !IsLiked;
            SetLiked(target, byUser: true); // optimistic
            bool ok = await _spotify.SetLikedAsync(id, target);
            if (v != _version) return; // the song changed meanwhile: its own lookup decides the heart
            if (!ok) SetLiked(!target, byUser: false);
            else { IsKnown = true; _lastChecked = DateTimeOffset.Now; }
        }
        finally { _busy = false; }
    }
}
