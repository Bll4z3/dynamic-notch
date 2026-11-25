using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Dispatching;

namespace DynamicNotch.ViewModels;

/// <summary>The kinds of activity pill (see <see cref="ActivityViewModel.Priority"/> for which interrupts which).</summary>
public enum ActivityKind { Update, Download, Volume, Brightness, CapsLock, Charging, LowBattery, NextSong }

/// <summary>
/// One activity pill's content. The view picks its layout from <see cref="Kind"/>:
/// Level is 0..1 (null = none / indeterminate); On is "muted" for volume, "on" for Caps Lock,
/// "plugged in" for charging and "finished" for downloads. NextSong carries the cover in <see cref="Artwork"/>
/// (title = song, subtitle = artist).
/// </summary>
public sealed record Activity(
    ActivityKind Kind,
    string Title,
    double? Level = null,
    string Value = "",
    bool On = false,
    string? Subtitle = null,
    bool Failed = false,
    Action? OnClick = null,
    Action<double>? OnSet = null,
    string? Key = null,
    Windows.Graphics.Imaging.SoftwareBitmap? Artwork = null);

/// <summary>
/// The ONE activity pill host's brain, shared by volume, brightness, Caps Lock, charging, downloads
/// and updates: one pill at a time; a higher-priority activity cross-morphs over a lower one; a lower
/// one waits (up to <see cref="Motion.ActivityStaleMs"/>) and is dropped if it goes stale; repeated
/// updates of the same kind update in place and extend the timer; hovering holds it; clicking
/// dismisses it (running its action). Event-driven: one one-shot timer, nothing polls.
/// </summary>
public sealed class ActivityViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<ActivityKind, (Activity activity, DateTime at)> _pending = new();
    private bool _held;

    public ActivityViewModel(SettingsService settings)
    {
        _settings = settings;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => { _timer.Stop(); Dismiss(); };
    }

    private Activity? _current;
    /// <summary>The pill showing now (null = none).</summary>
    public Activity? Current { get => _current; private set { if (Set(ref _current, value)) OnPropertyChanged(nameof(HasActivity)); } }
    public bool HasActivity => _current != null;

    /// <summary>Raised after <see cref="Current"/> was replaced. True when it's the same kind (update in place, no entrance).</summary>
    public event Action<Activity, bool>? Updated;

    /// <summary>Low battery &gt; charging &gt; Caps Lock &gt; volume = brightness &gt; download &gt; update available.</summary>
    public static int Priority(ActivityKind k) => k switch
    {
        ActivityKind.LowBattery => 6,
        ActivityKind.Charging => 5,
        ActivityKind.CapsLock => 4,
        ActivityKind.Volume or ActivityKind.Brightness => 3,
        ActivityKind.Download or ActivityKind.NextSong => 2,
        _ => 1,
    };

    private bool Allowed(ActivityKind k)
    {
        var s = _settings.Current;
        return k switch
        {
            ActivityKind.Volume => s.ShowVolumePill,
            ActivityKind.Brightness => s.ShowBrightnessPill,
            ActivityKind.CapsLock => s.ShowCapsLockPill,
            ActivityKind.Charging or ActivityKind.LowBattery => s.ShowBatteryPills,
            ActivityKind.Download => s.ShowDownloadPill,
            ActivityKind.NextSong => s.ShowNextSongPill,
            _ => true,
        };
    }

    /// <summary>Same kind (and, for downloads, the same file): updated in place.</summary>
    private static bool SameKind(Activity a, Activity b) => a.Kind == b.Kind && a.Key == b.Key;

    public void Show(Activity a)
    {
        if (!Allowed(a.Kind)) return;
        var cur = Current;
        if (cur != null && SameKind(cur, a))
        {
            Current = a;
            Updated?.Invoke(a, true);
            Restart(a);
            return;
        }
        if (cur == null || Priority(a.Kind) >= Priority(cur.Kind))
        {
            Current = a;
            Updated?.Invoke(a, false);
            Restart(a);
            return;
        }
        _pending[a.Kind] = (a, DateTime.UtcNow); // wait behind the higher-priority pill (latest value wins)
    }

    /// <summary>Removes a waiting or showing activity with this key (e.g. a download that was cancelled).</summary>
    public void Withdraw(string key)
    {
        foreach (var k in _pending.Where(p => p.Value.activity.Key == key).Select(p => p.Key).ToList()) _pending.Remove(k);
        if (Current?.Key == key) Dismiss();
    }

    /// <summary>Pause the auto-dismiss while the pointer is over the pill.</summary>
    public void Hold(bool hold)
    {
        _held = hold;
        if (Current == null) return;
        if (hold) _timer.Stop();
        else Restart(Current);
    }

    /// <summary>The pill was clicked: dismiss it and run its action.</summary>
    public void Click()
    {
        var click = Current?.OnClick;
        Dismiss();
        click?.Invoke();
    }

    public void Dismiss()
    {
        _timer.Stop();
        // The next waiting activity that isn't stale cross-morphs in; the rest are dropped.
        var now = DateTime.UtcNow;
        var next = _pending.Values
            .Where(p => (now - p.at).TotalMilliseconds <= Motion.ActivityStaleMs)
            .OrderByDescending(p => Priority(p.activity.Kind)).ThenByDescending(p => p.at)
            .Select(p => p.activity).FirstOrDefault();
        _pending.Clear();
        if (next != null)
        {
            Current = next;
            Updated?.Invoke(next, false);
            Restart(next);
        }
        else Current = null;
    }

    private void Restart(Activity a)
    {
        _timer.Stop();
        if (_held) return;
        _timer.Interval = TimeSpan.FromMilliseconds(Duration(a));
        _timer.Start();
    }

    private static int Duration(Activity a) => a.Kind switch
    {
        ActivityKind.Volume or ActivityKind.Brightness => Motion.ActivitySliderMs,
        ActivityKind.CapsLock => Motion.ActivityCapsMs,
        ActivityKind.Download => a.On ? Motion.ActivityDownloadDoneMs : Motion.ActivityDownloadIdleMs,
        ActivityKind.Update => Motion.ActivityUpdateMs,
        ActivityKind.NextSong => Motion.ActivityNextSongMs,
        _ => Motion.ActivityBatteryMs,
    };
}
