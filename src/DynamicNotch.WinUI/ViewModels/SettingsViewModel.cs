using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using DynamicNotch.Design;
using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

/// <summary>One entry in the Settings tab list. Add more tabs by adding entries with their own view model.</summary>
public sealed record SettingsTab(string Title, string Icon, object Content);

public sealed class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(SecretStore secrets, ClaudeService claude, PageRegistry pages, SettingsService settings, FocusTimerViewModel timer,
                             UpdateService updates, FontService fonts, ThemeService theme)
    {
        ApiKey = new ApiKeyTabViewModel(secrets, claude);
        Layout = new Views.LayoutTabViewModel(pages, settings);
        Focus = new FocusTabViewModel(settings, timer);
        General = new GeneralTabViewModel(settings, fonts, pages);
        Pills = new PillsTabViewModel(settings);
        Updates = new UpdatesTabViewModel(settings, updates);
        Customisation = new CustomisationTabViewModel(settings, theme, fonts);
        PaletteTab = new Views.PaletteTabViewModel(settings);
        Tabs = new ObservableCollection<SettingsTab>
        {
            new("General", Icons.Settings, General),
            new("Customisation", Icons.Palette, Customisation),
            // (The Claude API key is a section of General: ApiKey is shown there.)
            new("Layout", Icons.Dashboard, Layout),
            new("Command Palette", Icons.Search, PaletteTab),
            new("Pills", Icons.Bell, Pills),
            new("Focus Timer", Icons.Timer, Focus),
            new("Updates", Icons.UpdateArrow, Updates),
        };
        _selected = Tabs[0];
    }

    public ObservableCollection<SettingsTab> Tabs { get; }
    public ApiKeyTabViewModel ApiKey { get; }
    public Views.LayoutTabViewModel Layout { get; }
    public FocusTabViewModel Focus { get; }
    public GeneralTabViewModel General { get; }
    public PillsTabViewModel Pills { get; }
    public UpdatesTabViewModel Updates { get; }
    public CustomisationTabViewModel Customisation { get; }
    public Views.PaletteTabViewModel PaletteTab { get; }

    /// <summary>Selects the Updates tab (tray "Check for updates…", update pill).</summary>
    public void SelectUpdates() => SelectedTab = Tabs.First(t => ReferenceEquals(t.Content, Updates));
    /// <summary>Ask needs a key: General, with its Claude API key box focused (see <see cref="ApiKeyFocusPending"/>).</summary>
    public void SelectApiKey()
    {
        ApiKeyFocusPending = true;
        SelectedTab = Tabs.First(t => ReferenceEquals(t.Content, General));
    }

    /// <summary>Set by <see cref="SelectApiKey"/>; the window focuses the key box once General is showing, then clears it.</summary>
    public bool ApiKeyFocusPending { get; set; }

    private SettingsTab _selected;
    public SettingsTab SelectedTab
    {
        get => _selected;
        set { if (value != null && Set(ref _selected, value)) OnPropertyChanged(nameof(SelectedIndex)); }
    }
    public int SelectedIndex => Tabs.IndexOf(SelectedTab);
}

public enum KeyTestResult { None, Testing, Works, Rejected }

/// <summary>"Claude API key" tab: save / remove / paste / test. Never shows the full key once saved.</summary>
public sealed class ApiKeyTabViewModel : ObservableObject
{
    private readonly SecretStore _secrets;
    private readonly ClaudeService _claude;

