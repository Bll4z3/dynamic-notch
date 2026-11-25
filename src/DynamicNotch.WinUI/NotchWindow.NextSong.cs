using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Dispatching;

namespace DynamicNotch;

/// <summary>
/// "Next song": when the playing track changes, the closed notch grows into a small pill with the new song's cover,
/// title and artist (Views/NextSongView.cs), which goes away by itself after about 4 seconds (hover holds it, a click
/// opens the media player). Track changes come from the media session's MediaPropertiesChanged event (MediaService
/// raises one snapshot per change). Only a real change of song shows it:
/// <list type="bullet">
/// <item>not the first song the app sees (start-up, or the media page being switched back on);</item>
/// <item>not pause / resume, seeking or timeline updates (same title and artist);</item>
/// <item>not switching to another player (a different app, not a new song);</item>
/// <item>not the brief empty title some players send between songs (the next real title is compared with the last
/// one);</item>
/// <item>not while the notch is open (the media player shows the song itself).</item>
/// </list>
/// The pill waits a moment after the change, so a player that reports the title and the artist in separate events
/// shows both at once. Settings ▸ Pills ▸ Next song switches it off.
/// </summary>
public sealed partial class NotchWindow
{
    private DispatcherQueueTimer _nextSongTimer = null!;
    private string? _songKey, _songSource;
    private readonly long _songWatchStart = Environment.TickCount64;
    private const int NextSongSettleMs = 350, NextSongStartupQuietMs = 4000;

    private void BuildNextSong()
    {
        _nextSongTimer = OneShot(DispatcherQueue, NextSongSettleMs, ShowNextSong);
        Vm.Music.TrackChanged += OnSongMaybeChanged;
        // The cover can arrive a moment after the title: a pill already showing that song picks it up.
        Vm.Music.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not ("" or null or nameof(MusicViewModel.Artwork))) return;
            if (Vm.Activity.Current is { Kind: ActivityKind.NextSong } a && Vm.Music.Artwork is { } art && !ReferenceEquals(a.Artwork, art)
                && string.Equals(a.Title.Trim(), Vm.Music.Snapshot.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                _nextSongView.UpdateArt(art);
        };
        OnSongMaybeChanged(); // whatever is already playing is the baseline, never a "next song"
    }

    private void OnSongMaybeChanged()
    {
        var s = Vm.Music.Snapshot;
        if (!s.HasMedia) { _songKey = null; _songSource = null; _nextSongTimer.Stop(); return; } // player closed: start over
        if (string.IsNullOrWhiteSpace(s.Title)) return; // between songs: compare the next real title with the last one
        string key = s.Title.Trim().ToLowerInvariant(); // the title: an artist-only change is the same song, refined
        bool first = _songKey == null;
        bool sameSource = string.Equals(s.SourceApp, _songSource, StringComparison.Ordinal);
        bool changed = !string.Equals(key, _songKey, StringComparison.Ordinal);
        _songKey = key;
        _songSource = s.SourceApp;
        if (first || !sameSource || !changed) return;
        if (Environment.TickCount64 - _songWatchStart < NextSongStartupQuietMs) return; // players settling at start-up
        Restart(_nextSongTimer, NextSongSettleMs);
    }

    private void ShowNextSong()
    {
        if (_state == NotchState.Expanded || !Vm.Settings.Current.ShowNextSongPill) return;
        var s = Vm.Music.Snapshot;
        if (string.IsNullOrWhiteSpace(s.Title)) return;
        string key = s.Title.Trim().ToLowerInvariant();
        if (key != _songKey) return; // it changed again meanwhile and its own timer is running
        Vm.Activity.Show(new Activity(ActivityKind.NextSong, s.Title.Trim(),
            Subtitle: string.IsNullOrWhiteSpace(s.Artist) ? null : s.Artist.Trim(),
            Artwork: s.Artwork,
            OnClick: () => OpenFeature(PageId.Music)));
    }

    /// <summary>Dev (--activity-demo nextsong): a sample "Next song" pill with the current cover, if any.</summary>
    internal void DevNextSong(string title, string artist) =>
        Vm.Activity.Show(new Activity(ActivityKind.NextSong, title, Subtitle: artist, Artwork: Vm.Music.Artwork, OnClick: () => OpenFeature(PageId.Music)));
}
