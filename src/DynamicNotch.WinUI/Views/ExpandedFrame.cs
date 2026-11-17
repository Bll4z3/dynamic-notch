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
        var gear = new IconButton(26, Theme.IconSmall) { Icon = Icons.Settings, Foreground = Theme.SecondaryText, Command = vm.OpenSettingsCommand, Tooltip = "Settings" };
        _gearWrap.Child = gear;
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

        Music = new MusicPage(vm.Music);
        _pageViews[PageId.Music] = Music;
        Shelf = new ClipboardShelfPage(vm.Clipboard, vm.Shelf);
        _pageViews[PageId.Clipboard] = Shelf;
        Usage = new UsagePage(vm.Usage);
        _pageViews[PageId.Usage] = Usage;
        Hub = new HubPage(vm.Hub);
        _pageViews[PageId.Hub] = Hub;
        FocusTimer = new FocusPage(vm.Focus);
        _pageViews[PageId.Focus] = FocusTimer;
        // Ask keeps its slot in the strip (so page indices line up) but draws on its own full-width layer
        // with its own header and dots: it slides with the pager, and the shared header/footer fade out
        // as it arrives (as in the WPF app).
        _pageViews[PageId.Ask] = new Grid();
        Ask = new AskPage(vm.Ask, vm.Status, notch);
        _askLayer.Width = Theme.ExpandedWidth;
        _askLayer.Height = Theme.ExpandedMaxHeight;
        _askLayer.VerticalAlignment = VerticalAlignment.Top;
        _askLayer.Children.Add(Ask);
        Children.Add(_askLayer);
        notch.InsertScalar("AskIdx", -100);
        Anim.VisualOf(_askLayer, translation: true).StartAnimation("Translation",
            Anim.Expr(notch.Compositor, "Vector3((N.AskIdx - N.Page) * N.Slot, 0, 0)", ("N", notch)));
        var chrome = Anim.Expr(notch.Compositor, "Clamp(Abs(N.Page - N.AskIdx), 0, 1)", ("N", notch));
        Anim.VisualOf(_header).StartAnimation("Opacity", chrome);
        Anim.VisualOf(_footer).StartAnimation("Opacity", chrome);
        SetAskActive(false);

        Stagger.Add(_dateWrap, 0);
        Stagger.Add(_gearWrap, 0);
        foreach (var (el, order) in Music.StaggerBlocks) Stagger.Add(el, order);
        foreach (var (el, order) in Shelf.StaggerBlocks) Stagger.Add(el, order);
        foreach (var (el, order) in Usage.StaggerBlocks) Stagger.Add(el, order);
        foreach (var (el, order) in FocusTimer.StaggerBlocks) Stagger.Add(el, order);
        foreach (var (el, order) in Hub.StaggerBlocks) Stagger.Add(el, order);
        foreach (var (el, order) in Ask.StaggerBlocks) Stagger.Add(el, order);
        Stagger.Add(_footerInner, 5);

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
    public HubPage Hub { get; }
    public AskPage Ask { get; }

    /// <summary>
    /// While the Ask page is current, its layer takes every click over the open notch (the other
    /// pages' invisible header / footer underneath must not); otherwise it takes none.
    /// </summary>
    public void SetAskActive(bool active)
    {
        _askLayer.IsHitTestVisible = active;
        _askLayer.Background = active ? Theme.TransparentBrush : null;
        _content.IsHitTestVisible = !active;
    }
    /// <summary>Clipboard hover preview card, above everything (never hit-testable).</summary>
    public PreviewCard Preview { get; } = new();
    public PageNavigator Navigator { get; }
    public AlbumGlow Glow { get; }
    public StaggerGroup Stagger { get; } = new();

    /// <summary>Only enabled pages take part in the pager and the navigator.</summary>
    public void SetPages()
    {
        _strip.Children.Clear();
        foreach (var id in _vm.Pages.Enabled) _strip.Children.Add(_pageViews[id]);
        var enabled = _vm.Pages.Ordered.Where(p => _vm.Pages.IsEnabled(p.Id)).ToList();
        Navigator.SetPages(enabled);
        Ask.Dots.SetPages(enabled);
        int ask = _vm.Pages.IndexOf(PageId.Ask);
        _n.InsertScalar("AskIdx", ask >= 0 ? ask : -100);
        _askLayer.Visibility = ask >= 0 ? Visibility.Visible : Visibility.Collapsed;
        Glow.SetMusicIndex(_vm.Pages.IndexOf(PageId.Music));
        LayoutPages();
    }

    private void LayoutPages()
    {
        double w = _viewport.ActualWidth > 0 ? _viewport.ActualWidth : Theme.ExpandedWidth - Theme.ExpandedPadding.Left - Theme.ExpandedPadding.Right;
        double h = _viewport.ActualHeight > 0 ? _viewport.ActualHeight
            : Theme.ExpandedContentHeight - Theme.HeaderHeight - Theme.S2 - Theme.FooterHeight;
        foreach (var page in _pageViews.Values) { page.Width = w; page.Height = h; page.VerticalAlignment = VerticalAlignment.Top; }
        // The music page is shorter: its content centres in the space above the raised footer.
        Music.Height = Math.Max(0, h - (Theme.ExpandedHeight - Theme.MusicExpandedHeight));
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
