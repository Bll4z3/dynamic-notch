using System.Diagnostics;
using DynamicNotch.Services;
using DynamicNotch.Services.Lyrics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.ViewModels;

/// <summary>
/// Synced lyrics for whatever is playing: finds them when the track changes (<see cref="LyricsService"/>), keeps a
/// playback clock that is exact between the player's sporadic timeline reports, says which line is current, refines
/// estimated word timings from the sung syllables the audio analysis hears, seeks to a line, and tells the glow when
/// words are being sung.
/// <para>
/// The clock lives in two places that agree: here (Stopwatch-interpolated, for the app's decisions) and on the
/// compositor (<see cref="Clock"/>.T, a linear animation restarted only on play / pause / seek / re-sync), which every
/// word's fill reads through an expression. So the karaoke wipes run at the display's refresh rate with no per-frame
/// work in the app, and freeze exactly where they are on pause. The app's per-frame tick (line changes, refinement)
/// runs only while lyrics are on screen and music plays.
/// </para>
/// </summary>
public sealed class LyricsViewModel : ObservableObject
{
    // ---- Tuning ----
    /// <summary>Lines become current this much before they're sung, so the scroll lands as the first word lights (s).</summary>
    public const double LineLead = 0.16;
    /// <summary>A reported position this far from the local clock is a jump (seek / drift) and re-syncs it (s).</summary>
    private const double ResyncThreshold = 0.09;
    /// <summary>The closed notch shows breaks up to this long as dots; longer ones fall back to the normal closed notch (s).</summary>
    public const double CollapsedDotsMaxGap = 12;
    /// <summary>Waiting for the player to report the duration before looking lyrics up anyway (ms), and a debounce for skips (ms).</summary>
    private const int DurationWaitMs = 1500, LookupDebounceMs = 280;

    private readonly MediaService _media;
    private readonly LyricsService _service;
    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _lookupTimer;
    private CancellationTokenSource? _lookupCts;
    private string _trackKey = "";
    private long _trackSince;
    private MediaSnapshot _snap = MediaSnapshot.Empty;

    public LyricsViewModel(MediaService media, LyricsService service, SettingsService settings)
    {
        _media = media;
        _service = service;
        _settings = settings;
        _isOpen = settings.Current.LyricsPanelOpen;
        _lookupTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _lookupTimer.IsRepeating = false;
        _lookupTimer.Tick += (_, _) => { _lookupTimer.Stop(); _ = LookupAsync(); };
        media.Changed += OnMedia;
        LyricsCache.Cleared += () => Ui.Post(() => { if (Status is LyricsStatus.NotFound or LyricsStatus.Offline) Retry(); });
        ApplySettings(settings.Current.Custom);
    }

    // =================================================================================================================
    // State
    // =================================================================================================================
    private LyricsStatus _status = LyricsStatus.Idle;
    public LyricsStatus Status { get => _status; private set { if (Set(ref _status, value)) Notify(); } }

    private LyricsDocument? _doc;
    public LyricsDocument? Document => _doc;

    public bool HasLyrics => _status == LyricsStatus.Ready && _doc is { Lines.Count: > 0 };
    public bool HasSynced => HasLyrics && _doc!.IsSynced;
    public bool CanSeek => _snap.CanSeek;
    public string SourceApp => _snap.SourceApp;
    public bool IsPlaying => _snap.IsPlaying;

    /// <summary>Track progress 0..1 (plain lyrics scroll with it).</summary>
    public double Progress => _snap.Duration > TimeSpan.Zero ? Math.Clamp(PositionAt(Stopwatch.GetTimestamp()) / _snap.Duration.TotalSeconds, 0, 1) : 0;

