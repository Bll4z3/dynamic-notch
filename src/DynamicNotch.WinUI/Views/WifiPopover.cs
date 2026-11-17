using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>
/// The Wi-Fi popup (page 4 ▸ Quick actions ▸ Wi-Fi). Header switch turns the radio on / off. Networks are
/// sorted connected → signal, with signal bars, a lock for secured ones and "Connected / Saved /
/// Secured / Open". A click connects (no password first; a secured unknown network expands a password
/// box inline). Scans only while open: on open and every 10 s. The password box takes keyboard focus
/// deliberately (the notch gives it back afterwards) and the keyboard hook passes keys through meanwhile.
/// </summary>
public sealed class WifiPopover
{
    private readonly Popover _pop;
    private readonly WifiService _wifi;
    private readonly QuickActionsService _quick;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly ScrollViewer _scroll = new();
    private readonly EmptyState _empty = new();
    private readonly PillButton _refresh;
    private readonly Spinner _refreshSpin = new(12);
    private readonly DispatcherQueueTimer _rescan;
    private readonly Dictionary<string, PopRow> _rows = new();
    /// <summary>What each row shows, so a rescan only touches rows whose network changed.</summary>
    private readonly Dictionary<string, (byte bars, bool secured, bool connected, bool saved)> _shown = new();
    private readonly SkeletonRows _skeleton = new();
    private IReadOnlyList<WifiNetworkItem> _items = Array.Empty<WifiNetworkItem>();
    private bool _scanning, _first;
    private string? _busySsid, _expandedSsid;

    public WifiPopover(Popover pop, WifiService wifi, QuickActionsService quick)
    {
        _pop = pop;
        _wifi = wifi;
        _quick = quick;
        _scroll.Content = _list;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        pop.Body.Children.Add(_scroll);
        pop.Body.Children.Add(_skeleton);
        pop.Body.Children.Add(_empty);
        pop.Switch.Visibility = Visibility.Visible;
        pop.Switch.Toggled += async on =>
        {
            await _quick.SetWifiAsync(on);
            await Task.Delay(on ? 1500 : 200); // the adapter needs a moment after power-on before it can scan
            await ScanAsync();
        };

        var more = new PillButton(Icons.Settings, "More settings", quiet: true) { Height = Theme.HubSmallPillHeight };
        more.Click += () => QuickActionsService.Open("ms-settings:network-wifi");
        _refresh = new PillButton(null, "Refresh", quiet: true) { Height = Theme.HubSmallPillHeight };
        _refresh.Click += async () => await ScanAsync();
        pop.Footer.Children.Add(more);
        pop.Footer.Children.Add(_refresh);
        _refreshSpin.Visibility = Visibility.Collapsed;
        _refreshSpin.VerticalAlignment = VerticalAlignment.Center;
        pop.Footer.Children.Add(_refreshSpin);

        _rescan = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _rescan.Interval = TimeSpan.FromMilliseconds(Motion.WifiRescanMs);
        _rescan.Tick += async (_, _) => { if (_pop.IsOpen && _busySsid == null && _expandedSsid == null) await ScanAsync(); };

        pop.Opened += async () =>
        {
            _first = true;
            _rescan.Start();
            await OpenAsync();
        };
        pop.Closed += () =>
        {
            _rescan.Stop();
            _expandedSsid = null;
            FocusReleased?.Invoke();
        };
    }

    /// <summary>The password box wants keyboard focus (the notch takes it, remembering the previous window).</summary>
    public event Action? FocusRequested;
    /// <summary>Focus can go back to where the user was.</summary>
    public event Action? FocusReleased;

    /// <summary>Page 4 came into view: initialise and read the last report in the background, so the first open has rows.</summary>
    public void Warm()
    {
        if (_quick.HasWifi && _quick.WifiOn) _ = _wifi.WarmAsync();
    }

