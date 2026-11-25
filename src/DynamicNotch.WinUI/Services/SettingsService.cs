using System.IO;
using System.Text.Json;

namespace DynamicNotch.Services;

public sealed class AppSettings
{
    public bool MinimalNotch { get; set; }
    public bool StartWithWindows { get; set; }
    public bool ShowAppNotifications { get; set; } = true;
    public bool ShowDevicePills { get; set; } = true;
    public bool ShowNetworkPills { get; set; } = true;
    public bool ShowBatteryPills { get; set; } = true;
    public int LowBatteryPercent { get; set; } = 20;
    /// <summary>Ids of notch pages switched off in Settings ▸ Pages (see PageRegistry).</summary>
    public List<string> DisabledPages { get; set; } = new();
    /// <summary>The page order chosen in Settings ▸ Pages (page ids; empty = the built-in order).</summary>
    public List<string> PageOrder { get; set; } = new();

    // ---- Focus timer (Settings ▸ Focus Timer) ----
    public List<FocusInterval> FocusRoutine { get; set; } = Services.FocusRoutine.Defaults();
    /// <summary>Start the next phase by itself when one ends (otherwise wait for Start).</summary>
    public bool FocusAutoStart { get; set; } = true;
    public bool FocusSound { get; set; } = true;

    // ---- Fullscreen apps ----
    /// <summary>Hide the notch while another app is fullscreen (video, game, presentation).</summary>
    public bool HideWhenFullscreen { get; set; } = true;

    // ---- Appearance (Settings ▸ General) ----
    /// <summary>The chosen UI font (FontService id, e.g. "bundled:Geist"); null = the default (Inter).</summary>
    public string? FontId { get; set; }

    // ---- Activity pills (Settings ▸ Pills) ----
    public bool ShowVolumePill { get; set; } = true;
    public bool ShowBrightnessPill { get; set; } = true;
    public bool ShowCapsLockPill { get; set; } = true;
    public bool ShowDownloadPill { get; set; } = true;
    // (Charging and low battery use ShowBatteryPills.)

    // ---- Notifications + Quick actions page ----
    /// <summary>The notch's own Do Not Disturb: app notification pills are held back (they still go to the history).</summary>
    public bool DoNotDisturb { get; set; }

    // ---- Settings ▸ Customisation (accent, shape, motion, notch look, behaviour, performance) ----
    public CustomSettings Custom { get; set; } = new();

    // ---- Screenshot shelf (Settings ▸ General) ----
    /// <summary>New screenshots (Win+Shift+S, PrtScn) land in the notch's Screenshots page.</summary>
    public bool ScreenshotShelf { get; set; } = true;
    /// <summary>How many screenshots the shelf keeps (older ones drop off; their files stay where they are).</summary>
    public int ScreenshotKeep { get; set; } = 10;
    /// <summary>The notch springs open on a new screenshot (and closes again after a few seconds).</summary>
    public bool ScreenshotAutoOpen { get; set; } = true;
    /// <summary>Where Save puts a copy (null = the Windows Screenshots folder).</summary>
    public string? ScreenshotSaveFolder { get; set; }

    /// <summary>
    /// The notch's layout (Settings ▸ Layout): every feature as a page, a header icon or hidden, in order (see
    /// PageRegistry). It replaced the separate page list (PageOrder / DisabledPages) and header-icon list
    /// (HeaderIconOrder / HiddenHeaderIcons), which are read once to carry old settings over.
    /// </summary>
    public List<LayoutItem> Layout { get; set; } = new();
    public List<string> HeaderIconOrder { get; set; } = new();
    public List<string> HiddenHeaderIcons { get; set; } = new();

    // ---- Lyrics: the panel stays open (or closed) between songs and sessions ----
    public bool LyricsPanelOpen { get; set; }

    // ---- Spotify (Settings ▸ General): your own Spotify app's Client ID, for the Like button ----
    public string? SpotifyClientId { get; set; }

    // ---- Command palette (Settings ▸ Command Palette) ----
    public PaletteSettings Palette { get; set; } = new();

    // ---- Updates (Settings ▸ Updates) ----
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    /// <summary>The newest version already announced with a pill (each new version is announced once).</summary>
    public string? LastAnnouncedVersion { get; set; }
}

/// <summary>Settings and app data live in %LocalAppData%\DynamicNotch (virtualised per-package under MSIX).</summary>
public sealed class SettingsService
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicNotch");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(DataFolder, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public event Action? Changed;

    public void Load()
    {
        bool legacy = false;
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path);
                Current = JsonSerializer.Deserialize<AppSettings>(text) ?? new AppSettings();
                legacy = RemovedKeys.Any(k => text.Contains($"\"{k}\"", StringComparison.Ordinal));
            }
        }
        catch { Current = new AppSettings(); }
        Current.FocusRoutine = FocusRoutine.Sanitize(Current.FocusRoutine); // a hand-edited file can't break the timer
        Current.Custom = (Current.Custom ?? new CustomSettings()).Sanitize();
        Current.ScreenshotKeep = Math.Clamp(Current.ScreenshotKeep, 3, 30);
        Current.Palette ??= new PaletteSettings();
        Current.Palette.Shortcuts ??= new();
        Current.Palette.Disabled ??= new();
        Current.HeaderIconOrder ??= new();
        Current.HiddenHeaderIcons ??= new();
        // A file from an older version: unknown keys are ignored on load; rewrite it once so they're gone for good.
        if (legacy) Save();
    }

    /// <summary>Keys older versions saved for settings that no longer exist (2.11: Sounds, Looks, Distance from the top).</summary>
    private static readonly string[] RemovedKeys =
        { "SoundsEnabled", "SoundPreset", "SoundVolume", "SoundEvents", "SavedLooks", "TopOffset" };

    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Save();
        Changed?.Invoke();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;

    /// <summary>
    /// Applies a change now but writes the file once things go quiet (~400 ms): for values that change
    /// continuously, like a slider being dragged. Never writes from the UI thread more than once per burst.
    /// </summary>
    public void UpdateDeferred(Action<AppSettings> change)
    {
        change(Current);
        Changed?.Invoke();
        if (_saveTimer == null)
        {
            _saveTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _saveTimer.IsRepeating = false;
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); var snapshot = JsonSerializer.Serialize(Current, Json); _ = Task.Run(() => WriteRaw(snapshot)); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void WriteRaw(string json)
    {
        try { Directory.CreateDirectory(DataFolder); File.WriteAllText(_path, json); } catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, Json));
        }
        catch { /* settings are best-effort */ }
    }

    public static T? ReadJson<T>(string fileName)
    {
        try
        {
            var p = Path.Combine(DataFolder, fileName);
            return File.Exists(p) ? JsonSerializer.Deserialize<T>(File.ReadAllText(p)) : default;
        }
        catch { return default; }
    }

    public static void WriteJson<T>(string fileName, T value)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(Path.Combine(DataFolder, fileName), JsonSerializer.Serialize(value, Json));
        }
        catch { }
    }
}