    public ApiKeyTabViewModel(SecretStore secrets, ClaudeService claude)
    {
        _secrets = secrets;
        _claude = claude;
        var remove = new RelayCommand(Remove, () => _secrets.HasKey);
        RemoveCommand = remove;
        OpenKeyPageCommand = new RelayCommand(OpenKeyPage);
        _secrets.Changed += () => { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(HasKey)); remove.RaiseCanExecuteChanged(); };
    }

    public ICommand RemoveCommand { get; }
    public ICommand OpenKeyPageCommand { get; }

    public bool HasKey => _secrets.HasKey;
    public string StatusText => _secrets.Masked() is { } masked ? $"Key saved ({masked})" : "No key saved";

    private KeyTestResult _test;
    public KeyTestResult TestResult
    {
        get => _test;
        private set { if (Set(ref _test, value)) { OnPropertyChanged(nameof(TestText)); OnPropertyChanged(nameof(IsTesting)); } }
    }
    public bool IsTesting => TestResult == KeyTestResult.Testing;

    private string _testDetail = "";
    public string TestText => TestResult switch
    {
        KeyTestResult.Testing => "Testing…",
        KeyTestResult.Works => "Key works",
        KeyTestResult.Rejected => _testDetail,
        _ => "",
    };

    /// <summary>Saves the typed key (encrypted). Returns false when the box is empty.</summary>
    public bool Save(string typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return false;
        _secrets.Set(typed);
        TestResult = KeyTestResult.None;
        return true;
    }

    private void Remove()
    {
        _secrets.Remove();
        TestResult = KeyTestResult.None;
    }

    /// <summary>Tests the typed key if there is one, otherwise the saved key (max_tokens 1).</summary>
    public async Task TestAsync(string typed)
    {
        string? key = string.IsNullOrWhiteSpace(typed) ? _secrets.Get() : typed;
        if (string.IsNullOrWhiteSpace(key))
        {
            _testDetail = "Paste a key first";
            TestResult = KeyTestResult.Rejected;
            OnPropertyChanged(nameof(TestText));
            return;
        }
        TestResult = KeyTestResult.Testing;
        var error = await _claude.TestKeyAsync(key);
        _testDetail = error == null ? "" : error.Kind == ClaudeErrorKind.InvalidKey ? "Key rejected" : error.Message;
        TestResult = error == null ? KeyTestResult.Works : KeyTestResult.Rejected;
        OnPropertyChanged(nameof(TestText));
    }

    private static void OpenKeyPage()
    {
        try { Process.Start(new ProcessStartInfo(AppConfig.ApiKeyPageUrl) { UseShellExecute = true }); }
        catch { /* no default browser */ }
    }
}

/// <summary>
/// "Focus Timer" tab: the routine as an editable list (type, minutes, reorder, delete, add), plus the
/// auto-start and sound switches. Every valid edit is saved straight to settings.json; the timer picks
/// it up live (right away when idle, at the next phase while a round is running).
/// </summary>
public sealed class FocusTabViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly FocusTimerViewModel _timer;
    private readonly DispatcherQueueTimer _messageTimer;

    public FocusTabViewModel(SettingsService settings, FocusTimerViewModel timer)
    {
        _settings = settings;
        _timer = timer;
        Items = FocusRoutine.Sanitize(settings.Current.FocusRoutine);
        _messageTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _messageTimer.IsRepeating = false;
        _messageTimer.Interval = TimeSpan.FromSeconds(2.5);
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); Message = ""; };
        timer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or nameof(FocusTimerViewModel.RoutinePending)) OnPropertyChanged(nameof(PendingNote));
        };
    }

    /// <summary>The routine being edited (a copy; saved on every change).</summary>
    public List<FocusInterval> Items { get; private set; }

    /// <summary>Raised after the list changed (argument: index of a newly added row, or -1).</summary>
    public event Action<int>? ItemsChanged;

    public string TotalText
    {
        get
        {
            int n = FocusRoutine.FocusCount(Items);
            return $"Total: {FocusRoutine.Duration(FocusRoutine.Total(Items))} · {n} focus {(n == 1 ? "session" : "sessions")}";
        }
    }

    public string PendingNote => _timer.RoutinePending ? "The timer is running: changes apply at the next phase." : "";

    private string _message = "";
    public string Message { get => _message; private set => Set(ref _message, value); }

    public void ShowMessage(string text)
    {
        Message = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    public bool AutoStart
    {
        get => _settings.Current.FocusAutoStart;
        set { if (value != AutoStart) { _settings.Update(s => s.FocusAutoStart = value); OnPropertyChanged(); } }
    }

    public bool PlaySound
    {
        get => _settings.Current.FocusSound;
        set
        {
            if (value == PlaySound) return;
            _settings.Update(s => s.FocusSound = value);
            OnPropertyChanged();
            if (value) Chime.Play(); // a preview of what it sounds like
        }
    }

    public bool CanAdd => Items.Count < FocusRoutine.MaxIntervals;

    public void Add(IntervalKind kind)
    {
        if (!CanAdd) { ShowMessage($"Up to {FocusRoutine.MaxIntervals} intervals"); return; }
        Items.Add(new FocusInterval(kind, kind switch { IntervalKind.Focus => 25, IntervalKind.ShortBreak => 5, _ => 15 }));
        Save(Items.Count - 1);
    }

    /// <summary>Returns false (and changes nothing) when it's the last interval.</summary>
    public bool Remove(int index)
    {
        if (Items.Count <= 1) { ShowMessage("Keep at least one interval"); return false; }
        if (index < 0 || index >= Items.Count) return false;
        Items.RemoveAt(index);
        Save();
        return true;
    }

    public void Move(int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= Items.Count || to < 0 || to >= Items.Count) return;
        (Items[index], Items[to]) = (Items[to], Items[index]);
        Save();
    }

    public void CycleKind(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        Items[index].Kind = Items[index].Kind switch
        {
            IntervalKind.Focus => IntervalKind.ShortBreak,
            IntervalKind.ShortBreak => IntervalKind.LongBreak,
            _ => IntervalKind.Focus,
        };
        Save();
    }

    /// <summary>Validated minutes: null / empty / out of range is refused (returns the value kept).</summary>
    public int SetMinutes(int index, int? minutes)
    {
        if (index < 0 || index >= Items.Count) return 0;
        var item = Items[index];
        if (minutes is not { } m || m < FocusRoutine.MinMinutes || m > FocusRoutine.MaxMinutes)
        {
            ShowMessage($"Use {FocusRoutine.MinMinutes}–{FocusRoutine.MaxMinutes} minutes");
            if (minutes is { } bad && bad > 0) { item.Minutes = Math.Clamp(bad, FocusRoutine.MinMinutes, FocusRoutine.MaxMinutes); Save(); }
            return item.Minutes;
        }
        if (item.Minutes == m) return m;
        item.Minutes = m;
        Save();
        return m;
    }

    public void ResetDefaults()
    {
        Items = FocusRoutine.Defaults();
        Save();
    }

    private void Save(int added = -1)
    {
        Items = FocusRoutine.Sanitize(Items);
        var copy = Items.Select(i => i.Clone()).ToList();
        _settings.Update(s => s.FocusRoutine = copy);
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(PendingNote));
        ItemsChanged?.Invoke(added);
    }
}

