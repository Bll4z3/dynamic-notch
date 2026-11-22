using Microsoft.UI.Dispatching;

namespace DynamicNotch.ViewModels;

/// <summary>
/// The palette's quick timer ("timer 5m") and stopwatch ("stopwatch"). While one runs, the collapsed notch
/// shows it in the same spot as the Focus timer (which wins if both run). A countdown that finishes raises
/// <see cref="Finished"/> (the window pops a pill). Ticks once a second, only while running.
/// </summary>
public sealed class QuickTimerViewModel : ObservableObject
{
    private readonly DispatcherQueueTimer _tick;
    private DateTimeOffset _start, _end;

    public QuickTimerViewModel()
    {
        _tick = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => OnTick();
    }

    public bool IsCountdown { get; private set; }
    public bool IsStopwatch { get; private set; }
    public bool IsActive => IsCountdown || IsStopwatch;
    public TimeSpan Duration { get; private set; }

    public TimeSpan Remaining => IsCountdown ? Max0(_end - DateTimeOffset.Now) : TimeSpan.Zero;
    public TimeSpan Elapsed => IsStopwatch ? DateTimeOffset.Now - _start : TimeSpan.Zero;

    /// <summary>What the collapsed notch shows ("4:59", "1:02:10").</summary>
    public string Display => Digits(IsCountdown ? Remaining + TimeSpan.FromMilliseconds(999) : Elapsed);

    public event Action<TimeSpan>? Finished;

    public void StartCountdown(TimeSpan d)
    {
        IsStopwatch = false;
        IsCountdown = true;
        Duration = d;
        _end = DateTimeOffset.Now + d;
        _tick.Start();
        Raise();
    }

    public void StartStopwatch()
    {
        IsCountdown = false;
        IsStopwatch = true;
        _start = DateTimeOffset.Now;
        _tick.Start();
        Raise();
    }

    public void Stop()
    {
        IsCountdown = IsStopwatch = false;
        _tick.Stop();
        Raise();
    }

    private void OnTick()
    {
        if (IsCountdown && Remaining <= TimeSpan.Zero)
        {
            var d = Duration;
            Stop();
            Finished?.Invoke(d);
            return;
        }
        OnPropertyChanged(nameof(Display));
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Display));
    }

    private static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    public static string Digits(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>Reads "5m", "90s", "1h 30m", "1:30", "10" (minutes) as a duration.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0) return null;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mins)) return mins > 0 ? TimeSpan.FromMinutes(mins) : null;
        if (TimeSpan.TryParseExact(text, new[] { @"m\:ss", @"mm\:ss", @"h\:mm\:ss" }, null, out var ts)) return ts > TimeSpan.Zero ? ts : null;
        var total = TimeSpan.Zero;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(\d+(?:\.\d+)?)\s*(h|hr|hrs|hour|hours|m|min|mins|minute|minutes|s|sec|secs|second|seconds)\b"))
        {
            double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            total += m.Groups[2].Value[0] switch { 'h' => TimeSpan.FromHours(v), 'm' => TimeSpan.FromMinutes(v), _ => TimeSpan.FromSeconds(v) };
        }
        return total > TimeSpan.Zero ? total : null;
    }
}
