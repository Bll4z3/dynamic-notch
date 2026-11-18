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

        for (int i = 0; i < 7; i++)
            _tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : Theme.GapS2 });
        _tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.HubTileHeight) });
        _tiles.RowDefinitions.Add(new RowDefinition { Height = Theme.GapS2 });
        _tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.HubTileHeight) });
        // Wi-Fi and Bluetooth open a popup to pick a network / device (their on/off switch lives in its header).
        _wifi = AddTile(0, 0, Icons.Wifi, "Wi-Fi", "Choose a network", () => OpenPopover(_wifiPop!, _wifi!));
        _bluetooth = AddTile(0, 1, Icons.Bluetooth, "Bluetooth", "Choose a device", () => OpenPopover(_btPop!, _bluetooth!));
        _airplane = AddTile(0, 2, Icons.Plane, "Airplane", "Open airplane mode settings", () => QuickActionsService.Open("ms-settings:network-airplanemode"));
        _dnd = AddTile(0, 3, Icons.Moon, "Do not disturb", "Hold back notification pills (they still appear here)", () => vm.DoNotDisturb = !vm.DoNotDisturb);
        _mic = AddTile(1, 0, Icons.Mic, "Mute mic", "Mute or unmute your microphone", vm.ToggleMic);
        _saver = AddTile(1, 1, Icons.Leaf, "Energy saver", "Open energy saver settings", () => QuickActionsService.Open("ms-settings:batterysaver"));
        _night = AddTile(1, 2, Icons.NightLight, "Night light", "Open night light settings", () => QuickActionsService.Open("ms-settings:nightlight"));
        _cast = AddTile(1, 3, Icons.Cast, "Cast", "Open Cast (Win+K)", QuickActionsService.OpenCast);
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
            pop.Closed += () => { PerfLog.End(); PopoverClosed?.Invoke(); };
        }
        _wifiView = new WifiPopover(_wifiPop, vm.Wifi, vm.Quick);
        _wifiView.FocusRequested += () => FocusRequested?.Invoke();
        _wifiView.FocusReleased += () => FocusReleased?.Invoke();
        _btView = new BluetoothPopover(_btPop, vm.BluetoothDevices, vm.Quick, vm.Devices);
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
        _wifiView.Warm();
        _btView.Warm();
    }

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    // ---- Quick actions --------------------------------------------------------------------------------
    private Tile AddTile(int row, int col, string icon, string label, string tip, Action click)
    {
        var tile = new Tile(icon, label) { Tooltip = tip };
        tile.Click += click;
        SetRow(tile, row * 2);
        SetColumn(tile, col * 2);
        _tiles.Children.Add(tile);
        return tile;
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
    }

    /// <summary>A quick-action tile: rounded (small radius), white when on, icon over a short label.</summary>
    private sealed class Tile : Grid
    {
        private readonly Border _back = new(), _lighten = new();
        private readonly VectorIcon _icon = new();
        private readonly TextBlock _label = new();
        private readonly SolidColorBrush _bg = new(Theme.TileColor), _fg = new(Theme.PrimaryTextColor);
        private bool _active, _available = true;

        public Tile(string icon, string label)
        {
            Background = Theme.TransparentBrush;
            _back.CornerRadius = _lighten.CornerRadius = Theme.RadiusSmall;
            _back.Background = _bg;
            _back.BorderBrush = Theme.TileBorderBrush;
            _back.BorderThickness = new Thickness(1);
            _lighten.Background = Theme.PillLightenBrush;
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            _icon.Data = icon;
            _icon.Size = Theme.IconMedium;
            _icon.Foreground = _fg;
            _icon.HorizontalAlignment = HorizontalAlignment.Center;
            _label.Text = label;
            _label.FontFamily = Theme.FontMedium;
            _label.FontWeight = FontWeights.Medium;
            _label.FontSize = Theme.FontTileLabel;
            _label.Foreground = _fg;
            _label.TextTrimming = TextTrimming.CharacterEllipsis;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.Margin = Theme.GapTopS1;
            _label.IsHitTestVisible = false;
            stack.Children.Add(_icon);
            stack.Children.Add(_label);
            Children.Add(_back);
            Children.Add(_lighten);
            Children.Add(stack);
            Motions.HoverFade(this, _lighten);
            Motions.PressScale(this);
            Tapped += (_, e) => { e.Handled = true; Click?.Invoke(); };
        }

        public event Action? Click;
        public string? Tooltip { set => ToolTipService.SetToolTip(this, value); }
        public string Icon { set => _icon.Data = value; }

        public bool Active
        {
            set
            {
                if (value == _active) return;
                _active = value;
                _back.Background = value ? Theme.AccentBrush : _bg; // selected tiles carry the accent (live)
                _icon.Foreground = _label.Foreground = value ? Theme.OnAccentBrush : _fg;
                // (VectorIcon rebuilds its path with the new brush.)
            }
        }

        /// <summary>No such hardware: dimmed (a click opens the matching Settings page instead).</summary>
        public bool Available { set { _available = value; Opacity = value ? 1 : 0.45; } }
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
