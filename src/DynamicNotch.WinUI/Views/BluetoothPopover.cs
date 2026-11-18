using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DynamicNotch.Views;

/// <summary>
/// The Bluetooth popup (page 4 ▸ Quick actions ▸ Bluetooth). Header switch turns the radio on / off.
/// "My devices": paired devices with their type icon, "Connected / Paired" and a battery ring when the
/// device reports one; a click connects or disconnects Classic audio devices in place (other kinds open
/// Bluetooth settings, shown with a chevron). "Nearby": unpaired devices found while the popup is open,
/// each with a Pair pill. Actions show a spinner, then a check or a shake.
/// Opening does no device work on the UI thread: the rows are the service's warm cache (kept between
/// opens, primed when page 4 is first shown), diffed in place; the watchers start on a worker thread and
/// their events arrive in batches. A row is only refilled when its device actually changed. A first-ever
/// open with nothing cached shows skeleton rows.
/// </summary>
public sealed class BluetoothPopover
{
    private readonly Popover _pop;
    private readonly BluetoothDeviceService _bt;
    private readonly QuickActionsService _quick;
    private readonly DevicesViewModel _devices;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly StackPanel _paired = new() { Spacing = 2 }, _nearby = new() { Spacing = 2 };
    private readonly TextBlock _pairedHeader, _nearbyHeader;
    private readonly EmptyState _empty = new();
    private readonly SkeletonRows _skeleton = new();
    private readonly Dictionary<string, PopRow> _rows = new();
    /// <summary>What each row currently shows (device + battery), so unchanged rows aren't rebuilt.</summary>
    private readonly Dictionary<string, (BtDeviceItem item, int? battery)> _shown = new();
    private readonly HashSet<string> _busy = new();
    private const int NearbyCap = 12;
    private bool _showAllNearby;
    private readonly PillButton _moreNearby = new(Icons.ChevronDown, "Show more", quiet: true) { Height = Theme.HubSmallPillHeight, Compact = true };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _idle;

    public BluetoothPopover(Popover pop, BluetoothDeviceService bt, QuickActionsService quick, DevicesViewModel devices)
    {
        _pop = pop;
        _bt = bt;
        _quick = quick;
        _devices = devices;
        _pairedHeader = Header("My devices");
        _nearbyHeader = Header("Nearby");
        _list.Children.Add(_pairedHeader);
        _list.Children.Add(_paired);
        _list.Children.Add(_nearbyHeader);
        _list.Children.Add(_nearby);
        _moreNearby.HorizontalAlignment = HorizontalAlignment.Center;
        _moreNearby.Margin = new Thickness(0, Theme.S1, 0, Theme.S1);
        _moreNearby.Visibility = Visibility.Collapsed;
        _moreNearby.Click += () => { _showAllNearby = true; Refresh(animateNew: true); };
        _list.Children.Add(_moreNearby);
        pop.Body.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        pop.Body.Children.Add(_skeleton);
        pop.Body.Children.Add(_empty);
        pop.Switch.Visibility = Visibility.Visible;
        pop.Switch.Toggled += async on => { await _quick.SetBluetoothAsync(on); Refresh(animateNew: true); };
        var more = new PillButton(Icons.Settings, "More settings", quiet: true) { Height = Theme.HubSmallPillHeight };
        more.Click += () => QuickActionsService.Open("ms-settings:bluetooth");
        pop.Footer.Children.Add(more);

        // While closed, cache updates (the warm-up prime) build rows ahead of time, so opening never creates them,
        // but only once things are quiet: measured, building them while page 4 slid in caused a 95 ms frame gap.
        _idle = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _idle.IsRepeating = false;
        _idle.Interval = TimeSpan.FromMilliseconds(800);
        _idle.Tick += (_, _) => { _idle.Stop(); if (!_pop.IsOpen) Refresh(animateNew: false); };
        bt.Changed += () =>
        {
            if (_pop.IsOpen) Refresh(animateNew: true);
            else { _idle.Stop(); _idle.Start(); }
        };
        quick.Changed += () => { if (_pop.IsOpen) Refresh(animateNew: true); };
        pop.Opened += () =>
        {
            Refresh(animateNew: false);                          // cached rows, at once
            Popover.Stagger(_paired.Children.Concat(_nearby.Children).OfType<PopRow>(), 0.04);
            if (_quick.HasBluetooth && _quick.BluetoothOn)
                PerfLog.Time("StartWatching (worker thread)", _bt.StartWatching); // returns at once
        };
        pop.Closed += () => { _bt.StopWatching(); _showAllNearby = false; };
    }