    private async Task OpenAsync()
    {
        _pop.Switch.IsOn = _quick.WifiOn;
        // 1. Whatever is cached, at once (rows kept from last time, or the warm read).
        if (_quick.WifiOn && _wifi.Cached.Count > 0)
        {
            _items = _wifi.Cached;
            ShowItems(animateNew: false);
        }
        else if (_quick.WifiOn && _rows.Count == 0) _skeleton.Show(true);
        Popover.Stagger(_list.Children.OfType<PopRow>(), 0.04);
        PerfLog.Mark($"cached rows on screen ({_rows.Count})");

        // 2. Nothing touches the radio until the popup's first frame is on screen.
        await NextFrameAsync();
        if (!_pop.IsOpen) return;
        var availability = await _wifi.InitialiseAsync();
        PerfLog.Mark("Wi-Fi InitialiseAsync done");
        if (availability != WifiAvailability.Ready) { _skeleton.Show(false); ShowAvailability(availability); return; }
        // 3. Windows' last report (no scan, instant), then a real scan.
        if (_quick.WifiOn && !_scanning)
        {
            var report = await _wifi.ReadAsync();
            PerfLog.Mark("Wi-Fi last report read");
            if (_pop.IsOpen && !_scanning && report.Count > 0) { _items = report; ShowItems(animateNew: true); }
        }
        if (_pop.IsOpen) await ScanAsync();
    }

    private static Task NextFrameAsync()
    {
        var tcs = new TaskCompletionSource();
        EventHandler<object>? h = null;
        h = (_, _) => { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= h; tcs.TrySetResult(); };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += h;
        return tcs.Task;
    }

    private void ShowItems(bool animateNew)
    {
        _skeleton.Show(false);
        _pop.Caption = _wifi.ConnectedSsid is { } ssid ? $"Connected to {ssid}" : "Not connected";
        Render(_items, animateNew);
        if (_items.Count > 0) _empty.Hide();
    }

    private void ShowAvailability(WifiAvailability a)
    {
        _list.Children.Clear();
        _rows.Clear();
        _shown.Clear();
        _pop.Caption = "Unavailable";
        switch (a)
        {
            case WifiAvailability.NoAdapter:
                _empty.Show(Icons.Wifi, "No Wi-Fi adapter on this PC", null, null);
                break;
            case WifiAvailability.AccessDenied:
                _empty.Show(Icons.Wifi, "Wi-Fi access is blocked", "Allow apps to control radios in Windows settings.",
                    ("Open settings", () => QuickActionsService.Open("ms-settings:privacy-radios")));
                break;
            default:
                _empty.Show(Icons.Wifi, "Wi-Fi list isn't available here", "Use Windows' network list instead.",
                    ("Show networks", () => QuickActionsService.Open("ms-availablenetworks:")));
                break;
        }
    }

    private async Task ScanAsync()
    {
        if (_scanning) return;
        if (!_quick.WifiOn)
        {
            _pop.Switch.IsOn = false;
            _pop.Caption = "Off";
            _skeleton.Show(false);
            Render(Array.Empty<WifiNetworkItem>(), false);
            _empty.Show(Icons.Wifi, "Wi-Fi is off", "Turn it on with the switch above.", null);
            return;
        }
        _pop.Switch.IsOn = true;
        _scanning = true;
        _refreshSpin.Visibility = Visibility.Visible;
        if (_rows.Count == 0) _skeleton.Show(true);
        try { _items = await _wifi.ScanAsync(); PerfLog.Mark("Wi-Fi ScanAsync done"); }
        finally { _scanning = false; _refreshSpin.Visibility = Visibility.Collapsed; }
        ShowItems(animateNew: true);
        if (_items.Count == 0)
        {
            if (_wifi.NamesHidden)
                _empty.Show(Icons.Wifi, "Network names are hidden", "Windows shows Wi-Fi names to apps only with location access on.",
                    ("Location settings", () => QuickActionsService.Open("ms-settings:privacy-location")));
            else _empty.Show(Icons.Wifi, "No networks found", "Try Refresh, or move closer to a router.", null);
        }
        else _empty.Hide();
        _first = false;
    }

