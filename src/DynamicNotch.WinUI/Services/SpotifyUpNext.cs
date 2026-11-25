using DynamicNotch.ViewModels;
using Microsoft.UI.Dispatching;

namespace DynamicNotch.Services;

/// <summary>
/// Spotify reports a skip to Windows 2-3 seconds late, so the media player would keep showing the old song after Next.
/// A moment after each new song (once, so it never adds up to a rate limit), this asks your Spotify queue what plays
/// next (GET /me/player/queue) and decodes its cover; Next then shows that song at once (MediaService.UpNext) while
/// Spotify catches up. The artist is written the way Spotify writes it to Windows (all artists or just the first),
/// learnt from the song playing now, so the late report matches the prediction and nothing animates twice.
/// Needs a connected account with the playlist / playback permissions; otherwise Next simply waits for Spotify.
/// </summary>
public sealed class SpotifyUpNext
{
    private readonly MediaService _media;
    private readonly MusicViewModel _music;
    private readonly SpotifyService _spotify;
    private readonly DispatcherQueueTimer _timer;
    private int _version;

    public SpotifyUpNext(MediaService media, MusicViewModel music, SpotifyService spotify)
    {
        _media = media;
        _music = music;
        _spotify = spotify;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.IsRepeating = false;
        _timer.Interval = TimeSpan.FromMilliseconds(1800); // Spotify's queue catches up with the new song
        _timer.Tick += async (_, _) => { _timer.Stop(); await FetchAsync(); };
        music.TrackChanged += () => { _version++; _timer.Stop(); _timer.Start(); };
        // When the notch opens on the player, Spotify's own state confirms the play / pause shown (at most every 10 s).
        // (Also once the Spotify session shows up while the player is open: the notch can open before it has loaded.)
        music.PropertyChanged += (_, e) =>
        {
            if (!_music.IsVisible) { _confirmedThisOpen = false; return; }
            if (!_confirmedThisOpen && _music.SourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) { _confirmedThisOpen = true; _ = ConfirmPlayingAsync(); }
        };
        spotify.Changed += () => { _version++; _timer.Stop(); _timer.Start(); };
    }

    private bool _firstArtistOnly;
    private DateTimeOffset _lastConfirm;
    private bool _confirmedThisOpen;

    private async Task ConfirmPlayingAsync()
    {
        var s = _music.Snapshot;
        if (!s.SourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase) || !_spotify.HasLibraryScopes || _spotify.LimitedUntil != null) return;
        if (DateTimeOffset.Now - _lastConfirm < TimeSpan.FromSeconds(10)) return;
        _lastConfirm = DateTimeOffset.Now;
        var state = await _spotify.PlaybackStateAsync();
        MediaService.LogState(state is { } st ? $"Spotify Web API: '{st.Track}' {(st.IsPlaying ? "playing" : "paused")}; the session shows '{_music.Snapshot.Title}' {(_music.Snapshot.IsPlaying ? "playing" : "paused")}" : "Spotify Web API: no playback state");
        if (state is { Track: { } track }) _media.ConfirmPlaying(track, state.Value.IsPlaying);
    }

    /// <summary>The queue view is playing <paramref name="t"/>: the player shows it straight away (cover included).</summary>
    public async Task ExpectAsync(SpotifyTrack t, IEnumerable<string> passing)
    {
        Windows.Graphics.Imaging.SoftwareBitmap? art = null;
        if (t.ImageUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var load = ImageLoader.LoadAsync(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromUri(uri), 200);
            if (await Task.WhenAny(load, Task.Delay(700)) == load) art = await load; // never hold the change for long
        }
        _media.ExpectTrack(new MediaService.MediaPrediction(t.Name, _firstArtistOnly ? t.PrimaryArtist : t.Artists, art, t.Duration, _music.Snapshot.Title), passing);
    }

    private async Task FetchAsync()
    {
        var s = _music.Snapshot;
        if (!s.SourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(s.Title) || !_spotify.HasLibraryScopes || _spotify.LimitedUntil != null) return;
        int v = ++_version;
        var r = await _spotify.UpNextAsync();
        if (v != _version || !r.Ok) return;
        var current = r.Value.Current;
        // The session's thumbnail still missing / stale: Spotify's own cover for the playing song fills in.
        if (current?.ImageUrl is { } curUrl && _media.NeedsArtwork && string.Equals(current.Name.Trim(), s.Title.Trim(), StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(curUrl, UriKind.Absolute, out var curUri)
            && await ImageLoader.LoadAsync(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromUri(curUri), 200) is { } curArt && v == _version)
            _media.SupplyArtwork(current.Name, curArt);
        if (r.Value.Next is not { } next) return;
        // Only if Spotify's "currently playing" is the song on screen (else the queue is for another moment).
        if (current != null && !string.Equals(current.Name.Trim(), s.Title.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        bool firstOnly = current != null && string.Equals(s.Artist.Trim(), current.PrimaryArtist.Trim(), StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(s.Artist.Trim(), current.Artists.Trim(), StringComparison.OrdinalIgnoreCase);
        if (current != null) _firstArtistOnly = firstOnly;
        Windows.Graphics.Imaging.SoftwareBitmap? art = null;
        if (next.ImageUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            art = await ImageLoader.LoadAsync(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromUri(uri), 200);
        if (v != _version) return;
        _media.UpNext = new MediaService.MediaPrediction(next.Name, firstOnly ? next.PrimaryArtist : next.Artists, art, next.Duration, s.Title);
        SpotifyService.Log("up next: predicted");
    }
}