    /// <summary>Page 4 came into view: fill the paired-device cache in the background (once per run).</summary>
    public void Warm()
    {
        if (_quick.HasBluetooth && _quick.BluetoothOn) _bt.Prime();
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["SectionText"], Margin = new Thickness(Theme.S2, Theme.S2, 0, Theme.S1),
    };

    private void Refresh(bool animateNew) => PerfLog.Time("refresh pass", () => RefreshCore(animateNew));

    private void RefreshCore(bool animateNew)
    {
        _pop.Switch.IsOn = _quick.BluetoothOn;
        if (!_quick.HasBluetooth) { Clear(); _empty.Show(Icons.Bluetooth, "No Bluetooth on this PC", null, null); _pop.Caption = "Unavailable"; return; }
        if (!_quick.BluetoothOn) { _bt.StopWatching(); Clear(); _empty.Show(Icons.Bluetooth, "Bluetooth is off", "Turn it on with the switch above.", null); _pop.Caption = "Off"; return; }
        if (_pop.IsOpen) _bt.StartWatching();

        var all = _bt.Devices.ToList();
        var connected = all.Where(d => d.Connected).Select(d => d.Name).ToList();
        _pop.Caption = connected.Count == 0 ? "No devices connected" : string.Join(", ", connected);

        var paired = all.Where(d => d.Paired).OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var nearbyAll = all.Where(d => !d.Paired).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Measured: ~36 nearby devices here at ~12 ms per row to build; nobody scans that many, so show 12 until asked.
        var nearby = _showAllNearby ? nearbyAll : nearbyAll.Take(NearbyCap).ToList();
        var keep = new HashSet<string>(paired.Concat(nearby).Select(d => d.Id));
        foreach (var gone in _rows.Keys.Where(k => !keep.Contains(k) && !_busy.Contains(k)).ToList())
        {
            var row = _rows[gone];
            _paired.Children.Remove(row);
            _nearby.Children.Remove(row);
            _rows.Remove(gone);
            _shown.Remove(gone);
        }
        var arriving = new List<PopRow>();
        _created = 0;
        _passClock.Restart();
        bool deferred = Sync(_paired, paired, arriving);
        deferred |= Sync(_nearby, nearby, arriving);
        int hidden = nearbyAll.Count - nearby.Count;
        _moreNearby.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        _moreNearby.SetContent(Icons.ChevronDown, $"Show {hidden} more");
        if (animateNew) Popover.Stagger(arriving);
        if (deferred) ContinueNextFrame(animateNew);
        PerfLog.Mark($"rows on screen ({_rows.Count})");

        bool anyPaired = _paired.Children.Count > 0, anyNearby = _nearby.Children.Count > 0;
        _pairedHeader.Visibility = anyPaired ? Visibility.Visible : Visibility.Collapsed;
        _nearbyHeader.Visibility = anyNearby ? Visibility.Visible : Visibility.Collapsed;
        bool empty = !anyPaired && !anyNearby;
        _skeleton.Show(empty && !_bt.PairedEnumerated);
        if (empty && _bt.PairedEnumerated) _empty.ShowLoading("Looking for devices…");
        else _empty.Hide();
    }

    /// <summary>
    /// Time allowed for building new rows per pass (at least one row is always built). Measured: a nearby
    /// burst of ~25 devices built in one pass stalled the UI thread for 80–155 ms, and even 4 rows per pass
    /// cost 10–40 ms; a small time budget per frame keeps every frame short (the rest follow next frame).
    /// </summary>
    private const double RowBudgetMs = 6;
    private readonly System.Diagnostics.Stopwatch _passClock = new();
    private int _created;
    private bool _continuePending;

    private void ContinueNextFrame(bool animateNew)
    {
        if (_continuePending) return;
        _continuePending = true;
        EventHandler<object>? h = null;
        h = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= h;
            _continuePending = false;
            Refresh(animateNew);
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += h;
    }