    private bool _isOpen;
    /// <summary>The lyrics panel is open on the media player (remembered between sessions).</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!Set(ref _isOpen, value)) return;
            _settings.Update(s => s.LyricsPanelOpen = value);
            UpdateTicking();
        }
    }

    public void TogglePanel() => IsOpen = !IsOpen;

    /// <summary>The current item (line or break) of synced lyrics, -1 before the first.</summary>
    public int ActiveIndex { get; private set; } = -1;

    /// <summary>The document (or its status) changed: views rebuild / show the state.</summary>
    public event Action? DocumentChanged;
    /// <summary>The current line changed (old, new): views restyle and scroll.</summary>
    public event Action<int, int>? ActiveChanged;
    /// <summary>The clock jumped (seek, resume after a long pause): views snap instead of animating a long way.</summary>
    public event Action? ClockJumped;
    /// <summary>Within the current line, the word being sung changed (line, word): the closed notch's long-line glide.</summary>
    public event Action<int, int>? ActiveWordChanged;
    /// <summary>A short message for the panel ("Copied", "This player can't seek").</summary>
    public event Action<string>? Message;
    /// <summary>A display setting changed (text size, word-by-word): views re-layout.</summary>
    public event Action? DisplayChanged;

    private void Notify() => OnPropertyChanged(string.Empty);

    // ---- Settings (Settings ▸ Customisation ▸ Lyrics) ----
    public bool WordByWord { get; private set; } = true;
    public double TextScale { get; private set; } = 1;
    public double OffsetSeconds { get; private set; }
    public bool ShowOnClosedNotch { get; private set; } = true;

    public void ApplySettings(CustomSettings s)
    {
        bool display = s.LyricsWordByWord != WordByWord || Math.Abs(s.LyricsTextSize - TextScale) > 0.001;
        bool offset = Math.Abs(s.LyricsOffsetMs / 1000.0 - OffsetSeconds) > 0.0005;
        WordByWord = s.LyricsWordByWord;
        TextScale = s.LyricsTextSize;
        OffsetSeconds = s.LyricsOffsetMs / 1000.0;
        ShowOnClosedNotch = s.LyricsOnClosedNotch;
        if (offset) { PushClock(); UpdateActive(jump: true); }
        if (display)
        {
            if (_doc != null) foreach (var l in _doc.Lines) if (l.Times != null) WriteTimes(l);
            DisplayChanged?.Invoke();
        }
        OnPropertyChanged(nameof(ShowOnClosedNotch));
    }

    // =================================================================================================================
    // Track changes and lookup
    // =================================================================================================================
    private void OnMedia(MediaSnapshot s)
    {
        var prev = _snap;
        _snap = s;
        string key = s.HasMedia && s.Title.Length > 0 ? $"{s.SourceApp}\u001f{s.Title}\u001f{s.Artist}" : "";
        if (key != _trackKey)
        {
            _trackKey = key;
            _trackSince = Environment.TickCount64;
            _lookupCts?.Cancel();
            _lookupCts = null;
            SetDocument(null, key.Length == 0 ? LyricsStatus.Idle : LyricsStatus.Loading);
            _lookupTimer.Stop();
            if (key.Length > 0)
            {
                _lookupTimer.Interval = TimeSpan.FromMilliseconds(LookupDebounceMs);
                _lookupTimer.Start();
            }
        }
        else if (_status == LyricsStatus.Loading && _lookupCts == null && !_lookupTimer.IsRunning && key.Length > 0)
        {
            // Waiting for the duration: it arrived (or a little more time passed).
            _lookupTimer.Interval = TimeSpan.FromMilliseconds(30);
            _lookupTimer.Start();
        }
        Resync(prev, s);
        if (prev.CanSeek != s.CanSeek || prev.IsPlaying != s.IsPlaying) Notify();
        UpdateTicking();
    }

    /// <summary>Looks the song up again (after the cache was cleared, or once back online).</summary>
    public void Retry()
    {
        if (_trackKey.Length == 0) return;
        SetDocument(null, LyricsStatus.Loading);
        _lookupTimer.Stop();
        _lookupTimer.Interval = TimeSpan.FromMilliseconds(30);
        _lookupTimer.Start();
    }

    private async Task LookupAsync()
    {
        var s = _snap;
        string key = _trackKey;
        if (key.Length == 0) return;
        // Players often report the title a moment before the duration: wait briefly for it (it picks the right version).
        if (s.Duration <= TimeSpan.Zero && Environment.TickCount64 - _trackSince < DurationWaitMs)
        {
            _lookupTimer.Interval = TimeSpan.FromMilliseconds(DurationWaitMs - (Environment.TickCount64 - _trackSince) + 20);
            _lookupTimer.Start();
            return;
        }
        var q = new LyricsQuery(TitleCleaner.CleanTitle(s.Title, s.Artist), TitleCleaner.CleanArtist(s.Artist), (s.Album ?? "").Trim(),
                                s.Duration.TotalSeconds, s.Title, s.Artist);
        var cts = _lookupCts = new CancellationTokenSource();
        try
        {
            var result = await Task.Run(() => _service.LookupAsync(q, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || key != _trackKey) return;
            _lookupCts = null;
            if (result.Raw == null)
            {
                SetDocument(null, result.Unavailable ? LyricsStatus.Offline : LyricsStatus.NotFound);
                MusicReactiveService.Log($"lyrics: none for '{q.Artist} - {q.Title}' ({(result.Unavailable ? "offline" : "not found")})");
                return;
            }
            var doc = await Task.Run(() => LyricsBuilder.Build(result.Raw, s.Duration.TotalSeconds));
            if (cts.IsCancellationRequested || key != _trackKey) return;
            if (doc == null || doc.Lines.Count == 0)
                SetDocument(null, result.Raw.Instrumental || doc != null ? LyricsStatus.Instrumental : LyricsStatus.NotFound);
            else SetDocument(doc, LyricsStatus.Ready);
            MusicReactiveService.Log($"lyrics: '{q.Artist} - {q.Title}' → {doc?.Kind} ({doc?.Lines.Count ?? 0} items, {(result.FromCache ? "cache" : "network")})");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (key == _trackKey) { _lookupCts = null; SetDocument(null, LyricsStatus.NotFound); }
        }
    }

    private void SetDocument(LyricsDocument? doc, LyricsStatus status)
    {
        _doc = doc;
        ActiveIndex = -1;
        _refine.Reset();
        _status = status;
        OnPropertyChanged(nameof(Status));
        Notify();
        DocumentChanged?.Invoke();
        UpdateActive(jump: true);
        UpdateTicking();
    }

    // =================================================================================================================
    // The clock
    // =================================================================================================================
    private double _anchorPos;   // song position (s) at _anchorTicks
    private long _anchorTicks;
    private double _rate = 1;
    private bool _playing;
    private Compositor? _compositor;

    /// <summary>
    /// The compositor clock: "T" = lyrics time (song position minus the sync offset, seconds), running at the playback
    /// rate while playing; "V" / "H" = the singer's live level / held-note amount (0..1) while the analysis runs.
    /// </summary>
    public CompositionPropertySet? Clock { get; private set; }

    /// <summary>Creates the compositor clock (the notch window's compositor, which every lyric view shares).</summary>
    public void Attach(Compositor c)
    {
        if (_compositor != null) return;
        _compositor = c;
        Clock = c.CreatePropertySet();
        Clock.InsertScalar("T", 0);
        Clock.InsertScalar("V", 0);
        Clock.InsertScalar("H", 0);
        PushClock();
    }

    /// <summary>Song position (s) at a Stopwatch time, interpolated from the last report.</summary>
    public double PositionAt(long ticks) =>
        _playing ? _anchorPos + (ticks - _anchorTicks) / (double)Stopwatch.Frequency * _rate : _anchorPos;

    /// <summary>The lyrics' time now (position minus the sync offset).</summary>
    public double Now => PositionAt(Stopwatch.GetTimestamp()) - OffsetSeconds;

    /// <summary>
    /// The player reported a snapshot: re-anchor when playing / paused / rate changed, or when its position disagrees
    /// with the local clock by more than a frame or so (a seek, or drift). Small disagreements are left alone so the
    /// wipes never twitch.
    /// </summary>
    private void Resync(MediaSnapshot prev, MediaSnapshot s)
    {
        long now = Stopwatch.GetTimestamp();
        double reported = s.HasMedia ? s.PositionNow.TotalSeconds : 0;
        double local = PositionAt(now);
        bool stateChanged = s.IsPlaying != _playing || Math.Abs(s.PlaybackRate - _rate) > 0.001 || !ReferenceEquals(prev, s) && prev.Title != s.Title;
        double error = reported - local;
        if (!stateChanged && Math.Abs(error) < ResyncThreshold) return;
        bool jump = Math.Abs(error) > 0.6;
        _anchorPos = reported;
        _anchorTicks = now;
        _playing = s.IsPlaying;
        _rate = s.PlaybackRate > 0 ? s.PlaybackRate : 1;
        PushClock();
        UpdateActive(jump);
        if (jump) ClockJumped?.Invoke();
    }

    /// <summary>Restarts the compositor clock from the local one (frozen while paused).</summary>
    private void PushClock()
    {
        if (Clock == null || _compositor == null) return;
        float t = (float)Now;
        Clock.StopAnimation("T");
        Clock.InsertScalar("T", t);
        if (!_playing) return;
        var a = _compositor.CreateScalarKeyFrameAnimation();
        const float span = 3600;
        a.InsertKeyFrame(0, t);
        a.InsertKeyFrame(1, t + span * (float)_rate, _compositor.CreateLinearEasingFunction());
        a.Duration = TimeSpan.FromSeconds(span);
        Clock.StartAnimation("T", a);
    }

    // =================================================================================================================
    // Per-word compositor values
    // =================================================================================================================
    /// <summary>The line's property set ("s{i}", "e{i}" = word i's fill start / end in lyrics time), created on first use.</summary>
    public CompositionPropertySet? TimesFor(LyricLine line)
    {
        if (_compositor == null) return null;
        if (line.Times == null)
        {
            line.Times = _compositor.CreatePropertySet();
            WriteTimes(line);
        }
        return line.Times;
    }

    /// <summary>Word-by-word: each word's own times. Otherwise the whole line lights at once in a quick left-to-right sweep.</summary>
    private void WriteTimes(LyricLine line)
    {
        var p = line.Times;
        if (p == null) return;
        int n = line.Words.Count;
        for (int i = 0; i < n; i++)
        {
            var w = line.Words[i];
            double s = WordByWord ? w.Start : line.Start + 0.22 * i / Math.Max(1, n);
            double e = WordByWord ? Math.Max(w.End, w.Start + 0.04) : s + 0.14;
            p.InsertScalar($"s{i}", (float)s);
            p.InsertScalar($"e{i}", (float)e);
        }
        p.InsertScalar("ls", (float)line.Start);
        p.InsertScalar("le", (float)line.End);
    }

    // =================================================================================================================
    // Visibility and the per-frame tick
    // =================================================================================================================
    private bool _panelVisible, _collapsedEnabled, _ticking;

    /// <summary>The lyrics panel is on screen (notch open on the media player, lyrics open).</summary>
    public bool PanelVisible { get => _panelVisible; set { if (_panelVisible == value) return; _panelVisible = value; UpdateTicking(); } }

    /// <summary>The closed notch may show the current line (closed, not minimal, playing, setting on).</summary>
    public bool CollapsedEnabled { get => _collapsedEnabled; set { if (_collapsedEnabled == value) return; _collapsedEnabled = value; UpdateTicking(); OnPropertyChanged(nameof(CollapsedItem)); } }

    /// <summary>The audio analysis (vocal level, held notes, syllable onsets), while it runs.</summary>
    public MusicReactiveService? Analyser { get; set; }

    private void UpdateTicking()
    {
        bool want = (_panelVisible || _collapsedEnabled) && HasSynced && _playing;
        if (want == _ticking) return;
        _ticking = want;
        if (want) { _onsetSeen = -1; CompositionTarget.Rendering += OnTick; UpdateActive(jump: false); }
        else
        {
            CompositionTarget.Rendering -= OnTick;
            Clock?.InsertScalar("V", 0);
            Clock?.InsertScalar("H", 0);
        }
    }

    private int _onsetSeen = -1, _activeWord = -1;
    private readonly List<(long Ticks, float Strength)> _onsetBuffer = new();
    private float _vocal, _hold;
    private long _lastTick;

    private void OnTick(object? sender, object e)
    {
        // An exception escaping a frame callback takes the whole app down: log it (at most once a second) instead.
        try { Tick(); }
        catch (Exception ex)
        {
            if (Environment.TickCount64 - _lastTickError > 1000) { _lastTickError = Environment.TickCount64; App.LogError(ex); }
        }
    }

    private long _lastTickError;

    private void Tick()
    {
        long ticks = Stopwatch.GetTimestamp();
        float dt = _lastTick == 0 ? 0.016f : (float)Math.Clamp((ticks - _lastTick) / (double)Stopwatch.Frequency, 0, 0.1);
        _lastTick = ticks;
        UpdateActive(jump: false);
        var doc = _doc;
        if (doc == null) return;
        double t = Now;

        // The singer, live: feeds held-word pulses; onsets refine estimated word timings.
        var a = Analyser;
        var f = a?.Latest;
        float vTarget = f is { Signal: true } ? Math.Clamp(f.Vocal * 1.4f, 0, 1) : 0, hTarget = f?.VocalHold ?? 0;
        _vocal += (vTarget - _vocal) * (1 - MathF.Exp(-dt / 0.08f));
        _hold += (hTarget - _hold) * (1 - MathF.Exp(-dt / 0.12f));
        Clock?.InsertScalar("V", _vocal);
        Clock?.InsertScalar("H", _hold);
        if (a != null)
        {
            _onsetBuffer.Clear();
            _onsetSeen = a.CopyVocalOnsets(_onsetSeen, _onsetBuffer);
            int idx = doc.ItemAt(t);
            if (idx >= 0 && WordByWord && doc.Lines[idx] is { IsInterlude: false } line && line.Words.Count > 0 && line.Words[0].Estimated)
            {
                bool changed = false;
                foreach (var (onTicks, strength) in _onsetBuffer)
                    changed |= _refine.OnOnset(line, PositionAt(onTicks) - OffsetSeconds, strength, t);
                changed |= _refine.OnFrame(line, t, _hold, dt);
                if (changed) WriteTimes(line);
            }
        }

        // The word being sung (the closed notch keeps it in view on long lines).
        int li = ActiveIndex;
        int wi = -1;
        if (li >= 0 && li < doc.Lines.Count)
        {
            var words = doc.Lines[li].Words;
            for (int i = 0; i < words.Count; i++) if (words[i].Start <= t) wi = i; else break;
        }
        if (wi != _activeWord) { _activeWord = wi; ActiveWordChanged?.Invoke(li, wi); }
    }

    private void UpdateActive(bool jump)
    {
        int idx = _doc is { IsSynced: true } d ? d.ItemAt(Now + LineLead) : -1;
        if (idx == ActiveIndex) return;
        int old = ActiveIndex;
        ActiveIndex = idx;
        _activeWord = -1;
        if (_doc != null && idx >= 0) _refine.Begin(_doc.Lines[idx]);
        ActiveChanged?.Invoke(old, idx);
        OnPropertyChanged(nameof(CollapsedItem));
    }

    /// <summary>
    /// What the closed notch shows now: the current line's index, or -1 for nothing (no synced lyrics, before the first
    /// line, a long break, after the last line). A short break shows its dots.
    /// </summary>
    public int CollapsedItem
    {
        get
        {
            if (!_collapsedEnabled || !ShowOnClosedNotch || !HasSynced || ActiveIndex < 0) return -1;
            var l = _doc!.Lines[ActiveIndex];
            if (l.IsInterlude) return l.End - l.Start <= CollapsedDotsMaxGap ? ActiveIndex : -1;
            // The outro: a few seconds after the last line, the normal closed notch comes back.
            if (ActiveIndex == _doc.Lines.Count - 1 && Now > l.End + 4) return -1;
            return ActiveIndex;
        }
    }

    // =================================================================================================================
    // Seeking, copying
    // =================================================================================================================
    /// <summary>Plays from the line's first word (clicking a line). Says so when the player can't seek.</summary>
    public async void SeekTo(LyricLine line)
    {
        if (!_snap.HasMedia || _doc is not { IsSynced: true }) return;
        if (!_snap.CanSeek)
        {
            Message?.Invoke(string.IsNullOrEmpty(_snap.SourceApp) ? "This player doesn't support seeking" : $"{_snap.SourceApp} doesn't support seeking from here");
            return;
        }
        double target = Math.Max(0, line.Start + OffsetSeconds - 0.05);
        bool ok = await _media.SeekAsync(TimeSpan.FromSeconds(target));
        if (!ok) Message?.Invoke($"{(string.IsNullOrEmpty(_snap.SourceApp) ? "The player" : _snap.SourceApp)} didn't accept the seek");
    }

    /// <summary>Copies a line's text (right-click a line).</summary>
    public void Copy(LyricLine line)
    {
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(line.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            Message?.Invoke("Copied");
        }
        catch { Message?.Invoke("Couldn't copy"); }
    }

    // =================================================================================================================
    // The glow's vocal input
    // =================================================================================================================
    /// <summary>
    /// From the lyric timing: how much a word is being sung right now (0..1: a word in progress, softer between the
    /// words of a line, easing off after it), a serial that changes with every new word, and whether it is a held note.
    /// Level is -1 when there are no synced lyrics or nothing plays (the glow then uses the audio alone).
    /// </summary>
    public (float Level, int Word, bool Held) VocalSignal()
    {
        if (!HasSynced || !_playing) return (-1, 0, false);
        double t = Now;
        int i = _doc!.ItemAt(t);
        if (i < 0) return (0, 0, false);
        var line = _doc.Lines[i];
        if (line.IsInterlude || line.Words.Count == 0) return (0, i * 128, false);
        if (t >= line.End) return ((float)Math.Max(0, 1 - (t - line.End) / 0.4) * 0.7f, i * 128 + 127, false);
        for (int k = line.Words.Count - 1; k >= 0; k--)
        {
            var w = line.Words[k];
            if (w.Start > t) continue;
            float level = t < w.End ? (float)Math.Clamp((t - w.Start) / 0.06, 0, 1) : 0.72f;
            if (w.Backing) level *= 0.6f;
            return (level, i * 128 + Math.Min(k, 126), w.Held && t < w.End);
        }
        return (0, i * 128, false);
    }

    // =================================================================================================================
    // Word timing refinement (estimated timings only)
    // =================================================================================================================
    private readonly WordTimingRefiner _refine = new();
}