    /// <summary>Keyed update: existing rows change in place, new ones spring in (staggered), gone ones leave.</summary>
    private void Render(IReadOnlyList<WifiNetworkItem> items, bool animateNew)
    {
        var keep = new HashSet<string>(items.Select(i => i.Ssid));
        foreach (var gone in _rows.Keys.Where(k => !keep.Contains(k) && k != _busySsid && k != _expandedSsid).ToList())
        {
            var row = _rows[gone];
            _rows.Remove(gone);
            _shown.Remove(gone);
            _list.Children.Remove(row);
        }
        var arriving = new List<PopRow>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!_rows.TryGetValue(item.Ssid, out var row))
            {
                PerfLog.Mark("first network row built");
                row = new PopRow();
                var captured = item.Ssid;
                row.Tapped += async (_, e) => { e.Handled = true; await OnRowClicked(captured); };
                _rows[item.Ssid] = row;
                _list.Children.Insert(Math.Min(i, _list.Children.Count), row);
                arriving.Add(row);
            }
            else
            {
                int at = _list.Children.IndexOf(row);
                if (at != i && at >= 0) _list.Children.Move((uint)at, (uint)Math.Min(i, _list.Children.Count - 1));
            }
            var key = (item.Bars, item.Secured, item.Connected, item.Saved);
            if (!_shown.TryGetValue(item.Ssid, out var shown) || shown != key)
            {
                Fill(row, item);
                _shown[item.Ssid] = key;
            }
        }
        if (animateNew) Popover.Stagger(arriving);
    }

    private void Fill(PopRow row, WifiNetworkItem item)
    {
        row.Icon = item.Bars >= 3 ? Icons.Wifi : item.Bars == 2 ? Icons.WifiMid : Icons.WifiLow;
        row.Title = item.Ssid;
        row.Caption = item.Caption;
        row.CaptionBrush = item.Connected ? Theme.SuccessBrush : Theme.SecondaryText;
        row.IsActive = item.Connected;
        if (_busySsid == item.Ssid) return; // keep the spinner
        var status = new List<UIElement>();
        if (item.Secured) status.Add(new VectorIcon { Data = Icons.Lock, Size = Theme.IconSmall - 2, Foreground = Theme.TertiaryText, VerticalAlignment = VerticalAlignment.Center });
        if (item.Connected)
        {
            var disconnect = PopRow.Action("Disconnect");
            disconnect.Click += () =>
            {
                _wifi.Disconnect();
                _ = Task.Delay(800).ContinueWith(_ => Ui.Post(async () => await ScanAsync()));
            };
            status.Add(disconnect);
        }
        row.SetStatus(status.ToArray());
    }

    private async Task OnRowClicked(string ssid)
    {
        var item = _items.FirstOrDefault(i => i.Ssid == ssid);
        if (item == null || item.Connected || _busySsid != null || !_rows.TryGetValue(ssid, out var row)) return;
        if (_expandedSsid == ssid) return; // the password form handles it
        await Connect(item, row, null, automatic: true);
    }

    private async Task Connect(WifiNetworkItem item, PopRow row, string? password, bool automatic)
    {
        _busySsid = item.Ssid;
        _shown.Remove(item.Ssid); // the next scan redraws this row whatever happens
        row.SetStatus(new Spinner());
        row.Caption = "Connecting…";
        var outcome = await _wifi.ConnectAsync(item, password, automatic);
        _busySsid = null;
        switch (outcome)
        {
            case WifiConnectOutcome.Connected:
                CollapsePassword(row);
                row.SetStatus(PopRow.Check());
                row.Caption = "Connected";
                await Task.Delay(700);
                await ScanAsync();
                break;
            case WifiConnectOutcome.NeedsPassword:
                Fill(row, item);
                ExpandPassword(item, row, null);
                break;
            case WifiConnectOutcome.WrongPassword:
                Fill(row, item);
                row.Shake();
                ExpandPassword(item, row, "Wrong password. Try again.");
                break;
            default:
                Fill(row, item);
                row.Shake();
                row.Caption = "Couldn't connect";
                row.CaptionBrush = Theme.DangerBrush;
                break;
        }
    }

    private void ExpandPassword(WifiNetworkItem item, PopRow row, string? error)
    {
        foreach (var other in _rows.Values.Where(r => r != row)) CollapsePassword(other);
        _expandedSsid = item.Ssid;
        row.Expander.Children.Clear();
        var stack = new StackPanel { Spacing = Theme.S2 };
        var box = new PasswordBox
        {
            PlaceholderText = "Password", Height = Theme.PasswordBoxHeight, FontFamily = Theme.Font, FontSize = Theme.FontBody,
            Padding = new Thickness(Theme.S3, 5, Theme.S3, 5), VerticalContentAlignment = VerticalAlignment.Center,
        };
        InputStyle.Apply(box);
        var auto = new CheckBox { Content = "Connect automatically", IsChecked = true, FontFamily = Theme.Font, FontSize = Theme.FontCaption, MinHeight = 24, Padding = new Thickness(6, 0, 0, 0) };
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(auto);
        var connect = PopRow.Action("Connect", primary: true);
        Grid.SetColumn(connect, 1);
        line.Children.Add(connect);
        stack.Children.Add(box);
        if (error != null) stack.Children.Add(new TextBlock { Text = error, Style = (Style)Application.Current.Resources["CaptionText"], Foreground = Theme.DangerBrush });
        stack.Children.Add(line);
        row.Expander.Children.Add(stack);
        row.Expander.Visibility = Visibility.Visible;
        if (Anim.Enabled) Motions.PopIn(stack);

        async Task Submit()
        {
            if (string.IsNullOrEmpty(box.Password)) { row.Shake(); return; }
            string pw = box.Password;
            box.Password = "";
            FocusReleased?.Invoke();
            await Connect(item, row, pw, auto.IsChecked == true);
        }
        connect.Click += async () => await Submit();
        box.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; await Submit(); } };
        // Deliberate focus: only now does the notch take the keyboard (the hook passes keys through while typing).
        FocusRequested?.Invoke();
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => box.Focus(FocusState.Programmatic));
    }

    private void CollapsePassword(PopRow row)
    {
        if (row.Expander.Visibility == Visibility.Collapsed) return;
        row.Expander.Children.Clear();
        row.Expander.Visibility = Visibility.Collapsed;
        if (_rows.FirstOrDefault(kv => kv.Value == row).Key == _expandedSsid) { _expandedSsid = null; FocusReleased?.Invoke(); }
    }
}

