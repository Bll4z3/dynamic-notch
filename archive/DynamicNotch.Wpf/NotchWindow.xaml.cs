using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using DynamicNotch.Views;

namespace DynamicNotch;

public enum NotchState { Collapsed, Pill, Expanded }

/// <summary>
/// The overlay window. Owns the notch state machine (collapsed ▸ pill ▸ expanded) and drives every
/// visual from springs, so any interaction can interrupt and re-target an animation mid-flight.
/// Per frame it only touches the shape geometry, clips, transforms and opacity, never layout.
/// The window never takes focus, except when the user deliberately invokes Ask (the hotkey or a
/// click into its text box); focus then goes back to the previous window when the notch closes.
/// </summary>
public partial class NotchWindow : Window
{
    private const int AskHotkeyId = 0x4E4F; // "NO"

    private readonly MainViewModel _vm;
    private IntPtr _hwnd;
    private KeyboardHookService? _keys;

    // Springs (pixel springs get a coarser rest precision so they settle promptly).
    private readonly Spring _width = new(Motion.Notch, Theme.CollapsedWidth) { Precision = 100 };
    private readonly Spring _height = new(Motion.Notch, Theme.CollapsedHeight) { Precision = 100 };
    private readonly Spring _pillContent = new(Motion.Pop, 0);
    private readonly Spring _collapsedContent = new(Motion.FadeIn, 0);
    private readonly Spring _page = new(Motion.Page, 0);
    private readonly Spring _dropGlow = new(Motion.Hover, 0);

    private NotchState _state = NotchState.Collapsed;
    private bool _hover, _mouseOver, _hoverPill, _pinned, _dragIn, _outgoingDrag;
    private bool _expandedRevealed;
    private readonly DispatcherTimer _expandTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _dragLeaveTimer;

    // Ask: focus ownership, region selection hint.
    private bool _hasFocus, _selecting, _hint;
    private IntPtr _previousForeground;
    private double _morphFullHeight = Theme.ExpandedHeight;

    private StaggerGroup? _stagger;

    // Shared-element ("matched geometry") pairs between the collapsed notch and the expanded notch.
    private sealed class Matched
    {
        public required FrameworkElement Collapsed, Expanded;
        public readonly ScaleTransform CScale = new(), EScale = new();
        public readonly TranslateTransform CMove = new(), EMove = new();
        public Func<bool> Active = () => true;
        /// <summary>+1 = collapsed element hugs the left body edge, -1 = right edge, 0 = centred.</summary>
        public int Edge;
    }
    private readonly List<Matched> _matched = new();
    private readonly TranslateTransform _collapsedRightShift = new();
    private readonly TranslateTransform _footerShift = new();
    private (double, double, double, double, bool, NotchState) _lastFrameKey;

    private Native.WinEventDelegate? _foregroundHook;
    private IntPtr _foregroundHookHandle;

