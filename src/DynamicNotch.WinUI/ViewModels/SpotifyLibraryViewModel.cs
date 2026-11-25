using System.Collections.ObjectModel;
using DynamicNotch.Services;
using Microsoft.UI.Dispatching;

namespace DynamicNotch.ViewModels;

/// <summary>Where the media player's Spotify panel is: closed, the playlist panel, or one playlist's songs.</summary>
public enum SpotifyPanelView { Closed, Panel, Playlist, Queue }

/// <summary>One group of playlists in the panel (a header only when both groups show).</summary>
public sealed record PlaylistSection(string? Header, IReadOnlyList<SpotifyPlaylist> Items);

/// <summary>
/// The media player's Spotify panel (Views/SpotifyPanel.cs, Views/PlaylistView.cs): shown while Spotify is the
/// player. The arrow under the controls opens the panel (the notch grows downwards): a search bar, then your recently
/// played playlists and / or the ones picked in Settings. A playlist opens its songs (paged); a song plays from there
/// with the playlist carrying on after it. Typing searches songs (debounced, at most 10 results); a result plays, or
/// "+" queues it. Every Spotify call goes through <see cref="SpotifyService"/>; problems become a short notice
/// ("Open Spotify to play", offline, rate limited…), never an exception.
/// </summary>
public sealed class SpotifyLibraryViewModel : ObservableObject
{
    private readonly MusicViewModel _music;
    private readonly SpotifyService _spotify;
    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _debounce;
    private CancellationTokenSource? _searchCts, _playlistCts;
    private int _searchVersion, _sectionsVersion;
    private DateTimeOffset _sectionsLoaded;
    private List<SpotifyPlaylist>? _recent, _mine;
    private const int SearchDebounceMs = 300;
    /// <summary>With fewer recent playlists than this, your own playlists fill the panel up.</summary>
    private const int FillUpTo = 6;