/// <summary>Centred icon + title + caption (+ optional action pill), or a spinner with a line — popup empty / error states.</summary>
public sealed class EmptyState : Grid
{
    private readonly StackPanel _stack = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = Theme.S1 };

    public EmptyState()
    {
        Children.Add(_stack);
        Visibility = Visibility.Collapsed;
    }

    public void Show(string icon, string title, string? caption, (string label, Action action)? button)
    {
        _stack.Children.Clear();
        _stack.Children.Add(new VectorIcon { Data = icon, Size = Theme.IconLarge, Foreground = Theme.TertiaryText, HorizontalAlignment = HorizontalAlignment.Center });
        _stack.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BaseText"], FontFamily = Theme.FontSemiBold, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = Theme.GapTopS1 });
        if (caption != null)
            _stack.Children.Add(new TextBlock { Text = caption, Style = (Style)Application.Current.Resources["CaptionText"], Foreground = Theme.SecondaryText, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, TextAlignment = TextAlignment.Center, MaxWidth = 240, HorizontalAlignment = HorizontalAlignment.Center });
        if (button is { } b)
        {
            var pill = PopRow.Action(b.label);
            pill.HorizontalAlignment = HorizontalAlignment.Center;
            pill.Margin = Theme.GapTopS2;
            pill.Click += b.action;
            _stack.Children.Add(pill);
        }
        Appear();
    }

    public void ShowLoading(string text)
    {
        _stack.Children.Clear();
        _stack.Children.Add(new Spinner(18) { HorizontalAlignment = HorizontalAlignment.Center });
        _stack.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionText"], Foreground = Theme.SecondaryText, HorizontalAlignment = HorizontalAlignment.Center, Margin = Theme.GapTopS1 });
        Appear();
    }

    private void Appear()
    {
        bool was = Visibility == Visibility.Visible;
        Visibility = Visibility.Visible;
        if (!was && Anim.Enabled) Motions.PopIn(_stack);
    }

    public void Hide() => Visibility = Visibility.Collapsed;
}
