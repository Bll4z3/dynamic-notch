using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.Services.Palette;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>Settings ▸ Command Palette: the view model is just the settings it edits.</summary>
public sealed class PaletteTabViewModel
{
    public PaletteTabViewModel(SettingsService settings) => Settings = settings;
    public SettingsService Settings { get; }
}

/// <summary>
/// Settings ▸ Command Palette: the global hotkey (click Change, press the new keys), a switch per palette
/// function, and your own shortcuts (keyword · name · what it opens), which you can add, edit and delete.
/// </summary>
internal sealed class PaletteTabView : Grid
{
    private readonly SettingsService _settings;
    private readonly TextBlock _hotkeyText = new(), _hotkeyWarn = new();
    private readonly PillButton _change;
    private readonly StackPanel _shortcutList = new();
    private readonly TextBox _keyword = new(), _name = new(), _target = new();
    private readonly Segmented _kind;
    private readonly PillButton _add;
    private bool _recording;
    private PaletteShortcut? _editing;

    public PaletteTabView(PaletteTabViewModel vm)
    {
        _settings = vm.Settings;
        var stack = new StackPanel { Margin = new Thickness(0, 0, Theme.S3, Theme.S2) };
        stack.Children.Add(SettingsUi.Title("Command Palette"));
        stack.Children.Add(SettingsUi.Subtitle("Search apps and files, do maths and conversions, run commands and more, right from the notch."));

        // ---- Hotkey ----
        stack.Children.Add(Section("Hotkey"));
        var hk = new Grid { MinHeight = 44 };
        hk.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hk.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hk.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hkText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hkText.Children.Add(new TextBlock { Text = "Open the palette from anywhere", Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
        _hotkeyWarn.Style = SettingsUi.StyleOf("CaptionText");
        _hotkeyWarn.Foreground = Theme.DangerBrush;
        _hotkeyWarn.TextWrapping = TextWrapping.Wrap;
        _hotkeyWarn.TextTrimming = TextTrimming.None;
        _hotkeyWarn.Margin = Theme.GapTopS1;
        hkText.Children.Add(_hotkeyWarn);
        hk.Children.Add(hkText);
        var chip = new Border
        {
            Background = Theme.InputBrush, BorderBrush = Theme.CardBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3, Child = _hotkeyText,
        };
        _hotkeyText.FontFamily = Theme.MonoFont;
        _hotkeyText.FontSize = Theme.FontCaption + 1;
        _hotkeyText.Foreground = Theme.PrimaryText;
        Grid.SetColumn(chip, 1);
        hk.Children.Add(chip);
        _change = new PillButton(null, "Change", quiet: true) { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS2 };
        _change.Click += StartRecording;
        Grid.SetColumn(_change, 2);
        hk.Children.Add(_change);
        stack.Children.Add(SettingsUi.Card(hk));
        ShowHotkey();

        // ---- Functions ----
        stack.Children.Add(Section("What it can do"));
        var fns = new StackPanel();
        bool first = true;
        foreach (var f in PaletteFunctions.All)
        {
            if (!first) fns.Children.Add(Divider());
            first = false;
            fns.Children.Add(FunctionRow(f));
        }
        stack.Children.Add(SettingsUi.Card(fns));

        // ---- Custom shortcuts ----
        stack.Children.Add(Section("Your shortcuts"));
        var sc = new StackPanel();
        sc.Children.Add(_shortcutList);
        sc.Children.Add(new TextBlock { Text = "Add a shortcut", Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, Theme.S3, 0, Theme.S2) });
        var fields = new Grid { ColumnSpacing = Theme.S2 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        Field(_keyword, "Keyword, e.g. mail");
        Field(_name, "Name, e.g. Open Gmail");
        Grid.SetColumn(_name, 1);
        fields.Children.Add(_keyword);
        fields.Children.Add(_name);
        sc.Children.Add(fields);
        _kind = new Segmented(("Url", "Website"), ("App", "App"), ("File", "File"), ("Folder", "Folder"), ("Command", "Command")) { Margin = Theme.GapTopS2 };
        _kind.Value = "Url";
        _kind.Selected += _ => UpdateTargetHint();
        sc.Children.Add(_kind);
        var targetRow = new Grid { Margin = Theme.GapTopS2, ColumnSpacing = Theme.S2 };
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Field(_target, "");
        targetRow.Children.Add(_target);
        var browse = new PillButton(Icons.Folder, "Browse", quiet: true) { VerticalAlignment = VerticalAlignment.Center };
        browse.Click += Browse;
        Grid.SetColumn(browse, 1);
        targetRow.Children.Add(browse);
        _add = new PillButton(Icons.Plus, "Add") { VerticalAlignment = VerticalAlignment.Center };
        _add.Click += AddOrSave;
        Grid.SetColumn(_add, 2);
        targetRow.Children.Add(_add);
        sc.Children.Add(targetRow);
        stack.Children.Add(SettingsUi.Card(sc));
        UpdateTargetHint();
        RefreshShortcuts();

        Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        KeyDown += OnKey;
    }

    private static TextBlock Section(string text) => new() { Text = text, Style = SettingsUi.StyleOf("SectionText"), Margin = new Thickness(Theme.S1, Theme.S4, 0, Theme.S2) };
    private static Rectangle Divider() => new() { Height = 1, Fill = Theme.CardBorderBrush, Margin = new Thickness(0, Theme.S1, 0, Theme.S1) };

    private static void Field(TextBox box, string placeholder)
    {
        box.PlaceholderText = placeholder;
        box.FontFamily = Theme.Font;
        box.FontSize = Theme.FontBody;
        box.Height = Theme.SettingsFieldHeight;
        box.Padding = Theme.SettingsFieldPadding;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        InputStyle.Apply(box);
    }

    private FrameworkElement FunctionRow(PaletteFunction f)
    {
        var g = new Grid { MinHeight = 44 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new VectorIcon { Data = f.Icon, Size = Theme.IconSmall, Foreground = Theme.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Theme.S3, 0) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = f.Name, Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = $"{f.Description} · e.g. {f.Example}", Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var sw = new PillSwitch { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3, IsOn = !_settings.Current.Palette.Disabled.Contains(f.Id) };
        ToolTipService.SetToolTip(sw, f.Name);
        sw.Toggled += on => _settings.Update(s => { s.Palette.Disabled.Remove(f.Id); if (!on) s.Palette.Disabled.Add(f.Id); });
        Grid.SetColumn(sw, 2);
        g.Children.Add(sw);
        return g;
    }

    // ---- Hotkey recording ------------------------------------------------------------------------------
    private void ShowHotkey()
    {
        var p = _settings.Current.Palette;
        _hotkeyText.Text = _recording ? "Press keys…" : HotkeyLabel(p.HotkeyModifiers, p.HotkeyKey);
        _hotkeyWarn.Text = !_recording && !NotchWindow.PaletteHotkeyAvailable ? "Another app is using this shortcut, so it won't open the palette. Pick a different one." : "";
        _hotkeyWarn.Visibility = _hotkeyWarn.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _change.SetContent(null!, _recording ? "Cancel" : "Change");
    }

    public static string HotkeyLabel(uint mods, uint key)
    {
        if (key == 0) return "Off";
        var parts = new List<string>();
        if ((mods & 0x2) != 0) parts.Add("Ctrl");
        if ((mods & 0x1) != 0) parts.Add("Alt");
        if ((mods & 0x4) != 0) parts.Add("Shift");
        if ((mods & 0x8) != 0) parts.Add("Win");
        parts.Add(((VirtualKey)key) switch
        {
            VirtualKey.Space => "Space", VirtualKey.Enter => "Enter",
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)key).ToString(),
            >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString(),
            var k => k.ToString(),
        });
        return string.Join("+", parts);
    }

    private void StartRecording()
    {
        _recording = !_recording;
        ShowHotkey();
        if (_recording) Focus(FocusState.Programmatic);
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key;
        if (key == VirtualKey.Escape) { _recording = false; ShowHotkey(); return; }
        if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftShift or VirtualKey.RightShift) return;
        bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        uint mods = (Down(VirtualKey.Menu) ? 0x1u : 0) | (Down(VirtualKey.Control) ? 0x2u : 0) | (Down(VirtualKey.Shift) ? 0x4u : 0);
        if ((mods & 0x3) == 0) { _hotkeyWarn.Text = "Use Ctrl or Alt with a key (for example Alt+Space or Ctrl+Shift+K)."; _hotkeyWarn.Visibility = Visibility.Visible; return; }
        _recording = false;
        _settings.Update(s => { s.Palette.HotkeyModifiers = mods; s.Palette.HotkeyKey = (uint)key; });
        // The window registers it on the settings change; show whether that worked a moment later.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ShowHotkey);
    }

