using System.Globalization;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicNotch.Services;

/// <summary>
/// Ticks exactly on minute boundaries (the timer is re-aligned to the next :00 every tick, not a
/// fast poll). Also re-ticks when the system clock, time zone or regional format changes, so the
/// 12/24-hour setting is followed live.
/// </summary>
public sealed class ClockService : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal);

    public event Action<DateTime>? Tick;

    public void Start()
    {
        _timer.Tick += (_, _) => Fire();
        SystemEvents.TimeChanged += OnSystemChange;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Fire();
    }

    private void OnSystemChange(object? sender, EventArgs e) => _timer.Dispatcher.InvokeAsync(() =>
    {
        TimeZoneInfo.ClearCachedData();
        Fire();
    });

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.Locale) return;
        _timer.Dispatcher.InvokeAsync(() =>
        {
            CultureInfo.CurrentCulture.ClearCachedData();
            Thread.CurrentThread.CurrentCulture = new CultureInfo(CultureInfo.CurrentCulture.Name, useUserOverride: true);
            Fire();
        });
    }

    private void Fire()
    {
        var now = DateTime.Now;
        Tick?.Invoke(now);
        // Re-align: wake 50ms after the next minute boundary.
        var next = now.AddMinutes(1);
        next = new DateTime(next.Year, next.Month, next.Day, next.Hour, next.Minute, 0, next.Kind);
        _timer.Stop();
        _timer.Interval = next - now + TimeSpan.FromMilliseconds(50);
        _timer.Start();
    }

    /// <summary>"Tue 10 Nov 2026" in the user's language.</summary>
    public static string FormatDate(DateTime t) => t.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>Time following the system short-time pattern (12h "5:56 PM" or 24h "17:56").</summary>
    public static string FormatTime(DateTime t)
    {
        var pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
        return t.ToString(pattern, CultureInfo.CurrentCulture);
    }

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.TimeChanged -= OnSystemChange;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }
}
