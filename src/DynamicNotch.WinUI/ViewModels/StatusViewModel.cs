using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

/// <summary>Clock + PC battery shown on the collapsed notch and in the expanded header.</summary>
public sealed class StatusViewModel : ObservableObject
{
    public StatusViewModel(ClockService clock, BatteryService battery)
    {
        clock.Tick += t =>
        {
            DateText = ClockService.FormatDate(t);
            TimeText = ClockService.FormatTime(t);
        };
        battery.Changed += s =>
        {
            HasBattery = s.HasBattery;
            BatteryPercent = s.HasBattery ? s.Percent : -1;
            IsCharging = s.IsCharging;
        };
    }

    private string _date = "";
    public string DateText { get => _date; private set => Set(ref _date, value); }

    private string _time = "";
    public string TimeText { get => _time; private set => Set(ref _time, value); }

    private bool _hasBattery;
    public bool HasBattery { get => _hasBattery; private set => Set(ref _hasBattery, value); }

    private double _percent = -1;
    public double BatteryPercent { get => _percent; private set => Set(ref _percent, value); }

    private bool _charging;
    public bool IsCharging { get => _charging; private set => Set(ref _charging, value); }
}
