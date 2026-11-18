using System.Collections.ObjectModel;
using DynamicNotch.Design;
using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

/// <summary>One entry in the notification history (kept in memory only; never written to disk).</summary>
public sealed class HistoryItem
{
    private static long _ids;
    public long Id { get; } = Interlocked.Increment(ref _ids);
    public required PillSource Source { get; init; }
    public required string Title { get; init; }
    public string Body { get; init; } = "";
    public string? AppName { get; init; }
    public string Glyph { get; init; } = Icons.Bell;
    public Windows.Graphics.Imaging.SoftwareBitmap? Logo { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    public Action? OnClick { get; init; }
}

/// <summary>
/// Page "Notifications + Controls": the notification history (left) and the quick actions (right).
/// History holds every pill the notch has shown or held back (app notifications, devices, Wi-Fi, VPN,
/// battery, updates), newest first, up to <see cref="Theme.HubHistoryMax"/>, for this session only.
/// </summary>
public sealed class HubViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly QuickActionsService _quick;
    private readonly AudioService _audio;
    private readonly BrightnessService _brightness;
    private readonly NotificationService _notifications;

    public HubViewModel(SettingsService settings, QuickActionsService quick, AudioService audio, BrightnessService brightness, NotificationService notifications,
                        WifiService wifi, BluetoothDeviceService bluetoothDevices, DevicesViewModel devices)
    {
        _settings = settings;
        _quick = quick;
        Quick = quick;
        Wifi = wifi;
        BluetoothDevices = bluetoothDevices;
        Devices = devices;
        _audio = audio;
        _brightness = brightness;
        _notifications = notifications;
        quick.Changed += () => RaiseQuick();
        audio.MicMuteChanged += _ => OnPropertyChanged(nameof(MicMuted));
        audio.VolumeChanged += v => { _volume = v; OnPropertyChanged(nameof(Volume)); OnPropertyChanged(nameof(Muted)); };
        brightness.SupportChanged += _ => { OnPropertyChanged(nameof(HasBrightness)); OnPropertyChanged(nameof(Brightness)); };
        brightness.Changed += _ => OnPropertyChanged(nameof(Brightness));
        notifications.AccessChanged += _ => Ui.Post(() => OnPropertyChanged(nameof(EmptyText)));
    }

    /// <summary>Services behind the Wi-Fi and Bluetooth popovers (and the connected devices' batteries).</summary>
    public QuickActionsService Quick { get; }
    public WifiService Wifi { get; }
    public BluetoothDeviceService BluetoothDevices { get; }
    public DevicesViewModel Devices { get; }

    // ---- History -----------------------------------------------------------------------------------
    public ObservableCollection<HistoryItem> History { get; } = new();

    public string EmptyText => _notifications.Access switch
    {
        NotificationAccess.NoPackageIdentity => "App notifications appear here in the installed (MSIX) build. Device, Wi-Fi and battery alerts still show up.",
        NotificationAccess.Denied => "Turn on notification access to see app notifications here.",
        _ => "You're all caught up",
    };

    public void Record(PillRequest p)
    {
        History.Insert(0, new HistoryItem
        {
            Source = p.Source, Title = p.Title, Body = p.Subtitle, AppName = p.AppName,
            Glyph = p.Glyph ?? Icons.Bell, Logo = p.Logo, OnClick = p.OnClick,
        });
        while (History.Count > Theme.HubHistoryMax) History.RemoveAt(History.Count - 1);
    }

    public void Remove(HistoryItem item) => History.Remove(item);

    // ---- Clear all with Undo: the items are only purged once the undo window has passed ----
    private List<HistoryItem>? _cleared;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _undoTimer;

    /// <summary>True while the cleared items can still be brought back.</summary>
    public bool CanUndo => _cleared != null;

    /// <summary>Empties the list but keeps the items for <see cref="Motion.UndoWindowMs"/> so Undo can restore them.</summary>
    public void ClearWithUndo()
    {
        if (History.Count == 0) return;
        PurgeCleared(); // an older undo window ends now
        _cleared = History.ToList();
        History.Clear();
        _undoTimer ??= CreateUndoTimer();
        _undoTimer.Stop();
        _undoTimer.Start();
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Restores the cleared items in their original order (newest first). Returns false when the window has passed.</summary>
    public bool Undo()
    {
        if (_cleared == null) return false;
        _undoTimer?.Stop();
        var items = _cleared;
        _cleared = null;
        // Anything that arrived after the clear stays on top; the restored items go back underneath.
        foreach (var item in items) History.Add(item);
        while (History.Count > Theme.HubHistoryMax) History.RemoveAt(History.Count - 1);
        OnPropertyChanged(nameof(CanUndo));
        return true;
    }

    private void PurgeCleared()
    {
        _undoTimer?.Stop();
        if (_cleared == null) return;
        _cleared = null; // gone for good (history is memory-only, so this is the purge)
        OnPropertyChanged(nameof(CanUndo));
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateUndoTimer()
    {
        var t = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromMilliseconds(Motion.UndoWindowMs);
        t.Tick += (_, _) => PurgeCleared();
        return t;
    }

    // ---- Quick actions -------------------------------------------------------------------------------
    public bool HasWifi => _quick.HasWifi;
    public bool HasBluetooth => _quick.HasBluetooth;
    public bool WifiOn => _quick.WifiOn;
    public bool BluetoothOn => _quick.BluetoothOn;
    public bool EnergySaverOn => _quick.EnergySaverOn;
    public bool HasMicrophone => _audio.HasMicrophone;
    public bool MicMuted => _audio.MicMuted;

    public bool DoNotDisturb
    {
        get => _settings.Current.DoNotDisturb;
        set { if (value == DoNotDisturb) return; _settings.Update(s => s.DoNotDisturb = value); OnPropertyChanged(); }
    }

    public Task ToggleWifiAsync() => _quick.SetWifiAsync(!WifiOn);
    public Task ToggleBluetoothAsync() => _quick.SetBluetoothAsync(!BluetoothOn);
    public void ToggleMic() { _audio.SetMicMuted(!MicMuted); OnPropertyChanged(nameof(MicMuted)); }

    private void RaiseQuick()
    {
        foreach (var n in new[] { nameof(HasWifi), nameof(HasBluetooth), nameof(WifiOn), nameof(BluetoothOn), nameof(EnergySaverOn), nameof(HasMicrophone) })
            OnPropertyChanged(n);
    }

    // ---- Sliders --------------------------------------------------------------------------------------
    private VolumeState? _volume;
    public double Volume => (_volume ?? _audio.Current).Level;
    public bool Muted => (_volume ?? _audio.Current).Muted;
    public bool HasVolume => _audio.IsAvailable;
    public void SetVolume(double v) => _audio.SetVolume(v);
    public void ToggleMute() => _audio.ToggleMute();

    public bool HasBrightness => _brightness.IsSupported;
    public double Brightness => _brightness.Level / 100.0;
    public void SetBrightness(double v) => _brightness.Set((int)Math.Round(v * 100));

    /// <summary>Re-reads live values when the page becomes visible (another app may have changed them).</summary>
    public void Refresh()
    {
        _volume = null;
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(Muted));
        OnPropertyChanged(nameof(MicMuted));
        RaiseQuick();
    }
}