    /// <summary>
    /// Puts <paramref name="target"/>'s rows in the order of <paramref name="items"/>, reusing rows by device id.
    /// Returns true when some new rows were left for the next frame.
    /// </summary>
    private bool Sync(StackPanel target, IEnumerable<BtDeviceItem> items, List<PopRow> arriving)
    {
        int i = 0;
        bool deferred = false;
        foreach (var item in items)
        {
            if (!_rows.TryGetValue(item.Id, out var row))
            {
                if (_created > 0 && _passClock.Elapsed.TotalMilliseconds > RowBudgetMs) { deferred = true; continue; }
                _created++;
                PerfLog.Mark(item.Paired ? "first paired row built" : "first nearby row built");
                row = new PopRow();
                string id = item.Id;
                row.Tapped += async (_, e) => { e.Handled = true; await OnRowClicked(id); };
                _rows[item.Id] = row;
                arriving.Add(row);
            }
            var owner = row.Parent as StackPanel;
            if (owner != target)
            {
                owner?.Children.Remove(row);
                target.Children.Insert(Math.Min(i, target.Children.Count), row);
                if (owner != null) arriving.Add(row); // just paired: it moves up into My devices
            }
            else
            {
                int at = target.Children.IndexOf(row);
                if (at != i) target.Children.Move((uint)at, (uint)Math.Min(i, target.Children.Count - 1));
            }
            int? battery = item.Paired && item.Connected
                ? _devices.Items.FirstOrDefault(d => string.Equals(d.Name, item.Name, StringComparison.OrdinalIgnoreCase))?.Battery : null;
            if (!_shown.TryGetValue(item.Id, out var shown) || shown.item != item || shown.battery != battery)
            {
                Fill(row, item, battery);
                _shown[item.Id] = (item, battery);
            }
            i++;
        }
        return deferred;
    }

    private void Clear()
    {
        _rows.Clear();
        _shown.Clear();
        _paired.Children.Clear();
        _nearby.Children.Clear();
        _skeleton.Show(false);
        _pairedHeader.Visibility = _nearbyHeader.Visibility = Visibility.Collapsed;
    }

    private void Fill(PopRow row, BtDeviceItem item, int? battery)
    {
        row.Icon = BluetoothService.GlyphFor(item.Kind);
        row.Title = item.Name;
        row.Caption = item.Caption;
        row.CaptionBrush = item.Connected ? Theme.SuccessBrush : Theme.SecondaryText;
        row.IsActive = item.Connected;
        if (_busy.Contains(item.Id)) return;
        var status = new List<UIElement>();
        if (item.Paired)
        {
            if (battery is int pct)
                status.Add(new BatteryRing { RingSize = Theme.HeaderRingSize, Thickness = Theme.HeaderRingThickness, Percent = pct, ShowNumber = true, VerticalAlignment = VerticalAlignment.Center });
            if (!item.CanToggleInApp)
                status.Add(new VectorIcon { Data = Icons.ChevronRight, Size = Theme.IconSmall, Foreground = Theme.TertiaryText, VerticalAlignment = VerticalAlignment.Center });
            ToolTipService.SetToolTip(row, item.CanToggleInApp ? (item.Connected ? "Click to disconnect" : "Click to connect") : "Opens Bluetooth settings");
        }
        else
        {
            string pairId = item.Id;
            var pair = PopRow.Chip("Pair", async () => await Pair(pairId));
            status.Add(pair);
        }
        row.SetStatus(status.ToArray());
    }

    private void Refill(string id, PopRow row)
    {
        _shown.Remove(id); // force the next refresh to redraw it
        var latest = _bt.Devices.FirstOrDefault(d => d.Id == id);
        if (latest != null && _rows.ContainsKey(id)) Refresh(animateNew: false);
    }

    private async Task OnRowClicked(string id)
    {
        var item = _bt.Devices.FirstOrDefault(d => d.Id == id);
        if (item == null || !item.Paired || _busy.Contains(id) || !_rows.TryGetValue(id, out var row)) return;
        if (!item.CanToggleInApp) { await _bt.SetConnectedAsync(id, !item.Connected); return; } // opens Settings
        _busy.Add(id);
        row.SetStatus(new Spinner());
        row.Caption = item.Connected ? "Disconnecting…" : "Connecting…";
        var outcome = await _bt.SetConnectedAsync(id, !item.Connected);
        _busy.Remove(id);
        if (outcome == BtActionOutcome.Done)
        {
            row.SetStatus(PopRow.Check());
            await Task.Delay(900); // the device event updates the row (caption / ring) right after
        }
        else
        {
            row.Shake();
            row.Caption = "Couldn't change the connection";
            row.CaptionBrush = Theme.DangerBrush;
            await Task.Delay(1600);
        }
        Refill(id, row);
    }

    private async Task Pair(string id)
    {
        if (_busy.Contains(id) || !_rows.TryGetValue(id, out var row)) return;
        _busy.Add(id);
        row.SetStatus(new Spinner());
        row.Caption = "Pairing…";
        var outcome = await _bt.PairAsync(id);
        _busy.Remove(id);
        if (outcome == BtActionOutcome.Done) row.SetStatus(PopRow.Check());
        else
        {
            row.Shake();
            row.Caption = "Pairing failed. Try from Settings.";
            row.CaptionBrush = Theme.DangerBrush;
            var settings = PopRow.Action("Settings");
            settings.Click += () => QuickActionsService.Open("ms-settings:bluetooth");
            row.SetStatus(settings);
        }
    }
}
