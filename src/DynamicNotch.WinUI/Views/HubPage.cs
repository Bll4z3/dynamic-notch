using System.ComponentModel;
using System.Globalization;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.Views;

/// <summary>
/// Page "Notifications + Controls". Left half: the notification history (<see cref="HistoryPanel"/>).
/// Right half: quick actions as rounded tiles (Wi-Fi, Bluetooth, airplane mode, Do Not Disturb, mute mic,
/// energy saver, night light, Cast) and volume / brightness sliders. Rows and tiles use the small radius.
/// </summary>
public sealed class HubPage : Grid
{
    private readonly HubViewModel _vm;
    private readonly Grid _rightHeader = new(), _tiles = new(), _sliders = new();
    private readonly Tile _wifi, _bluetooth, _airplane, _dnd, _mic, _saver, _night, _cast;
    private readonly SliderRow _volume, _brightness;

    public HubPage(HubViewModel vm)
    {
        _vm = vm;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = Theme.GapS4 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ================= Left: notification history =================
        History = new HistoryPanel(vm);
        Children.Add(History);

        // ================= Right: quick actions =================
        var right = new Grid();
        SetColumn(right, 2);
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.HubHeaderHeight) });
        right.RowDefinitions.Add(new RowDefinition { Height = Theme.GapS2 });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = Theme.GapS2 });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var rtitle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rtitle.Children.Add(new VectorIcon { Data = Icons.Dashboard, Size = Theme.IconSmall, Foreground = Theme.SecondaryText, VerticalAlignment = VerticalAlignment.Center });
        rtitle.Children.Add(new TextBlock { Text = "Quick actions", Style = StyleOf("SectionText"), Margin = Theme.GapLeftS2 });
        _rightHeader.Children.Add(rtitle);
        right.Children.Add(_rightHeader);

        // Row 1: Wi-Fi and Bluetooth as wide status tiles (icon disc · name · live status · chevron); each
        // opens its popup, which springs out of the tile. Row 2: six compact toggles (icon only, tooltip names).
        _tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.HubTileHeight) });
        _tiles.RowDefinitions.Add(new RowDefinition { Height = Theme.GapS2 });
        _tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.HubToggleHeight) });
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = Theme.GapS2 });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _wifi = new Tile(Icons.Wifi, "Wi-Fi", wide: true) { Tooltip = "Choose a network" };
        _wifi.Click += () => OpenPopover(_wifiPop!, _wifi!);
        _bluetooth = new Tile(Icons.Bluetooth, "Bluetooth", wide: true) { Tooltip = "Choose a device" };
        _bluetooth.Click += () => OpenPopover(_btPop!, _bluetooth!);
        SetColumn(_bluetooth, 2);
        top.Children.Add(_wifi);
        top.Children.Add(_bluetooth);
        _tiles.Children.Add(top);
        var row = new Grid();
        for (int i = 0; i < 11; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(6) });
        SetRow(row, 2);
        _tiles.Children.Add(row);
        _dnd = AddToggle(row, 0, Icons.Moon, "Do not disturb", "hold back notification pills (they still appear here)", () => vm.DoNotDisturb = !vm.DoNotDisturb);
        _mic = AddToggle(row, 1, Icons.Mic, "Microphone", "mute or unmute", vm.ToggleMic);
        _saver = AddToggle(row, 2, Icons.Leaf, "Energy saver", "open energy saver settings", () => QuickActionsService.Open("ms-settings:batterysaver"));
        _night = AddToggle(row, 3, Icons.NightLight, "Night light", "open night light settings", () => QuickActionsService.Open("ms-settings:nightlight"));
        _airplane = AddToggle(row, 4, Icons.Plane, "Airplane mode", "open airplane mode settings", () => QuickActionsService.Open("ms-settings:network-airplanemode"));
        _cast = AddToggle(row, 5, Icons.Cast, "Cast", "open Cast (Win+K)", QuickActionsService.OpenCast);
        SetRow(_tiles, 2);
        right.Children.Add(_tiles);

        _sliders.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _sliders.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _volume = new SliderRow(Icons.VolumeHigh, Theme.VolumeColor, "Volume (click the speaker to mute)");
        _volume.Track.UserChanged += vm.SetVolume;
        _volume.IconClick += vm.ToggleMute;
        _brightness = new SliderRow(Icons.SunCore + " " + Icons.SunRays, Theme.BrightnessColor, "Brightness");
        _brightness.Track.UserChanged += vm.SetBrightness;
        SetRow(_brightness, 1);
        _sliders.Children.Add(_volume);
        _sliders.Children.Add(_brightness);
        SetRow(_sliders, 4);
        right.Children.Add(_sliders);
        Children.Add(right);

        // ---- Popovers: over the whole page (the dimmed rest closes them), card on the right half ----
        _wifiPop = new Popover(Icons.Wifi, "Wi-Fi");
        _btPop = new Popover(Icons.Bluetooth, "Bluetooth");
        foreach (var pop in new[] { _wifiPop, _btPop })
        {
            SetColumnSpan(pop, 3);
            Children.Add(pop);
            pop.Closed += () => { PerfLog.End(); RefreshStatus(); PopoverClosed?.Invoke(); };
        }
        _wifiView = new WifiPopover(_wifiPop, vm.Wifi, vm.Quick);
        _wifiView.FocusRequested += () => FocusRequested?.Invoke();
        _wifiView.FocusReleased += () => FocusReleased?.Invoke();
        _btView = new BluetoothPopover(_btPop, vm.BluetoothDevices, vm.Quick, vm.Devices);
        vm.BluetoothDevices.Changed += RefreshStatus;
        SizeChanged += (_, e) =>
        {
            double w = (e.NewSize.Width - Theme.S4) / 2;
            _wifiPop.CardWidth = w;
            _btPop.CardWidth = w;
        };

        vm.PropertyChanged += OnVmChanged;
        RefreshQuick(animate: false);
    }

    private readonly Popover _wifiPop, _btPop;
    private readonly WifiPopover _wifiView;
    private readonly BluetoothPopover _btView;

    /// <summary>A popup's text box wants keyboard focus / is done with it.</summary>
    public event Action? FocusRequested, FocusReleased;
    public event Action? PopoverClosed;

    public bool IsPopoverOpen => _wifiPop.IsOpen || _btPop.IsOpen;

    /// <summary>Dev (--perf-popovers): opens a popup as a click would.</summary>
    public void DevOpenPopover(bool bluetooth) => OpenPopover(bluetooth ? _btPop : _wifiPop, bluetooth ? _bluetooth : _wifi);

    private void OpenPopover(Popover pop, FrameworkElement from)
    {
        PerfLog.Begin(pop == _btPop ? "Bluetooth popup" : "Wi-Fi popup");
        (pop == _wifiPop ? _btPop : _wifiPop).Close();
        pop.Open(from);
    }

    /// <summary>Closes an open popup (Esc, notch closing, page change). Returns true when one was open.</summary>
    public bool ClosePopover()
    {
        bool was = IsPopoverOpen;
        _wifiPop.Close();
        _btPop.Close();
        return was;
    }

    public HistoryPanel History { get; }

    /// <summary>Blocks for the notch's staggered entrance.</summary>
    public IEnumerable<(FrameworkElement element, int order)> StaggerBlocks =>
        History.StaggerBlocks.Concat(new (FrameworkElement, int)[] { (_rightHeader, 1), (_tiles, 2), (_sliders, 3) });

    /// <summary>Called when the page comes into view: fresh ages and live values.</summary>
    public void OnShown()
    {
        _vm.Refresh();
        History.OnShown();
        // Warm the popups' caches in the background so their first open has content.
        _ = WarmAsync();
    }

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    // ---- Quick actions --------------------------------------------------------------------------------
    private Tile AddToggle(Grid row, int col, string icon, string label, string tip, Action click)
    {
        var tile = new Tile(icon, label) { Tooltip = tip };
        tile.Click += click;
        SetColumn(tile, col * 2);
        row.Children.Add(tile);
        return tile;
    }

    /// <summary>The wide tiles' status lines: Wi-Fi's network, Bluetooth's connected devices.</summary>
    private void RefreshStatus()
    {
        _wifi.Status = !_vm.HasWifi ? "Unavailable" : !_vm.WifiOn ? "Off" : _vm.Wifi.ConnectedSsid is { } ssid ? ssid : "On";
        var connected = _vm.BluetoothDevices.Devices.Where(d => d.Connected).Select(d => d.Name).ToList();
        _bluetooth.Status = !_vm.HasBluetooth ? "Unavailable" : !_vm.BluetoothOn ? "Off"
            : connected.Count switch { 0 => "On", 1 => connected[0], _ => $"{connected.Count} devices" };
    }

    private async Task WarmAsync()
    {
        _btView.Warm();
        if (_vm.HasWifi && _vm.WifiOn) { try { await _vm.Wifi.WarmAsync(); } catch { } }
        RefreshStatus();
    }


    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HubViewModel.EmptyText) or nameof(HubViewModel.CanUndo)) return;
        RefreshQuick(animate: true);
    }

    private void RefreshQuick(bool animate)
    {
        _wifi.Active = _vm.WifiOn;
        _wifi.Available = _vm.HasWifi;
        _bluetooth.Active = _vm.BluetoothOn;
        _bluetooth.Available = _vm.HasBluetooth;
        _dnd.Active = _vm.DoNotDisturb;
        _mic.Active = _vm.MicMuted;
        _mic.Icon = _vm.MicMuted ? Icons.MicOff : Icons.Mic;
        _mic.Available = _vm.HasMicrophone;
        _saver.Active = _vm.EnergySaverOn;
        _volume.Visibility = _vm.HasVolume ? Visibility.Visible : Visibility.Collapsed;
        _volume.SetValue(_vm.Volume, _vm.Muted, animate);
        _volume.Icon = _vm.Muted || _vm.Volume <= 0.001 ? Icons.VolumeMute : _vm.Volume < 0.34 ? Icons.VolumeLow : _vm.Volume < 0.67 ? Icons.VolumeMid : Icons.VolumeHigh;
        _brightness.Visibility = _vm.HasBrightness ? Visibility.Visible : Visibility.Collapsed;
        _brightness.SetValue(_vm.Brightness, false, animate);
        RefreshStatus();
    }

    /// <summary>
    /// A quick-action tile, in two shapes. Wide (Wi-Fi, Bluetooth): an icon disc · name over a live status
    /// line · a chevron that says "this opens a menu". Compact (the toggles): a centred icon, its name in the
    /// tooltip. Both use the small radius, a hairline border, a hover lighten and a press squash. "On" is an
    /// accent layer that cross-fades in (a compositor spring) while the icon cross-fades to the on-accent
    /// colour, instead of the colours snapping. Unavailable hardware is dimmed.
    /// </summary>
    private sealed class Tile : Grid
    {
        private readonly Border _back = new(), _on = new(), _lighten = new(), _disc = new(), _discOn = new();
        private readonly VectorIcon _iconOff = new(), _iconOn = new();
        private readonly TextBlock _status = new();
        private readonly CompSpring _active;
        private readonly bool _wide;
        private readonly string _label;
        private string? _tip;
        private bool _isOn;

        public Tile(string icon, string label, bool wide = false)
        {
            _wide = wide;
            _label = label;
            Background = Theme.TransparentBrush;
            foreach (var b in new[] { _back, _on, _lighten }) b.CornerRadius = Theme.RadiusSmall;
            _back.Background = Theme.TileBrush;
            _back.BorderBrush = Theme.TileBorderBrush;
            _back.BorderThickness = new Thickness(1);
            _lighten.Background = Theme.PillLightenBrush;
            _lighten.IsHitTestVisible = _on.IsHitTestVisible = false;
            Children.Add(_back);
            Children.Add(wide ? new Border() : _on); // wide tiles light up their disc, not the whole tile
            Children.Add(_lighten);

            foreach (var (i, brush) in new[] { (_iconOff, Theme.PrimaryText), (_iconOn, Theme.OnAccentBrush) })
            {
                i.Data = icon;
                i.Size = wide ? Theme.IconSmall + 1 : Theme.IconMedium;
                i.Foreground = brush;
                i.HorizontalAlignment = HorizontalAlignment.Center;
                i.VerticalAlignment = VerticalAlignment.Center;
            }
            _on.Background = Theme.AccentBrush;
            var iconHost = new Grid { IsHitTestVisible = false };
            if (wide)
            {
                // Icon disc: grey when off, accent when on (cross-fading), with the name and status beside it.
                double d = Theme.HubTileHeight - 18;
                _disc.Width = _disc.Height = _discOn.Width = _discOn.Height = d;
                _disc.CornerRadius = _discOn.CornerRadius = new CornerRadius(d / 2);
                _disc.Background = Theme.QuietPillBrush;
                _disc.BorderBrush = Theme.TileBorderBrush;
                _disc.BorderThickness = new Thickness(1);
                _discOn.Background = Theme.AccentBrush;
                iconHost.Children.Add(_disc);
                iconHost.Children.Add(_discOn);
            }
            iconHost.Children.Add(_iconOff);
            iconHost.Children.Add(_iconOn);

            if (wide)
            {
                var line = new Grid { Margin = new Thickness(9, 0, 10, 0), IsHitTestVisible = false };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                iconHost.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(iconHost);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS2 };
                text.Children.Add(new TextBlock { Text = label, FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold, FontSize = Theme.FontBody - 1, Foreground = Theme.PrimaryText, TextTrimming = TextTrimming.CharacterEllipsis });
                _status.FontFamily = Theme.Font;
                _status.FontSize = Theme.FontCaption;
                _status.Foreground = Theme.SecondaryText;
                _status.TextTrimming = TextTrimming.CharacterEllipsis;
                _status.MaxLines = 1;
                text.Children.Add(_status);
                Grid.SetColumn(text, 1);
                line.Children.Add(text);
                var chevron = new VectorIcon { Data = Icons.ChevronRight, Size = Theme.IconSmall - 2, Foreground = Theme.TertiaryText, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(chevron, 2);
                line.Children.Add(chevron);
                Children.Add(line);
            }
            else Children.Add(iconHost);

            var v = Anim.VisualOf(this);
            var (active, set) = Motions.Value(v.Compositor, Motion.Hover, 0);
            _active = active;
            var c = v.Compositor;
            Anim.VisualOf(wide ? _discOn : _on).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
            Anim.VisualOf(_iconOn).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
            Anim.VisualOf(_iconOff).StartAnimation("Opacity", Anim.Expr(c, "1 - Clamp(p.v, 0, 1)", ("p", set)));
            Motions.HoverFade(this, _lighten);
            Motions.PressScale(this);
            Tapped += (_, e) => { e.Handled = true; Click?.Invoke(); };
        }

        public event Action? Click;

        /// <summary>The tooltip's description (compact tiles prefix their name and state).</summary>
        public string? Tooltip { set { _tip = value; UpdateTip(); } }

        public string Icon { set { _iconOff.Data = value; _iconOn.Data = value; } }

        /// <summary>Wide tiles: the status line ("Connected to HomeNet", "Off"…).</summary>
        public string Status { set => _status.Text = value; }

        public bool Active
        {
            set
            {
                if (value == _isOn && IsLoaded) return;
                _isOn = value;
                if (IsLoaded) _active.SetTarget(value ? 1 : 0, Motion.Hover); else _active.Snap(value ? 1 : 0);
                UpdateTip();
            }
        }

        /// <summary>No such hardware: dimmed (a click opens the matching Settings page instead).</summary>
        public bool Available { set => Opacity = value ? 1 : 0.45; }

        private void UpdateTip() =>
            ToolTipService.SetToolTip(this, _wide ? _tip : $"{_label}{(_isOn ? " · on" : "")}: {_tip}");
    }

    /// <summary>Icon button (mute) · interactive level track · value.</summary>
    private sealed class SliderRow : Grid
    {
        private readonly IconButton _button;
        private readonly TextBlock _value = new();

        public SliderRow(string icon, Windows.UI.Color fill, string tip)
        {
            Height = Theme.HubSliderHeight;
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Theme.ActivityValueWidth) });
            _button = new IconButton(26, Theme.IconSmall) { Icon = icon, Foreground = Theme.PrimaryText, Tooltip = tip, VerticalAlignment = VerticalAlignment.Center };
            _button.Click += () => IconClick?.Invoke();
            Children.Add(_button);
            Track = new LevelTrack { Interactive = true, Margin = Theme.GapLeftS2, FillBrush = Theme.AccentBrush };
            Track.UserChanged += v => _value.Text = Math.Round(v * 100).ToString(CultureInfo.CurrentCulture);
            SetColumn(Track, 1);
            Children.Add(Track);
            _value.Style = (Style)Application.Current.Resources["TimeText"];
            _value.Foreground = Theme.PrimaryText;
            _value.HorizontalAlignment = HorizontalAlignment.Right;
            _value.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(_value, 2);
            Children.Add(_value);
        }

        public LevelTrack Track { get; }
        public event Action? IconClick;
        public string Icon { set => _button.Icon = value; }

        public void SetValue(double v, bool muted, bool animate)
        {
            Track.SetValue(v, animate);
            Track.Dimmed = muted;
            if (!Track.IsDragging) _value.Text = Math.Round(v * 100).ToString(CultureInfo.CurrentCulture);
            _value.Foreground = muted ? Theme.TertiaryText : Theme.PrimaryText;
        }
    }
}