/// <summary>"General" tab: app-wide behaviour switches.</summary>
public sealed class GeneralTabViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    private readonly PageRegistry _pages;

    public GeneralTabViewModel(SettingsService settings, FontService fonts, PageRegistry pages)
    {
        _settings = settings;
        _pages = pages;
        Fonts = fonts;
        fonts.FontChanged += _ => OnPropertyChanged(nameof(CurrentFont));
        pages.Changed += () => OnPropertyChanged(nameof(ScreenshotShelf));
    }

    // ---- Spotify (the media player's Like button) ----
    /// <summary>Set by the app after start-up.</summary>
    public SpotifyService? Spotify { get; set; }

    public string SpotifyClientId
    {
        get => _settings.Current.SpotifyClientId ?? "";
        set { var v = value.Trim(); if (v != SpotifyClientId) _settings.Update(s => s.SpotifyClientId = v.Length == 0 ? null : v); }
    }

    /// <summary>What the notch's playlist panel lists: "recent", "selected" or "both".</summary>
    public string SpotifyPlaylistSource
    {
        get => _settings.Current.SpotifyPlaylistSource;
        set { if (value != SpotifyPlaylistSource && value is "recent" or "selected" or "both") _settings.Update(s => s.SpotifyPlaylistSource = value); }
    }

    /// <summary>The playlists picked for the notch's playlist panel, in the order they were picked.</summary>
    public IReadOnlyList<SpotifyPinnedPlaylist> PinnedPlaylists => _settings.Current.SpotifyPinnedPlaylists;

    public void SetPinned(SpotifyPlaylist p, bool pinned)
    {
        bool has = PinnedPlaylists.Any(x => x.Id == p.Id);
        if (pinned == has) return;
        _settings.Update(s =>
        {
            if (pinned) s.SpotifyPinnedPlaylists.Add(new SpotifyPinnedPlaylist { Id = p.Id, Uri = p.Uri, Name = p.Name, ImageUrl = p.ImageUrl });
            else s.SpotifyPinnedPlaylists.RemoveAll(x => x.Id == p.Id);
        });
    }

    /// <summary>Refreshes the stored names / covers of picked playlists from a fresh list of yours.</summary>
    public void UpdatePinnedDetails(IReadOnlyList<SpotifyPlaylist> mine)
    {
        bool changed = false;
        foreach (var pin in PinnedPlaylists)
            if (mine.FirstOrDefault(m => m.Id == pin.Id) is { } m && (m.Name != pin.Name || m.ImageUrl != pin.ImageUrl || m.Uri != pin.Uri)) { changed = true; break; }
        if (!changed) return;
        _settings.Update(s =>
        {
            foreach (var pin in s.SpotifyPinnedPlaylists)
                if (mine.FirstOrDefault(m => m.Id == pin.Id) is { } m) { pin.Name = m.Name; pin.ImageUrl = m.ImageUrl; pin.Uri = m.Uri; }
        });
    }

    // ---- Screenshot shelf ----
    /// <summary>On = new screenshots land in the notch (and the Screenshots page is shown); off = neither.</summary>
    public bool ScreenshotShelf
    {
        get => _settings.Current.ScreenshotShelf && _pages.IsShown(PageId.Screenshots);
        set
        {
            if (value == ScreenshotShelf) return;
            _settings.Update(s => s.ScreenshotShelf = value);
            if (!_pages.TrySetEnabled(PageId.Screenshots, value)) _settings.Update(s => s.ScreenshotShelf = true); // it's the only page left
            OnPropertyChanged();
        }
    }

    public bool ScreenshotAutoOpen
    {
        get => _settings.Current.ScreenshotAutoOpen;
        set { if (value != ScreenshotAutoOpen) { _settings.Update(s => s.ScreenshotAutoOpen = value); OnPropertyChanged(); } }
    }

    public int ScreenshotKeep
    {
        get => _settings.Current.ScreenshotKeep;
        set { if (value != ScreenshotKeep) { _settings.Update(s => s.ScreenshotKeep = Math.Clamp(value, 3, 30)); OnPropertyChanged(); } }
    }

    /// <summary>Where Save puts copies (empty = the Windows Screenshots folder).</summary>
    public string ScreenshotSaveFolder
    {
        get => string.IsNullOrWhiteSpace(_settings.Current.ScreenshotSaveFolder) ? ScreenshotService.ScreenshotsFolder : _settings.Current.ScreenshotSaveFolder!;
        set { _settings.Update(s => s.ScreenshotSaveFolder = string.Equals(value, ScreenshotService.ScreenshotsFolder, StringComparison.OrdinalIgnoreCase) ? null : value); OnPropertyChanged(); }
    }

    public FontService Fonts { get; }
    public FontChoice CurrentFont => Fonts.Current;
    public void ApplyFont(FontChoice f) => Fonts.Apply(f);
    public void ResetFont() => Fonts.ResetToDefault();

    public bool HideWhenFullscreen
    {
        get => _settings.Current.HideWhenFullscreen;
        set { if (value != HideWhenFullscreen) { _settings.Update(s => s.HideWhenFullscreen = value); OnPropertyChanged(); } }
    }

    /// <summary>Minimal notch (also in the tray menu). The app wires <see cref="MinimalChanged"/> to the notch.</summary>
    public bool MinimalNotch
    {
        get => _settings.Current.MinimalNotch;
        set { if (value == MinimalNotch) return; MinimalChanged?.Invoke(value); OnPropertyChanged(); }
    }
    public event Action<bool>? MinimalChanged;

    /// <summary>Start with Windows (also in the tray menu). The app does the registration and calls <see cref="RefreshStartup"/>.</summary>
    public bool StartWithWindows
    {
        get => _settings.Current.StartWithWindows;
        set { if (value == StartWithWindows) return; StartupChanged?.Invoke(value); }
    }
    public event Action<bool>? StartupChanged;

    /// <summary>Re-reads the toggles changed elsewhere (tray menu).</summary>
    public void RefreshToggles()
    {
        OnPropertyChanged(nameof(MinimalNotch));
        OnPropertyChanged(nameof(StartWithWindows));
    }
}

