using System.ComponentModel;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>
/// The small Settings window, styled like the notch (same as the WPF one): a black card with the
/// global radius, a hairline highlight and a soft shadow, on a see-through window. It opens and closes
/// with a scale + fade spring, closes on Esc or the × button, and drags by its header band only.
/// The tab list is data-driven (<see cref="SettingsViewModel.Tabs"/>) with a highlight that springs
/// between tabs. Only one instance exists (<see cref="ShowSingle"/>). All motion is on the compositor.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    private readonly SettingsViewModel _vm;
    private readonly IntPtr _hwnd;
    private readonly RootControl _root = new();
    private readonly Grid _card = new(), _shadowHost = new();
    private readonly Border _tabHighlight = new();
    private readonly Dictionary<object, FrameworkElement> _views = new();
    private readonly CompSpring _open, _tab;
    private readonly DispatcherQueueTimer _closeTimer;
    private bool _closing;

    /// <summary>Raised with the window handle when it opens / IntPtr.Zero when it closes (for the keyboard hook).</summary>
    public static event Action<IntPtr>? HandleChanged;

    private SettingsWindow(SettingsViewModel vm)
    {
        _vm = vm;
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Dynamic Notch Settings";
        SystemBackdrop = new TransparentBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        Native.MakeSeeThroughPopup(_hwnd);
        Native.SetExStyle(_hwnd, Native.WS_EX_APPWINDOW | Native.WS_EX_TOPMOST, Native.WS_EX_TOOLWINDOW);

        // ---- Card: tab column | divider + content ----
        _card.Width = Theme.SettingsWidth;
        _card.Height = Theme.SettingsHeight;
        _card.Background = Theme.NotchBrush;
        _card.BorderBrush = Theme.HighlightBorderBrush;
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = Theme.Radius;
        _card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Theme.SettingsTabWidth) });
        _card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tabs = new Grid { Margin = Theme.SettingsTabPadding };
        tabs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tabs.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = Theme.CardHeaderMargin };
        heading.Children.Add(new VectorIcon { Data = Icons.Settings, Size = Theme.IconSmall, Foreground = Theme.SecondaryText, VerticalAlignment = VerticalAlignment.Center });
        heading.Children.Add(new TextBlock { Text = "Settings", Style = AskPage.TextStyle("SectionText"), Margin = Theme.GapLeftS2 });
        tabs.Children.Add(heading);
        var list = new Grid();
        Grid.SetRow(list, 1);
        _tabHighlight.Height = Theme.SettingsTabHeight;
        _tabHighlight.VerticalAlignment = VerticalAlignment.Top;
        _tabHighlight.CornerRadius = Theme.Radius;
        _tabHighlight.Background = Theme.CardHoverBrush;
        list.Children.Add(_tabHighlight);
        var rows = new StackPanel();
        foreach (var tab in vm.Tabs) rows.Children.Add(TabRow(tab));
        list.Children.Add(rows);
        tabs.Children.Add(list);
        _card.Children.Add(tabs);

        var contentHost = new Border { BorderBrush = Theme.CardBorderBrush, BorderThickness = new Thickness(1, 0, 0, 0) };
        Grid.SetColumn(contentHost, 1);
        var content = new Grid { Margin = Theme.SettingsPadding };
        _views[vm.ApiKey] = new ApiKeyTabView(vm.ApiKey);
        _views[vm.Pages] = new PagesTabView(vm.Pages);
        _views[vm.Focus] = new FocusTabView(vm.Focus);
        _views[vm.General] = new GeneralTabView(vm.General);
        foreach (var v in _views.Values) content.Children.Add(v);
        var close = new IconButton(26, Theme.IconSmall) { Icon = Icons.Close, Foreground = Theme.SecondaryText, Tooltip = "Close (Esc)", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        close.Click += AnimateClose;
        content.Children.Add(close);
        contentHost.Child = content;
        _card.Children.Add(contentHost);

        // Room around the card for its shadow.
        var stage = new Grid { Padding = new Thickness(Theme.SettingsShadowRoom) };
        stage.Children.Add(_shadowHost);
        stage.Children.Add(_card);
        _root.Content = stage;
        _root.IsTabStop = true;
        _root.UseSystemFocusVisuals = false;
        Content = _root;

        // ---- Motion: open / close (scale + fade), the tab highlight ----
        var cv = Anim.VisualOf(_card);
        var c = cv.Compositor;
        var props = c.CreatePropertySet();
        _open = new CompSpring(props, "o", Motion.WindowOpen, 0);
        _tab = new CompSpring(props, "tab", Motion.TabHighlight, 0, precision: 50);
        cv.CenterPoint = new Vector3((float)Theme.SettingsWidth / 2, (float)Theme.SettingsHeight / 2, 0);
        string s = $"({Anim.F(Motion.WindowStartScale)} + (1 - {Anim.F(Motion.WindowStartScale)}) * p.o)";
        foreach (var v in new[] { cv, ElementCompositionPreview.GetElementVisual(_shadowHost) })
        {
            v.CenterPoint = cv.CenterPoint;
            v.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.o, 0, 1)", ("p", props)));
            v.StartAnimation("Scale", Anim.Expr(c, $"Vector3({s}, {s}, 1)", ("p", props)));
        }
        Anim.VisualOf(_tabHighlight, translation: true).StartAnimation("Translation", Anim.Expr(c, "Vector3(0, p.tab, 0)", ("p", props)));
        BuildShadow(c);

        _closeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); Close(); };

        vm.PropertyChanged += OnVmChanged;
        ShowTab(animate: false);

        // Drag only from the header band at the top of the card, and never from a control inside it.
        // (Every container here is a Grid or Border, so testing the source's type alone made the whole
        // window draggable, including presses on buttons, switches and empty content.)
        _card.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(_card);
            if (!pt.Properties.IsLeftButtonPressed || pt.Position.Y > Theme.SettingsDragBandHeight) return;
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            Native.BeginWindowDrag(_hwnd);
            e.Handled = true;
        };
        _root.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { AnimateClose(); e.Handled = true; } };
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnVmChanged;
            _instance = null;
            HandleChanged?.Invoke(IntPtr.Zero);
        };
    }

    /// <summary>Marks an element (a tab row, a list row…) that must never start a window drag.</summary>
    internal const string NoDragTag = "settings:nodrag";

    /// <summary>True when the element, or any parent up to the card, is something the user clicks to act.</summary>
    private bool IsInteractive(DependencyObject? d)
    {
        for (; d != null && !ReferenceEquals(d, _card); d = VisualTreeHelper.GetParent(d))
        {
            if (d is IconButton or PillButton or TextButton or Control or Microsoft.UI.Xaml.Documents.Hyperlink) return true;
            if (d is FrameworkElement { Tag: NoDragTag }) return true;
        }
        return false;
    }

    /// <summary>Opens the window, or brings the existing one to the front.</summary>
    public static void ShowSingle(SettingsViewModel vm)
    {
        if (_instance != null)
        {
            _instance.Activate();
            Native.SetForegroundWindow(_instance._hwnd);
            return;
        }
        var w = _instance = new SettingsWindow(vm);
        // Centred on the primary monitor, in physical pixels.
        var (bounds, scale) = Native.GetPrimaryMonitor();
        int width = (int)Math.Round((Theme.SettingsWidth + 2 * Theme.SettingsShadowRoom) * scale);
        int height = (int)Math.Round((Theme.SettingsHeight + 2 * Theme.SettingsShadowRoom) * scale);
        w.AppWindow.MoveAndResize(new RectInt32(bounds.Left + (bounds.Width - width) / 2, bounds.Top + (bounds.Height - height) / 2, width, height));
        w.Activate();
        Native.MakeSeeThroughPopup(w._hwnd); // showing re-applies the presenter frame
        Native.SetForegroundWindow(w._hwnd);
        HandleChanged?.Invoke(w._hwnd);
        w._open.Snap(0);
        w._open.SetTarget(1, Motion.WindowOpen);
        w.FocusFirst();
    }

    private void FocusFirst() =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_views[_vm.SelectedTab.Content] is ApiKeyTabView key) key.FocusBox();
            else _root.Focus(FocusState.Programmatic);
        });

    private void AnimateClose()
    {
        if (_closing) return;
        _closing = true;
        if (!Anim.Enabled) { Close(); return; }
        _open.SetTarget(0, Motion.FadeOut);
        _closeTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.05, _open.Model.RemainingDuration));
        _closeTimer.Start();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedIndex)) ShowTab(animate: true);
    }

    private void ShowTab(bool animate)
    {
        double y = _vm.SelectedIndex * Theme.SettingsTabHeight;
        if (animate) _tab.SetTarget(y, Motion.TabHighlight); else _tab.Snap(y);
        foreach (var (key, view) in _views) view.Visibility = ReferenceEquals(key, _vm.SelectedTab.Content) ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement TabRow(SettingsTab tab)
    {
        var row = new Grid { Height = Theme.SettingsTabHeight, Background = Theme.TransparentBrush, Tag = NoDragTag };
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3 };
        line.Children.Add(new VectorIcon { Data = tab.Icon, Size = Theme.IconSmall, Foreground = Theme.PrimaryText, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = tab.Title, Style = AskPage.TextStyle("BaseText"), Margin = Theme.GapLeftS2, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(line);
        Motions.PressScale(row);
        row.Tapped += (_, _) => _vm.SelectedTab = tab;
        return row;
    }

    /// <summary>Soft shadow under the card: a drop shadow masked by a live surface of the card.</summary>
    private void BuildShadow(Compositor c)
    {
        var size = new Vector2((float)Theme.SettingsWidth, (float)Theme.SettingsHeight);
        var surface = c.CreateVisualSurface();
        surface.SourceVisual = ElementCompositionPreview.GetElementVisual(_card);
        surface.SourceSize = size;
        var shadow = c.CreateDropShadow();
        shadow.BlurRadius = (float)Theme.SettingsShadowBlur;
        shadow.Offset = new Vector3(0, (float)Theme.SettingsShadowOffsetY, 0);
        shadow.Opacity = (float)Theme.SettingsShadowOpacity;
        shadow.Color = Color.FromArgb(255, 0, 0, 0);
        shadow.Mask = c.CreateSurfaceBrush(surface);
        var caster = c.CreateSpriteVisual();
        caster.Size = size;
        caster.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(_shadowHost, caster);
    }

    /// <summary>A focusable root, so Esc arrives even when no field has focus.</summary>
    private sealed class RootControl : UserControl { }
}

/// <summary>"Claude API key" tab: masked key box with show / hide and Paste, Save / Remove / Test, status.</summary>
internal sealed class ApiKeyTabView : StackPanel
{
    private readonly ApiKeyTabViewModel _vm;
    private readonly PasswordBox _box = new();
    private readonly IconButton _eye;
    private readonly PillButton _remove;
    private readonly VectorIcon _check = new() { Data = Icons.Check, Size = Theme.IconSmall, Foreground = Theme.SuccessBrush, Margin = Theme.GapRightS2, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new(), _test = new();
    private readonly CompSpring _checkSpring;

    public ApiKeyTabView(ApiKeyTabViewModel vm)
    {
        _vm = vm;
        Children.Add(new TextBlock { Text = "Claude API key", Style = AskPage.TextStyle("TitleText") });
        Children.Add(new TextBlock { Text = "Paste your API key to turn on Ask Claude.", Style = AskPage.TextStyle("SecondaryText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = Theme.GapTopS1 });
        var linkText = new TextBlock { FontFamily = Theme.Font, FontSize = Theme.FontBody, Margin = Theme.GapTopS1 };
        var link = new Hyperlink { Foreground = Theme.AccentBrush, UnderlineStyle = UnderlineStyle.None };
        link.Inlines.Add(new Run { Text = AppConfig.ApiKeyPageLabel });
        link.Click += (_, _) => vm.OpenKeyPageCommand.Execute(null); // opens the browser only when clicked
        linkText.Inlines.Add(link);
        Children.Add(linkText);

        // Key box: masked by default, the eye toggles plain text; Paste fills it from the clipboard.
        var keyRow = new Grid { Margin = Theme.GapTopS3 };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _box.PlaceholderText = "sk-ant-…";
        _box.PasswordRevealMode = PasswordRevealMode.Hidden;
        _box.FontFamily = Theme.MonoFont;
        _box.FontSize = Theme.FontBody;
        _box.Height = Theme.SettingsFieldHeight;
        _box.Padding = Theme.AskInputPadding;
        _box.VerticalContentAlignment = VerticalAlignment.Center;
        _box.SelectionHighlightColor = Theme.AccentBrush;
        InputStyle.Apply(_box);
        _box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Save(); e.Handled = true; } };
        keyRow.Children.Add(_box);
        _eye = new IconButton(26, Theme.IconSmall) { Icon = Icons.Eye, Foreground = Theme.SecondaryText, Tooltip = "Show / hide", Margin = Theme.GapLeftS1, VerticalAlignment = VerticalAlignment.Center };
        _eye.Click += ToggleReveal;
        Grid.SetColumn(_eye, 1);
        keyRow.Children.Add(_eye);
        var paste = new PillButton(Icons.Paste, "Paste", quiet: true) { Margin = Theme.GapLeftS1, VerticalAlignment = VerticalAlignment.Center };
        paste.Click += Paste;
        Grid.SetColumn(paste, 2);
        keyRow.Children.Add(paste);
        Children.Add(keyRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = Theme.GapTopS3 };
        var save = new PillButton(Icons.Check, "Save");
        save.Click += Save;
        actions.Children.Add(save);
        _remove = new PillButton(Icons.Delete, "Remove key", quiet: true) { Command = vm.RemoveCommand, Margin = Theme.GapLeftS2 };
        actions.Children.Add(_remove);
        var test = new PillButton(Icons.Sparkle, "Test connection", quiet: true) { Margin = Theme.GapLeftS2 };
        test.Click += async () => await _vm.TestAsync(_box.Password.Trim());
        actions.Children.Add(test);
        Children.Add(actions);

        // Status: masked key only; a spring check after Save; the test result in green / red.
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = Theme.GapTopS4 };
        statusRow.Children.Add(_check);
        _status.Style = AskPage.TextStyle("SecondaryText");
        _status.VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(_status);
        Children.Add(statusRow);
        _test.FontFamily = Theme.FontSemiBold;
        _test.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _test.FontSize = Theme.FontBody;
        _test.TextWrapping = TextWrapping.Wrap;
        _test.Margin = Theme.GapTopS1;
        Children.Add(_test);

        var cv = Anim.VisualOf(_check);
        var c = cv.Compositor;
        var (spring, set) = Motions.Value(c, Motion.Pop, 0);
        _checkSpring = spring;
        cv.CenterPoint = new Vector3((float)Theme.IconSmall / 2, (float)Theme.IconSmall / 2, 0);
        cv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
        cv.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.4 + 0.6 * p.v, 0.4 + 0.6 * p.v, 1)", ("p", set)));

        vm.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    public void FocusBox() => _box.Focus(FocusState.Programmatic);

    private void Refresh()
    {
        _status.Text = _vm.StatusText;
        _remove.Active = _vm.HasKey;
        _test.Text = _vm.TestText;
        _test.Foreground = _vm.TestResult switch
        {
            KeyTestResult.Works => Theme.SuccessBrush,
            KeyTestResult.Rejected => Theme.DangerBrush,
            _ => Theme.SecondaryText,
        };
    }

    private void ToggleReveal()
    {
        bool reveal = _box.PasswordRevealMode != PasswordRevealMode.Visible;
        _box.PasswordRevealMode = reveal ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        _eye.Icon = reveal ? Icons.EyeOff : Icons.Eye;
        _box.Focus(FocusState.Programmatic);
    }

    private async void Paste()
    {
        try
        {
            var data = Clipboard.GetContent();
            if (!data.Contains(StandardDataFormats.Text)) return;
            var text = (await data.GetTextAsync()).Trim();
            if (text.Length > 0) _box.Password = text;
        }
        catch { /* clipboard busy */ }
    }

    private void Save()
    {
        if (!_vm.Save(_box.Password.Trim())) return;
        _box.Password = ""; // never show the full key after saving
        _checkSpring.Snap(0);
        _checkSpring.SetTarget(1, Motion.Pop);
    }
}

/// <summary>"Pages" tab: one card row per notch page with a pill switch; at least one stays on.</summary>
internal sealed class PagesTabView : Grid
{
    public PagesTabView(PagesTabViewModel vm)
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Pages", Style = AskPage.TextStyle("TitleText") });
        top.Children.Add(new TextBlock { Text = "Choose which pages the notch shows.", Style = AskPage.TextStyle("SecondaryText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = Theme.GapTopS1 });
        var rows = new StackPanel { Margin = new Thickness(0, 0, Theme.S3, 0) };
        foreach (var row in vm.Rows) rows.Children.Add(Row(row));
        // Five pages no longer fit the card's height on every display scale: the list scrolls.
        var scroll = new ScrollViewer { Content = rows, Margin = Theme.GapTopS3, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 5 * (Theme.PageRowHeight + Theme.S2) };
        top.Children.Add(scroll);
        var message = new TextBlock { FontFamily = Theme.FontSemiBold, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = Theme.FontBody, Foreground = Theme.DangerBrush, Margin = Theme.GapTopS1 };
        top.Children.Add(message);
        Children.Add(top);
        var footer = new TextBlock { Text = "Changes apply instantly.", Style = AskPage.TextStyle("CaptionText") };
        SetRow(footer, 1);
        Children.Add(footer);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PagesTabViewModel.Message)) message.Text = vm.Message; };
    }

    private static FrameworkElement Row(PageRowViewModel row)
    {
        var card = new Border
        {
            Background = Theme.CardBrush, BorderBrush = Theme.CardBorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius, Padding = Theme.PageRowPadding, Margin = Theme.PageRowMargin, MinHeight = Theme.PageRowHeight,
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Icon in a rounded tile
        var tile = new Border
        {
            Width = Theme.PageIconTile, Height = Theme.PageIconTile, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = Theme.Radius, Background = Theme.TileBrush, BorderBrush = Theme.TileBorderBrush, BorderThickness = new Thickness(1),
            Child = row.UseClaudeLogo
                ? new ClaudeLogo(Theme.IconMedium, Theme.PrimaryText) { HorizontalAlignment = HorizontalAlignment.Center }
                : new VectorIcon { Data = row.Icon, Size = Theme.IconMedium, Foreground = Theme.PrimaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        g.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3 };
        text.Children.Add(new TextBlock { Text = row.Name, Style = AskPage.TextStyle("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = row.Description, Style = AskPage.TextStyle("CaptionText"), Foreground = Theme.SecondaryText, Margin = Theme.GapTopS1 });
        SetColumn(text, 1);
        g.Children.Add(text);
        var sw = new PillSwitch { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3, IsOn = row.IsEnabled };
        ToolTipService.SetToolTip(sw, row.Name);
        sw.Toggled += on => row.IsEnabled = on;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageRowViewModel.IsEnabled)) sw.IsOn = row.IsEnabled;
            if (e.PropertyName == nameof(PageRowViewModel.ShakeToken)) sw.Shake();
        };
        SetColumn(sw, 2);
        g.Children.Add(sw);
        card.Child = g;
        return card;
    }
}
