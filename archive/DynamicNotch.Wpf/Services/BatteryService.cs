using System.Windows;
using Microsoft.Win32;
using Windows.Devices.Power;
using Windows.System.Power;

namespace DynamicNotch.Services;

public readonly record struct BatteryState(bool HasBattery, double Percent, bool IsCharging, bool IsPluggedIn);

/// <summary>
/// The PC's own battery, for the notch ring. Event-driven: Battery.AggregateBattery.ReportUpdated
/// plus SystemEvents.PowerModeChanged as a backup trigger. Never polls.
/// </summary>
public sealed class BatteryService : IDisposable
{
    private Battery? _battery;

    public BatteryState State { get; private set; }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<BatteryState>? Changed;

    public void Start()
    {
        try
        {
            _battery = Battery.AggregateBattery;
            _battery.ReportUpdated += OnReportUpdated;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Battery API unavailable: {ex.Message}"); }
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Refresh();
    }

    private void OnReportUpdated(Battery sender, object args) => Application.Current?.Dispatcher.InvokeAsync(Refresh);

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange) Application.Current?.Dispatcher.InvokeAsync(Refresh);
    }

    private void Refresh()
    {
        var next = Read();
        if (next == State) return;
        State = next;
        Changed?.Invoke(next);
    }

    private BatteryState Read()
    {
        try
        {
            if (_battery == null) return default;
            var report = _battery.GetReport();
            if (report.Status == BatteryStatus.NotPresent) return default;

            double percent;
            if (report.FullChargeCapacityInMilliwattHours is int full && full > 0 &&
                report.RemainingCapacityInMilliwattHours is int remaining)
                percent = Math.Clamp(100.0 * remaining / full, 0, 100);
            else
                percent = PowerManager.RemainingChargePercent;

            bool plugged = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
            return new BatteryState(true, Math.Round(percent), report.Status == BatteryStatus.Charging, plugged);
        }
        catch { return default; }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_battery != null) _battery.ReportUpdated -= OnReportUpdated;
    }
}