/// <summary>"Pills" tab: which transient pills the notch shows (each kind can be switched off).</summary>
public sealed class PillsTabViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    public PillsTabViewModel(SettingsService settings) => _settings = settings;

    private bool Get(Func<AppSettings, bool> read) => read(_settings.Current);
    private void Put(bool value, Func<AppSettings, bool> read, Action<AppSettings, bool> write, string name)
    {
        if (value == read(_settings.Current)) return;
        _settings.Update(s => write(s, value));
        OnPropertyChanged(name);
    }

    public bool Volume { get => Get(s => s.ShowVolumePill); set => Put(value, s => s.ShowVolumePill, (s, v) => s.ShowVolumePill = v, nameof(Volume)); }
    public bool Brightness { get => Get(s => s.ShowBrightnessPill); set => Put(value, s => s.ShowBrightnessPill, (s, v) => s.ShowBrightnessPill = v, nameof(Brightness)); }
    public bool CapsLock { get => Get(s => s.ShowCapsLockPill); set => Put(value, s => s.ShowCapsLockPill, (s, v) => s.ShowCapsLockPill = v, nameof(CapsLock)); }
    public bool Battery { get => Get(s => s.ShowBatteryPills); set => Put(value, s => s.ShowBatteryPills, (s, v) => s.ShowBatteryPills = v, nameof(Battery)); }
    public bool Downloads { get => Get(s => s.ShowDownloadPill); set => Put(value, s => s.ShowDownloadPill, (s, v) => s.ShowDownloadPill = v, nameof(Downloads)); }
    public bool NextSong { get => Get(s => s.ShowNextSongPill); set => Put(value, s => s.ShowNextSongPill, (s, v) => s.ShowNextSongPill = v, nameof(NextSong)); }
    public bool AppNotifications { get => Get(s => s.ShowAppNotifications); set => Put(value, s => s.ShowAppNotifications, (s, v) => s.ShowAppNotifications = v, nameof(AppNotifications)); }
    public bool Devices { get => Get(s => s.ShowDevicePills); set => Put(value, s => s.ShowDevicePills, (s, v) => s.ShowDevicePills = v, nameof(Devices)); }
    public bool Network { get => Get(s => s.ShowNetworkPills); set => Put(value, s => s.ShowNetworkPills, (s, v) => s.ShowNetworkPills = v, nameof(Network)); }
}

