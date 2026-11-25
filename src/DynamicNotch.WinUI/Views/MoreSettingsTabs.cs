using System.ComponentModel;
using DynamicNotch.Design;
using DynamicNotch.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace DynamicNotch.Views;

/// <summary>"Pills" tab: one switch per kind of pill, in two cards (activity pills, alerts). The list scrolls.</summary>
internal sealed class PillsTabView : Grid
{
    public PillsTabView(PillsTabViewModel vm)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(SettingsUi.Title("Pills"));
        var sub = SettingsUi.Subtitle("Choose which pills the notch shows.");
        SetRow(sub, 1);
        Children.Add(sub);

        var stack = new StackPanel { Margin = new Thickness(0, 0, Theme.S3, 0) };
        stack.Children.Add(Section("Activity"));
        stack.Children.Add(Card(vm,
            ("Volume", "When the volume or mute changes", () => vm.Volume, v => vm.Volume = v, nameof(vm.Volume)),
            ("Brightness", "Laptop screens only", () => vm.Brightness, v => vm.Brightness = v, nameof(vm.Brightness)),
            ("Caps Lock", "When Caps Lock turns on or off", () => vm.CapsLock, v => vm.CapsLock = v, nameof(vm.CapsLock)),
            ("Charging and battery", "Plugged in, unplugged, low battery", () => vm.Battery, v => vm.Battery = v, nameof(vm.Battery)),
            ("Downloads", "Progress of downloads in your Downloads folder", () => vm.Downloads, v => vm.Downloads = v, nameof(vm.Downloads)),
            ("Next song", "The cover, title and artist when the song changes", () => vm.NextSong, v => vm.NextSong = v, nameof(vm.NextSong))));
        stack.Children.Add(Section("Alerts"));
        stack.Children.Add(Card(vm,
            ("App notifications", "Mirrors Windows notifications (MSIX build)", () => vm.AppNotifications, v => vm.AppNotifications = v, nameof(vm.AppNotifications)),
            ("Devices", "Headphones and other Bluetooth devices", () => vm.Devices, v => vm.Devices = v, nameof(vm.Devices)),
            ("Wi-Fi and VPN", "Connected, disconnected, changed", () => vm.Network, v => vm.Network = v, nameof(vm.Network))));
        var scroll = new ScrollViewer { Content = stack, Margin = Theme.GapTopS2, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(scroll, 2);
        Children.Add(scroll);
    }

    private static TextBlock Section(string text) => new() { Text = text, Style = SettingsUi.StyleOf("SectionText"), Margin = new Thickness(Theme.S1, Theme.S3, 0, Theme.S2) };

    private static Border Card(INotifyPropertyChanged vm, params (string title, string desc, Func<bool> get, Action<bool> set, string prop)[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Rectangle { Height = 1, Fill = Theme.CardBorderBrush, Margin = new Thickness(0, Theme.S1, 0, Theme.S1) });
            var r = rows[i];
            stack.Children.Add(SettingsUi.ToggleRow(r.title, r.desc, r.get, r.set, vm, r.prop));
        }
        return SettingsUi.Card(stack);
    }
}

/// <summary>"Updates" tab: current version, automatic checks, Check now, the result and Download / Restart.</summary>
internal sealed class UpdatesTabView : Grid
{
    public UpdatesTabView(UpdatesTabViewModel vm)
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Right margin: the cards line up with every other tab's (which leave room for a scroll bar).
        var top = new StackPanel { Margin = new Thickness(0, 0, Theme.S3, 0) };
        top.Children.Add(SettingsUi.Title("Updates"));
        top.Children.Add(SettingsUi.Subtitle(vm.VersionText));

        var auto = SettingsUi.Card(SettingsUi.ToggleRow("Check for updates automatically", "Once a day, from GitHub. No personal data is sent.",
            () => vm.AutoCheck, v => vm.AutoCheck = v, vm, nameof(UpdatesTabViewModel.AutoCheck)));
        auto.Margin = Theme.GapTopS3;
        top.Children.Add(auto);

        var status = new TextBlock { Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        var checkedText = new TextBlock { Style = SettingsUi.StyleOf("CaptionText"), Margin = Theme.GapTopS1 };
        var progress = new LevelTrack { Margin = Theme.GapTopS2, FillBrush = Theme.AccentBrush, Visibility = Visibility.Collapsed };
        var statusStack = new StackPanel();
        statusStack.Children.Add(status);
        statusStack.Children.Add(checkedText);
        statusStack.Children.Add(progress);
        var statusCard = SettingsUi.Card(statusStack);
        statusCard.Margin = Theme.GapTopS3;
        top.Children.Add(statusCard);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = Theme.GapTopS3 };
        var check = new PillButton(Icons.Retry, "Check now", quiet: true);
        check.Click += async () => await vm.CheckAsync();
        var get = new PillButton(Icons.Download, vm.ActionText) { Margin = Theme.GapLeftS2 };
        get.Click += async () => await vm.GetAsync();
        var page = new PillButton(Icons.Link, "Release notes", quiet: true) { Margin = Theme.GapLeftS2 };
        page.Click += vm.OpenPage;
        actions.Children.Add(check);
        actions.Children.Add(get);
        actions.Children.Add(page);
        top.Children.Add(actions);
        Children.Add(top);

        var note = new TextBlock
        {
            Text = "Updates install in place for the zip download: the notch closes, the new files are copied in and it starts again. " +
                   "Your settings and API key are kept.",
            Style = SettingsUi.StyleOf("CaptionText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
        };
        SetRow(note, 1);
        Children.Add(note);

        void Refresh()
        {
            status.Text = vm.StatusText;
            checkedText.Text = vm.LastCheckedText;
            get.SetContent(Icons.Download, vm.ActionText);
            get.Visibility = vm.CanGet ? Visibility.Visible : Visibility.Collapsed;
            check.Active = !vm.IsBusy;
            progress.Visibility = vm.Progress > 0 && vm.Progress < 1 ? Visibility.Visible : Visibility.Collapsed;
            progress.SetValue(vm.Progress);
        }
        vm.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }
}
