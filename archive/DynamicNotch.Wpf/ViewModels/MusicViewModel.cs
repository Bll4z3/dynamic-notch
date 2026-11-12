using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

public sealed class MusicViewModel : ObservableObject
{
    private readonly MediaService _media;
    private readonly DispatcherTimer _clock;
    private MediaSnapshot _snap = MediaSnapshot.Empty;
    private bool _isVisible;
    private bool _isScrubbing;

    public MusicViewModel(MediaService media)
    {
        _media = media;
        _media.Changed += OnChanged;
        // The clock only ticks while the player page is on screen AND music is playing.
        _clock = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) => UpdateTime();

        PlayPauseCommand = new RelayCommand(() => _media.PlayPause());
        NextCommand = new RelayCommand(() => _media.Next());
        PreviousCommand = new RelayCommand(() => _media.Previous());
        CycleSessionCommand = new RelayCommand(() => _media.CycleSession());
    }

    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand CycleSessionCommand { get; }

    public string Title => string.IsNullOrEmpty(_snap.Title) ? (HasMedia ? "Unknown title" : "Nothing playing") : _snap.Title;
    public string Artist => string.IsNullOrEmpty(_snap.Artist) ? (HasMedia ? _snap.SourceApp : "Play something in Spotify, Apple Music or a browser") : _snap.Artist;
    public BitmapSource? Artwork => _snap.Artwork;
    public bool HasArtwork => _snap.Artwork != null;
    public bool HasMedia => _snap.HasMedia;
    public bool IsPlaying => _snap.IsPlaying;
    public bool CanSeek => _snap.CanSeek;
    public bool CanNext => _snap.CanNext;
    public bool CanPrevious => _snap.CanPrevious;
    public string SourceApp => _snap.SourceApp;
    public bool HasMultipleSessions => _snap.SessionCount > 1;
    public string SessionHint => HasMultipleSessions ? $"{_snap.SourceApp} · click art to switch ({_snap.SessionCount})" : _snap.SourceApp;

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private string _elapsed = "0:00";
    public string ElapsedText { get => _elapsed; private set => Set(ref _elapsed, value); }

    private string _remaining = "-0:00";
    public string RemainingText { get => _remaining; private set => Set(ref _remaining, value); }

    /// <summary>Set by the view: true while the expanded player is visible.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set { if (Set(ref _isVisible, value)) UpdateClock(); }
    }

    public event Action? TrackChanged;

    public void BeginScrub() => _isScrubbing = true;

    public void PreviewScrub(double fraction)
    {
        var d = _snap.Duration;
        var p = TimeSpan.FromTicks((long)(d.Ticks * Math.Clamp(fraction, 0, 1)));
        Progress = Math.Clamp(fraction, 0, 1);
        ElapsedText = Format(p);
        RemainingText = "-" + Format(d - p);
    }

    public void CommitScrub(double fraction)
    {
        _isScrubbing = false;
        if (!CanSeek) { UpdateTime(); return; }
        var target = TimeSpan.FromTicks((long)(_snap.Duration.Ticks * Math.Clamp(fraction, 0, 1)));
        _media.Seek(target);
    }

    private void OnChanged(MediaSnapshot s)
    {
        bool trackChanged = s.Title != _snap.Title || s.Artist != _snap.Artist || !ReferenceEquals(s.Artwork, _snap.Artwork);
        _snap = s;
        OnPropertyChanged(string.Empty); // refresh all bindings
        UpdateTime();
        UpdateClock();
        if (trackChanged) TrackChanged?.Invoke();
    }

    private void UpdateClock()
    {
        if (_isVisible && _snap.IsPlaying && _snap.Duration > TimeSpan.Zero) _clock.Start();
        else _clock.Stop();
    }

    private void UpdateTime()
    {
        if (_isScrubbing) return;
        var d = _snap.Duration;
        var p = _snap.PositionNow;
        Progress = d > TimeSpan.Zero ? p.TotalSeconds / d.TotalSeconds : 0;
        ElapsedText = Format(p);
        RemainingText = "-" + Format(d > p ? d - p : TimeSpan.Zero);
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