/// <summary>"Updates" tab: version, automatic checks, Check now, Download / Restart to update.</summary>
public sealed class UpdatesTabViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly UpdateService _updates;

    public UpdatesTabViewModel(SettingsService settings, UpdateService updates)
    {
        _settings = settings;
        _updates = updates;
        updates.Changed += () =>
        {
            foreach (var n in new[] { nameof(StatusText), nameof(CanGet), nameof(ActionText), nameof(IsBusy), nameof(Progress), nameof(LastCheckedText) })
                OnPropertyChanged(n);
        };
    }

    public string VersionText => $"Dynamic Notch {UpdateService.CurrentVersionText}";

    public bool AutoCheck
    {
        get => _settings.Current.AutoCheckUpdates;
        set { if (value == AutoCheck) return; _settings.Update(s => s.AutoCheckUpdates = value); OnPropertyChanged(); }
    }

    public string StatusText => string.IsNullOrEmpty(_updates.Message)
        ? _updates.Available is { } a ? $"Version {a.Version.ToString(3)} is available"
          : _updates.LastChecked != null ? $"No newer version found (you have {UpdateService.CurrentVersionText})" : "Not checked yet"
        : _updates.Message;

    public string LastCheckedText => _updates.LastChecked is { } t ? $"Last checked {t.LocalDateTime:g}" : "";
    public bool IsBusy => _updates.Status is UpdateStatus.Checking or UpdateStatus.Downloading;
    public double Progress => _updates.Progress;
    public bool CanGet => _updates.Available != null && _updates.Status != UpdateStatus.Downloading;

    public string ActionText => _updates.Status switch
    {
        UpdateStatus.Ready => "Restart to update",
        UpdateStatus.Downloading => "Downloading…",
        _ => UpdateService.CanInstallInPlace ? "Download and install" : "Open download page",
    };

    public Task CheckAsync() => _updates.CheckAsync(manual: true);
    public Task GetAsync() => _updates.GetUpdateAsync();
    public void OpenPage() => _updates.OpenReleasePage();
}
