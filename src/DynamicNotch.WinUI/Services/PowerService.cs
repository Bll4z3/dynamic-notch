using Windows.Devices.Power;
using Windows.System.Power;

namespace DynamicNotch.Services;

/// <summary>Charger plugged in / out and low battery, from Windows.System.Power.PowerManager events.</summary>
public sealed class PowerService : IDisposable
{
    private readonly Func<int> _lowThreshold;
    private PowerSupplyStatus _lastSupply;
    private bool _warnedLow;
    private bool _hasBattery;

    public PowerService(Func<int> lowThreshold) => _lowThreshold = lowThreshold;

    /// <summary>The charger was connected (true) or removed (false). Args: plugged, percent, time estimate text (may be null).</summary>
    public event Action<bool, int, string?>? PlugChanged;
    /// <summary>The battery dropped to the low threshold while on battery.</summary>
    public event Action<int>? LowBattery;

    public void Start()
    {
        try
        {
            _hasBattery = PowerManager.BatteryStatus != BatteryStatus.NotPresent;
            if (!_hasBattery) return; // desktops: nothing to report
            _lastSupply = PowerManager.PowerSupplyStatus;
            _warnedLow = PowerManager.RemainingChargePercent <= _lowThreshold();
            PowerManager.PowerSupplyStatusChanged += OnSupplyChanged;
            PowerManager.RemainingChargePercentChanged += OnPercentChanged;
        }
        catch { _hasBattery = false; }
    }

    private void OnSupplyChanged(object? sender, object e) => Ui.Post(() =>
    {
        var supply = PowerManager.PowerSupplyStatus;
        bool wasPlugged = _lastSupply != PowerSupplyStatus.NotPresent;
        bool isPlugged = supply != PowerSupplyStatus.NotPresent;
        _lastSupply = supply;
        if (wasPlugged == isPlugged) return;
        PlugChanged?.Invoke(isPlugged, PowerManager.RemainingChargePercent, Estimate(isPlugged));
        if (isPlugged) _warnedLow = false;
    });

    private void OnPercentChanged(object? sender, object e) => Ui.Post(() =>
    {
        int pct = PowerManager.RemainingChargePercent;
        bool onBattery = PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent;
        if (onBattery && pct <= _lowThreshold() && !_warnedLow)
        {
            _warnedLow = true;
            LowBattery?.Invoke(pct);
        }
        else if (pct > _lowThreshold() + 5) _warnedLow = false;
    });

    /// <summary>"1 h 20 min until full" while charging, "3 h 5 min left" on battery, or null when Windows can't tell yet.</summary>
    public static string? Estimate(bool charging)
    {
        try
        {
            if (!charging)
            {
                var left = PowerManager.RemainingDischargeTime;
                return left > TimeSpan.Zero && left < TimeSpan.FromDays(2) ? $"{Duration(left)} left" : null;
            }
            var r = Battery.AggregateBattery.GetReport();
            if (r.ChargeRateInMilliwatts is int rate && rate > 0 && r.FullChargeCapacityInMilliwattHours is int full &&
                r.RemainingCapacityInMilliwattHours is int remaining && full > remaining)
                return $"{Duration(TimeSpan.FromHours((double)(full - remaining) / rate))} until full";
        }
        catch { }
        return null;
    }

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";

    public void Dispose()
    {
        if (!_hasBattery) return;
        PowerManager.PowerSupplyStatusChanged -= OnSupplyChanged;
        PowerManager.RemainingChargePercentChanged -= OnPercentChanged;
    }
}
