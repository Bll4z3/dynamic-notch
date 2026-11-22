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

    public SpotifyLikeViewModel(MusicViewModel music, SpotifyService spotify)
    {
        _music = music;
        _spotify = spotify;
        _check = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _check.IsRepeating = false;
        _check.Interval = TimeSpan.FromMilliseconds(900); // Spotify's API catches up with a new song a moment later
        _check.Tick += async (_, _) => { _check.Stop(); await RefreshAsync(); };
        music.TrackChanged += Schedule;
        music.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MusicViewModel.SourceApp) or "" or null) { OnPropertyChanged(nameof(IsVisible)); Schedule(); } };
        spotify.Changed += () => { OnPropertyChanged(nameof(IsConnected)); Schedule(); };
        Schedule();
    }

    /// <summary>Raised when a click needs a Spotify connection first (the app opens Settings).</summary>
    public event Action? ConnectRequested;

    /// <summary>Spotify is the player on the media page.</summary>
    public bool IsVisible => _music.HasMedia && _music.SourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
    public bool IsConnected => _spotify.IsConnected;

    private bool _liked;
    public bool IsLiked { get => _liked; private set => Set(ref _liked, value); }

    /// <summary>True while the state came from your account (not a guess).</summary>
    public bool IsKnown { get; private set; }

    private void Schedule()
    {
        _version++;
        if (!IsVisible || !_spotify.IsConnected) { _trackId = null; IsKnown = false; IsLiked = false; return; }
        _check.Stop();
        _check.Start();
    }

    private async Task RefreshAsync()
    {
        int v = _version;
        var id = await _spotify.CurrentTrackIdAsync();
        if (v != _version) return;
        _trackId = id;
        if (id == null) { IsKnown = false; IsLiked = false; return; }
        var liked = await _spotify.IsLikedAsync(id);
        if (v != _version || liked == null) return;
        IsKnown = true;
        IsLiked = liked.Value;
    }

    /// <summary>Like / unlike the playing song. Returns the new state (the view animates to it straight away).</summary>
    public async Task ToggleAsync()
    {
        if (!_spotify.IsConnected) { ConnectRequested?.Invoke(); return; }
        _trackId ??= await _spotify.CurrentTrackIdAsync();
        if (_trackId is not { } id) return;
        bool target = !IsLiked;
        IsLiked = target; // optimistic
        if (!await _spotify.SetLikedAsync(id, target)) IsLiked = !target;
    }
}
