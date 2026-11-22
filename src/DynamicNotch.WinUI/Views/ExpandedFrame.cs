using System.ComponentModel;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.Views;

/// <summary>
/// The open notch (same layout as the WPF expanded layer): header (battery ring, bold time, lighter
/// date, settings), a horizontal pager clipped to its viewport, and the footer (notification hint,
/// page navigator, connected devices). The album glow sits behind everything. Motion comes from the
/// notch's property set: the pager follows "Page", the footer follows the body height, and the
/// blocks enter with a <see cref="StaggerGroup"/>.
/// </summary>
public sealed class ExpandedFrame : Grid
{
    private readonly MainViewModel _vm;
    private readonly CompositionPropertySet _n;
    private readonly Grid _content = new(), _header = new(), _footer = new(), _footerInner = new();
    private readonly Canvas _viewport = new(); // a Canvas: pages beyond the first must not be layout-clipped
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly RectangleGeometry _viewportClip = new();
    private readonly Border _dateWrap = new(), _gearWrap = new(), _glowHost = new();
    private readonly Grid _askLayer = new();
    private readonly CrossfadeText _date = new();
    private readonly TextButton _notify = new();
    private readonly Dictionary<PageId, FrameworkElement> _pageViews = new();

    public ExpandedFrame(MainViewModel vm, CompositionPropertySet notch)
    {
        _vm = vm;
        _n = notch;
        Width = Theme.ExpandedWidth;
        Height = Theme.ExpandedMaxHeight;
        VerticalAlignment = VerticalAlignment.Top;

        // Album glow behind the content (clipped by the notch shape like everything else).
        _glowHost.Width = Theme.ExpandedWidth;
        _glowHost.Height = Theme.ExpandedMaxHeight;
        _glowHost.IsHitTestVisible = false;
        Children.Add(_glowHost);
        Glow = new AlbumGlow(_glowHost, notch);

        _content.Margin = Theme.ExpandedPadding;
        _content.Height = Theme.ExpandedContentHeight;
        _content.VerticalAlignment = VerticalAlignment.Top;
        foreach (var h in new[] { Theme.HeaderRow, Theme.GapS2, new GridLength(1, GridUnitType.Star), Theme.FooterRow })
            _content.RowDefinitions.Add(new RowDefinition { Height = h });
        Children.Add(_content);

        // ---- Header: battery ring + bold time (shared with the collapsed notch) + lighter date · settings ----
        var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        HeaderRing = new BatteryRing { RingSize = Theme.HeaderRingSize, Thickness = Theme.HeaderRingThickness, Margin = Theme.GapRightS2, VerticalAlignment = VerticalAlignment.Center, GreenWhileCharging = true };
        HeaderTime = new CrossfadeText { VerticalAlignment = VerticalAlignment.Center };
        HeaderTime.SetStyle(t => t.Style = (Style)Application.Current.Resources["HeaderTimeText"]);
        _date.SetStyle(t => t.Style = (Style)Application.Current.Resources["HeaderDateText"]);
        _dateWrap.Child = _date;
        _dateWrap.Margin = Theme.GapLeftS2;
        _dateWrap.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(HeaderRing);
        left.Children.Add(HeaderTime);
        left.Children.Add(_dateWrap);
        _header.Children.Add(left);
        // Right: the header icons, in the order chosen in Settings ▸ Layout (Settings always among them). Each
        // feature has one identical icon button; Ask and the palette slide in over the pages, the rest open in
        // the header pop-up. The bell carries a dot while there's unread. Reordering slides them into place.
        _gear = new IconButton(26, Theme.IconSmall) { Icon = Icons.Settings, Foreground = Theme.SecondaryText, Command = vm.OpenSettingsCommand, Tooltip = "Settings" };
        _iconViews[PageRegistry.SettingsId] = _gear;
        Motions.SlideIntoPlace(_gear);
        foreach (var def in PageRegistry.All)
        {
            var id = def.Id;
            var button = new IconButton(26, Theme.IconSmall) { Icon = def.Icon, Foreground = Theme.SecondaryText, Tooltip = def.Name };
            button.Click += () => FeatureRequested?.Invoke(id);
            _iconButtons[id] = button;
            FrameworkElement host = button;
            if (id == PageId.Notifications)
            {
                var bellHost = new Grid();
                bellHost.Children.Add(button);
                _badge.Width = _badge.Height = Theme.HeaderBadgeSize;
                _badge.Fill = Theme.AccentBrush;
                _badge.Stroke = Theme.NotchBrush;
                _badge.StrokeThickness = 1.5;
                _badge.HorizontalAlignment = HorizontalAlignment.Right;
                _badge.VerticalAlignment = VerticalAlignment.Top;
                _badge.Margin = new Thickness(0, 3, 4, 0);
                _badge.IsHitTestVisible = false;
                bellHost.Children.Add(_badge);
                host = bellHost;
            }
            Motions.SlideIntoPlace(host);
            _iconViews[id.ToString()] = host;
        }
        _gearWrap.Child = _icons;
        _gearWrap.HorizontalAlignment = HorizontalAlignment.Right;
        _gearWrap.VerticalAlignment = VerticalAlignment.Center;
        _header.Children.Add(_gearWrap);
        _content.Children.Add(_header);

        // ---- Pager: pages side by side, slid by the page spring, clipped to the viewport ----
        SetRow(_viewport, 2);
        _viewport.Clip = _viewportClip;
        _viewport.Children.Add(_strip);
        _content.Children.Add(_viewport);
        _viewport.SizeChanged += (_, e) =>
        {
            _viewportClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            _n.InsertScalar("Slot", (float)e.NewSize.Width);
            LayoutPages();
        };
        _n.InsertScalar("Slot", (float)(Theme.ExpandedWidth - Theme.ExpandedPadding.Left - Theme.ExpandedPadding.Right));
        Anim.VisualOf(_strip, translation: true).StartAnimation("Translation", Anim.Expr(notch.Compositor, "Vector3(-N.Page * N.Slot, 0, 0)", ("N", notch)));

        // ---- Footer: notification-permission hint · page navigator · connected devices ----
        SetRow(_footer, 3);
        _notify.HorizontalAlignment = HorizontalAlignment.Left;
        _notify.VerticalAlignment = VerticalAlignment.Center;
        _notify.Command = vm.EnableNotificationsCommand;
        Navigator = new PageNavigator(notch) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Navigator.PageClicked += i => _vm.PageIndex = i;
        var devices = new DevicesRow(vm.Devices) { VerticalAlignment = VerticalAlignment.Center };
        _footerInner.Children.Add(_notify);
        _footerInner.Children.Add(Navigator);
        _footerInner.Children.Add(devices);
        _footer.Children.Add(_footerInner);
        _content.Children.Add(_footer);
        // The footer keeps a constant distance from the notch's bottom edge as the height changes
        // between the music page and the taller pages (compositor transform only).
        var shift = Anim.Expr(notch.Compositor, $"Vector3(0, Clamp(N.H - {Anim.F(Theme.ExpandedHeight)}, {Anim.F(Theme.MusicExpandedHeight - Theme.ExpandedHeight)}, 0), 0)", ("N", notch));
        Anim.VisualOf(_footer, translation: true).StartAnimation("Translation", shift);

        Music = new MusicPage(vm.Music, vm.SpotifyLike);
        _pageViews[PageId.Music] = Music;
        Shelf = new ClipboardShelfPage(vm.Clipboard, vm.Shelf);
        _pageViews[PageId.Clipboard] = Shelf;
        Usage = new UsagePage(vm.Usage);
        _pageViews[PageId.Usage] = Usage;
        FocusTimer = new FocusPage(vm.Focus);
        _pageViews[PageId.Focus] = FocusTimer;
        Screenshots = new ScreenshotPage(vm.Screenshots);
        _pageViews[PageId.Screenshots] = Screenshots;
        Notifications = new HistoryPanel(vm.Hub);
        _pageViews[PageId.Notifications] = Notifications;
        QuickActions = new QuickActionsPanel(vm.Hub);
        _pageViews[PageId.QuickActions] = QuickActions;
        // ---- Ask Claude and the command palette: each on its own full-width layer (they resize the notch).
        // As a page, the layer slides with the pager (an empty slot holds its place in the strip) while the
        // shared header / footer fade out; as a header icon it slides in from the right over the pages.
        Ask = new AskPage(vm.Ask, vm.Status, notch);
        Ask.CloseRequested += () => OverlayRequested?.Invoke(NotchOverlay.None);
        _pageViews[PageId.Ask] = new Grid();
        AddOverlayLayer(_askLayer, Ask, "AskOv", "AskIdx");
        Palette = new CommandPaletteView(vm.Palette, notch);
        Palette.CloseRequested += () => OverlayRequested?.Invoke(NotchOverlay.None);
        _pageViews[PageId.Palette] = new Grid();
        AddOverlayLayer(_paletteLayer, Palette, "PalOv", "PalIdx");
        string Near(string idx) => $"(N.{idx} >= 0 ? 1 - Clamp(Abs(N.Page - N.{idx}), 0, 1) : 0)";
        Anim.VisualOf(_content).StartAnimation("Opacity", Anim.Expr(notch.Compositor,
            $"1 - Clamp(Max(Max(N.AskOv, N.PalOv), Max({Near("AskIdx")}, {Near("PalIdx")})), 0, 1)", ("N", notch)));
        SetSurfaces(false, false);

        Stagger.Add(_dateWrap, 0);
        Stagger.Add(_gearWrap, 0);
        foreach (var (el, order) in Music.StaggerBlocks) Stagger.Add(el, order);
        foreach (var (el, order) in Shelf.StaggerBlocks) Stagger.Add(el, order);
        foreach (var (el, order) in Usage.StaggerBlocks) Stagger.Add(el, order);
        foreach (var (el, order) in FocusTimer.StaggerBlocks) Stagger.Add(el, order);
        foreach (var (el, order) in Screenshots.StaggerBlocks) Stagger.Add(el, order);
        foreach (var (el, order) in Notifications.StaggerBlocks) Stagger.Add(el, order);
        Stagger.Add(_footerInner, 5);

        // ---- Header pop-up (the header-icon features): over the pager and footer, under the header ----
        Popup = new HeaderPopup();
        SetRow(Popup, 2);
        SetRowSpan(Popup, 2);
        _content.Children.Add(Popup);
        Popup.Changed += _ => UpdateIconStates();
        Popup.Shown += OnPanelShown;
        Popup.CloseInner = id => id == PageId.QuickActions && QuickActions.ClosePopover();
        var badge = Anim.VisualOf(_badge);
        badge.CenterPoint = new System.Numerics.Vector3((float)Theme.HeaderBadgeSize / 2, (float)Theme.HeaderBadgeSize / 2, 0);
        var (badgeSpring, badgeSet) = Motions.Value(notch.Compositor, Motion.Pop, 0);
        _badgeShow = badgeSpring;
        badge.StartAnimation("Scale", Anim.Expr(notch.Compositor, "Vector3(p.v, p.v, 1)", ("p", badgeSet)));
        badge.StartAnimation("Opacity", Anim.Expr(notch.Compositor, "Clamp(p.v, 0, 1)", ("p", badgeSet)));
        vm.Hub.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HubViewModel.UnreadCount)) UpdateBadge(); };
        UpdateBadge();

        // Preview layer on top of everything in the open notch.
        var previewLayer = new Canvas { IsHitTestVisible = false };
        previewLayer.Children.Add(Preview);
        Children.Add(previewLayer);

        vm.Status.PropertyChanged += OnStatusChanged;
        vm.PropertyChanged += OnVmChanged;
        OnStatusChanged(null, new PropertyChangedEventArgs(""));
        OnVmChanged(null, new PropertyChangedEventArgs(""));
        SetPages();
    }

    public BatteryRing HeaderRing { get; }
    public CrossfadeText HeaderTime { get; }
    public MusicPage Music { get; }
    public ClipboardShelfPage Shelf { get; }
    public UsagePage Usage { get; }
    public FocusPage FocusTimer { get; }
    public ScreenshotPage Screenshots { get; }
    /// <summary>The pop-up under the header icons (every feature that's a header icon).</summary>
    public HeaderPopup Popup { get; }
    public HistoryPanel Notifications { get; }
    public QuickActionsPanel QuickActions { get; }
    public AskPage Ask { get; }
    public CommandPaletteView Palette { get; }

    /// <summary>A header icon was clicked (the window opens the feature as a pop-up or an overlay).</summary>
    public event Action<PageId>? FeatureRequested;
    /// <summary>Ask's × / the palette asked to go back to the pages.</summary>
    public event Action<NotchOverlay>? OverlayRequested;

    private readonly Grid _paletteLayer = new();
    private readonly IconButton _gear;
    private readonly StackPanel _icons = new() { Orientation = Orientation.Horizontal, Spacing = Theme.HeaderIconGap };
    private readonly Dictionary<string, FrameworkElement> _iconViews = new();
    private readonly Dictionary<PageId, IconButton> _iconButtons = new();
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _badge = new();
    private readonly CompSpring _badgeShow;
    private NotchOverlay _overlay;

    private void UpdateBadge() => _badgeShow.SetTarget(_vm.Hub.UnreadCount > 0 ? 1 : 0, Motion.Pop);

    /// <summary>A feature in the pop-up came into view: fresh ages, live values, unread cleared.</summary>
    private void OnPanelShown(PageId id)
    {
        switch (id)
        {
            case PageId.Notifications: Notifications.OnShown(); _vm.Hub.MarkRead(); break;
            case PageId.QuickActions: QuickActions.OnShown(); break;
            case PageId.Screenshots: Screenshots.OnShown(); break;
        }
    }

    /// <summary>A page came into view (the same refreshes as in the pop-up).</summary>
    public void OnPageShown(PageId id) => OnPanelShown(id);

    /// <summary>The open feature's icon is lit (its pop-up or overlay is showing).</summary>
    public void UpdateIconStates(NotchOverlay? overlay = null)
    {
        if (overlay is { } o) _overlay = o;
        foreach (var (id, b) in _iconButtons)
        {
            bool on = Popup.Current == id || id == PageId.Ask && _overlay == NotchOverlay.Ask || id == PageId.Palette && _overlay == NotchOverlay.Palette;
            b.Foreground = on ? Theme.PrimaryText : Theme.SecondaryText;
            b.Backing = on ? Theme.CardHoverBrush : null;
        }
    }

    private void AddOverlayLayer(Grid layer, FrameworkElement view, string key, string idx)
    {
        layer.Width = Theme.ExpandedWidth;
        layer.Height = Theme.ExpandedMaxHeight;
        layer.VerticalAlignment = VerticalAlignment.Top;
        layer.Children.Add(view);
        Children.Add(layer);
        _n.InsertScalar(key, 0);
        _n.InsertScalar(idx, -100);
        var v = Anim.VisualOf(layer, translation: true);
        v.StartAnimation("Translation", Anim.Expr(_n.Compositor,
            $"Vector3(N.{idx} >= 0 ? (N.{idx} - N.Page) * N.Slot : (1 - Clamp(N.{key}, 0, 1)) * {Anim.F(Motion.HeaderPopupSlide)}, 0, 0)", ("N", _n)));
        v.StartAnimation("Opacity", Anim.Expr(_n.Compositor, $"N.{idx} >= 0 ? 1 : Clamp(N.{key} * 1.3, 0, 1)", ("N", _n)));
    }

    /// <summary>
    /// The Ask / palette layer that's showing takes every click over the open notch (the faded pages
    /// underneath must not); with neither, the pages take them.
    /// </summary>
    public void SetSurfaces(bool ask, bool palette)
    {
        _askLayer.IsHitTestVisible = ask;
        _askLayer.Background = ask ? Theme.TransparentBrush : null;
        _paletteLayer.IsHitTestVisible = palette;
        _paletteLayer.Background = palette ? Theme.TransparentBrush : null;
        _content.IsHitTestVisible = !ask && !palette;
    }

    /// <summary>Clipboard hover preview card, above everything (never hit-testable).</summary>
    public PreviewCard Preview { get; } = new();
    public PageNavigator Navigator { get; }
    public AlbumGlow Glow { get; }
    public StaggerGroup Stagger { get; } = new();

    /// <summary>
    /// Applies the layout (Settings ▸ Layout): pages into the pager (in order), header-icon and hidden features
    /// into the pop-up (hidden ones simply have no icon), the header icons in order, the page dots, and where
    /// Ask / the palette live. Moved icons and pages slide into place.
    /// </summary>
    public void SetPages()
    {
        var pages = _vm.Pages;
        _strip.Children.Clear();
        foreach (var def in PageRegistry.All)
        {
            var id = def.Id;
            if (id is PageId.Ask or PageId.Palette) continue; // their layers stay; only the empty slot moves
            var view = _pageViews[id];
            if (pages.IsEnabled(id)) { if (Popup.Has(id)) Popup.RemovePanel(id); }
            else if (!Popup.Has(id))
            {
                Detach(view);
                bool narrow = id is PageId.Notifications or PageId.QuickActions;
                double width = narrow ? Theme.HeaderPopupWidth : Theme.ExpandedWidth - Theme.ExpandedPadding.Left - Theme.ExpandedPadding.Right;
                view.Width = narrow ? double.NaN : width - Theme.HeaderPopupPadding.Left - Theme.HeaderPopupPadding.Right - 2;
                view.Height = narrow ? double.NaN : Theme.HeaderPopupInnerHeight;
                Popup.SetPanel(id, view, width);
            }
        }
        foreach (var id in pages.Enabled) { Detach(_pageViews[id]); _strip.Children.Add(_pageViews[id]); }
        _n.InsertScalar("AskIdx", pages.IndexOf(PageId.Ask) is var a and >= 0 ? a : -100);
        _n.InsertScalar("PalIdx", pages.IndexOf(PageId.Palette) is var b and >= 0 ? b : -100);
        var enabled = pages.Enabled.Select(PageRegistry.Def).ToList();
        Navigator.SetPages(enabled);
        Ask.Dots.SetPages(enabled);
        Palette.Dots.SetPages(enabled);
        // After SetPages (it shows the dots): only a page-mode Ask / palette keeps its dots.
        Ask.PageMode = pages.IsEnabled(PageId.Ask);
        Palette.PageMode = pages.IsEnabled(PageId.Palette);
        Glow.SetMusicIndex(pages.IndexOf(PageId.Music));
        LayoutIcons();
        LayoutPages();
    }

    private static void Detach(FrameworkElement view)
    {
        if (view.Parent is Microsoft.UI.Xaml.Controls.Panel p) p.Children.Remove(view);
        else if (view.Parent is Border b && ReferenceEquals(b.Child, view)) b.Child = null;
    }

    /// <summary>The header icons in the layout's order; moved ones slide over.</summary>
    public void LayoutIcons()
    {
        var visible = _vm.Pages.IconIds.Where(_iconViews.ContainsKey).Select(id => _iconViews[id]).ToList();
        if (_icons.Children.Cast<UIElement>().SequenceEqual(visible)) return;
        _icons.Children.Clear();
        foreach (var el in visible) _icons.Children.Add(el);
    }

    private void LayoutPages()
    {
        double w = _viewport.ActualWidth > 0 ? _viewport.ActualWidth : Theme.ExpandedWidth - Theme.ExpandedPadding.Left - Theme.ExpandedPadding.Right;
        double h = _viewport.ActualHeight > 0 ? _viewport.ActualHeight
            : Theme.ExpandedContentHeight - Theme.HeaderHeight - Theme.S2 - Theme.FooterHeight;
        foreach (var page in _strip.Children.OfType<FrameworkElement>()) { page.Width = w; page.Height = h; page.VerticalAlignment = VerticalAlignment.Top; }
        // The music page is shorter: its content centres in the space above the raised footer.
        if (_vm.Pages.IsEnabled(PageId.Music)) Music.Height = Math.Max(0, h - (Theme.ExpandedHeight - Theme.MusicExpandedHeight));
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        var s = _vm.Status;
        HeaderTime.Text = s.TimeText;
        _date.Text = s.DateText;
        HeaderRing.Percent = s.BatteryPercent;
        HeaderRing.IsCharging = s.IsCharging;
        HeaderRing.Visibility = s.HasBattery ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("" or null or nameof(MainViewModel.ShowNotificationPrompt) or nameof(MainViewModel.NotificationPromptText))) return;
        _notify.Visibility = _vm.ShowNotificationPrompt ? Visibility.Visible : Visibility.Collapsed;
        _notify.SetContent(Icons.Bell, _vm.NotificationPromptText);
    }
}
