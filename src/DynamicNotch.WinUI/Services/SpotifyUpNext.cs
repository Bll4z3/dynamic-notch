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
        spotify.Changed += () => { _version++; _timer.Stop(); _timer.Start(); };
    }

    private async Task FetchAsync()
    {
        var s = _music.Snapshot;
        if (!s.SourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(s.Title) || !_spotify.HasLibraryScopes || _spotify.LimitedUntil != null) return;
        int v = ++_version;
        var r = await _spotify.UpNextAsync();
        if (v != _version || !r.Ok || r.Value.Next is not { } next) return;
        var current = r.Value.Current;
        // Only if Spotify's "currently playing" is the song on screen (else the queue is for another moment).
        if (current != null && !string.Equals(current.Name.Trim(), s.Title.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        bool firstOnly = current != null && string.Equals(s.Artist.Trim(), current.PrimaryArtist.Trim(), StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(s.Artist.Trim(), current.Artists.Trim(), StringComparison.OrdinalIgnoreCase);
        Windows.Graphics.Imaging.SoftwareBitmap? art = null;
        if (next.ImageUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            art = await ImageLoader.LoadAsync(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromUri(uri), 200);
        if (v != _version) return;
        _media.UpNext = new MediaService.MediaPrediction(next.Name, firstOnly ? next.PrimaryArtist : next.Artists, art, next.Duration, s.Title);
        SpotifyService.Log("up next: predicted");
    }
}