/// <summary>
/// Moves estimated word timings toward what the singer actually does, while the line plays. A syllable onset just
/// before the next word's estimated start makes that word light now (the singer is ahead); while a note is held
/// (steady pitch and level, no new syllable) the next word waits, up to a limit. The rest of the line is re-spread
/// over what's left, so the line still ends where it should. Only future words move: a lit word never un-lights.
/// </summary>
internal sealed class WordTimingRefiner
{
    // ---- Tuning ----
    private const double MaxPullEarly = 0.35;  // how much earlier than estimated a word may start (s)
    private const double MaxHoldExtra = 0.32;  // how much longer a held word may run (s)
    private const double MinWord = 0.08;       // the shortest a word may get when the rest of the line compresses (s)

    private LyricLine? _line;
    private readonly HashSet<int> _confirmed = new();
    private double _held, _lastOnset = -10;

    public void Reset() { _line = null; _confirmed.Clear(); _held = 0; }

    public void Begin(LyricLine line)
    {
        if (ReferenceEquals(line, _line)) return;
        _line = line;
        _confirmed.Clear();
        _held = 0;
    }

    private static int WordAt(LyricLine line, double t)
    {
        int k = -1;
        for (int i = 0; i < line.Words.Count; i++) if (line.Words[i].Start <= t) k = i; else break;
        return k;
    }