    public NotchWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        _expandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.HoverExpandDelayMs) };
        _expandTimer.Tick += (_, _) => { _expandTimer.Stop(); _hover = true; UpdateState(); };

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.HoverCollapseDelayMs) };
        _collapseTimer.Tick += (_, _) => OnCollapseTimer();

        _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _dragLeaveTimer.Tick += (_, _) => { _dragLeaveTimer.Stop(); SetDragIn(false); ScheduleCollapseCheck(); };

        CollapsedRight.RenderTransform = _collapsedRightShift;
        FooterHost.RenderTransform = _footerShift;
        WireSprings();
        WireGlow();
        WirePreview();

        { var enabledPages = PageRegistry.All.Where(p => _vm.Pages.IsEnabled(p.Id)).ToList(); Dots.SetPages(enabledPages); AskPageView.Dots.SetPages(enabledPages); }
        _vm.PagesReconfigured += OnPagesReconfigured;
        Dots.PageClicked += i => _vm.PageIndex = i;
        AskPageView.Dots.PageClicked += i => _vm.PageIndex = i;

        _vm.PropertyChanged += OnVmChanged;
        _vm.Music.PropertyChanged += OnMusicChanged;
        _vm.Ask.PropertyChanged += OnAskChanged;
        _vm.Ask.SelectAreaRequested += () => _ = SelectAreaAsync();
        AskPageView.NotchSizeChanged += () => { if (_state == NotchState.Expanded && _vm.IsAskPage) ApplyTargets(); };
        AskPageView.FocusRequested += () => { TakeFocus(); Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(UpdateKeyCapture)); };
        ShelfPageView.OutgoingDrag += active => { _outgoingDrag = active; if (!active) ScheduleCollapseCheck(); else UpdateState(); };

        Loaded += (_, _) =>
        {
            LayoutPages();
            _stagger = new StaggerGroup(ExpandedLayer);
            // The header ring/time only exist on pages 1-2; the Ask page has its own header.
            AddMatched(NotchRing, HeaderRing, edge: +1, active: () => !Near(AskIdx));
            AddMatched(CollapsedTime, HeaderTime, edge: 0, active: () => !Near(AskIdx));
            AddMatched(MiniArt, MusicPageView.ArtElement, edge: -1,
                active: () => _vm.Music.IsPlaying && !_vm.MinimalNotch && Near(MusicIdx));
            ApplyPageVisibility();
            ApplyTargets(immediate: true);
            UpdateGlowColor(animate: false);
            OnFrame();
        };
        PageViewport.SizeChanged += (_, _) => LayoutPages();

        // Esc / typing keys arrive here only while the user gave the notch focus (Ask).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CollapseAsk(); e.Handled = true; }
        };
        Deactivated += (_, _) => OnDeactivated();
    }

    /// <summary>Connects the global keyboard hook (arrow keys / Space / Esc without focus).</summary>
    public void AttachKeyboard(KeyboardHookService keys)
    {
        _keys = keys;
        keys.KeyPressed += OnHookKey;
        keys.OutsideClick += () =>
        {
            if (!_pinned || _vm.Ask.IsBusy || _selecting) return; // keep an in-flight answer on screen
            Unpin();
            ScheduleCollapseCheck();
        };
        UpdateKeyCapture();
    }

    // =========================================================================================
    // Window setup: styles, position, topmost, DPI / display changes, hotkey
    // =========================================================================================
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Doesn't activate on click: the user's app keeps keyboard focus unless Ask is invoked.
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST, Native.WS_EX_APPWINDOW);
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        PositionWindow();

        UpdateHotkey();

        // Re-assert topmost whenever another window (e.g. a full-screen app) comes to the front.
        _foregroundHook = (_, _, _, _, _, _, _) => Native.ReassertTopmost(_hwnd);
        _foregroundHookHandle = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundHook, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(PositionWindow, DispatcherPriority.Background);

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_foregroundHookHandle != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHookHandle);
        if (_hwnd != IntPtr.Zero && _hotkeyRegistered) Native.UnregisterHotKey(_hwnd, AskHotkeyId);
        base.OnClosed(e);
    }

    /// <summary>Top-centre of the primary monitor, in physical pixels (DPI-correct on any monitor setup).</summary>
    private void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        var (bounds, scale) = Native.GetPrimaryMonitor();
        int w = (int)Math.Round(Theme.HostWidth * scale);
        int h = (int)Math.Round(Theme.HostHeight * scale);
        int x = bounds.Left + (bounds.Width - w) / 2;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, bounds.Top, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                Dispatcher.InvokeAsync(PositionWindow, DispatcherPriority.Background);
                break;
            case Native.WM_SETTINGCHANGE:
                SpringAnimator.RefreshSystemSetting();
                break;
            case Native.WM_HOTKEY when wParam.ToInt32() == AskHotkeyId:
                OnAskHotkey();
                handled = true;
                break;
            case 0x0100 /* WM_KEYDOWN */ when _hasFocus && wParam.ToInt32() == 0x1B /* VK_ESCAPE */:
                // Handled here so Esc works even when no element inside the notch holds keyboard focus.
                Dispatcher.BeginInvoke(new Action(CollapseAsk));
                handled = true;
                break;
            case Native.WM_MOUSEACTIVATE when !_hasFocus:
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    // =========================================================================================
    // State machine
    // =========================================================================================
    private NotchState DesiredState()
    {
        if (_hint) return NotchState.Pill;
        if (_hover || _pinned || _dragIn || _outgoingDrag) return NotchState.Expanded;
        if (_vm.HasPill) return NotchState.Pill;
        return NotchState.Collapsed;
    }

    private void UpdateState()
    {
        var next = DesiredState();
        if (next == _state) { ApplyTargets(); return; }

        var prev = _state;
        _state = next;

        // Pills wait while the notch is open; they resume (with a fresh timer) when it closes.
        _vm.HoldPill(next == NotchState.Expanded || _hint);

        if (next == NotchState.Expanded && _vm.IsShelfPage) _vm.Shelf.RefreshExistence();
        if (prev == NotchState.Expanded && next != NotchState.Expanded)
        {
            Unpin();
            ShelfPageView.ClosePreview();
            if (!_selecting)
            {
                _vm.Ask.CancelIfBusy();     // collapsing with a request in flight cancels it
                ReleaseFocus(restore: true); // and hands focus back to where the user was
            }
        }

        ApplyTargets();
    }

    /// <summary>Expanded size for the current page: Ask sizes itself to its state and content.</summary>
    private Size ExpandedTarget() => _vm.IsAskPage
        ? AskPageView.DesiredNotchSize
        : new Size(Theme.ExpandedWidth, _vm.IsMusicPage ? Theme.MusicExpandedHeight : Theme.ExpandedHeight);

    private void ApplyTargets(bool immediate = false)
    {
        bool minimal = _vm.MinimalNotch;
        bool playing = _vm.Music.IsPlaying;

        var expanded = ExpandedTarget();
        _morphFullHeight = expanded.Height;

        double w, h;
        SpringPreset preset = Motion.Notch;
        switch (_state)
        {
            case NotchState.Expanded:
                w = expanded.Width; h = expanded.Height;
                if (_vm.IsAskPage && _expandedRevealed) preset = Motion.AskResize;
                break;
            case NotchState.Pill:
                w = Theme.PillWidth; h = Theme.PillHeight; preset = Motion.Pop; // pop with a little overshoot
                break;
            default:
                w = minimal ? Theme.MinimalWidth : playing ? Theme.CollapsedPlayingWidth : Theme.CollapsedWidth;
                h = minimal ? Theme.MinimalHeight : Theme.CollapsedHeight;
                break;
        }

        if (immediate) { _width.Snap(w); _height.Snap(h); }
        else { _width.SetTarget(w, preset); _height.SetTarget(h, preset); }

        // Content leaving the stage fades out right away (faster than it arrives); incoming
        // content waits for the container (see RevealContent, evaluated every frame).
        if (_state != NotchState.Expanded && _expandedRevealed)
        {
            _expandedRevealed = false;
            _stagger?.Hide();
        }
        if (_state != NotchState.Pill) _pillContent.SetTarget(0, Motion.FadeOut);
        // Toward the expanded notch the collapsed bits fade a little slower so their shared-element flight
        // reads; toward a pill they get out of the way quickly.
        if (_state != NotchState.Collapsed || minimal)
            _collapsedContent.SetTarget(0, _state == NotchState.Expanded ? Motion.FadeIn : Motion.FadeOut);
        CollapsedMusic.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        HeaderRing.ShowNumber = _state == NotchState.Expanded; // the % shows while the notch is open

        ExpandedLayer.IsHitTestVisible = _state == NotchState.Expanded;
        AskLayer.IsHitTestVisible = _state == NotchState.Expanded && _vm.IsAskPage;
        PillLayer.IsHitTestVisible = _state == NotchState.Pill && !_hint;
        HoverStrip.Width = (minimal ? Theme.MinimalWidth : Theme.CollapsedWidth) + 60;

        Bars.IsPlaying = _state == NotchState.Collapsed && playing && !minimal;
        _vm.Music.IsVisible = _state == NotchState.Expanded && _vm.IsMusicPage;

        ShelfPageView.SetDropActive(_dragIn);
        UpdateGlowLevel();
        UpdateKeyCapture();
        RevealContent();
        if (immediate) OnFrame();
    }

    /// <summary>Starts incoming content once the container is far enough through its morph.</summary>
    private void RevealContent()
    {
        double p = MorphProgress();
        switch (_state)
        {
            case NotchState.Expanded when !_expandedRevealed && p >= Motion.ContentRevealThreshold:
                _expandedRevealed = true;
                _stagger?.Show();
                if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_PROFILE") == "1") LogMusicCentres();
                break;
            case NotchState.Pill when Progress(_height.Value, Theme.CollapsedHeight, Theme.PillHeight) >= Motion.ContentRevealThreshold:
                _pillContent.SetTarget(1, Motion.Pop);
                break;
            case NotchState.Collapsed when !_vm.MinimalNotch && p < 0.3 && _height.Value > Theme.CollapsedHeight * 0.6:
                _collapsedContent.SetTarget(1, Motion.FadeIn);
                break;
        }
    }

    /// <summary>Profile mode: confirm the music details column is centred on the cover (logged to frames.log).</summary>
    private void LogMusicCentres()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_state != NotchState.Expanded || !_vm.IsMusicPage) return;
            var (cover, details) = MusicPageView.MeasureCentres();
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(SettingsService.DataFolder, "frames.log"),
                    $"[{DateTime.Now:HH:mm:ss}] music centres: cover={cover:F2} details={details:F2} diff={details - cover:F2} DIP{Environment.NewLine}");
            }
            catch { }
        };
        t.Start();
    }

    /// <summary>0 = collapsed height, 1 = fully expanded for the current page (clamped).</summary>
    private double MorphProgress() => Math.Clamp(Progress(_height.Value, Theme.CollapsedHeight, _morphFullHeight), 0, 1);

    private static double Progress(double v, double from, double to) => to == from ? 1 : (v - from) / (to - from);

    private static double SmoothStep(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    // =========================================================================================
    // Per-frame rendering from springs
    // =========================================================================================
    private void WireSprings()
    {
        SpringAnimator.Instance.Frame += OnFrame;

        _pillContent.ValueChanged += v =>
        {
            PillLayer.Opacity = Math.Clamp(v, 0, 1);
            BlurFx.Reveal(PillLayer, v, Motion.PillBlur);
            double s = Motion.PillStartScale + (1 - Motion.PillStartScale) * v;
            PillScale.ScaleX = PillScale.ScaleY = s;
        };
        _collapsedContent.ValueChanged += v =>
        {
            CollapsedLayer.Opacity = Math.Clamp(v, 0, 1);
            BlurFx.Reveal(CollapsedLayer, v, Motion.CollapsedBlur);
        };
        _page.ValueChanged += _ =>
        {
            UpdatePager();
            // Light motion blur while sliding, proportional to speed; gone once the page settles.
            double blur = _page.IsSettled ? 0 : Math.Min(Motion.PageBlurMax, Math.Abs(_page.Velocity) * Motion.PageBlurPerVelocity);
            BlurFx.Set(PageStrip, blur);
            BlurFx.Set(AskLayer, blur);
        };
        _dropGlow.ValueChanged += v => Shape.Glow = v;
    }

    private void OnFrame()
    {
        Shape.SetSize(_width.Value, _height.Value);
        var body = Shape.BodyRect;

        // Only when the body actually moved: frames driven by other springs (visualiser, hover
        // fills) must not touch the clips or shared-element transforms, or the whole window redraws.
        var key = (body.Width, body.Height, NotchRoot.ActualWidth, _page.Value, _vm.Music.IsPlaying, _state);
        if (key == _lastFrameKey) { RevealContent(); return; }
        _lastFrameKey = key;

        // Content layers are clipped to the live body shape, so nothing ever overflows the morph.
        ClipTo(ExpandedLayer, Theme.ExpandedWidth);
        ClipTo(PillLayer, Theme.PillWidth);
        ClipTo(CollapsedLayer, Theme.CollapsedPlayingWidth);

        // The footer (navigator, devices) keeps a constant distance from the notch's bottom edge as
        // the height changes between the music page and the taller pages (transform only).
        _footerShift.Y = Math.Clamp(body.Height - Theme.ExpandedHeight, Theme.MusicExpandedHeight - Theme.ExpandedHeight, 0);

        // Collapsed side groups hug the body edges (transform only).
        double edgeShift = (Theme.CollapsedPlayingWidth - body.Width) / 2;
        _collapsedRightShift.X = -edgeShift;

        UpdateMatched(body);
        ApplyGlowOpacity();
        RevealContent();
    }

    private void ClipTo(FrameworkElement layer, double layerWidth)
    {
        double layerLeft = (NotchRoot.ActualWidth - layerWidth) / 2;
        layer.Clip = Shape.BodyClip(-layerLeft);
    }

    // ---- Shared-element transitions -----------------------------------------------------------
    private void AddMatched(FrameworkElement collapsed, FrameworkElement expanded, int edge, Func<bool>? active = null)
    {
        var m = new Matched { Collapsed = collapsed, Expanded = expanded, Edge = edge };
        if (active != null) m.Active = active;
        collapsed.RenderTransformOrigin = expanded.RenderTransformOrigin = new Point(0.5, 0.5);
        collapsed.RenderTransform = new TransformGroup { Children = { m.CScale, m.CMove } };
        expanded.RenderTransform = new TransformGroup { Children = { m.EScale, m.EMove } };
        _matched.Add(m);
        // Re-measure the pair whenever either side's layout changes (e.g. the clock text width).
        collapsed.SizeChanged += (_, _) => { _lastFrameKey = default; OnFrame(); };
        expanded.SizeChanged += (_, _) => { _lastFrameKey = default; OnFrame(); };
    }

    /// <summary>Layout rectangle of an element in NotchRoot coordinates, ignoring render transforms.</summary>
    private Rect LayoutRect(FrameworkElement el)
    {
        var offset = new Vector();
        DependencyObject? d = el;
        while (d is Visual v && !ReferenceEquals(d, NotchRoot))
        {
            offset += VisualTreeHelper.GetOffset(v);
            d = VisualTreeHelper.GetParent(d);
        }
        return new Rect(offset.X, offset.Y, el.ActualWidth, el.ActualHeight);
    }

    private void UpdateMatched(Rect body)
    {
        double p = MorphProgress();
        double edgeShift = (Theme.CollapsedPlayingWidth - body.Width) / 2;
        foreach (var m in _matched)
        {
            if (m.Collapsed.ActualHeight <= 0 || m.Expanded.ActualHeight <= 0) continue;
            var a = LayoutRect(m.Collapsed);
            a.X += m.Edge * edgeShift;                // where the collapsed element sits right now
            var b = LayoutRect(m.Expanded);
            if (ReferenceEquals(m.Expanded, MusicPageView.ArtElement)) b.X += PageTranslate.X;

            bool active = m.Active() && _state != NotchState.Pill; // shared elements only travel between collapsed and expanded
            double t = active ? p : 1;                // inactive: expanded element rests in place
            double ch = a.Height + (b.Height - a.Height) * t;
            var cc = new Point(a.X + a.Width / 2 + (b.X + b.Width / 2 - a.X - a.Width / 2) * t,
                               a.Y + a.Height / 2 + (b.Y + b.Height / 2 - a.Y - a.Height / 2) * t);

            // Collapsed element: follows the edge and flies toward the expanded slot.
            double cs = active ? ch / a.Height : 1;
            m.CScale.ScaleX = m.CScale.ScaleY = cs;
            var ac = LayoutRect(m.Collapsed);
            m.CMove.X = (active ? cc.X : a.X + a.Width / 2) - (ac.X + ac.Width / 2);
            m.CMove.Y = (active ? cc.Y : a.Y + a.Height / 2) - (ac.Y + ac.Height / 2);
            if (m.Edge == -1) m.CMove.X -= -edgeShift; // the right group already carries the edge shift
            m.Collapsed.Opacity = active ? 1 - SmoothStep(0, 0.45, p) : 1;

            // Expanded element: starts at the collapsed element's spot and size, settles in place.
            double es = ch / b.Height;
            m.EScale.ScaleX = m.EScale.ScaleY = active ? es : 1;
            m.EMove.X = active ? cc.X - (b.X + b.Width / 2) : 0;
            m.EMove.Y = active ? cc.Y - (b.Y + b.Height / 2) : 0;
            m.Expanded.Opacity = _state == NotchState.Expanded || p > 0.01
                ? SmoothStep(Motion.MatchedFadeFrom, Motion.MatchedFadeTo, p)
                : 0;
        }
    }

    // =========================================================================================
    // Pages
    // =========================================================================================
    private void LayoutPages()
    {
        double w = PageViewport.ActualWidth, h = PageViewport.ActualHeight;
        if (w <= 0) return;
        MusicPageView.Width = ShelfPageView.Width = w;
        ShelfPageView.Height = h;
        // The music page is shorter: its content centres in the space above the raised footer.
        MusicPageView.VerticalAlignment = VerticalAlignment.Top;
        MusicPageView.Height = Math.Max(0, h - (Theme.ExpandedHeight - Theme.MusicExpandedHeight));
        AskLayer.Width = Theme.ExpandedWidth;
        UpdatePager();
    }

    private void UpdatePager()
    {
        double slot = PageViewport.ActualWidth;
        PageTranslate.X = -_page.Value * slot;
        int ask = AskIdx;
        AskTranslate.X = ask >= 0 ? (ask - _page.Value) * slot : 4 * slot; // Ask slides in from the right
        double sharedChrome = ask >= 0 ? Math.Clamp(Math.Abs(_page.Value - ask), 0, 1) : 1;
        HeaderHost.Opacity = FooterHost.Opacity = sharedChrome; // Ask has its own header and dots
        Dots.Position = AskPageView.Dots.Position = _page.Value;
        ApplyGlowOpacity();
    }

    private void GoToPage(int index) => _vm.PageIndex = index;

    // ---- Pages switched on/off (Settings ▸ Pages) -------------------------------------------------
    private int MusicIdx => _vm.Pages.IndexOf(PageId.Music);
    private int AskIdx => _vm.Pages.IndexOf(PageId.Ask);
    /// <summary>True while the pager is (about) on the given enabled-page index.</summary>
    private bool Near(int index) => index >= 0 && Math.Abs(_page.Value - index) < 0.5;

    /// <summary>Only enabled pages take part in the pager; switched-off ones leave the layout entirely.</summary>
    private void ApplyPageVisibility()
    {
        bool music = _vm.Pages.IsEnabled(PageId.Music);
        bool clip = _vm.Pages.IsEnabled(PageId.Clipboard);
        bool ask = _vm.Pages.IsEnabled(PageId.Ask);
        MusicPageView.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        ShelfPageView.Visibility = clip ? Visibility.Visible : Visibility.Collapsed;
        AskLayer.Visibility = ask ? Visibility.Visible : Visibility.Collapsed;
        NotchRoot.AllowDrop = clip; // the shelf drop target goes with the page
        if (!clip) ShelfPageView.ClosePreview();
        { var enabledPages = PageRegistry.All.Where(p => _vm.Pages.IsEnabled(p.Id)).ToList(); Dots.SetPages(enabledPages); AskPageView.Dots.SetPages(enabledPages); }
        UpdateHotkey();
    }

    /// <summary>Ctrl+Shift+Space exists only while the Ask page is switched on.</summary>
    private bool _hotkeyRegistered;
    private void UpdateHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool want = _vm.Pages.IsEnabled(PageId.Ask);
        if (want && !_hotkeyRegistered)
        {
            _hotkeyRegistered = Native.RegisterHotKey(_hwnd, AskHotkeyId, AppConfig.AskHotkeyModifiers, AppConfig.AskHotkeyVirtualKey);
            if (!_hotkeyRegistered) System.Diagnostics.Debug.WriteLine($"{AppConfig.AskHotkeyLabel} is already used by another app.");
        }
        else if (!want && _hotkeyRegistered)
        {
            Native.UnregisterHotKey(_hwnd, AskHotkeyId);
            _hotkeyRegistered = false;
        }
    }

    private void OnPagesReconfigured(double from, bool currentRemoved)
    {
        if (!_vm.Pages.IsEnabled(PageId.Ask))
        {
            _vm.Ask.CancelIfBusy();
            if (_hasFocus) ReleaseFocus(restore: true);
        }
        ApplyPageVisibility();
        UpdateLayout();          // the strip re-flows before the pager is repositioned
        _page.Snap(from);        // same visual spot in the new layout; PageIndex then springs on from here
        _lastFrameKey = default;
        LayoutPages();
        ApplyTargets();
    }

    // =========================================================================================
    // View-model reactions
    // =========================================================================================
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.PageIndex):
                _page.SetTarget(_vm.PageIndex, Motion.Page);
                if (_vm.IsShelfPage) _vm.Shelf.RefreshExistence();
                else ShelfPageView.ClosePreview();
                if (!_vm.IsAskPage && _hasFocus) ReleaseFocus(restore: true); // no text boxes on the other pages
                _vm.Music.IsVisible = _state == NotchState.Expanded && _vm.IsMusicPage;
                ApplyTargets(); // the notch resizes for the Ask page
                break;
            case nameof(MainViewModel.CurrentPill):
                if (!_hint) ShowPill(_vm.CurrentPill);
                UpdateState();
                break;
            case nameof(MainViewModel.MinimalNotch):
                ApplyTargets();
                break;
        }
    }

    private void OnMusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or null or nameof(MusicViewModel.Artwork) or nameof(MusicViewModel.IsPlaying))
        {
            MiniArtBrush.ImageSource = _vm.Music.Artwork;
            UpdateGlowColor(animate: true);
            if (_state == NotchState.Collapsed) ApplyTargets();
            else UpdateGlowLevel();
        }
    }

    private void OnAskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AskViewModel.State) && _hasFocus)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(AskPageView.KeepFocus));
        if (e.PropertyName != nameof(AskViewModel.IsBusy) || _vm.Ask.IsBusy) return;
        // The answer finished: if nobody is looking, the notch may close again as usual.
        if (!_pinned && !_hasFocus && !_mouseOver) ScheduleCollapseCheck();
    }

    private void ShowPill(PillRequest? p)
    {
        if (p == null) return;
        bool isApp = p.Source == PillSource.App;
        PillLogo.Visibility = p.Logo != null ? Visibility.Visible : Visibility.Collapsed;
        PillLogoBrush.ImageSource = p.Logo;
        PillGlyph.Data = p.Logo != null ? null : p.Glyph ?? Icons.Bell;

        PillApp.Text = isApp ? p.AppName ?? "" : "";
        PillApp.Visibility = isApp && !string.IsNullOrEmpty(p.AppName) ? Visibility.Visible : Visibility.Collapsed;
        PillTitle.Text = p.Title;
        PillBody.Text = p.Subtitle;
        PillBody.Visibility = string.IsNullOrEmpty(p.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        PillBadge.Visibility = string.IsNullOrEmpty(p.Badge) ? Visibility.Collapsed : Visibility.Visible;
        PillBadgeText.Text = p.Badge == "off" ? "Off" : p.Badge ?? "";
        PillBadgeText.Foreground = p.Badge == "off" ? Theme.SecondaryText : Theme.PrimaryText;

        // Re-trigger the pop even if a pill is already showing.
        if (_state == NotchState.Pill)
        {
            _pillContent.Snap(0.6);
            _height.Impulse(-90);
        }
    }

    // =========================================================================================
    // Hover / pin / keyboard
    // =========================================================================================
    private void Notch_MouseEnter(object sender, MouseEventArgs e)
    {
        _mouseOver = true;
        _collapseTimer.Stop();
        NotchRing.ShowNumber = true;
        UpdateKeyCapture();
        if (_state == NotchState.Pill)
        {
            if (_hint) return;
            _hoverPill = true;
            _vm.HoldPill(true);
            return;
        }
        if (!_hover) _expandTimer.Start();
    }

    private void Notch_MouseLeave(object sender, MouseEventArgs e)
    {
        _mouseOver = false;
        _expandTimer.Stop();
        NotchRing.ShowNumber = false;
        UpdateKeyCapture();
        if (_hoverPill)
        {
            _hoverPill = false;
            _vm.HoldPill(false);
        }
        ScheduleCollapseCheck();
    }

    private void Pill_MouseEnter(object sender, MouseEventArgs e) { if (!_hint) _vm.HoldPill(true); }
    private void Pill_MouseLeave(object sender, MouseEventArgs e) { if (_state == NotchState.Pill && !_hint) _vm.HoldPill(false); }

    private void Pill_Click(object sender, MouseButtonEventArgs e)
    {
        if (_hint) return;
        _vm.DismissPillCommand.Execute(null);
        e.Handled = true;
    }

    private void ScheduleCollapseCheck()
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void OnCollapseTimer()
    {
        _collapseTimer.Stop();
        if (_pinned || _dragIn || _outgoingDrag || _selecting) return;
        // Typing into Ask, or waiting on an answer: stay open even if the mouse wandered off.
        if (_hasFocus || _vm.Ask.IsBusy) return;
        if (IsPointerOverNotch()) { _collapseTimer.Start(); return; } // still there (e.g. after a drop)
        _hover = false;
        UpdateState();
    }

    /// <summary>Hit-tests the real cursor position (works even when WPF missed a MouseLeave during a drag).</summary>
    private bool IsPointerOverNotch()
    {
        if (!Native.GetCursorPos(out var pt) || !IsLoaded) return false;
        var local = Shape.PointFromScreen(new Point(pt.X, pt.Y));
        var body = Shape.BodyRect;
        body.Inflate(Theme.CornerRadius, 2);
        return body.Contains(local);
    }

    private void Notch_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_state == NotchState.Pill) return; // the pill handles its own click (dismiss)
        if (!_pinned) Pin();
    }

    /// <summary>Keeps the notch open (until Esc or a click elsewhere) without taking focus.</summary>
    private void Pin()
    {
        _pinned = true;
        _hover = true;
        if (_keys != null && IsLoaded)
        {
            double h = Math.Max(Theme.ExpandedHeight, _morphFullHeight);
            var tl = NotchRoot.PointToScreen(new Point((NotchRoot.ActualWidth - Theme.ExpandedWidth) / 2 - Theme.CornerRadius, 0));
            var br = NotchRoot.PointToScreen(new Point((NotchRoot.ActualWidth + Theme.ExpandedWidth) / 2 + Theme.CornerRadius, h));
            _keys.SetPinned(true, (int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
        }
        UpdateState();
    }

    private void Unpin()
    {
        if (!_pinned) return;
        _pinned = false;
        _keys?.SetPinned(false, 0, 0, 0, 0);
        UpdateKeyCapture();
        ScheduleCollapseCheck();
    }

    /// <summary>Tells the hook which keys to take. Everything else always passes through untouched.</summary>
    private void UpdateKeyCapture()
    {
        if (_keys == null) return;
        bool open = _state == NotchState.Expanded;
        // While the user types in the notch, the hook stays completely out of the way.
        _keys.TypingActive = _hasFocus;
        _keys.ArrowsActive = !_hasFocus && (open || _mouseOver);
        _keys.PlayPauseActive = !_hasFocus && _mouseOver && open && _vm.IsMusicPage && _vm.Music.HasMedia;
        _keys.EscapeActive = !_hasFocus && _pinned;
    }

    private void OnHookKey(NotchKey key)
    {
        switch (key)
        {
            case NotchKey.Left:
                if (_state != NotchState.Expanded) { _hover = true; UpdateState(); }
                GoToPage(_vm.PageIndex - 1);
                break;
            case NotchKey.Right:
                if (_state != NotchState.Expanded) { _hover = true; UpdateState(); }
                GoToPage(_vm.PageIndex + 1);
                break;
            case NotchKey.PlayPause:
                if (_vm.Music.PlayPauseCommand.CanExecute(null)) _vm.Music.PlayPauseCommand.Execute(null);
                break;
            case NotchKey.Escape:
                CollapseAsk();
                break;
        }
    }

    // =========================================================================================
    // Ask: hotkey, focus ownership, region selection
    // =========================================================================================
    /// <summary>Ctrl+Shift+Space: open straight onto Ask with the box focused; again closes it.</summary>
    private void OnAskHotkey()
    {
        if (_selecting) return;
        if (_state == NotchState.Expanded && _vm.IsAskPage && (_pinned || _hasFocus))
        {
            CollapseAsk();
            return;
        }
        if (!_vm.GoTo(PageId.Ask)) return; // Ask switched off in Settings ▸ Pages
        _page.SetTarget(_vm.PageIndex, Motion.Page);
        Pin();
        TakeFocus();
        AskPageView.FocusInput();
        UpdateKeyCapture();
    }

    /// <summary>Esc / hotkey: close the notch and give focus back to the previous window.</summary>
    private void CollapseAsk()
    {
        _hover = false;
        Unpin();
        ReleaseFocus(restore: true);
        UpdateState();
        ScheduleCollapseCheck();
    }

    /// <summary>Take keyboard focus (only on a deliberate invoke), remembering who had it.</summary>
    private void TakeFocus()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!_hasFocus)
        {
            var fg = Native.GetForegroundWindow();
            if (fg != _hwnd && fg != IntPtr.Zero) _previousForeground = fg;
        }
        _hasFocus = true;
        Native.SetExStyle(_hwnd, 0, Native.WS_EX_NOACTIVATE);
        Activate();
        Native.SetForegroundWindow(_hwnd);
        UpdateKeyCapture();
    }

    /// <summary>Drop focus; optionally hand it back to the window the user was in.</summary>
    private void ReleaseFocus(bool restore)
    {
        if (!_hasFocus) return;
        _hasFocus = false;
        Keyboard.ClearFocus();
        if (_hwnd != IntPtr.Zero) Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, 0);
        var prev = _previousForeground;
        _previousForeground = IntPtr.Zero;
        if (restore && prev != IntPtr.Zero && Native.IsWindow(prev)) Native.SetForegroundWindow(prev);
        UpdateKeyCapture();
    }

    /// <summary>Another window was activated (the user clicked elsewhere): stop owning focus.</summary>
    private void OnDeactivated()
    {
        if (!_hasFocus || _selecting) return;
        _hasFocus = false;
        _previousForeground = IntPtr.Zero;
        if (_hwnd != IntPtr.Zero) Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, 0);
        UpdateKeyCapture();
        if (!_vm.Ask.IsBusy) { Unpin(); ScheduleCollapseCheck(); }
    }

    /// <summary>"Select area": shrink to a hint pill, show the overlay, capture, then come back to Ask.</summary>
    private async Task SelectAreaAsync()
    {
        if (_selecting) return;
        _selecting = true;
        bool hadFocus = _hasFocus;
        ReleaseFocus(restore: false);
        _hint = true;
        ShowPill(new PillRequest(PillSource.System, "Drag to select", "Enter for the whole screen · Esc to cancel", Icons.Crop));
        _hover = false;
        _pinned = false;
        _keys?.SetPinned(false, 0, 0, 0, 0);
        UpdateState();

        Int32Rect? region = null;
        try { region = await RegionSelectorWindow.SelectAsync(); }
        catch { /* overlay failed: treat as cancel */ }

        _hint = false;
        if (region is { } r)
        {
            await Task.Delay(60); // let the overlay disappear before the capture
            try { _vm.Ask.Attach(ScreenCaptureService.Capture(r, new Window[] { this })); }
            catch { /* capture failed: nothing attached */ }
        }

        _selecting = false;
        if (_vm.CurrentPill != null) ShowPill(_vm.CurrentPill);
        _vm.GoTo(PageId.Ask);
        Pin();
        if (hadFocus || region != null) { TakeFocus(); AskPageView.FocusInput(); }
        UpdateKeyCapture();
    }

    // =========================================================================================
    // Music page album glow
    // =========================================================================================
    private readonly Spring _glowFade = new(Motion.GlowCrossfade, 1);
    private readonly Spring _glowLevel = new(Motion.GlowDim, 0);
    private bool _glowFrontIsA = true;
    private Color _glowColor;
    private double _glowPulse = 1;
    private int _glowPulseGeneration;

    private void WireGlow()
    {
        Canvas_SetGlowPosition();
        _glowFade.ValueChanged += v =>
        {
            var (front, back) = _glowFrontIsA ? (GlowA, GlowB) : (GlowB, GlowA);
            front.Opacity = Math.Clamp(v, 0, 1);
            back.Opacity = 1 - Math.Clamp(v, 0, 1);
        };
        _glowLevel.ValueChanged += _ => ApplyGlowOpacity();
    }

    private void Canvas_SetGlowPosition()
    {
        // Ellipse centre on the notch's bottom edge, horizontally centred: only the top half rises in.
        System.Windows.Controls.Canvas.SetLeft(GlowGroup, (Theme.ExpandedWidth - Theme.GlowWidth) / 2);
        System.Windows.Controls.Canvas.SetTop(GlowGroup, Theme.MusicExpandedHeight - Theme.GlowHeight / 2);
    }

    private static Brush GlowBrush(Color c)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xFF, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0x70, c.R, c.G, c.B), 0.45),
                new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
            },
        };
        brush.Freeze();
        return brush;
    }

    private void UpdateGlowColor(bool animate)
    {
        var color = AlbumColorService.Extract(_vm.Music.Artwork);
        if (color == _glowColor && GlowA.Fill != null) return;
        _glowColor = color;
        if (!animate || GlowA.Fill == null)
        {
            GlowA.Fill = GlowBrush(color);
            _glowFrontIsA = true;
            _glowFade.Snap(1);
            return;
        }
        // Paint the hidden ellipse with the new colour and cross-fade to it (~600 ms).
        _glowFrontIsA = !_glowFrontIsA;
        (_glowFrontIsA ? GlowA : GlowB).Fill = GlowBrush(color);
        _glowFade.Snap(0);
        _glowFade.SetTarget(1, Motion.GlowCrossfade);
    }

    /// <summary>Brighter while playing, dimmed (spring) while paused; pulse only while it can be seen.</summary>
    private void UpdateGlowLevel()
    {
        bool open = _state == NotchState.Expanded;
        bool playing = _vm.Music.IsPlaying;
        double target = !open || MusicIdx < 0 ? 0 : playing ? Theme.GlowMaxOpacity : Theme.GlowPausedOpacity;
        _glowLevel.SetTarget(target, Motion.GlowDim);
        if (open && playing && _vm.IsMusicPage) StartGlowPulse();
    }

    private void StartGlowPulse()
    {
        if (!SpringAnimator.AnimationsEnabled) return;
        int gen = ++_glowPulseGeneration;
        double t = 0, sinceDraw = 0;
        SpringAnimator.Instance.RunWhile(dt =>
        {
            bool keep = gen == _glowPulseGeneration && _state == NotchState.Expanded && _vm.Music.IsPlaying
                        && Near(MusicIdx) && SpringAnimator.AnimationsEnabled;
            if (!keep)
            {
                _glowPulse = 1;
                GlowScale.ScaleX = GlowScale.ScaleY = 1;
                ApplyGlowOpacity();
                return false;
            }
            t += dt; sinceDraw += dt;
            if (sinceDraw >= 1.0 / Theme.GlowPulseFps) // slow, subtle: a capped redraw rate is plenty
            {
                sinceDraw = 0;
                double phase = 0.5 + 0.5 * Math.Sin(2 * Math.PI * t / Theme.GlowPulsePeriod);
                GlowScale.ScaleX = GlowScale.ScaleY = 1 + Theme.GlowPulseScale * phase;
                _glowPulse = 1 - Theme.GlowPulseOpacity * (1 - phase);
                ApplyGlowOpacity();
            }
            return true;
        });
    }

    private void ApplyGlowOpacity()
    {
        int music = MusicIdx;
        double musicVisible = music >= 0 ? 1 - Math.Clamp(Math.Abs(_page.Value - music), 0, 1) : 0; // only on the music page
        double opened = SmoothStep(0.3, 1, MorphProgress());           // only once the notch is open
        GlowLayer.Opacity = Math.Clamp(_glowLevel.Value, 0, 1) * musicVisible * opened * _glowPulse;
    }

    // =========================================================================================
    // Clipboard hover preview card
    // =========================================================================================
    private readonly Spring _previewShow = new(Motion.PreviewPop, 0);
    private readonly Spring _previewX = new(Motion.PreviewFollow, 0) { Precision = 50 };
    private readonly Spring _previewY = new(Motion.PreviewFollow, 0) { Precision = 50 };
    private readonly ScaleTransform _previewScale = new(1, 1);
    private readonly TranslateTransform _previewMove = new();

    private void WirePreview()
    {
        PreviewCard.RenderTransform = new TransformGroup { Children = { _previewScale, _previewMove } };
        _previewShow.ValueChanged += v =>
        {
            PreviewCard.Opacity = Math.Clamp(v, 0, 1);
            BlurFx.Reveal(PreviewCard, v, Motion.PreviewBlur);
            double s = Motion.PreviewStartScale + (1 - Motion.PreviewStartScale) * Math.Clamp(v, 0, 1.2);
            _previewScale.ScaleX = _previewScale.ScaleY = s;
            if (v <= 0.01 && _previewShow.Target == 0) PreviewCard.Visibility = Visibility.Collapsed;
        };
        _previewX.ValueChanged += v => _previewMove.X = v;
        _previewY.ValueChanged += v => _previewMove.Y = v;
        ShelfPageView.PreviewRequested += ShowPreview;
        ShelfPageView.PreviewEnded += () => _previewShow.SetTarget(0, Motion.FadeOut);
        AskPageView.ThumbPreviewRequested += ShowImagePreview;
        AskPageView.ThumbPreviewEnded += () => _previewShow.SetTarget(0, Motion.FadeOut);
        // Re-clamp once the card has laid out its new content (text / image sizes differ).
        PreviewCard.SizeChanged += (_, e) => { if (_previewShow.Target > 0) PositionPreview(e.NewSize, snap: false); };
    }

    private bool _previewFlipped;

    private void ShowPreview(ClipItemViewModel item, FrameworkElement tile)
    {
        if (_state != NotchState.Expanded || !_vm.IsShelfPage) return;
        bool wasHidden = PreviewCard.Visibility != Visibility.Visible || _previewShow.Target == 0;
        PreviewCard.Show(item);
        if (wasHidden) _previewShow.Snap(0);
        PreviewCard.Visibility = Visibility.Visible; // after Snap(0), which hides it
        _previewShow.SetTarget(1, Motion.PreviewPop);
        PreviewCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        PositionPreview(PreviewCard.DesiredSize, snap: wasHidden);
    }

    /// <summary>Ask screenshot hover: the full capture, fitted up to the preview size and inside the notch.</summary>
    private void ShowImagePreview(ImageSource img)
    {
        if (_state != NotchState.Expanded || !_vm.IsAskPage) return;
        bool wasHidden = PreviewCard.Visibility != Visibility.Visible || _previewShow.Target == 0;
        double chrome = 2 * Theme.PreviewPadding.Top + 2;
        double maxH = Math.Min(Theme.ThumbPreviewMaxHeight, _height.Value - 2 * Theme.PreviewInset - chrome);
        double maxW = Math.Min(Theme.ThumbPreviewMaxWidth, _width.Value - 2 * Theme.PreviewInset - chrome);
        PreviewCard.ShowImage(img, Math.Max(40, maxW), Math.Max(40, maxH));
        if (wasHidden) _previewShow.Snap(0);
        PreviewCard.Visibility = Visibility.Visible;
        _previewShow.SetTarget(1, Motion.PreviewPop);
        PreviewCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        PositionPreview(PreviewCard.DesiredSize, snap: wasHidden);
    }

    /// <summary>The card follows the cursor while it shows.</summary>
    private void ExpandedLayer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_previewShow.Target > 0 && PreviewCard.Visibility == Visibility.Visible)
            PositionPreview(new Size(PreviewCard.ActualWidth, PreviewCard.ActualHeight), snap: false);
    }

    /// <summary>
    /// To the right of the cursor and slightly below it; flips to the left when it would pass the
    /// notch's right edge, and is clamped vertically so it stays inside the notch (and the screen).
    /// </summary>
    private void PositionPreview(Size size, bool snap)
    {
        if (size.Width <= 0) return;
        var p = Mouse.GetPosition(ExpandedLayer);
        double bodyLeft = (Theme.ExpandedWidth - _width.Value) / 2 + Theme.PreviewInset;
        double bodyRight = (Theme.ExpandedWidth + _width.Value) / 2 - Theme.PreviewInset;
        double bodyBottom = _height.Value - Theme.PreviewInset;

        double x = p.X + Theme.PreviewCursorOffsetX;
        bool flip = x + size.Width > bodyRight;
        if (flip) x = p.X - Theme.PreviewCursorOffsetX - size.Width;
        x = Math.Clamp(x, bodyLeft, Math.Max(bodyLeft, bodyRight - size.Width));
        double y = Math.Clamp(p.Y + Theme.PreviewCursorOffsetY, Theme.PreviewInset, Math.Max(Theme.PreviewInset, bodyBottom - size.Height));

        if (flip != _previewFlipped)
        {
            _previewFlipped = flip; // pop out of the side facing the cursor
            PreviewCard.RenderTransformOrigin = new Point(flip ? 1 : 0, 0.5);
        }
        if (snap) { _previewX.Snap(x); _previewY.Snap(y); }
        else { _previewX.SetTarget(x, Motion.PreviewFollow); _previewY.SetTarget(y, Motion.PreviewFollow); }
    }

    // =========================================================================================
    // Shelf drop target: the notch springs open to the shelf page as soon as files arrive.
    // =========================================================================================
    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void SetDragIn(bool on)
    {
        if (_dragIn == on) return;
        _dragIn = on;
        _dropGlow.SetTarget(on ? 1 : 0, Motion.Hover);
        ShelfPageView.SetDropActive(on);
    }

    private void Notch_DragEnter(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        if (_outgoingDrag || !HasFiles(e) || !_vm.Pages.IsEnabled(PageId.Clipboard)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Link | DragDropEffects.Copy;
        if (!_dragIn)
        {
            _vm.GoTo(PageId.Clipboard);
            SetDragIn(true);
            UpdateState();
        }
        e.Handled = true;
    }

    private void Notch_DragOver(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        e.Effects = !_outgoingDrag && HasFiles(e) ? DragDropEffects.Link | DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Notch_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; confirm after a beat.
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
    }

    private void Notch_Drop(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        if (!_outgoingDrag && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _vm.Shelf.AddPaths(files);
        SetDragIn(false);
        _hover = true;
        ScheduleCollapseCheck();
        e.Handled = true;
    }
}
