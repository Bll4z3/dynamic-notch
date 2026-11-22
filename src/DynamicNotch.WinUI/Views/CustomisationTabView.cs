using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DynamicNotch.Views;

/// <summary>
/// Settings ▸ Customisation. A live <see cref="NotchPreview"/> pinned at the top; below it, plain sections
/// (Colour, Shape, Font, Notch, Closed notch, Motion, Behaviour, Performance), each a small heading over one
/// card. Every option is one row: a short label (plus a one-line description where it isn't obvious) on the
/// left and its control on the right, all controls in one aligned column. When the window is too narrow the
/// control moves under its label. A pinned footer holds Reset (confirm + Undo), Export and Import.
/// Every control writes through <see cref="ThemeService"/>, so a change is live in the preview, the notch
/// and the Settings window at once; changes from elsewhere (reset, undo, import) animate every control.
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

        var preview = new NotchPreview(vm.Theme) { Margin = new Thickness(0, Theme.S2, Theme.S3, Theme.S1) };
        preview.ModeSwitch.HorizontalAlignment = HorizontalAlignment.Right;
        preview.ModeSwitch.VerticalAlignment = VerticalAlignment.Center;
        preview.ModeSwitch.Margin = new Thickness(0, 0, 40, 0); // clear of the close button
        head.Children.Add(preview.ModeSwitch);
        SetRow(preview, 1);
        Children.Add(preview);

        var stack = new StackPanel { Margin = new Thickness(0, 0, Theme.S3, Theme.S3) };
        AddSection(stack, "Colour", BuildColour());
        AddSection(stack, "Shape", BuildShape());
        AddSection(stack, "Font", BuildFont());
        AddSection(stack, "Notch", BuildNotch());
        AddSection(stack, "Closed notch", BuildClosedNotch());
        AddSection(stack, "Motion", BuildMotion());
        AddSection(stack, "Behaviour", BuildBehaviour());
        AddSection(stack, "Performance", BuildPerformance());
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
        // Sections spring in each time the tab is shown.
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

    // ---- Layout ----------------------------------------------------------------------------------------------
    /// <summary>A section: its heading, then its rows in one card (hairlines between rows).</summary>
    private void AddSection(StackPanel stack, string title, IEnumerable<UIElement> rows)
    {
        var block = new StackPanel();
        var header = SettingsUi.SectionHeader(title);
        if (_order == 0) header.Margin = new Thickness(Theme.S1, Theme.S3, 0, Theme.S2);
        block.Children.Add(header);
        var list = new StackPanel();
        foreach (var row in rows)
        {
            // Rows that only extend the one above (the custom colour picker, a warning, the font sample) get no hairline.
            if (list.Children.Count > 0 && row is not FrameworkElement { Tag: Extension }) list.Children.Add(SettingsUi.RowDivider());
            list.Children.Add(row);
        }
        block.Children.Add(SettingsUi.Card(list));
        stack.Children.Add(block);
        _stagger.Add(block, _order++);
    }

    /// <summary>Marks an element that belongs to the row above it (no divider in between).</summary>
    private const string Extension = "customisation:extension";

    /// <summary>
    /// One setting: label (and optional one-line description) on the left, the control on the right in a column
    /// of fixed width so every control lines up. Narrower than that, the control drops under the label.
    /// <paramref name="natural"/> keeps the control at its own size (switches) instead of the column width.
    /// </summary>
    private static Grid Row(string title, string? description, FrameworkElement control, bool natural = false)
    {
        var g = new Grid { MinHeight = Theme.SettingsRowMinHeight, Padding = new Thickness(0, Theme.S1, 0, Theme.S1), Background = Theme.TransparentBrush };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        if (!string.IsNullOrEmpty(description))
            text.Children.Add(new TextBlock { Text = description, Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = Theme.GapTopS1 });
        g.Children.Add(text);

        control.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(control);
        bool? stacked = null;
        void Place(double width)
        {
            bool stack = !natural && width > 0 && width < Theme.SettingsControlWidth + Theme.SettingsLabelMinWidth + Theme.S4;
            if (stacked == stack) return;
            stacked = stack;
            SetColumn(control, stack ? 0 : 1);
            SetRow(control, stack ? 1 : 0);
            control.Width = stack || natural ? double.NaN : Theme.SettingsControlWidth;
            control.HorizontalAlignment = stack ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            control.Margin = stack ? new Thickness(0, Theme.S2, 0, Theme.S1) : new Thickness(Theme.S4, 0, 0, 0);
        }
        Place(double.PositiveInfinity);
        g.SizeChanged += (_, e) => Place(e.NewSize.Width);
        return g;
    }

    /// <summary>A switch row kept in sync with a customisation value; a tap anywhere on the row flips it.</summary>
    private Grid Toggle(string title, string? description, Func<CustomSettings, bool> get, Action<CustomSettings, bool> set, Action? demo = null)
    {
        var sw = new PillSwitch();
        ToolTipService.SetToolTip(sw, title);
        sw.Toggled += on => { Update(s => set(s, on)); demo?.Invoke(); };
        _refreshers.Add(_ => sw.IsOn = get(_theme.Current));
        var row = Row(title, description, sw, natural: true);
        row.Tag = SettingsWindow.NoDragTag;
        row.Tapped += (_, e) => { e.Handled = true; sw.Flip(); };
        return row;
    }

    private Grid Slider(string title, string? description, double min, double max, double step, Func<double, string> format,
                        Func<CustomSettings, double> get, Action<CustomSettings, double> set, Action? demo = null)
    {
        var s = new ValueSlider(null, min, max, step, format);
        AutomationName(s, title);
        s.Changed += (v, final) => { Update(c => set(c, v), continuous: !final); if (final) demo?.Invoke(); };
        _refreshers.Add(animate => s.SetValue(get(_theme.Current), animate));
        return Row(title, description, s);
    }

    private Grid Choice(string title, string? description, Func<CustomSettings, string> get, Action<CustomSettings, string> set, Action? demo,
                        params (string id, string label)[] options)
    {
        var seg = new Segmented(options);
        AutomationName(seg, title);
        seg.Selected += v => { Update(s => set(s, v)); demo?.Invoke(); };
        _refreshers.Add(_ => seg.Value = get(_theme.Current));
        return Row(title, description, seg);
    }

    private static void AutomationName(UIElement el, string name) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(el, name);

    private static FrameworkElement Extend(FrameworkElement el)
    {
        el.Tag = Extension;
        return el;
    }

    // ---- Colour ----------------------------------------------------------------------------------------------
    private IEnumerable<UIElement> BuildColour()
    {
        var swatches = new SwatchGrid();
        var picker = new ColorPicker { Margin = new Thickness(0, Theme.S1, 0, Theme.S2), Visibility = Visibility.Collapsed };
        var contrast = new TextBlock { Style = SettingsUi.StyleOf("CaptionText"), Foreground = new SolidColorBrush(Theme.BatteryAmber), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, Theme.S2), Visibility = Visibility.Collapsed };
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
        _refreshers.Add(animate =>
        {
            var s = _theme.Current;
            swatches.Select(s.MatchWindowsAccent ? "" : s.AccentPreset, animate);
            if (s.AccentPreset == "custom" && ColorUtil.TryParse(s.CustomAccent, out var c)) picker.SetColor(c);
            picker.SetRecent(s.RecentColors);
            if (s.AccentPreset != "custom") picker.Visibility = Visibility.Collapsed;
            var chosen = _theme.ChosenAccent();
            contrast.Visibility = ThemeService.IsLowContrast(chosen) ? Visibility.Visible : Visibility.Collapsed;
            contrast.Text = $"This colour is hard to see on the black notch (contrast {Theme.Contrast(chosen, Theme.NotchColor):0.0}:1). Text on it adapts automatically.";
        });
        yield return Row("Accent colour", "Switches, sliders and selections", swatches);
        yield return Extend(picker);
        yield return Extend(contrast);
        yield return Toggle("Match Windows accent", "Use your Windows colour and follow it",
            s => s.MatchWindowsAccent, (s, v) => s.MatchWindowsAccent = v, () => _theme.PlayDemo(ThemeService.Part.Accent));
        yield return Toggle("Follow album colour", "Use the song's colour while music plays",
            s => s.FollowAlbumColor, (s, v) => s.FollowAlbumColor = v);
    }

    // ---- Shape -----------------------------------------------------------------------------------------------
    private IEnumerable<UIElement> BuildShape()
    {
        yield return Slider("Notch corners", null, Theme.MinCornerRadius, Theme.MaxCornerRadius, 1, v => $"{v:0} px",
            s => s.CornerRadius, (s, v) => s.CornerRadius = v);
        var tile = Slider("Card corners", "Tiles, rows and cards", Theme.MinCornerRadiusSmall, Theme.MaxCornerRadiusSmall, 1, v => $"{v:0} px",
            s => ThemeService.EffectiveTileRadius(s), (s, v) => { s.TileRadius = v; s.LinkTileRadius = false; });
        _refreshers.Add(_ => tile.Opacity = _theme.Current.LinkTileRadius ? 0.55 : 1);
        yield return tile;
        yield return Toggle("Match notch corners", "Card corners follow the notch (about half)",
            s => s.LinkTileRadius, (s, v) => { if (!v) s.TileRadius = ThemeService.EffectiveTileRadius(s); s.LinkTileRadius = v; });
        yield return Slider("Button roundness", "100% is a full pill; switches always stay round", 0, 100, 5, v => v >= 100 ? "Pill" : $"{v:0}%",
            s => s.ButtonRoundness * 100, (s, v) => s.ButtonRoundness = v / 100);
    }

    // ---- Font ------------------------------------------------------------------------------------------------
    private IEnumerable<UIElement> BuildFont()
    {
        var picker = new GeneralTabView.FontPicker(_vm.Fonts);
        var sample = new GeneralTabView.SampleCard { Margin = new Thickness(0, Theme.S1, 0, Theme.S2) };
        picker.Hovered += f => sample.Show(f ?? _vm.Fonts.Current);
        picker.Selected += f => _vm.Fonts.Apply(f);
        _refreshers.Add(_ => { picker.Refresh(); sample.Show(_vm.Fonts.Current); });
        yield return Row("Font", "Used everywhere in the notch and Settings", picker);
        yield return Extend(sample);
    }

    // ---- Notch -----------------------------------------------------------------------------------------------
    private IEnumerable<UIElement> BuildNotch()
    {
        Action glowDemo = () => _theme.PlayDemo(ThemeService.Part.Glow);
        Action visDemo = () => _theme.PlayDemo(ThemeService.Part.Collapsed);
        yield return Choice("Size", null, s => s.NotchScale switch { 0.9 => "compact", 1.0 => "default", 1.15 => "large", _ => "custom" },
            (s, v) => s.NotchScale = v switch { "compact" => 0.9, "large" => 1.15, _ => 1.0 }, null,
            ("compact", "Compact"), ("default", "Default"), ("large", "Large"));
        yield return Slider("Exact size", null, 0.85, 1.25, 0.01, v => $"{v:0.00}x", s => s.NotchScale, (s, v) => s.NotchScale = Math.Round(v, 2));
        yield return Slider("Border highlight", "The thin light edge", 0, 20, 1, v => $"{v:0}%", s => s.Highlight * 100, (s, v) => s.Highlight = v / 100);
        yield return Slider("Shadow", null, 0, 150, 5, v => $"{v:0}%", s => s.ShadowIntensity * 100, (s, v) => s.ShadowIntensity = v / 100);
        yield return Toggle("Music glow", "Soft colour behind the album art", s => s.GlowEnabled, (s, v) => s.GlowEnabled = v, glowDemo);
        yield return Slider("Glow strength", null, 0, 150, 5, v => $"{v:0}%", s => s.GlowIntensity * 100, (s, v) => s.GlowIntensity = v / 100, glowDemo);
        yield return Choice("Glow colour", null, s => s.GlowSource, (s, v) => s.GlowSource = v, glowDemo, ("album", "Album"), ("accent", "Accent"));
        yield return Choice("Visualiser", "Music bars in the closed notch", s => s.Visualizer, (s, v) => s.Visualizer = v, visDemo, ("bars", "Bars"), ("wave", "Wave"), ("off", "Off"));
        yield return Choice("Visualiser colour", null, s => s.VisualizerColor, (s, v) => s.VisualizerColor = v, visDemo, ("accent", "Accent"), ("white", "White"));
    }

    // ---- Closed notch ----------------------------------------------------------------------------------------
    private IEnumerable<UIElement> BuildClosedNotch()
    {
        Action demo = () => _theme.PlayDemo(ThemeService.Part.Collapsed);
        yield return Toggle("Battery ring", null, s => s.ShowBatteryRing, (s, v) => s.ShowBatteryRing = v, demo);
        yield return Toggle("Clock", null, s => s.ShowClock, (s, v) => s.ShowClock = v, demo);
        yield return Toggle("Date", "Short date next to the time", s => s.ShowDate, (s, v) => s.ShowDate = v, demo);
        yield return Choice("Clock format", null, s => s.ClockFormat, (s, v) => s.ClockFormat = v, demo, ("system", "System"), ("12", "12-hour"), ("24", "24-hour"));
    }

    // ---- Motion ----------------------------------------------------------------------------------------------
    private IEnumerable<UIElement> BuildMotion()
    {
        Action demo = () => _theme.PlayDemo(ThemeService.Part.Motion);
        var style = Choice("Animation style", "", s => s.MotionFeel, (s, v) => s.MotionFeel = v, demo,
            ("smooth", "Smooth"), ("snappy", "Snappy"), ("bouncy", "Bouncy"), ("calm", "Calm"), ("reduced", "Reduced"));
        // The description says whether Windows' own "Animation effects" setting has turned motion off.
        var text = (StackPanel)style.Children[0];
        var note = new TextBlock { Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = Theme.GapTopS1 };
        text.Children.Add(note);
        _refreshers.Add(_ => note.Text = Anim.Enabled
            ? "Windows' Animation effects setting can turn all motion off"
            : "Off: Windows' Animation effects setting is off");
        yield return style;
        yield return Slider("Animation speed", null, 0.5, 1.5, 0.05, v => $"{v:0.00}x", s => s.MotionSpeed, (s, v) => s.MotionSpeed = v, demo);
    }

    // ---- Behaviour -------------------------------------------------------------------------------------------
    private IEnumerable<UIElement> BuildBehaviour()
    {
        yield return Slider("Open after hovering", "How long the pointer rests on the notch first", 0, 600, 10, v => $"{v:0} ms",
            s => s.HoverDelayMs, (s, v) => s.HoverDelayMs = (int)v);
        yield return Slider("Close after leaving", "How long it stays open once the pointer leaves", 150, 2000, 50, v => $"{v:0} ms",
            s => s.CollapseDelayMs, (s, v) => s.CollapseDelayMs = (int)v);
        yield return Choice("Screen", "“Under the mouse” is picked at start-up and when displays change", s => s.Monitor, (s, v) => s.Monitor = v, null,
            ("primary", "Primary"), ("mouse", "Under the mouse"));
    }

    // ---- Performance -----------------------------------------------------------------------------------------
    private IEnumerable<UIElement> BuildPerformance()
    {
        yield return Choice("Performance mode", "Low power slows the visualiser and stops the glow breathing. Automatic uses it on battery saver and in Remote Desktop.",
            s => s.PerformanceMode, (s, v) => s.PerformanceMode = v, null, ("auto", "Automatic"), ("standard", "Standard"), ("low", "Low power"));
    }

    // ---- Footer ----------------------------------------------------------------------------------------------
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

        _confirm.Children.Add(new TextBlock { Text = "Reset all customisation?", Style = SettingsUi.StyleOf("BaseText"), VerticalAlignment = VerticalAlignment.Center });
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