    // ---- Shortcuts ----------------------------------------------------------------------------------------
    private void RefreshShortcuts()
    {
        _shortcutList.Children.Clear();
        var list = _settings.Current.Palette.Shortcuts;
        if (list.Count == 0)
        {
            _shortcutList.Children.Add(new TextBlock { Text = "No shortcuts yet. Make one below: type its keyword in the palette and press Enter.", Style = SettingsUi.StyleOf("CaptionText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
            return;
        }
        foreach (var s in list)
        {
            var item = s;
            var g = new Grid { MinHeight = 40 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Border
            {
                Background = Theme.InputBrush, BorderBrush = Theme.CardBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Theme.S3, 0),
                Child = new TextBlock { Text = s.Keyword, FontFamily = Theme.MonoFont, FontSize = Theme.FontCaption + 1, Foreground = Theme.PrimaryText },
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(s.Name) ? s.Keyword : s.Name, Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = $"{s.Kind} · {s.Target}", Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var edit = new IconButton(28, Theme.IconSmall) { Icon = Icons.Pen, Foreground = Theme.SecondaryText, Tooltip = "Edit", VerticalAlignment = VerticalAlignment.Center };
            edit.Click += () => Edit(item);
            Grid.SetColumn(edit, 2);
            g.Children.Add(edit);
            var del = new IconButton(28, Theme.IconSmall) { Icon = Icons.Delete, Foreground = Theme.SecondaryText, Tooltip = "Delete", VerticalAlignment = VerticalAlignment.Center };
            del.Click += () => { _settings.Update(st => st.Palette.Shortcuts.Remove(item)); if (_editing == item) ClearForm(); RefreshShortcuts(); };
            Grid.SetColumn(del, 3);
            g.Children.Add(del);
            _shortcutList.Children.Add(g);
        }
    }

    private void Edit(PaletteShortcut s)
    {
        _editing = s;
        _keyword.Text = s.Keyword;
        _name.Text = s.Name;
        _kind.Value = s.Kind.ToString();
        _target.Text = s.Target;
        _add.SetContent(Icons.Check, "Save");
        UpdateTargetHint();
    }

    private void ClearForm()
    {
        _editing = null;
        _keyword.Text = _name.Text = _target.Text = "";
        _add.SetContent(Icons.Plus, "Add");
    }

    private void AddOrSave()
    {
        string keyword = _keyword.Text.Trim(), target = _target.Text.Trim();
        if (keyword.Length == 0 || target.Length == 0) return;
        var kind = Enum.TryParse<ShortcutKind>(_kind.Value, out var k) ? k : ShortcutKind.Url;
        _settings.Update(s =>
        {
            var item = _editing != null ? s.Palette.Shortcuts.FirstOrDefault(x => ReferenceEquals(x, _editing)) : null;
            if (item == null) { item = new PaletteShortcut(); s.Palette.Shortcuts.Add(item); }
            item.Keyword = keyword;
            item.Name = _name.Text.Trim();
            item.Kind = kind;
            item.Target = target;
        });
        ClearForm();
        RefreshShortcuts();
    }

    private void UpdateTargetHint() => _target.PlaceholderText = _kind.Value switch
    {
        "App" => "The app (.exe or shortcut) to open",
        "File" => "The file to open",
        "Folder" => "The folder to open",
        "Command" => "A command to run, e.g. code C:\\Projects",
        _ => "Web address, e.g. mail.google.com",
    };

    private async void Browse()
    {
        try
        {
            if (_kind.Value == "Folder")
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, SettingsWindow.CurrentHandle);
                if (await picker.PickSingleFolderAsync() is { } folder) _target.Text = folder.Path;
            }
            else
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, SettingsWindow.CurrentHandle);
                if (await picker.PickSingleFileAsync() is { } file)
                {
                    _target.Text = file.Path;
                    if (_kind.Value is "Url" or "Command") _kind.Value = "File";
                }
            }
        }
        catch { }
    }
}
