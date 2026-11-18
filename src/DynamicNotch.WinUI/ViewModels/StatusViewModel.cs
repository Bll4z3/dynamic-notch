using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

/// <summary>Clock + PC battery shown on the collapsed notch and in the expanded header.</summary>
public sealed class StatusViewModel : ObservableObject
{
    public StatusViewModel(ClockService clock, BatteryService battery)
    {
        clock.Tick += Format;
        battery.Changed += s =>
        {
            HasBattery = s.HasBattery;
            BatteryPercent = s.HasBattery ? s.Percent : -1;
            IsCharging = s.IsCharging;
        };
    }

    /// <summary>Show the short date next to the time on the collapsed notch (Settings ▸ Customisation).</summary>
    public static bool ShowDateCollapsed { get; set; }

    private void Format(DateTime t)
    {
        DateText = ClockService.FormatDate(t);
        TimeText = ClockService.FormatTime(t);
        CollapsedText = ShowDateCollapsed ? $"{ClockService.FormatShortDate(t)}  {TimeText}" : TimeText;
    }

    /// <summary>Re-formats now (clock format or date switch changed).</summary>
    public void Refresh() => Format(DateTime.Now);

    private string _date = "";
    public string DateText { get => _date; private set => Set(ref _date, value); }

    private string _time = "";
    public string TimeText { get => _time; private set => Set(ref _time, value); }

    private string _collapsed = "";
    /// <summary>What the collapsed notch's centre shows: the time, or the short date and the time.</summary>
    public string CollapsedText { get => _collapsed; private set => Set(ref _collapsed, value); }

    private bool _hasBattery;
    public bool HasBattery { get => _hasBattery; private set => Set(ref _hasBattery, value); }

    private double _percent = -1;
    public double BatteryPercent { get => _percent; private set => Set(ref _percent, value); }

    private bool _charging;
    public bool IsCharging { get => _charging; private set => Set(ref _charging, value); }

    /// <summary>Dev (`--charge-demo`): fake the charger state on the notch's own battery (nothing is read or changed on the PC).</summary>
    public void DevSetCharging(bool charging) => IsCharging = charging;
}