    /// <summary>An onset heard at lyrics time <paramref name="at"/> (now is <paramref name="now"/>). True when timings changed.</summary>
    public bool OnOnset(LyricLine line, double at, float strength, double now)
    {
        Begin(line);
        _lastOnset = at;
        if (strength < 0.35f || at < line.Start - 0.4 || at > line.End) return false;
        int k = WordAt(line, now);
        var words = line.Words;
        // Before the first word: the line's singing began.
        if (k < 0)
        {
            if (!_confirmed.Contains(0) && words[0].Start - at <= MaxPullEarly) { Respread(line, 0, at); _confirmed.Add(0); return true; }
            return false;
        }
        // The current word's own onset (it started about when estimated).
        if (at - words[k].Start < 0.12) { _confirmed.Add(k); return false; }
        // The next word: sung earlier than estimated.
        int j = k + 1;
        if (j < words.Count && !_confirmed.Contains(j))
        {
            double early = words[j].Start - at;
            double allow = Math.Max(MaxPullEarly, 0.5 * words[k].Duration);
            if (early > 0 && early <= allow && at - words[k].Start >= 0.12)
            {
                Respread(line, j, Math.Max(at, now - 0.02));
                _confirmed.Add(j);
                return true;
            }
        }
        return false;
    }

    /// <summary>Every frame: while a note is held, the next word waits for the singer (up to a limit).</summary>
    public bool OnFrame(LyricLine line, double now, float hold, float dt)
    {
        Begin(line);
        int k = WordAt(line, now);
        var words = line.Words;
        int j = k + 1;
        if (k < 0 || j >= words.Count || _confirmed.Contains(j)) return false;
        bool aboutToSwitch = now >= words[j].Start - 0.03;
        bool singerHolding = hold > 0.55f && now - _lastOnset > 0.15;
        if (!aboutToSwitch || !singerHolding || _held >= MaxHoldExtra) return false;
        double remaining = line.End - now;
        if (remaining < (words.Count - j) * MinWord * 1.5) return false;
        double push = Math.Min(dt, MaxHoldExtra - _held);
        _held += push;
        Respread(line, j, words[j].Start + push);
        return true;
    }

    /// <summary>Word <paramref name="from"/> starts at <paramref name="start"/>; it and the words after it fill the rest of the line in proportion.</summary>
    private static void Respread(LyricLine line, int from, double start)
    {
        var words = line.Words;
        double end = line.End, oldStart = words[from].Start;
        start = Math.Min(start, end - (words.Count - from) * MinWord);
        double oldSpan = Math.Max(0.001, end - oldStart), newSpan = Math.Max(0.001, end - start);
        double scale = newSpan / oldSpan;
        for (int i = from; i < words.Count; i++)
        {
            double s = start + (words[i].Start - oldStart) * scale, e = start + (words[i].End - oldStart) * scale;
            words[i].Start = s;
            words[i].End = Math.Max(s + MinWord, e);
        }
        if (from > 0) words[from - 1].End = Math.Max(words[from - 1].Start + MinWord, start); // the previous word runs right up to it
    }
}
