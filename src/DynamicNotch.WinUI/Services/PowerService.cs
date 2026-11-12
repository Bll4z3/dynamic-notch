using DynamicNotch.Design;
using Windows.System.Power;

namespace DynamicNotch.Services;

/// <summary>Charger plugged in/out and low-battery pills from Windows.System.Power.PowerManager events.</summary>
public sealed class PowerService : IDisposable
{
    private readonly Func<int> _lowThreshold;
    private PowerSupplyStatus _lastSupply;
    private bool _warnedLow;
    private bool _hasBattery;

    public PowerService(Func<int> lowThreshold) => _lowThreshold = lowThreshold;

    public event Action<PillRequest>? Pill;

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
        int pct = PowerManager.RemainingChargePercent;
        Pill?.Invoke(isPlugged
            ? new PillRequest(PillSource.Power, "Charging", $"{pct}%", Icons.Bolt, Badge: $"{pct}%")
            : new PillRequest(PillSource.Power, "On battery", $"{pct}% remaining", Icons.Battery, Badge: $"{pct}%"));
        if (isPlugged) _warnedLow = false;
    });

    private void OnPercentChanged(object? sender, object e) => Ui.Post(() =>
    {
        int pct = PowerManager.RemainingChargePercent;
        bool onBattery = PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent;
        if (onBattery && pct <= _lowThreshold() && !_warnedLow)
        {
            _warnedLow = true;
            Pill?.Invoke(new PillRequest(PillSource.Power, "Low battery", $"{pct}% remaining — plug in soon",
                Icons.BatteryLow, Badge: $"{pct}%"));
        }
        else if (pct > _lowThreshold() + 5) _warnedLow = false;
    });

    public void Dispose()
    {
        if (!_hasBattery) return;
        PowerManager.PowerSupplyStatusChanged -= OnSupplyChanged;
        PowerManager.RemainingChargePercentChanged -= OnPercentChanged;
    }
}
