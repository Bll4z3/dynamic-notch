using System.ComponentModel;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>Settings building blocks shared by the newer tabs (same card look as the Pages tab).</summary>
internal static class SettingsUi
{
    public static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    public static TextBlock Title(string text) => new() { Text = text, Style = StyleOf("TitleText") };

    public static TextBlock Subtitle(string text) => new()
    {
        Text = text, Style = StyleOf("SecondaryText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = Theme.GapTopS1,
    };

    public static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Background = Theme.CardBrush, BorderBrush = Theme.CardBorderBrush, BorderThickness = new Thickness(1),
        CornerRadius = Theme.Radius, Padding = padding ?? Theme.PageRowPadding, Child = child,
    };

    /// <summary>A label + description with a pill switch on the right, kept in sync with a view-model property.</summary>
    public static FrameworkElement ToggleRow(string title, string description, Func<bool> get, Action<bool> set,
                                             INotifyPropertyChanged vm, string property)
    {
        var g = new Grid { MinHeight = 40 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(description))
            text.Children.Add(new TextBlock { Text = description, Style = StyleOf("CaptionText"), Foreground = Theme.SecondaryText, Margin = Theme.GapTopS1 });
        g.Children.Add(text);
        var sw = new PillSwitch { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3, IsOn = get() };
        ToolTipService.SetToolTip(sw, title);
        sw.Toggled += on => set(on);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == property) sw.IsOn = get(); };
        Grid.SetColumn(sw, 1);
        g.Children.Add(sw);
        return g;
    }
}

/// <summary>
/// "Focus Timer" tab: auto-start / sound switches, the routine as a list of rounded rows (colour-coded
/// type chip that cycles on click, minutes with − / + steppers, move up / down, delete), buttons to add
/// a Focus, Short break or Long break, the live total and "Reset to defaults".
/// </summary>
internal sealed class FocusTabView : Grid
{
    private readonly FocusTabViewModel _vm;
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll = new();
    private readonly TextBlock _subtitle, _total = new(), _message = new();
    private readonly List<IntervalRow> _rows = new();
    private readonly PillButton[] _adders;

    public FocusTabView(FocusTabViewModel vm)
    {
        _vm = vm;
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            RowDefinitions.Add(new RowDefinition { Height = h });

        Children.Add(SettingsUi.Title("Focus Timer"));
        _subtitle = SettingsUi.Subtitle("Your routine, in order. Changes save instantly.");
        SetRow(_subtitle, 1);
        Children.Add(_subtitle);

        // Switches
        var toggles = new StackPanel();
        toggles.Children.Add(SettingsUi.ToggleRow("Auto-start next phase", "Otherwise each phase waits for Start",
            () => vm.AutoStart, v => vm.AutoStart = v, vm, nameof(FocusTabViewModel.AutoStart)));
        toggles.Children.Add(new Rectangle { Height = 1, Fill = Theme.CardBorderBrush, Margin = new Thickness(0, Theme.S1, 0, Theme.S1) });
        toggles.Children.Add(SettingsUi.ToggleRow("Play sound at phase end", "A soft chime",
            () => vm.PlaySound, v => vm.PlaySound = v, vm, nameof(FocusTabViewModel.PlaySound)));
        var card = SettingsUi.Card(toggles);
        card.Margin = Theme.GapTopS3;
        SetRow(card, 2);
        Children.Add(card);

        // Routine list
        _scroll.Content = _list;
        _scroll.Margin = Theme.GapTopS3;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _list.Margin = new Thickness(0, 0, Theme.S3, 0); // room for the scroll bar
        SetRow(_scroll, 3);
        Children.Add(_scroll);

        // Add buttons
        var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = Theme.GapTopS2 };
        _adders = new[] { IntervalKind.Focus, IntervalKind.ShortBreak, IntervalKind.LongBreak }.Select(k =>
        {
            var b = new PillButton(Icons.Plus, FocusRoutine.Name(k), quiet: true) { Margin = new Thickness(0, 0, Theme.S2, 0), Tooltip = $"Add a {FocusRoutine.Name(k).ToLowerInvariant()} at the end" };
            b.Click += () => vm.Add(k);
            add.Children.Add(b);
            return b;
        }).ToArray();
        SetRow(add, 4);
        Children.Add(add);

