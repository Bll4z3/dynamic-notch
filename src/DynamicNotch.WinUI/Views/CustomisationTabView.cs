using System.Globalization;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DynamicNotch.Views;

/// <summary>
/// Settings ▸ Customisation. A live <see cref="NotchPreview"/> pinned at the top; below it, scrolling cards
/// (Accent, Shape, Font, Sounds, Motion, Notch look, Collapsed notch, Behaviour, Performance, Looks) that
/// spring in with a short stagger whenever the tab opens; a pinned footer with Reset (confirm + Undo),
/// Export and Import. Every control writes through <see cref="ThemeService"/>, so the change is live in
/// the preview, the notch and the Settings window at once; when the values change from elsewhere (a Look,
/// Reset, Undo, Import) every control animates to the new value.
/// </summary>
internal sealed class CustomisationTabView : Grid
{
    private readonly CustomisationTabViewModel _vm;
    private readonly ThemeService _theme;
    private readonly StaggerGroup _stagger = new();
    private readonly List<Action<bool>> _refreshers = new(); // (animate) → re-read the value
    private readonly TextBlock _footerNote = new();
    private readonly StackPanel _footer = new() { Orientation = Orientation.Horizontal, Spacing = Theme.S2 };
    private readonly StackPanel _confirm = new() { Orientation = Orientation.Horizontal, Spacing = Theme.S2, Visibility = Visibility.Collapsed };
    private readonly PillButton _undo = new(Icons.Undo, "Undo") { Visibility = Visibility.Collapsed };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _undoTimer;
    private int _order;
    private bool _syncing;

