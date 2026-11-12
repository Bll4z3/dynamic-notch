using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

/// <summary>Connected Bluetooth devices for the bottom-right row of the expanded notch.</summary>
public sealed class DevicesViewModel : ObservableObject
{
    public DevicesViewModel(BluetoothService bluetooth)
    {
        bluetooth.DeviceConnected += OnConnected;
        bluetooth.DeviceDisconnected += OnDisconnected;
        bluetooth.BatteryChanged += (id, pct) =>
        {
            var item = Items.FirstOrDefault(i => i.Id == id && !i.IsLeaving);
            if (item != null) item.Battery = pct;
        };
    }

    public ObservableCollection<DeviceItemViewModel> Items { get; } = new();

    /// <summary>Used by `--demo` to preview the row without real hardware.</summary>
    internal void AddPreview(ConnectedDevice d) => OnConnected(d);
    internal void RemovePreview(string id) => OnDisconnected(id);

    private void OnConnected(ConnectedDevice d)
    {
        var existing = Items.FirstOrDefault(i => i.Id == d.Id);
        if (existing != null && !existing.IsLeaving) { existing.Battery = d.Battery; return; }
        if (existing != null) Items.Remove(existing);
        Items.Add(new DeviceItemViewModel(d.Id, d.Name, d.Glyph) { Battery = d.Battery });
    }

    private void OnDisconnected(string id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id && !i.IsLeaving);
        if (item == null) return;
        item.IsLeaving = true; // view springs it out, then it's removed
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        t.Tick += (_, _) => { t.Stop(); Items.Remove(item); };
        t.Start();
    }
}

public sealed class DeviceItemViewModel : ObservableObject
{
    public DeviceItemViewModel(string id, string name, string glyph)
    {
        Id = id;
        Name = name;
        Glyph = glyph;
    }

    public string Id { get; }
    public string Name { get; }
    public string Glyph { get; }

    private int? _battery;
    public int? Battery
    {
        get => _battery;
        set
        {
            if (Set(ref _battery, value))
            {
                OnPropertyChanged(nameof(RingPercent));
                OnPropertyChanged(nameof(Tooltip));
            }
        }
    }

    /// <summary>-1 tells the BatteryRing to draw no ring (device doesn't report battery).</summary>
    public double RingPercent => _battery ?? -1;

    public string Tooltip => _battery is int b ? $"{Name} · {b}%" : $"{Name} · battery not reported";

    private bool _isLeaving;
    public bool IsLeaving { get => _isLeaving; set => Set(ref _isLeaving, value); }
}