        // Total · message · reset
        var bottom = new Grid { Margin = Theme.GapTopS2 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _total.Style = SettingsUi.StyleOf("BaseText");
        _total.FontFamily = Theme.FontSemiBold;
        _total.FontWeight = FontWeights.SemiBold;
        _total.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_total);
        _message.Style = SettingsUi.StyleOf("BaseText");
        _message.FontFamily = Theme.FontSemiBold;
        _message.FontWeight = FontWeights.SemiBold;
        _message.Foreground = Theme.DangerBrush;
        _message.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_message);
        var reset = new TextButton { VerticalAlignment = VerticalAlignment.Center };
        reset.SetContent(Icons.Retry, "Reset to defaults");
        reset.Click += vm.ResetDefaults;
        SetColumn(reset, 1);
        bottom.Children.Add(reset);
        SetRow(bottom, 5);
        Children.Add(bottom);

        vm.ItemsChanged += OnItemsChanged;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FocusTabViewModel.Message) or nameof(FocusTabViewModel.PendingNote) or nameof(FocusTabViewModel.TotalText) or nameof(FocusTabViewModel.CanAdd))
                RefreshTexts();
        };
        Rebuild();
        RefreshTexts();
    }

    private void RefreshTexts()
    {
        bool msg = !string.IsNullOrEmpty(_vm.Message);
        _message.Text = _vm.Message;
        _total.Text = _vm.TotalText;
        _total.Visibility = msg ? Visibility.Collapsed : Visibility.Visible;
        bool pending = !string.IsNullOrEmpty(_vm.PendingNote);
        _subtitle.Text = pending ? _vm.PendingNote : "Your routine, in order. Changes save instantly.";
        _subtitle.Foreground = pending ? new SolidColorBrush(Theme.FocusB) : Theme.SecondaryText;
        foreach (var a in _adders) a.Active = _vm.CanAdd;
    }

    private void OnItemsChanged(int added)
    {
        if (_rows.Count == _vm.Items.Count) { for (int i = 0; i < _rows.Count; i++) _rows[i].Show(_vm.Items[i], i, _vm.Items.Count); }
        else Rebuild();
        if (added >= 0 && added < _rows.Count)
        {
            Motions.PopIn(_rows[added]);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _scroll.ChangeView(null, _scroll.ScrollableHeight, null));
        }
        RefreshTexts();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _rows.Clear();
        for (int i = 0; i < _vm.Items.Count; i++)
        {
            var row = new IntervalRow(_vm);
            row.Show(_vm.Items[i], i, _vm.Items.Count);
            _rows.Add(row);
            _list.Children.Add(row);
        }
    }

    /// <summary>One routine step: number · type chip · − minutes + · up / down · delete.</summary>
    private sealed class IntervalRow : Grid
    {
        private const double RowHeight = 40;
        private readonly FocusTabViewModel _vm;
        private readonly TextBlock _number = new(), _chipText = new();
        private readonly Grid _chip = new();
        private readonly Ellipse _chipDot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        private readonly SolidColorBrush _chipFill = new(), _chipBorder = new(), _chipFg = new();
        private readonly TextBox _box = new();
        private readonly IconButton _minus, _plus, _up, _down, _delete;
        private int _index;
        private bool _updating;

        public IntervalRow(FocusTabViewModel vm)
        {
            _vm = vm;
            Height = RowHeight;
            Margin = new Thickness(0, 0, 0, Theme.S1 + 2);
            Background = Theme.CardBrush;
            BorderBrush = Theme.CardBorderBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(RowHeight / 2);
            Padding = new Thickness(Theme.S3, 0, Theme.S2, 0);
            Tag = SettingsWindow.NoDragTag;
            var hover = new Border { Background = Theme.CardHoverBrush, CornerRadius = new CornerRadius(RowHeight / 2), Margin = new Thickness(-Theme.S3, 0, -Theme.S2, 0) };
            Children.Add(hover);
            Motions.HoverFade(this, hover);

            var g = new Grid();
            foreach (var w in new[] { 22.0, double.NaN, -1, 26, 46, 26, 30, 24, 24, 26 })
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? GridLength.Auto : w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });

            _number.Style = SettingsUi.StyleOf("TimeText");
            _number.Foreground = Theme.TertiaryText;
            _number.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(_number);

            // Colour-coded type chip; click cycles Focus → Short break → Long break.
            _chip.Height = 24;
            _chip.CornerRadius = new CornerRadius(12);
            _chip.Background = _chipFill;
            _chip.BorderBrush = _chipBorder;
            _chip.BorderThickness = new Thickness(1);
            _chip.MinWidth = 104;
            _chip.VerticalAlignment = VerticalAlignment.Center;
            var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            _chipDot.Fill = _chipFg;
            chipRow.Children.Add(_chipDot);
            _chipText.Style = SettingsUi.StyleOf("CaptionText");
            _chipText.FontFamily = Theme.FontSemiBold;
            _chipText.FontWeight = FontWeights.SemiBold;
            _chipText.FontSize = Theme.FontCaption + 1;
            _chipText.Foreground = _chipFg;
            _chipText.Margin = new Thickness(6, 0, 0, 0);
            chipRow.Children.Add(_chipText);
            _chip.Children.Add(chipRow);
            ToolTipService.SetToolTip(_chip, "Click to change the type");
            Motions.PressScale(_chip);
            _chip.Tapped += (_, e) => { e.Handled = true; _vm.CycleKind(_index); };
            Grid.SetColumn(_chip, 1);
            g.Children.Add(_chip);

            _minus = Small(Icons.Minus, "1 minute less", 3, () => Step(-1));
            _plus = Small(Icons.Plus, "1 minute more", 5, () => Step(+1));
            g.Children.Add(_minus);
            g.Children.Add(_plus);

            // Minutes: digits only, committed on Enter or when focus leaves; invalid input is refused.
            _box.Height = 28;
            _box.MinHeight = 28;
            _box.Padding = new Thickness(0, 4, 0, 4);
            _box.FontFamily = Theme.FontSemiBold;
            _box.FontSize = Theme.FontBody;
            _box.TextAlignment = TextAlignment.Center;
            _box.VerticalAlignment = VerticalAlignment.Center;
            _box.MaxLength = 3;
            _box.Margin = new Thickness(2, 0, 2, 0);
            InputStyle.Apply(_box);
            _box.BeforeTextChanging += (_, e) => { if (e.NewText.Any(ch => !char.IsDigit(ch))) e.Cancel = true; };
            _box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Commit(); e.Handled = true; } };
            _box.LostFocus += (_, _) => Commit();
            ToolTipService.SetToolTip(_box, $"Minutes ({FocusRoutine.MinMinutes}–{FocusRoutine.MaxMinutes})");
            Grid.SetColumn(_box, 4);
            g.Children.Add(_box);

            var min = new TextBlock { Text = "min", Style = SettingsUi.StyleOf("CaptionText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(min, 6);
            g.Children.Add(min);

            _up = Small(Icons.ChevronUp, "Move up", 7, () => _vm.Move(_index, -1));
            _down = Small(Icons.ChevronDown, "Move down", 8, () => _vm.Move(_index, +1));
            _delete = Small(Icons.Close, "Delete", 9, () => _vm.Remove(_index));
            g.Children.Add(_up);
            g.Children.Add(_down);
            g.Children.Add(_delete);
            Children.Add(g);
        }

        private static IconButton Small(string icon, string tip, int column, Action click)
        {
            var b = new IconButton(24, Theme.IconSmall) { Icon = icon, Foreground = Theme.SecondaryText, Tooltip = tip, VerticalAlignment = VerticalAlignment.Center };
            b.Click += click;
            Grid.SetColumn(b, column);
            return b;
        }

        public void Show(FocusInterval item, int index, int count)
        {
            _index = index;
            _number.Text = (index + 1).ToString();
            var (a, b) = FocusRoutine.Colors(item.Kind);
            _chipFill.Color = GradientRing.WithAlpha(a, 0x26);
            _chipBorder.Color = GradientRing.WithAlpha(a, 0x55);
            _chipFg.Color = Theme.Lerp(b, Theme.PrimaryTextColor, 0.25);
            _chipText.Text = FocusRoutine.Name(item.Kind);
            _updating = true;
            _box.Text = item.Minutes.ToString();
            _updating = false;
            _minus.Enabled = item.Minutes > FocusRoutine.MinMinutes;
            _plus.Enabled = item.Minutes < FocusRoutine.MaxMinutes;
            _up.Enabled = index > 0;
            _down.Enabled = index < count - 1;
            _delete.Enabled = count > 1;
        }

        private void Step(int delta)
        {
            int current = int.TryParse(_box.Text, out var v) ? v : _vm.Items[_index].Minutes;
            _vm.SetMinutes(_index, current + delta);
        }

        private void Commit()
        {
            if (_updating || _index >= _vm.Items.Count) return;
            int? typed = int.TryParse(_box.Text, out var v) ? v : null;
            if (typed == _vm.Items[_index].Minutes) return;
            int kept = _vm.SetMinutes(_index, typed);
            _box.Text = kept.ToString(); // refused / clamped input shows the value that was kept
        }
    }
}

/// <summary>"General" tab: hide-on-fullscreen and a note about the unofficial usage endpoint.</summary>
internal sealed class GeneralTabView : Grid
{
    public GeneralTabView(GeneralTabViewModel vm)
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel();
        top.Children.Add(SettingsUi.Title("General"));
        top.Children.Add(SettingsUi.Subtitle("How the notch behaves around other apps."));
        var card = SettingsUi.Card(SettingsUi.ToggleRow("Hide notch when an app is fullscreen", "Videos, games and presentations. It comes back when they exit.",
            () => vm.HideWhenFullscreen, v => vm.HideWhenFullscreen = v, vm, nameof(GeneralTabViewModel.HideWhenFullscreen)));
        card.Margin = Theme.GapTopS3;
        top.Children.Add(card);
        Children.Add(top);
        var note = new TextBlock
        {
            Text = "Claude Usage reads an unofficial, undocumented endpoint (api.anthropic.com/api/oauth/usage) with Claude Code's sign-in. " +
                   "It may change or stop working at any time. The sign-in token is only read into memory and sent to api.anthropic.com.",
            Style = SettingsUi.StyleOf("CaptionText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
        };
        SetRow(note, 1);
        Children.Add(note);
    }
}