    public SpotifyLibraryViewModel(MusicViewModel music, SpotifyService spotify, SettingsService settings)
    {
        _music = music;
        _spotify = spotify;
        _settings = settings;
        _debounce = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _debounce.IsRepeating = false;
        _debounce.Interval = TimeSpan.FromMilliseconds(SearchDebounceMs);
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SearchAsync(); };
        bool wasSpotify = IsSpotifySource;
        music.PropertyChanged += (_, _) =>
        {
            bool now = IsSpotifySource;
            if (now == wasSpotify) return;
            wasSpotify = now;
            OnPropertyChanged(nameof(IsSpotifySource));
            OnPropertyChanged(nameof(CanShowQueue));
            if (!now) Close(); // something else is playing: the panel goes with the arrow
        };
        music.TrackChanged += OnTrackChanged;
        spotify.QueueUpdated += q => SetQueue(q);
        spotify.Changed += () => { OnPropertyChanged(nameof(CanShowQueue)); if (!spotify.HasLibraryScopes) SetQueue(null); };
        spotify.Changed += () => { _recent = null; _mine = null; _sectionsLoaded = default; if (IsOpen) _ = LoadSectionsAsync(force: true); };
        settings.Changed += () =>
        {
            string key = PinnedKey();
            if (key == _pinnedKey) return;
            _pinnedKey = key;
            if (IsOpen) BuildSections();
        };
        _pinnedKey = PinnedKey();
    }

    /// <summary>Spotify is the media player's source (the arrow shows).</summary>
    public bool IsSpotifySource => _music.HasMedia && _music.SourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase);

    private SpotifyPanelView _view;
    public SpotifyPanelView View { get => _view; private set { if (Set(ref _view, value)) OnPropertyChanged(nameof(IsOpen)); } }
    /// <summary>The notch is tall for the panel (the playlist panel or a playlist's songs).</summary>
    public bool IsOpen => _view != SpotifyPanelView.Closed;

    /// <summary>A short message for the panel's notice pill (true = a problem). Raised on the UI thread.</summary>
    public event Action<string, bool>? Notice;
    /// <summary>"Open Settings" in the panel (connect / reconnect / pick playlists).</summary>
    public event Action? SettingsRequested;
    public void RequestSettings() => SettingsRequested?.Invoke();

    public void TogglePanel()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (!IsSpotifySource) return;
        View = SpotifyPanelView.Panel;
        _ = LoadSectionsAsync();
    }

    public void Close()
    {
        _playlistCts?.Cancel();
        ClearSearch();
        View = SpotifyPanelView.Closed;
    }

    // =========================================================================================================
    // The queue
    // =========================================================================================================
    private SpotifyPanelView _queueReturn;

    /// <summary>The queue button shows: Spotify plays and the account allows playback reading.</summary>
    public bool CanShowQueue => IsSpotifySource && _spotify.HasLibraryScopes;

    private SpotifyQueue? _queue;
    /// <summary>The queue on screen (null until loaded). Each change is raised with <see cref="QueueChanged"/> for the view to animate.</summary>
    public SpotifyQueue? Queue => _queue;
    public event Action? QueueChanged;

    private bool _queueLoading;
    public bool QueueLoading { get => _queueLoading; private set => Set(ref _queueLoading, value); }

    private string? _queueMessage;
    /// <summary>A problem loading the queue (offline, no device…), shown inline in the queue view.</summary>
    public string? QueueMessage { get => _queueMessage; private set => Set(ref _queueMessage, value); }

    /// <summary>Opens the queue view (from the player, or from the panel: Back returns there).</summary>
    public void OpenQueue()
    {
        if (!CanShowQueue) return;
        if (View != SpotifyPanelView.Queue) _queueReturn = View == SpotifyPanelView.Playlist ? SpotifyPanelView.Panel : View;
        ClearSearch();
        View = SpotifyPanelView.Queue;
        // Fresh unless a fetch just happened (the Next prediction fetches the queue a moment after every song).
        if (_spotify.LastQueue is { } last && _queue == null) SetQueue(last);
        if (DateTimeOffset.Now - _spotify.LastQueueAt > TimeSpan.FromSeconds(4)) _ = RefreshQueueAsync();
    }

    /// <summary>Dev (--spotify-panel queue-demo): shows songs from a read-only search as the queue, then moves it up every few seconds.</summary>
    internal async void DevQueueDemo()
    {
        var r = await _spotify.SearchTracksAsync("nines");
        if (!r.Ok || r.Value!.Count < 3) return;
        var all = r.Value;
        SetQueue(new SpotifyQueue(all[0], all.Skip(1).Take(6).ToList()));
        OpenQueue();
        for (int i = 1; i < 4; i++)
        {
            await Task.Delay(3500);
            SetQueue(new SpotifyQueue(all[i], all.Skip(i + 1).Take(6).ToList())); // the next song moves up; one joins at the end
        }
    }

    public void CloseQueue()
    {
        if (View == SpotifyPanelView.Queue) View = _queueReturn;
    }

    public async Task RefreshQueueAsync()
    {
        QueueLoading = _queue == null;
        SpotifyResult<SpotifyQueue> r;
        try { r = await _spotify.GetQueueAsync(); }
        catch (Exception ex) { App.LogError(ex); r = SpotifyResult<SpotifyQueue>.Fail(SpotifyError.Failed); }
        QueueLoading = false;
        if (r.Ok) return; // the queue itself arrives through QueueUpdated
        if (_queue is { Items.Count: > 0 }) Notice?.Invoke(_spotify.Describe(r.Error), true); // keep the songs on screen
        else QueueMessage = _spotify.Describe(r.Error);
    }

    private void SetQueue(SpotifyQueue? q)
    {
        _queue = q;
        if (q != null) QueueMessage = null;
        OnPropertyChanged(nameof(Queue));
        QueueChanged?.Invoke();
    }

    /// <summary>
    /// A new song: if it's the first one queued, the queue moves up at once (the fetch a moment later confirms it), so
    /// the open queue view animates the change instead of waiting for Spotify.
    /// </summary>
    private void OnTrackChanged()
    {
        if (_queue is not { } q || q.Items.Count == 0) return;
        var title = _music.Snapshot.Title.Trim();
        if (title.Length == 0 || q.Current != null && string.Equals(q.Current.Name.Trim(), title, StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(q.Items[0].Name.Trim(), title, StringComparison.OrdinalIgnoreCase))
            SetQueue(new SpotifyQueue(q.Items[0], q.Items.Skip(1).ToList()));
    }

    // =========================================================================================================
    // The playlist panel
    // =========================================================================================================
    private IReadOnlyList<PlaylistSection> _sections = Array.Empty<PlaylistSection>();
    public IReadOnlyList<PlaylistSection> Sections { get => _sections; private set => Set(ref _sections, value); }

    private bool _loading;
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }

    private string? _panelMessage;
    /// <summary>Shown instead of the playlists (not connected, offline, nothing to show…).</summary>
    public string? PanelMessage { get => _panelMessage; private set => Set(ref _panelMessage, value); }

    private bool _panelNeedsSettings;
    /// <summary>The message comes with an "Open Settings" button.</summary>
    public bool PanelNeedsSettings { get => _panelNeedsSettings; private set => Set(ref _panelNeedsSettings, value); }

    private string _pinnedKey;
    private string Source => _settings.Current.SpotifyPlaylistSource;
    private string PinnedKey() => Source + "|" + string.Join(",", _settings.Current.SpotifyPinnedPlaylists.Select(p => p.Id));

    /// <summary>Loads the recently played playlists (if the panel lists them) and rebuilds the sections.</summary>
    public async Task LoadSectionsAsync(bool force = false)
    {
        if (!_spotify.IsConnected) { ShowPanelMessage("Connect your Spotify account to see your playlists here.", settings: true); return; }
        if (!_spotify.HasLibraryScopes) { ShowPanelMessage("Reconnect Spotify in Settings to allow playlists and playback.", settings: true); return; }
        BuildSections(); // pinned ones (and the last recent list) show at once
        if (Source == "selected") return;
        if (!force && _recent != null && DateTimeOffset.Now - _sectionsLoaded < TimeSpan.FromMinutes(2)) return;
        int v = ++_sectionsVersion;
        IsLoading = _recent == null;
        SpotifyResult<List<SpotifyPlaylist>> r;
        try { r = await _spotify.RecentPlaylistsAsync(); }
        catch (Exception ex) { App.LogError(ex); r = SpotifyResult<List<SpotifyPlaylist>>.Fail(SpotifyError.Failed); } // never stuck on "Loading…"
        if (v != _sectionsVersion) return;
        IsLoading = false;
        if (r.Ok)
        {
            _recent = r.Value;
            // Spotify often reports no playlist for recent songs (empty contexts): your own playlists fill the panel up.
            if (_recent!.Count < FillUpTo && await _spotify.MyPlaylistsAsync() is { Ok: true } mine && v == _sectionsVersion) _mine = mine.Value;
            if (v != _sectionsVersion) return;
            _sectionsLoaded = DateTimeOffset.Now;
            BuildSections();
        }
        else if (_recent == null)
        {
            if (_settings.Current.SpotifyPinnedPlaylists.Count > 0 && Source == "both") { BuildSections(); Notice?.Invoke(_spotify.Describe(r.Error), true); }
            else ShowPanelMessage(_spotify.Describe(r.Error), settings: r.Error is SpotifyError.NotConnected or SpotifyError.MissingScopes or SpotifyError.NotRegistered);
        }
    }

    private void BuildSections()
    {
        var pinned = _settings.Current.SpotifyPinnedPlaylists
            .Select(p => new SpotifyPlaylist(p.Id, string.IsNullOrEmpty(p.Uri) ? $"spotify:playlist:{p.Id}" : p.Uri, p.Name, p.ImageUrl, null)).ToList();
        var recent = _recent ?? new List<SpotifyPlaylist>();
        // Liked Songs picked: Spotify's own "Liked Songs" context (when it shows up as a recent playlist) would repeat it.
        if (pinned.Any(p => p.Id == SpotifyService.LikedSongsId)) recent = recent.Where(r => r.Name != "Liked Songs").ToList();
        string recentHeader = "Recently played";
        // Few or no recent playlists: the rest of the panel shows your own (not already shown).
        List<SpotifyPlaylist> fill = recent.Count < FillUpTo && _recent != null && _mine is { Count: > 0 } mine
            ? mine.Where(m => recent.All(r => r.Id != m.Id) && pinned.All(p => p.Id != m.Id)).Take(12 - recent.Count).ToList()
            : new List<SpotifyPlaylist>();
        if (recent.Count == 0 && fill.Count > 0) { recent = fill; fill = new List<SpotifyPlaylist>(); recentHeader = "Your playlists"; }
        var sections = new List<PlaylistSection>();
        switch (Source)
        {
            case "selected":
                if (pinned.Count > 0) sections.Add(new PlaylistSection(null, pinned));
                break;
            case "both":
                if (pinned.Count > 0) sections.Add(new PlaylistSection("Pinned", pinned));
                var rest = recent.Where(r => pinned.All(p => p.Id != r.Id)).ToList();
                if (rest.Count > 0) sections.Add(new PlaylistSection(pinned.Count > 0 || recentHeader != "Recently played" || fill.Count > 0 ? recentHeader : null, rest));
                if (fill.Count > 0) sections.Add(new PlaylistSection("Your playlists", fill));
                break;
            default:
                if (recent.Count > 0) sections.Add(new PlaylistSection(recentHeader == "Recently played" && fill.Count == 0 ? null : recentHeader, recent));
                if (fill.Count > 0) sections.Add(new PlaylistSection("Your playlists", fill));
                break;
        }
        Sections = sections;
        if (sections.Count > 0) { PanelMessage = null; PanelNeedsSettings = false; return; }
        if (Source == "selected") ShowPanelMessage("Pick playlists for the notch in Settings ▸ General ▸ Notch playlists.", settings: true);
        else if (_recent != null) ShowPanelMessage(Source == "both" ? "No playlists yet. Pick some in Settings, or play one in Spotify." : "No recently played playlists yet. Play one in Spotify and it shows up here.", settings: Source == "both");
        else { PanelMessage = null; PanelNeedsSettings = false; } // still loading
    }

    private void ShowPanelMessage(string text, bool settings)
    {
        Sections = Array.Empty<PlaylistSection>();
        IsLoading = false;
        PanelMessage = text;
        PanelNeedsSettings = settings;
    }

    // =========================================================================================================
    // A playlist's songs
    // =========================================================================================================
    private SpotifyPlaylist? _playlist;
    public SpotifyPlaylist? Playlist { get => _playlist; private set => Set(ref _playlist, value); }

    /// <summary>The songs loaded so far (more load as the list scrolls).</summary>
    public ObservableCollection<SpotifyTrack> Tracks { get; } = new();

    private bool _tracksLoading;
    public bool TracksLoading { get => _tracksLoading; private set => Set(ref _tracksLoading, value); }

    private bool _forbidden;
    /// <summary>Not your playlist (403): only "Play playlist" is offered.</summary>
    public bool PlaylistForbidden { get => _forbidden; private set => Set(ref _forbidden, value); }

    private string? _playlistMessage;
    public string? PlaylistMessage { get => _playlistMessage; private set => Set(ref _playlistMessage, value); }

    private int _trackTotal;
    /// <summary>How many songs Spotify says the playlist has (0 until the first page is in).</summary>
    public int TrackTotal { get => _trackTotal; private set => Set(ref _trackTotal, value); }

    private int? _nextOffset;
    private bool _loadingMore;

    public void OpenPlaylist(SpotifyPlaylist p)
    {
        ClearSearch();
        _playlistCts?.Cancel();
        _playlistCts = new CancellationTokenSource();
        Playlist = p;
        Tracks.Clear();
        PlaylistForbidden = false;
        PlaylistMessage = null;
        TrackTotal = 0;
        _nextOffset = 0;
        View = SpotifyPanelView.Playlist;
        _ = LoadMoreAsync();
    }

    public void BackToPanel()
    {
        _playlistCts?.Cancel();
        if (View == SpotifyPanelView.Playlist) View = SpotifyPanelView.Panel;
        else if (View == SpotifyPanelView.Queue) CloseQueue();
    }

    /// <summary>The next page of songs (the list asks as you scroll near its end).</summary>
    public async Task LoadMoreAsync()
    {
        if (_loadingMore || _nextOffset is not { } offset || Playlist is not { } p || _playlistCts is not { } cts) return;
        _loadingMore = true;
        TracksLoading = Tracks.Count == 0;
        try
        {
            var r = await _spotify.PlaylistTracksAsync(p.Id, offset, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(p, Playlist)) return;
            if (r.Ok)
            {
                foreach (var t in r.Value!.Tracks) Tracks.Add(t);
                _nextOffset = r.Value.NextOffset;
                TrackTotal = r.Value.Total;
                if (Tracks.Count == 0 && _nextOffset == null) PlaylistMessage = "This playlist has no songs that can be played here.";
            }
            else if (r.Error == SpotifyError.Forbidden || r.Error == SpotifyError.NotFound)
            {
                _nextOffset = null;
                if (Tracks.Count == 0) PlaylistForbidden = true; // someone else's playlist (Development Mode)
            }
            else
            {
                if (Tracks.Count == 0) PlaylistMessage = _spotify.Describe(r.Error);
                else Notice?.Invoke(_spotify.Describe(r.Error), true);
            }
        }
        catch (OperationCanceledException) { }
        finally { _loadingMore = false; TracksLoading = false; }
    }

    /// <summary>Plays the song inside the playlist (offset = its URI), so the playlist carries on after it.</summary>
    public async Task PlayInPlaylistAsync(SpotifyTrack t)
    {
        if (Playlist is not { } p) return;
        Report(await _spotify.PlayInContextAsync(p.Uri, t.Uri), $"Playing {t.Name}");
    }

    /// <summary>Plays the playlist from the start (the only option for someone else's playlist).</summary>
    public async Task PlayPlaylistAsync(SpotifyPlaylist? p = null)
    {
        p ??= Playlist;
        if (p == null) return;
        Report(await _spotify.PlayInContextAsync(p.Uri, null), $"Playing {p.Name}");
    }

    // =========================================================================================================
    // Search
    // =========================================================================================================
    private string _query = "";
    /// <summary>The search box's text. Songs are searched ~300 ms after the last key press.</summary>
    public string Query
    {
        get => _query;
        set
        {
            value ??= "";
            if (value == _query) return;
            bool wasSearching = IsSearching;
            _query = value;
            OnPropertyChanged();
            if (IsSearching != wasSearching) OnPropertyChanged(nameof(IsSearching));
            _debounce.Stop();
            _searchCts?.Cancel();
            if (!IsSearching) { _searchVersion++; Results.Clear(); SearchLoading = false; SearchMessage = null; return; }
            SearchLoading = true;
            _debounce.Start();
        }
    }

    /// <summary>There's text in the box: results replace the playlists.</summary>
    public bool IsSearching => _query.Trim().Length > 0;

    public ObservableCollection<SpotifyTrack> Results { get; } = new();

    private bool _searchLoading;
    public bool SearchLoading { get => _searchLoading; private set => Set(ref _searchLoading, value); }

    private string? _searchMessage;
    public string? SearchMessage { get => _searchMessage; private set => Set(ref _searchMessage, value); }

    public void ClearSearch() => Query = "";

    private async Task SearchAsync()
    {
        string q = _query.Trim();
        if (q.Length == 0) return;
        int v = ++_searchVersion;
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            var r = await _spotify.SearchTracksAsync(q, cts.Token);
            if (v != _searchVersion) return;
            SearchLoading = false;
            Results.Clear();
            if (r.Ok)
            {
                foreach (var t in r.Value!) Results.Add(t);
                SearchMessage = Results.Count == 0 ? $"No songs found for “{q}”" : null;
            }
            else SearchMessage = _spotify.Describe(r.Error);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Plays a search result on its own.</summary>
    public async Task PlayTrackAsync(SpotifyTrack t) => Report(await _spotify.PlayTrackAsync(t.Uri), $"Playing {t.Name}");

    /// <summary>Enter in the search box: plays the top result (once results are in).</summary>
    public async Task PlayTopResultAsync()
    {
        if (_debounce.IsRunning) { _debounce.Stop(); await SearchAsync(); }
        if (Results.FirstOrDefault() is { } top) await PlayTrackAsync(top);
    }

    /// <summary>"+": adds the song to the end of the queue.</summary>
    public async Task QueueAsync(SpotifyTrack t) => Report(await _spotify.QueueAsync(t.Uri), $"Added to queue: {t.Name}", always: true);

    private void Report(SpotifyError e, string success, bool always = false)
    {
        if (e != SpotifyError.None) Notice?.Invoke(_spotify.Describe(e), true);
        else if (always) Notice?.Invoke(success, false);
    }
}
