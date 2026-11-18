using System.ComponentModel;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using DynamicNotch.Services;
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
    private readonly Border _tabHighlight = new(), _tabBar = new(), _tabGlow = new();
    private readonly List<(SettingsTab Tab, VectorIcon Icon, TextBlock Label)> _tabRows = new();
    private CompSpring _tabTrail = null!;
    private const double TabRowGap = 2;
    private readonly Dictionary<object, FrameworkElement> _views = new();
    private readonly Dictionary<object, Func<FrameworkElement>> _factories = new();
    private Grid _content = null!;
    private bool _hidden;
    private readonly CompSpring _open, _tab;
    private readonly DispatcherQueueTimer _closeTimer;
    private bool _closing;
    private SpriteVisual? _caster;

    /// <summary>Raised with the window handle when it opens / IntPtr.Zero when it closes (for the keyboard hook).</summary>
    public static event Action<IntPtr>? HandleChanged;

    /// <summary>The open Settings window's handle (file pickers need an owner), or IntPtr.Zero.</summary>
    public static IntPtr CurrentHandle => _instance?._hwnd ?? IntPtr.Zero;

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
        // The card fills the window (minus the shadow room), so resizing the window resizes it.
        // Surface: a near-black vertical gradient with two faint accent "aurora" washes (top-left behind the
        // sidebar, bottom-right in the dark tint). Static gradients drawn by the GPU, so they cost nothing
        // while idle (a live backdrop blur behind a see-through, always-on-top window would).
        _card.Background = Theme.SettingsSurfaceBrush;
        _card.BorderBrush = Theme.HighlightBorderBrush;
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = Theme.Radius;
        _card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Theme.SettingsTabWidth) });
        _card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var aurora = new Grid();
        aurora.Children.Add(new Border { Width = 620, Height = 480, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-260, -240, 0, 0), Background = Theme.AccentAuroraBrush });
        aurora.Children.Add(new Border { Width = 560, Height = 440, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -240, -240), Background = Theme.AccentAuroraDarkBrush });
        // A Border clips its child to its rounded corners, so the washes never spill past the card.
        var auroraClip = new Border { CornerRadius = Theme.Radius, Child = aurora, IsHitTestVisible = false };
        Grid.SetColumnSpan(auroraClip, 2);
        _card.Children.Add(auroraClip);

        // Sidebar: a slightly raised panel (left corners follow the radius token).
        var sidebar = new Border { Background = Theme.SidebarBrush, CornerRadius = new CornerRadius(Theme.Radius.TopLeft, 0, 0, Theme.Radius.BottomLeft), IsHitTestVisible = false };
        _card.Children.Add(sidebar);

        var tabs = new Grid { Margin = Theme.SettingsTabPadding };
        tabs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tabs.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tabs.Children.Add(BrandHeader());
        var list = new Grid();
        Grid.SetRow(list, 1);
        _tabHighlight.Height = Theme.SettingsTabHeight;
        _tabHighlight.VerticalAlignment = VerticalAlignment.Top;
        _tabHighlight.CornerRadius = Theme.RadiusSmall;
        _tabHighlight.Background = Theme.AccentTintBrush; // the selected tab carries the accent
        _tabHighlight.BorderBrush = Theme.AccentBorderBrush;
        _tabHighlight.BorderThickness = new Thickness(1);
        list.Children.Add(_tabHighlight);
        // The accent bar beside the selected tab, with a soft halo; it stretches as it travels (see Motion below).
        _tabGlow.Width = 26;
        _tabGlow.Height = Theme.SettingsTabHeight;
        _tabGlow.HorizontalAlignment = HorizontalAlignment.Left;
        _tabGlow.VerticalAlignment = VerticalAlignment.Top;
        _tabGlow.Margin = new Thickness(-11, 0, 0, 0);
        _tabGlow.Background = Theme.AccentHaloBrush;
        _tabGlow.IsHitTestVisible = false;
        list.Children.Add(_tabGlow);
        _tabBar.Width = Theme.SettingsTabBarWidth;
        _tabBar.Height = Theme.SettingsTabBarHeight;
        _tabBar.CornerRadius = new CornerRadius(Theme.SettingsTabBarWidth / 2);
        _tabBar.HorizontalAlignment = HorizontalAlignment.Left;
        _tabBar.VerticalAlignment = VerticalAlignment.Top;
        _tabBar.Margin = new Thickness(1, (Theme.SettingsTabHeight - Theme.SettingsTabBarHeight) / 2, 0, 0);
        _tabBar.Background = Theme.AccentBarBrush;
        _tabBar.IsHitTestVisible = false;
        list.Children.Add(_tabBar);
        var rows = new StackPanel { Spacing = 2 };
        foreach (var tab in vm.Tabs) rows.Children.Add(TabRow(tab));
        list.Children.Add(rows);
        tabs.Children.Add(list);
        _card.Children.Add(tabs);

        var contentHost = new Border { BorderBrush = Theme.CardBorderBrush, BorderThickness = new Thickness(1, 0, 0, 0) };
        Grid.SetColumn(contentHost, 1);
        var content = new Grid { Margin = Theme.SettingsPadding };
        // Tab views are built the first time each tab is shown (the Customisation tab alone is a big tree),
        // so opening Settings only builds the visible tab.
        _content = content;
        _factories[vm.ApiKey] = () => new ApiKeyTabView(vm.ApiKey);
        _factories[vm.Pages] = () => new PagesTabView(vm.Pages);
        _factories[vm.Focus] = () => new FocusTabView(vm.Focus);
        _factories[vm.General] = () => new GeneralTabView(vm.General);
        _factories[vm.Customisation] = () => new CustomisationTabView(vm.Customisation);
        _factories[vm.Pills] = () => new PillsTabView(vm.Pills);
        _factories[vm.Updates] = () => new UpdatesTabView(vm.Updates);
        var close = new IconButton(26, Theme.IconSmall) { Icon = Icons.Close, Foreground = Theme.SecondaryText, Tooltip = "Close (Esc)", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        close.Click += AnimateClose;
        content.Children.Add(close);
        contentHost.Child = content;
        _card.Children.Add(contentHost);

        // Resize grip (bottom-right): a system resize, clamped to the minimum size below.
        var grip = new Grid { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 4), Background = Theme.TransparentBrush, Tag = NoDragTag };
        Grid.SetColumnSpan(grip, 2);
        foreach (var (len, off) in new[] { (10.0, 3.0), (6.0, 7.0) })
            grip.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = 18 - off - len, Y1 = 18 - off, X2 = 18 - off, Y2 = 18 - off - len, Stroke = Theme.TertiaryText, StrokeThickness = 1.2, IsHitTestVisible = false });
        grip.ProtectedCursorSet(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast);
        ToolTipService.SetToolTip(grip, "Resize");
        grip.PointerPressed += (_, e) => { Native.BeginWindowResize(_hwnd); e.Handled = true; };
        _card.Children.Add(grip);
        AppWindow.Changed += (_, a) =>
        {
            if (!a.DidSizeChange) return;
            double scale = Native.GetPrimaryMonitor().scale;
            int minW = (int)Math.Round((Theme.SettingsMinWidth + 2 * Theme.SettingsShadowRoom) * scale);
            int minH = (int)Math.Round((Theme.SettingsMinHeight + 2 * Theme.SettingsShadowRoom) * scale);
            var size = AppWindow.Size;
            if (size.Width < minW || size.Height < minH) AppWindow.Resize(new SizeInt32(Math.Max(minW, size.Width), Math.Max(minH, size.Height)));
        };

        // Radius / accent / shadow tokens follow live in this window too.
        vm.Customisation.Theme.RegisterRoot(_root);
        Services.ThemeService.RadiusChanged += OnRadiusChanged;
        vm.Customisation.Theme.Changed += OnThemeChanged;

        // Room around the card for its shadow.
        var stage = new Grid { Padding = new Thickness(Theme.SettingsShadowRoom) };
        stage.Children.Add(_shadowHost);
        stage.Children.Add(_card);
        _root.Content = stage;
        _root.IsTabStop = true;
        _root.UseSystemFocusVisuals = false;
        Content = _root;

        // ---- Motion: open / close (scale + fade), the tab highlight ----
        var cv = Anim.VisualOf(_card, translation: true);
        var c = cv.Compositor;
        var props = c.CreatePropertySet();
        _open = new CompSpring(props, "o", Motion.WindowOpen, 0);
        _tab = new CompSpring(props, "tab", Motion.TabHighlight, 0, precision: 50);
        // The bar's trailing end: a slower spring, so the bar stretches toward the new tab and then catches up.
        _tabTrail = new CompSpring(props, "trail", Motion.TabTrail, 0, precision: 50);
        cv.CenterPoint = new Vector3((float)Theme.SettingsWidth / 2, (float)Theme.SettingsHeight / 2, 0);
        var shv = Anim.VisualOf(_shadowHost, translation: true);
        _card.SizeChanged += (_, e) =>
        {
            var centre = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            cv.CenterPoint = shv.CenterPoint = centre;
            if (_caster != null) SoftShadow.Layout(_caster, new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));
        };
        string s = $"({Anim.F(Motion.WindowStartScale)} + (1 - {Anim.F(Motion.WindowStartScale)}) * p.o)";
        foreach (var v in new[] { cv, shv })
        {
            v.CenterPoint = cv.CenterPoint;
            v.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.o, 0, 1)", ("p", props)));
            v.StartAnimation("Scale", Anim.Expr(c, $"Vector3({s}, {s}, 1)", ("p", props)));
            // ...and rises a few DIPs into place while it opens (sinks back as it closes).
            var rise = Anim.Expr(c, "Vector3(0, (1 - Clamp(p.o, 0, 1.2)) * R, 0)", ("p", props));
            rise.SetScalarParameter("R", (float)Motion.WindowRise);
            v.StartAnimation("Translation", rise);
        }
        Anim.VisualOf(_tabHighlight, translation: true).StartAnimation("Translation", Anim.Expr(c, "Vector3(0, p.tab, 0)", ("p", props)));
        // Elastic bar: spans from the trailing spring to the leading one (top-anchored scale), so a long jump
        // stretches it like a droplet before it settles back to its resting height.
        var bv = Anim.VisualOf(_tabBar, translation: true);
        bv.CenterPoint = Vector3.Zero;
        bv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, Min(p.tab, p.trail), 0)", ("p", props)));
        var stretch = Anim.Expr(c, "Vector3(1, (Abs(p.tab - p.trail) + B) / B, 1)", ("p", props));
        stretch.SetScalarParameter("B", (float)Theme.SettingsTabBarHeight);
        bv.StartAnimation("Scale", stretch);
        var gv = Anim.VisualOf(_tabGlow, translation: true);
        gv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (p.tab + p.trail) / 2, 0)", ("p", props)));
        BuildShadow(c);

        _closeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); HideWindow(); };

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
            Services.ThemeService.RadiusChanged -= OnRadiusChanged;
            vm.Customisation.Theme.Changed -= OnThemeChanged;
            _instance = null;
            HandleChanged?.Invoke(IntPtr.Zero);
        };
    }

    private void OnRadiusChanged() => BuildShadow(Anim.VisualOf(_card).Compositor); // the shadow's corners follow the radius

    private void OnThemeChanged(Services.ThemeService.Part parts)
    {
        if (parts.HasFlag(Services.ThemeService.Part.Surface) && _caster != null)
            _caster.Opacity = (float)Math.Min(1, Theme.SettingsShadowOpacity * Theme.ShadowIntensity);
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
    /// <summary>A new UI font: refresh the open window's text with a short cross-fade.</summary>
    public static void ApplyFont(ISet<string> previousSources)
    {
        var w = _instance;
        if (w == null) return;
        Services.FontService.Refresh(w._root, previousSources);
        if (!Anim.Enabled) return;
        var a = Anim.VisualOf(w._card).Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, (float)Motion.FontSwapDip);
        a.InsertKeyFrame(1, 1);
        a.Duration = TimeSpan.FromMilliseconds(260);
        // The card's opacity belongs to the open/close spring; fade its content row instead.
        foreach (var child in w._card.Children.OfType<UIElement>()) Anim.VisualOf(child).StartAnimation("Opacity", a);
    }

    public static void ShowSingle(SettingsViewModel vm)
    {
        PerfLog.Begin("Settings open");
        if (_instance is { } existing)
        {
            // Reopening reuses the hidden window: nothing is rebuilt, only the open spring plays.
            existing.ShowTab(animate: false);
            if (existing._hidden)
            {
                existing._hidden = false;
                existing._closing = false;
                existing.AppWindow.Show();
                Native.MakeSeeThroughPopup(existing._hwnd);
                HandleChanged?.Invoke(existing._hwnd);
                existing._open.Snap(0);
                existing._open.SetTarget(1, Motion.WindowOpen);
                existing.FocusFirst();
            }
            existing.Activate();
            Native.SetForegroundWindow(existing._hwnd);
            PerfLog.Mark("shown (reused window)");
            return;
        }
        var w = _instance = PerfLog.Time("new SettingsWindow", () => new SettingsWindow(vm));
        // Centred on the primary monitor, in physical pixels.
        var (bounds, scale) = Native.GetPrimaryMonitor();
        int width = (int)Math.Round((Theme.SettingsWidth + 2 * Theme.SettingsShadowRoom) * scale);
        // 540 DIPs tall by default; up to 620 when the screen has room (the Customisation tab scrolls under its pinned preview).
        double tall = Math.Clamp(bounds.Height / scale * 0.8 - 2 * Theme.SettingsShadowRoom, Theme.SettingsHeight, 620);
        int height = (int)Math.Round((tall + 2 * Theme.SettingsShadowRoom) * scale);
        w.AppWindow.MoveAndResize(new RectInt32(bounds.Left + (bounds.Width - width) / 2, bounds.Top + Math.Max(0, (bounds.Height - height) / 2), width, Math.Min(height, bounds.Height)));
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
            if (_views.GetValueOrDefault(_vm.SelectedTab.Content) is ApiKeyTabView key) key.FocusBox();
            else _root.Focus(FocusState.Programmatic);
        });

    /// <summary>Closing hides the window (kept for an instant reopen; its views and subscriptions stay single).</summary>
    private void HideWindow()
    {
        _hidden = true;
        AppWindow.Hide();
        HandleChanged?.Invoke(IntPtr.Zero);
    }

    private void AnimateClose()
    {
        if (_closing) return;
        _closing = true;
        if (!Anim.Enabled) { HideWindow(); return; }
        _open.SetTarget(0, Motion.FadeOut);
        _closeTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.05, _open.Model.RemainingDuration));
        _closeTimer.Start();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedIndex)) ShowTab(animate: true);
    }

    private object? _shownTab;

    private void ShowTab(bool animate)
    {
        double y = _vm.SelectedIndex * (Theme.SettingsTabHeight + TabRowGap);
        if (animate) { _tab.SetTarget(y, Motion.TabHighlight); _tabTrail.SetTarget(y, Motion.TabTrail); }
        else { _tab.Snap(y); _tabTrail.Snap(y); }
        foreach (var (tab, icon, label) in _tabRows)
        {
            bool on = ReferenceEquals(tab, _vm.SelectedTab);
            icon.Foreground = on ? Theme.AccentReadableBrush : Theme.SecondaryText;
            label.Foreground = on ? Theme.PrimaryText : Theme.SecondaryText;
            label.FontFamily = on ? Theme.FontSemiBold : Theme.FontMedium;
            label.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium;
        }
        var selected = _vm.SelectedTab.Content;
        if (!_views.ContainsKey(selected) && _factories.TryGetValue(selected, out var make))
        {
            var view = PerfLog.Time($"build {selected.GetType().Name.Replace("ViewModel", "")}", make);
            _views[selected] = view;
            _content.Children.Insert(0, view); // under the close button
        }
        foreach (var (key, view) in _views) view.Visibility = ReferenceEquals(key, selected) ? Visibility.Visible : Visibility.Collapsed;
        bool changed = !ReferenceEquals(_shownTab, selected);
        _shownTab = selected;
        if ((changed || !animate) && _views.TryGetValue(selected, out var shown)) PlayEntrance(shown);
    }

    /// <summary>
    /// The section's blocks fade and rise in with a short stagger (the Customisation tab runs its own card
    /// stagger). One-shot compositor keyframes: nothing runs once they land. Rows that own their translation
    /// (the Pages tab's drag-to-reorder list) are left alone, only top-level blocks move there.
    /// </summary>
    private static void PlayEntrance(FrameworkElement view)
    {
        if (!Anim.Enabled || view is CustomisationTabView) return;
        var blocks = new List<UIElement>();
        if (view is Panel panel)
            foreach (var child in panel.Children)
            {
                if (child.Visibility != Visibility.Visible) continue;
                if (view is not PagesTabView && child is ScrollViewer { Content: Panel inner })
                    blocks.AddRange(inner.Children.Where(x => x.Visibility == Visibility.Visible));
                else blocks.Add(child);
            }
        if (blocks.Count == 0) return;
        var c = Anim.VisualOf(view).Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.85f), new Vector2(0.25f, 1f));
        var duration = TimeSpan.FromMilliseconds(340 / Motion.Speed);
        for (int i = 0; i < blocks.Count; i++)
        {
            var v = Anim.VisualOf(blocks[i], translation: true);
            var delay = TimeSpan.FromMilliseconds(Math.Min(i, Theme.SettingsEntranceMax) * Theme.SettingsEntranceStepMs * Motion.StaggerScale);
            var o = c.CreateScalarKeyFrameAnimation();
            o.InsertKeyFrame(0, 0);
            o.InsertKeyFrame(1, 1, ease);
            o.Duration = duration;
            o.DelayTime = delay;
            o.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            var t = c.CreateVector3KeyFrameAnimation();
            t.InsertKeyFrame(0, new Vector3(0, (float)Theme.SettingsEntranceRise, 0));
            t.InsertKeyFrame(1, Vector3.Zero, ease);
            t.Duration = duration;
            t.DelayTime = delay;
            t.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            v.StartAnimation("Opacity", o);
            v.StartAnimation("Translation", t);
        }
    }

    /// <summary>App mark (an accent-gradient chip with a soft halo), the app name and "Settings".</summary>
    private static FrameworkElement BrandHeader()
    {
        var g = new Grid { Margin = new Thickness(Theme.S1, 0, 0, Theme.S4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = new Grid { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        mark.Children.Add(new Border { Width = 54, Height = 54, Margin = new Thickness(-12), Background = Theme.AccentHaloBrush, IsHitTestVisible = false });
        mark.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(9), Background = Theme.AccentGradientBrush, BorderBrush = Theme.AccentBorderBrush, BorderThickness = new Thickness(1),
            Child = new VectorIcon { Data = Icons.SparkleFilled, Size = Theme.IconSmall, Foreground = Theme.OnAccentBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        g.Children.Add(mark);
        var text = new StackPanel { Margin = new Thickness(Theme.S3 - 2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Dynamic Notch", Style = AskPage.TextStyle("BaseText"), FontFamily = Theme.FontBold, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = Theme.FontBody + 1 });
        text.Children.Add(new TextBlock { Text = "Settings", Style = AskPage.TextStyle("CaptionText"), Foreground = Theme.SecondaryText });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        return g;
    }

    private FrameworkElement TabRow(SettingsTab tab)
    {
        var row = new Grid { Height = Theme.SettingsTabHeight, Background = Theme.TransparentBrush, Tag = NoDragTag };
        var hover = new Border { Background = Theme.CardHoverBrush, CornerRadius = Theme.RadiusSmall, IsHitTestVisible = false };
        row.Children.Add(hover);
        Motions.HoverFade(row, hover, () => !ReferenceEquals(tab, _vm.SelectedTab));
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S3 + 2, 0, 0, 0) };
        var icon = new VectorIcon { Data = tab.Icon, Size = Theme.IconSmall, Foreground = Theme.SecondaryText, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = tab.Title, Style = AskPage.TextStyle("BaseText"), Margin = Theme.GapLeftS3, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(icon);
        line.Children.Add(label);
        row.Children.Add(line);
        _tabRows.Add((tab, icon, label));
        Motions.PressScale(row);
        row.Tapped += (_, _) => _vm.SelectedTab = tab;
        return row;
    }

    /// <summary>Soft shadow under the card: a drop shadow masked by a live surface of the card.</summary>
    /// <summary>
    /// Pre-rendered soft shadow under the card (SoftShadow). The old drop shadow was masked by a live surface
    /// of the whole card, re-rendered on every hover fade, slider drag or preview animation inside it.
    /// </summary>
    private void BuildShadow(Compositor c)
    {
        var caster = _caster = SoftShadow.Create(c, Theme.CornerRadius, Theme.SettingsShadowBlur,
            Math.Min(1, Theme.SettingsShadowOpacity * Theme.ShadowIntensity), Theme.SettingsShadowOffsetY);
        SoftShadow.Layout(caster, new Vector2((float)Math.Max(1, _card.ActualWidth), (float)Math.Max(1, _card.ActualHeight)));
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