    public CustomisationTabView(CustomisationTabViewModel vm)
    {
        _vm = vm;
        _theme = vm.Theme;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid();
        head.Children.Add(SettingsUi.Title("Customisation"));

        Children.Add(head);

        var preview = new NotchPreview(vm.Theme, vm.Sounds) { Margin = new Thickness(0, Theme.S2, Theme.S3, Theme.S1) };
        preview.ModeSwitch.HorizontalAlignment = HorizontalAlignment.Right;
        preview.ModeSwitch.VerticalAlignment = VerticalAlignment.Center;
        preview.ModeSwitch.Margin = new Thickness(0, 0, 40, 0); // clear of the close button
        head.Children.Add(preview.ModeSwitch);
        SetRow(preview, 1);
        Children.Add(preview);

        var stack = new StackPanel { Margin = new Thickness(0, 0, Theme.S3, Theme.S3) };
        AddSection(stack, "Accent", BuildAccent());
        AddSection(stack, "Shape", BuildShape());
        AddSection(stack, "Font", BuildFont());
        AddSection(stack, "Sounds", BuildSounds());
        AddSection(stack, "Motion", BuildMotion());
        AddSection(stack, "Notch look", BuildLook());
        AddSection(stack, "Collapsed notch", BuildCollapsed());
        AddSection(stack, "Behaviour", BuildBehaviour());
        AddSection(stack, "Performance", BuildPerformance());
        AddSection(stack, "Looks", BuildLooks());
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SetRow(scroll, 2);
        Children.Add(scroll);

        BuildFooter();
        var footerHost = new Grid { Margin = new Thickness(0, Theme.S2, Theme.S3, 0) };
        footerHost.Children.Add(_footer);
        footerHost.Children.Add(_confirm);
        _footerNote.Style = SettingsUi.StyleOf("CaptionText");
        _footerNote.HorizontalAlignment = HorizontalAlignment.Right;
        _footerNote.VerticalAlignment = VerticalAlignment.Center;
        footerHost.Children.Add(_footerNote);
        SetRow(footerHost, 3);
        Children.Add(footerHost);

        _undoTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _undoTimer.IsRepeating = false;
        _undoTimer.Interval = TimeSpan.FromMilliseconds(Motion.UndoWindowMs);
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); HideUndo(); _vm.ForgetUndo(); };

        _theme.Changed += _ => Sync(animate: true);
        vm.Fonts.FontChanged += _ => Sync(animate: true);
        // Cards spring in each time the tab is shown.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (Visibility == Visibility.Visible) Replay(); });
        Loaded += (_, _) => { Sync(animate: false); if (Visibility == Visibility.Visible) Replay(); };
    }

    private void Replay()
    {
        _stagger.Hide();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, _stagger.Show);
    }

    private void Sync(bool animate)
    {
        _syncing = true;
        try { foreach (var r in _refreshers) r(animate); }
        finally { _syncing = false; }
    }

    private void Update(Action<CustomSettings> change, bool continuous = false)
    {
        if (_syncing) return;
        _theme.Update(change, continuous);
    }

    // ---- Layout helpers ------------------------------------------------------------------------------------
    private void AddSection(StackPanel stack, string title, UIElement content)
    {
        var block = new StackPanel();
        block.Children.Add(new TextBlock { Text = title, Style = SettingsUi.StyleOf("SectionText"), Margin = new Thickness(Theme.S1, Theme.S4, 0, Theme.S2) });
        block.Children.Add(SettingsUi.Card(content, new Thickness(Theme.S3, Theme.S3, Theme.S3, Theme.S3)));
        stack.Children.Add(block);
        _stagger.Add(block, _order++);
    }

    private static Rectangle Divider() => new() { Height = 1, Fill = Theme.CardBorderBrush, Margin = new Thickness(0, Theme.S2, 0, Theme.S2) };

    private static TextBlock Label(string text, string? caption = null) => new()
    {
        Text = text, Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, Theme.S1),
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text, Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = Theme.GapTopS1,
    };

    /// <summary>A switch row kept in sync with a customisation value.</summary>
    private FrameworkElement Toggle(string title, string description, Func<CustomSettings, bool> get, Action<CustomSettings, bool> set, Action? demo = null)
    {
        var g = new Grid { MinHeight = 40 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
        if (description.Length > 0) text.Children.Add(new TextBlock { Text = description, Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = Theme.GapTopS1 });
        g.Children.Add(text);
        var sw = new PillSwitch { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3 };
        ToolTipService.SetToolTip(sw, title);
        sw.Toggled += on => { Update(s => set(s, on)); demo?.Invoke(); };
        Grid.SetColumn(sw, 1);
        g.Children.Add(sw);
        _refreshers.Add(_ => sw.IsOn = get(_theme.Current));
        return g;
    }

    private ValueSlider Slider(string label, double min, double max, double step, Func<double, string> format, Func<CustomSettings, double> get, Action<CustomSettings, double> set, string? caption = null, Action? demo = null)
    {
        var s = new ValueSlider(label, min, max, step, format, caption) { Margin = new Thickness(0, Theme.S1, 0, Theme.S1) };
        s.Changed += (v, final) => { Update(c => set(c, v), continuous: !final); if (final) demo?.Invoke(); };
        _refreshers.Add(animate => s.SetValue(get(_theme.Current), animate));
        return s;
    }

    private Segmented Choice(Func<CustomSettings, string> get, Action<CustomSettings, string> set, Action? demo, params (string id, string label)[] options)
    {
        var seg = new Segmented(options) { Margin = new Thickness(0, Theme.S1, 0, Theme.S1) };
        seg.Selected += v => { Update(s => set(s, v)); demo?.Invoke(); };
        _refreshers.Add(_ => seg.Value = get(_theme.Current));
        return seg;
    }

    // ---- a) Accent -----------------------------------------------------------------------------------------
    private UIElement BuildAccent()
    {
        var p = new StackPanel();
        var swatches = new SwatchGrid();
        var picker = new ColorPicker { Margin = Theme.GapTopS3, Visibility = Visibility.Collapsed };
        var contrast = new TextBlock { Style = SettingsUi.StyleOf("CaptionText"), Foreground = new SolidColorBrush(Theme.BatteryAmber), TextWrapping = TextWrapping.Wrap, Margin = Theme.GapTopS2, Visibility = Visibility.Collapsed };
        swatches.Picked += id =>
        {
            if (id == "custom")
            {
                picker.Visibility = Visibility.Visible;
                if (Anim.Enabled) Motions.PopIn(picker);
                Update(s => { s.AccentPreset = "custom"; s.MatchWindowsAccent = false; });
            }
            else Update(s => { s.AccentPreset = id; s.MatchWindowsAccent = false; });
            _theme.PlayDemo(ThemeService.Part.Accent);
        };
        picker.ColorChanged += (c, final) =>
        {
            Update(s =>
            {
                s.AccentPreset = "custom";
                s.CustomAccent = ColorUtil.ToHex(c);
                s.MatchWindowsAccent = false;
                if (final)
                {
                    s.RecentColors.RemoveAll(h => string.Equals(h, s.CustomAccent, StringComparison.OrdinalIgnoreCase));
                    s.RecentColors.Insert(0, s.CustomAccent);
                    if (s.RecentColors.Count > 8) s.RecentColors.RemoveRange(8, s.RecentColors.Count - 8);
                }
            }, continuous: !final);
            if (final) _theme.PlayDemo(ThemeService.Part.Accent);
        };
        p.Children.Add(swatches);
        p.Children.Add(picker);
        p.Children.Add(contrast);
        p.Children.Add(Divider());
        p.Children.Add(Toggle("Match Windows accent colour", "Uses your Windows accent and follows it when it changes",
            s => s.MatchWindowsAccent, (s, v) => s.MatchWindowsAccent = v, () => _theme.PlayDemo(ThemeService.Part.Accent)));
        p.Children.Add(Toggle("Follow album colour", "On the music page, the accent takes the song's colour (your colour otherwise)",
            s => s.FollowAlbumColor, (s, v) => s.FollowAlbumColor = v));
        _refreshers.Add(animate =>
        {
            var s = _theme.Current;
            swatches.Select(s.MatchWindowsAccent ? "" : s.AccentPreset, animate);
            if (s.AccentPreset == "custom" && ColorUtil.TryParse(s.CustomAccent, out var c)) picker.SetColor(c);
            picker.SetRecent(s.RecentColors);
            if (s.AccentPreset != "custom") picker.Visibility = Visibility.Collapsed;
            var chosen = _theme.ChosenAccent();
            bool low = ThemeService.IsLowContrast(chosen);
            contrast.Visibility = low ? Visibility.Visible : Visibility.Collapsed;
            contrast.Text = $"This colour is hard to see on the black notch (contrast {Design.Theme.Contrast(chosen, Design.Theme.NotchColor):0.0}:1). Text on it adapts automatically.";
        });
        return p;
    }

    // ---- b) Shape ------------------------------------------------------------------------------------------
    private UIElement BuildShape()
    {
        var p = new StackPanel();
        var corner = Slider("Notch corner radius", Design.Theme.MinCornerRadius, Design.Theme.MaxCornerRadius, 1, v => $"{v:0} px",
            s => s.CornerRadius, (s, v) => s.CornerRadius = v);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(corner);
        var reset = new PillButton(Icons.Retry, "Reset", quiet: true) { Height = Design.Theme.HubSmallPillHeight, VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3, Tooltip = $"Back to {Design.Theme.DefaultCornerRadius:0} px" };
        reset.Click += () => Update(s => { s.CornerRadius = Design.Theme.DefaultCornerRadius; s.TileRadius = Design.Theme.DefaultCornerRadiusSmall; });
        Grid.SetColumn(reset, 1);
        row.Children.Add(reset);
        p.Children.Add(row);
        p.Children.Add(Divider());
        var tile = Slider("Tile and card radius", Design.Theme.MinCornerRadiusSmall, Design.Theme.MaxCornerRadiusSmall, 1, v => $"{v:0} px",
            s => ThemeService.EffectiveTileRadius(s), (s, v) => { s.TileRadius = v; s.LinkTileRadius = false; });
        p.Children.Add(tile);
        p.Children.Add(Toggle("Link to corner radius", "Tiles, rows and cards follow the notch (about half its radius)",
            s => s.LinkTileRadius, (s, v) => { if (!v) s.TileRadius = ThemeService.EffectiveTileRadius(s); s.LinkTileRadius = v; }));
        _refreshers.Add(_ => tile.Opacity = _theme.Current.LinkTileRadius ? 0.55 : 1);
        p.Children.Add(Caption("Pills and switches always stay fully round (half their height), whatever the radius."));
        return p;
    }

    // ---- Font (moved from General) ---------------------------------------------------------------------------
    private UIElement BuildFont()
    {
        var p = new StackPanel();
        var picker = new GeneralTabView.FontPicker(_vm.Fonts);
        var sample = new GeneralTabView.SampleCard { Margin = Theme.GapTopS2 };
        picker.Hovered += f => sample.Show(f ?? _vm.Fonts.Current);
        picker.Selected += f => _vm.Fonts.Apply(f);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(picker);
        var reset = new PillButton(Icons.Retry, "Default", quiet: true) { Height = Design.Theme.HubSmallPillHeight, VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3, Tooltip = "Back to Inter" };
        reset.Click += _vm.Fonts.ResetToDefault;
        Grid.SetColumn(reset, 1);
        row.Children.Add(reset);
        p.Children.Add(row);
        p.Children.Add(sample);
        _refreshers.Add(_ => { picker.Refresh(); sample.Show(_vm.Fonts.Current); });
        return p;
    }

    // ---- c) Sounds -----------------------------------------------------------------------------------------
    private UIElement BuildSounds()
    {
        var p = new StackPanel();
        p.Children.Add(Toggle("Notch sounds", "Short, quiet sounds for notch events. Off while Windows is muted.", s => s.SoundsEnabled, (s, v) => s.SoundsEnabled = v));
        p.Children.Add(Slider("Volume", 0, 100, 5, v => $"{v:0}%", s => s.SoundVolume * 100, (s, v) => s.SoundVolume = v / 100,
            caption: "Notch sounds only (your music isn't affected)", demo: () => _vm.Sounds.Preview("notification")));
        p.Children.Add(Label("Sound preset"));
        var options = SoundPresets.Ids.Zip(SoundPresets.Names).Select(z => (z.First, z.Second)).ToArray();
        p.Children.Add(Choice(s => s.SoundPreset, (s, v) => s.SoundPreset = v, () => _vm.Sounds.Preview("notification"), options));

        // Per-event list, in an expandable card.
        var expander = new Grid { MinHeight = 36, Background = Theme.TransparentBrush, Margin = Theme.GapTopS2, Tag = SettingsWindow.NoDragTag };
        expander.Children.Add(new TextBlock { Text = "Events", Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, VerticalAlignment = VerticalAlignment.Center });
        var chevron = new VectorIcon { Data = Icons.ChevronDown, Size = Design.Theme.IconSmall, Foreground = Theme.SecondaryText, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        expander.Children.Add(chevron);
        var list = new StackPanel { Visibility = Visibility.Collapsed };
        foreach (var (id, name, _) in SoundEvents.All)
        {
            var row = new Grid { MinHeight = 34 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = name, Style = SettingsUi.StyleOf("BaseText"), VerticalAlignment = VerticalAlignment.Center });
            var play = new IconButton(26, Design.Theme.IconSmall) { Icon = Icons.Play, Foreground = Theme.SecondaryText, Tooltip = $"Play “{name}”", VerticalAlignment = VerticalAlignment.Center };
            string eid = id;
            play.Click += () => _vm.Sounds.Preview(eid);
            Grid.SetColumn(play, 1);
            row.Children.Add(play);
            var sw = new PillSwitch { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS2 };
            sw.Toggled += on => Update(s => s.SoundEvents[eid] = on);
            Grid.SetColumn(sw, 2);
            row.Children.Add(sw);
            _refreshers.Add(_ => sw.IsOn = _vm.Sounds.IsEventOn(eid));
            list.Children.Add(row);
        }
        expander.Tapped += (_, e) =>
        {
            e.Handled = true;
            bool open = list.Visibility != Visibility.Visible;
            list.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Data = open ? Icons.ChevronUp : Icons.ChevronDown;
            if (open && Anim.Enabled)
            {
                int k = 0;
                foreach (var r in list.Children.OfType<FrameworkElement>()) Motions.PopIn(r, Math.Min(k++, 8) * Motion.PopRowStaggerMs / 1000.0);
            }
        };
        p.Children.Add(expander);
        p.Children.Add(list);
        p.Children.Add(Caption("Notch expand / collapse are off by default because they happen often. The drag tick is limited to a few per second."));
        return p;
    }

    // ---- d) Motion -----------------------------------------------------------------------------------------
    private UIElement BuildMotion()
    {
        var p = new StackPanel();
        p.Children.Add(Label("Animation feel"));
        p.Children.Add(Choice(s => s.MotionFeel, (s, v) => s.MotionFeel = v, () => _theme.PlayDemo(ThemeService.Part.Motion),
            ("smooth", "Smooth"), ("snappy", "Snappy"), ("bouncy", "Bouncy"), ("calm", "Calm"), ("reduced", "Reduced")));
        p.Children.Add(Slider("Animation speed", 0.5, 1.5, 0.05, v => $"{v:0.00}x", s => s.MotionSpeed, (s, v) => s.MotionSpeed = v,
            demo: () => _theme.PlayDemo(ThemeService.Part.Motion)));
        var note = Caption("");
        _refreshers.Add(_ => note.Text = Anim.Enabled
            ? "Windows' “Animation effects” setting always wins: turn it off there and the notch stops animating."
            : "Windows' “Animation effects” is off, so the notch doesn't animate (this setting applies when it's on).");
        p.Children.Add(note);
        return p;
    }

    // ---- e) Notch look -------------------------------------------------------------------------------------
    private UIElement BuildLook()
    {
        var p = new StackPanel();
        p.Children.Add(Slider("Border highlight", 0, 20, 1, v => $"{v:0}%", s => s.Highlight * 100, (s, v) => s.Highlight = v / 100));
        p.Children.Add(Slider("Shadow", 0, 150, 5, v => $"{v:0}%", s => s.ShadowIntensity * 100, (s, v) => s.ShadowIntensity = v / 100));
        p.Children.Add(Divider());
        p.Children.Add(Toggle("Music glow", "The soft colour behind the album art", s => s.GlowEnabled, (s, v) => s.GlowEnabled = v, () => _theme.PlayDemo(ThemeService.Part.Glow)));
        p.Children.Add(Slider("Glow intensity", 0, 150, 5, v => $"{v:0}%", s => s.GlowIntensity * 100, (s, v) => s.GlowIntensity = v / 100, demo: () => _theme.PlayDemo(ThemeService.Part.Glow)));
        p.Children.Add(Label("Glow colour"));
        p.Children.Add(Choice(s => s.GlowSource, (s, v) => s.GlowSource = v, () => _theme.PlayDemo(ThemeService.Part.Glow), ("album", "Album colour"), ("accent", "Accent")));
        p.Children.Add(Divider());
        p.Children.Add(Label("Visualiser"));
        p.Children.Add(Choice(s => s.Visualizer, (s, v) => s.Visualizer = v, () => _theme.PlayDemo(ThemeService.Part.Collapsed), ("bars", "Bars"), ("wave", "Wave"), ("off", "Off")));
        p.Children.Add(Divider());
        p.Children.Add(Label("Notch size"));
        p.Children.Add(Choice(s => s.NotchScale switch { 0.9 => "compact", 1.0 => "default", 1.15 => "large", _ => "custom" },
            (s, v) => s.NotchScale = v switch { "compact" => 0.9, "large" => 1.15, _ => 1.0 }, null,
            ("compact", "Compact"), ("default", "Default"), ("large", "Large")));
        p.Children.Add(Slider("Scale", 0.85, 1.25, 0.01, v => $"{v:0.00}x", s => s.NotchScale, (s, v) => s.NotchScale = Math.Round(v, 2)));
        return p;
    }

    // ---- f) Collapsed notch --------------------------------------------------------------------------------
    private UIElement BuildCollapsed()
    {
        var p = new StackPanel();
        Action demo = () => _theme.PlayDemo(ThemeService.Part.Collapsed);
        p.Children.Add(Toggle("Battery ring", "", s => s.ShowBatteryRing, (s, v) => s.ShowBatteryRing = v, demo));
        p.Children.Add(Toggle("Clock", "", s => s.ShowClock, (s, v) => s.ShowClock = v, demo));
        p.Children.Add(Toggle("Date", "Shows the short date next to the time", s => s.ShowDate, (s, v) => s.ShowDate = v, demo));
        p.Children.Add(Label("Clock format"));
        p.Children.Add(Choice(s => s.ClockFormat, (s, v) => s.ClockFormat = v, demo, ("system", "Follow system"), ("12", "12-hour"), ("24", "24-hour")));
        return p;
    }

    // ---- g) Behaviour --------------------------------------------------------------------------------------
    private UIElement BuildBehaviour()
    {
        var p = new StackPanel();
        p.Children.Add(Slider("Hover to expand", 0, 600, 10, v => $"{v:0} ms", s => s.HoverDelayMs, (s, v) => s.HoverDelayMs = (int)v, "How long the pointer rests on the notch before it opens"));
        p.Children.Add(Slider("Collapse after leaving", 150, 2000, 50, v => $"{v:0} ms", s => s.CollapseDelayMs, (s, v) => s.CollapseDelayMs = (int)v));
        p.Children.Add(Slider("Distance from the top", 0, 40, 1, v => $"{v:0} px", s => s.TopOffset, (s, v) => s.TopOffset = v, "0 keeps the notch flush with the screen edge"));
        p.Children.Add(Label("Monitor"));
        p.Children.Add(Choice(s => s.Monitor, (s, v) => s.Monitor = v, null, ("primary", "Primary"), ("mouse", "Under the mouse")));
        p.Children.Add(Caption("“Under the mouse” picks the monitor when the notch starts and whenever displays change."));
        return p;
    }

    // ---- Performance ---------------------------------------------------------------------------------------
    private UIElement BuildPerformance()
    {
        var p = new StackPanel();
        p.Children.Add(Label("Performance mode"));
        p.Children.Add(Choice(s => s.PerformanceMode, (s, v) => s.PerformanceMode = v, null, ("auto", "Automatic"), ("standard", "Standard"), ("low", "Low power")));
        p.Children.Add(Caption("Low power slows the visualiser, stops the glow breathing and shortens staggers. Automatic switches to it on battery saver and in Remote Desktop."));
        return p;
    }

    // ---- h) Looks ------------------------------------------------------------------------------------------
    private UIElement BuildLooks()
    {
        var p = new StackPanel();
        var row = new Grid();
        for (int i = 0; i < BuiltInLooks.All.Length; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < BuiltInLooks.All.Length; i++)
        {
            var look = BuiltInLooks.All[i];
            var card = LookCard(look.Name, look.Description, look.Values);
            card.Margin = new Thickness(i == 0 ? 0 : Theme.S1, 0, i == BuiltInLooks.All.Length - 1 ? 0 : Theme.S1, 0);
            Grid.SetColumn(card, i);
            row.Children.Add(card);
        }
        p.Children.Add(row);

        var saved = new StackPanel { Margin = Theme.GapTopS3 };
        p.Children.Add(saved);
        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S2, Margin = Theme.GapTopS2 };
        var name = new TextBox { PlaceholderText = "Name this look", Width = 200, FontFamily = Theme.Font, FontSize = Design.Theme.FontBody };
        InputStyle.Apply(name);
        var save = new PillButton(Icons.Plus, "Save current look", quiet: true);
        save.Click += () => { _vm.SaveLook(name.Text); name.Text = ""; };
        saveRow.Children.Add(name);
        saveRow.Children.Add(save);
        p.Children.Add(saveRow);

        _refreshers.Add(_ =>
        {
            saved.Children.Clear();
            foreach (var look in _theme.Current.SavedLooks.ToList())
            {
                var r = new Grid { MinHeight = 36 };
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.Children.Add(Dot(look.Values));
                var title = new TextBox { Text = look.Name, FontFamily = Theme.Font, FontSize = Design.Theme.FontBody, Margin = Theme.GapLeftS2, BorderThickness = new Thickness(0), Background = Theme.TransparentBrush };
                ToolTipService.SetToolTip(title, "Click to rename");
                title.LostFocus += (_, _) => { if (title.Text != look.Name) _vm.RenameLook(look, title.Text); };
                Grid.SetColumn(title, 1);
                r.Children.Add(title);
                var apply = new PillButton(null, "Apply", quiet: true) { Height = Design.Theme.HubSmallPillHeight, VerticalAlignment = VerticalAlignment.Center };
                apply.Click += () => _vm.ApplyLook(look.Values);
                Grid.SetColumn(apply, 2);
                r.Children.Add(apply);
                var del = new IconButton(26, Design.Theme.IconSmall) { Icon = Icons.Delete, Foreground = Theme.SecondaryText, Tooltip = "Delete this look", Margin = Theme.GapLeftS1, VerticalAlignment = VerticalAlignment.Center };
                del.Click += () => _vm.DeleteLook(look);
                Grid.SetColumn(del, 4);
                r.Children.Add(del);
                saved.Children.Add(r);
            }
        });
        return p;
    }

    private FrameworkElement LookCard(string name, string description, LookValues values)
    {
        var card = new Grid { Background = Theme.TransparentBrush, Tag = SettingsWindow.NoDragTag, MinHeight = 74 };
        var back = new Border { Background = Theme.TileBrush, BorderBrush = Theme.TileBorderBrush, BorderThickness = new Thickness(1), CornerRadius = Design.Theme.RadiusSmall };
        var hover = new Border { Background = Theme.PillLightenBrush, CornerRadius = Design.Theme.RadiusSmall };
        card.Children.Add(back);
        card.Children.Add(hover);
        var s = new StackPanel { Margin = new Thickness(Theme.S2 + 2, Theme.S2, Theme.S2, Theme.S2), Spacing = 2 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        top.Children.Add(Dot(values));
        // A tiny corner sample: this look's radius on a mini card.
        top.Children.Add(new Border { Width = 22, Height = 14, BorderBrush = Theme.SecondaryText, BorderThickness = new Thickness(0, 0, 1.5, 1.5), CornerRadius = new CornerRadius(0, 0, Math.Min(10, values.CornerRadius / 3), 0), VerticalAlignment = VerticalAlignment.Center });
        s.Children.Add(top);
        s.Children.Add(new TextBlock { Text = name, Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
        s.Children.Add(new TextBlock { Text = description, Style = SettingsUi.StyleOf("CaptionText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        card.Children.Add(s);
        Motions.HoverFade(card, hover);
        Motions.PressScale(card);
        ToolTipService.SetToolTip(card, $"Apply the {name} look");
        card.Tapped += (_, e) => { e.Handled = true; _vm.ApplyLook(values); _theme.PlayDemo(ThemeService.Part.Accent); };
        return card;
    }

    private static FrameworkElement Dot(LookValues v)
    {
        var color = v.AccentPreset == "custom" && ColorUtil.TryParse(v.CustomAccent, out var c) ? c
            : AccentPresets.All.FirstOrDefault(a => a.Id == v.AccentPreset)?.Color ?? Design.Theme.DefaultAccent;
        return new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
    }

    // ---- i) Footer -----------------------------------------------------------------------------------------
    private void BuildFooter()
    {
        var reset = new PillButton(Icons.Retry, "Reset customisation", quiet: true);
        var export = new PillButton(Icons.Upload, "Export settings", quiet: true);
        var import = new PillButton(Icons.Download, "Import settings", quiet: true);
        reset.Click += () => { _footer.Visibility = Visibility.Collapsed; _confirm.Visibility = Visibility.Visible; if (Anim.Enabled) Motions.PopIn(_confirm); };
        export.Click += async () => await ExportAsync();
        import.Click += async () => await ImportAsync();
        _footer.Children.Add(reset);
        _footer.Children.Add(export);
        _footer.Children.Add(import);
        _undo.Click += () => { _vm.Undo(); HideUndo(); Note("Restored"); };
        _footer.Children.Add(_undo);

        _confirm.Children.Add(new TextBlock { Text = "Reset all customisation (your saved Looks stay)?", Style = SettingsUi.StyleOf("BaseText"), VerticalAlignment = VerticalAlignment.Center });
        var yes = new PillButton(Icons.Check, "Reset");
        var no = new PillButton(null, "Cancel", quiet: true);
        yes.Click += () =>
        {
            _vm.Reset();
            CloseConfirm();
            _undo.Visibility = Visibility.Visible;
            if (Anim.Enabled) Motions.PopIn(_undo);
            _undoTimer.Stop();
            _undoTimer.Start();
            Note("Customisation reset");
        };
        no.Click += CloseConfirm;
        _confirm.Children.Add(yes);
        _confirm.Children.Add(no);
    }

    private void CloseConfirm()
    {
        _confirm.Visibility = Visibility.Collapsed;
        _footer.Visibility = Visibility.Visible;
    }

    private void HideUndo() => _undo.Visibility = Visibility.Collapsed;

    private void Note(string text) => _footerNote.Text = text;

    private async Task ExportAsync()
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "DynamicNotch-settings", SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("Dynamic Notch settings", new List<string> { ".json" });
            InitializeWithWindow.Initialize(picker, SettingsWindow.CurrentHandle);
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            await Windows.Storage.FileIO.WriteTextAsync(file, _vm.ExportJson());
            Note($"Exported to {file.Name}");
        }
        catch (Exception ex) { Note($"Couldn't export: {ex.Message}"); }
    }

    private async Task ImportAsync()
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            InitializeWithWindow.Initialize(picker, SettingsWindow.CurrentHandle);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            var props = await file.GetBasicPropertiesAsync();
            if (props.Size > 256 * 1024) { Note("That file is too big to be a settings file."); return; }
            var error = _vm.ImportJson(await Windows.Storage.FileIO.ReadTextAsync(file));
            if (error != null) { Note(error); return; }
            Note($"Imported {file.Name}");
            _undo.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
        }
        catch (Exception ex) { Note($"Couldn't import: {ex.Message}"); }
    }
}
