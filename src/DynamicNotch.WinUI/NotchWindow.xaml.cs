using System.ComponentModel;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using DynamicNotch.Views;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace DynamicNotch;

public enum NotchState { Collapsed, Pill, Activity, Expanded }

/// <summary>
/// The overlay window. Owns the notch state machine (collapsed ▸ pill ▸ expanded), same rules as the
/// WPF app. The difference is who draws the frames: every spring is baked into a compositor animation
/// (<see cref="CompSpring"/>) and every visual follows the springs through compositor expressions, so
/// the morph runs at the display's refresh rate and the app does no per-frame work at all. The app
/// still knows where each spring is at any moment (same maths), which it uses to time content reveals
/// and to keep the click-through window region just big enough for the animated shape.
/// The window never takes focus, except when the user deliberately invokes Ask (NotchWindow.Ask.cs).
/// </summary>
public sealed partial class NotchWindow : Window
{
    private const int AskHotkeyId = 0x4E4F; // "NO"

    private IntPtr _hwnd;
    private HiddenWindow? _messages;            // hotkey + display / settings broadcasts
    private Native.WinEventDelegate? _foregroundHook;
    private IntPtr _foregroundHookHandle;
    private KeyboardHookService? _keys;
    private bool _hotkeyRegistered;

    // Window placement (physical pixels) and scale of the monitor it's on.
    private int _winLeft, _winTop;
    private double _scale = 1;

    // Compositor-played springs (pixel springs get a coarser rest precision so they settle promptly).
    private NotchShape _shape = null!;
    private CompSpring _width = null!, _height = null!, _pillContent = null!, _collapsedContent = null!;
    private CompSpring _page = null!; // the pager: fractional page index ("Page")

    private NotchState _state = NotchState.Collapsed;
    private bool _hover, _mouseOver, _hoverPill, _pinned, _expandedRevealed;
    private double _morphFullHeight = Theme.ExpandedHeight;

    private readonly DispatcherQueueTimer _expandTimer, _collapseTimer, _revealTimer, _regionTimer, _hoverWatch;

    public NotchWindow(MainViewModel vm)
    {
        Vm = vm;
        InitializeComponent();

        var q = DispatcherQueue.GetForCurrentThread();
        _expandTimer = OneShot(q, Motion.HoverExpandDelayMs, () => { _hover = true; UpdateState(); });
        _collapseTimer = OneShot(q, Motion.HoverCollapseDelayMs, OnCollapseTimer);
        _revealTimer = OneShot(q, 0, PlanReveal);
        _regionTimer = OneShot(q, 0, UpdateRegion);
        // WinUI misses pointer-exit when the cursor jumps out of the window, so while the pointer is
        // believed to be over the notch (only then) the real cursor position is checked a few times a second.
        _hoverWatch = q.CreateTimer();
        _hoverWatch.Interval = TimeSpan.FromMilliseconds(50);
        _hoverWatch.Tick += (_, _) => { if (!_mouseOver) _hoverWatch.Stop(); else if (!IsPointerOverNotch(margin: 4)) OnPointerLeft(); };

        SetUpWindow();
        ApplyThemeLayout();
        BuildComposition();
        BuildExpanded();
        BuildClipboard();
        BuildAsk();
        BuildFocus();
        BuildActivity();
        BuildHub();
        UpdateGlowColor(animate: false);
        // Any click on the notch pins it, including clicks that a button inside it handles.
        NotchRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Notch_PointerPressed), handledEventsToo: true);

        Vm.PropertyChanged += OnVmChanged;
        Vm.Music.PropertyChanged += OnMusicChanged;
        Vm.PagesReconfigured += OnPagesReconfigured;
    }

    public MainViewModel Vm { get; }

    /// <summary>The notch moved between states (previous, next): sounds hook in here.</summary>
    public event Action<NotchState, NotchState>? StateChanged;
    public bool IsExpanded => _state == NotchState.Expanded;

    private static DispatcherQueueTimer OneShot(DispatcherQueue q, int ms, Action tick)
    {
        var t = q.CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromMilliseconds(ms);
        t.Tick += (_, _) => { t.Stop(); tick(); };
        return t;
    }

    private static void Restart(DispatcherQueueTimer t, double ms)
    {
        t.Stop();
        t.Interval = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        t.Start();
    }

    // =========================================================================================
    // Window setup: see-through popup, never activated, topmost, position, hotkey
    // =========================================================================================
    private void SetUpWindow()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        SystemBackdrop = new TransparentBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        Native.MakeSeeThroughPopup(_hwnd);
        // Doesn't activate on click: the user's app keeps keyboard focus.
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST, Native.WS_EX_APPWINDOW);
        // (Not subclassing the WinUI window: that breaks its input. Messages go to a hidden window instead.)
        _messages = new HiddenWindow("DynamicNotch.Messages");
        _messages.Message += OnMessage;

        // Re-assert topmost whenever another window (e.g. a full-screen app) comes to the front.
        _foregroundHook = (_, _, _, _, _, _, _) => Native.ReassertTopmost(_hwnd);
        _foregroundHookHandle = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundHook, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Shows the notch without activating it.</summary>
    public void ShowNotch()
    {
        Bindings.Update(); // x:Bind in a Window isn't started by a Loading event; start it here
        PositionWindow();
        ApplyTargets(immediate: true);
        AppWindow.Show(activateWindow: false);
        Native.MakeSeeThroughPopup(_hwnd); // again: showing re-applies the presenter frame
        Native.ReassertTopmost(_hwnd);
        UpdateHotkey();
    }

    public void CloseNotch()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_foregroundHookHandle != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHookHandle);
        _foregroundHookHandle = IntPtr.Zero;
        if (_hotkeyRegistered && _messages != null) Native.UnregisterHotKey(_messages.Handle, AskHotkeyId);
        _hotkeyRegistered = false;
        _messages?.Dispose();
        _messages = null;
        Close();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Ui.Post(PositionWindow, DispatcherQueuePriority.Low);

    /// <summary>
    /// Top-centre of the chosen monitor (primary, or the one under the pointer), in physical pixels
    /// (DPI-correct on any monitor setup), pushed down by the top offset and drawn at the notch scale:
    /// the window grows by the scale and the content is scaled by a render transform, so every DIP→pixel
    /// conversion (region, hit tests, key capture) goes through <see cref="_scale"/> and stays exact.
    /// </summary>
    private void PositionWindow()
    {
        var (bounds, monitorScale) = UseMouseMonitor ? Native.GetMonitorUnderCursor() : Native.GetPrimaryMonitor();
        double scale = monitorScale * NotchScale;
        _scale = scale;
        ApplyNotchScale();
        int w = (int)Math.Round(Theme.HostWidth * scale);
        int h = (int)Math.Round(Theme.HostHeight * scale);
        _winLeft = bounds.Left + (bounds.Width - w) / 2;
        _winTop = bounds.Top + (int)Math.Round(TopOffset * monitorScale);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _winLeft, _winTop, w, h, Native.SWP_NOACTIVATE);
        UpdateRegion();
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)msg)
        {
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                Ui.Post(PositionWindow, DispatcherQueuePriority.Low);
                break;
            case Native.WM_SETTINGCHANGE:
                Anim.RefreshSystemSetting();
                break;
            case Native.WM_HOTKEY when wParam.ToInt32() == AskHotkeyId:
                Ui.Post(OnAskHotkey);
                break;
        }
    }

    // =========================================================================================
    // Layout from Theme (XAML has no x:Static) and the compositor tree
    // =========================================================================================
    private void ApplyThemeLayout()
    {
        Root.Width = Theme.HostWidth;
        Root.Height = Theme.HostHeight;
        HitStrip.Height = Theme.HoverStripHeight;

        CollapsedLayer.Width = Theme.CollapsedPlayingWidth;
        CollapsedLayer.Height = Theme.CollapsedHeight;
        NotchRing.RingSize = Theme.NotchRingSize;
        NotchRing.Thickness = Theme.NotchRingThickness;
        NotchRing.Margin = Theme.CollapsedPadding;
        NotchRing.ChargeHalo = true; // the closed notch breathes a soft green halo while charging
        NotchRing.GreenWhileCharging = true;
        CollapsedTime.SetStyle(t => t.Style = (Style)Application.Current.Resources["ClockTimeText"]);
        CollapsedRight.Margin = Theme.CollapsedPadding;
        Bars.Margin = Theme.GapRightS2;
        MiniArt.Width = MiniArt.Height = Theme.MiniArtSize;
        MiniArt.CornerRadius = MiniArtImage.CornerRadius = Theme.Radius;
        MiniArt.Background = Theme.TileBrush;
        MiniArtIcon.Size = 10;
        MiniArtIcon.Foreground = Theme.SecondaryText;
        MiniArtIcon.Data = Icons.Music;

        PillLayer.Width = Theme.PillWidth;
        PillLayer.Height = Theme.PillHeight;
        PillContent.Padding = Theme.PillPadding;
        PillGapColumn.Width = Theme.GapS3;
        PillIconBox.Width = PillIconBox.Height = Theme.PillIconSize;
        PillIconBox.CornerRadius = PillLogo.CornerRadius = Theme.Radius;
        PillIconBox.Background = Theme.TileBrush;
        PillIconBox.BorderBrush = Theme.TileBorderBrush;
        PillGlyph.Size = Theme.IconMedium;
        PillGlyph.Foreground = Theme.PrimaryText;
        PillBody.Margin = Theme.GapTopS1;
        PillBadge.Margin = Theme.GapLeftS2;
        // Pills (badges) take a radius of exactly half their height.
        PillBadge.SizeChanged += (_, e) => PillBadge.CornerRadius = new CornerRadius(e.NewSize.Height / 2);

        ExpandedLayer.Width = Theme.ExpandedWidth;
        ExpandedLayer.Height = Theme.ExpandedMaxHeight;
    }

    private void BuildComposition()
    {
        _shape = new NotchShape(ShapeHost, Theme.HostWidth, Theme.HostHeight);
        var c = _shape.Compositor;
        var n = _shape.Props;
        _width = new CompSpring(n, "W", Motion.Notch, Theme.CollapsedWidth, precision: 100);
        _height = new CompSpring(n, "H", Motion.Notch, Theme.CollapsedHeight, precision: 100);
        _shape.Follow(_width, _height); // the body path (continuous corners) follows both springs
        _pillContent = new CompSpring(n, "Pill", Motion.Pop, 0);
        _collapsedContent = new CompSpring(n, "Collapsed", Motion.FadeIn, 0);
        _page = new CompSpring(n, "Page", Motion.Page, Vm.PageIndex, precision: 0.5);
        n.InsertScalar("Full", (float)_morphFullHeight);

        // All content is clipped to the live body shape, so nothing ever overflows the morph.
        Anim.VisualOf(ContentRoot).Clip = _shape.CreateBodyClip();

        // Collapsed layer: fades with its spring; the side groups hug the body edges (transform only).
        float cpw = (float)Theme.CollapsedPlayingWidth;
        Anim.VisualOf(CollapsedLayer).StartAnimation("Opacity", Anim.Expr(c, "Clamp(N.Collapsed, 0, 1)", ("N", n)));
        var right = Anim.Expr(c, "Vector3((N.W - CPW) / 2, 0, 0)", ("N", n));
        right.SetScalarParameter("CPW", cpw);
        Anim.VisualOf(CollapsedRight, translation: true).StartAnimation("Translation", right);
        // (The battery ring and clock follow the edges through their shared-element flights: see NotchWindow.Expanded.cs.)

        // Pill: pops in from 90% scale, anchored at the top centre.
        var pill = Anim.VisualOf(PillLayer);
        pill.CenterPoint = new Vector3((float)Theme.PillWidth / 2, 0, 0);
        pill.StartAnimation("Opacity", Anim.Expr(c, "Clamp(N.Pill, 0, 1)", ("N", n)));
        var pillScale = Anim.Expr(c, "Vector3(S + (1 - S) * N.Pill, S + (1 - S) * N.Pill, 1)", ("N", n));
        pillScale.SetScalarParameter("S", (float)Motion.PillStartScale);
        pill.StartAnimation("Scale", pillScale);

        // Any retarget may move a reveal threshold or change how much room the shape needs.
        _width.Changed += OnMorphChanged;
        _height.Changed += OnMorphChanged;
    }


    private void OnMorphChanged()
    {
        PlanReveal();
        UpdateRegion();
    }

    /// <summary>Connects the global keyboard hook (arrow keys / Space / Esc without focus).</summary>
    public void AttachKeyboard(KeyboardHookService keys)
    {
        _keys = keys;
        keys.KeyPressed += OnHookKey;
        keys.OutsideClick += () =>
        {
            if (!_pinned || Vm.Ask.IsBusy || _selecting || DevHoldOpen) return; // keep an in-flight answer on screen
            Unpin();
            ScheduleCollapseCheck();
        };
        UpdateKeyCapture();
    }

    // =========================================================================================
    // State machine (same rules as the WPF app)
    // =========================================================================================
    private NotchState DesiredState()
    {
        if (_hint) return NotchState.Pill;
        if (_hover || _pinned || _dragIn || _outgoingDrag) return NotchState.Expanded;
        if (Vm.Activity.HasActivity) return NotchState.Activity;
        if (Vm.HasPill) return NotchState.Pill;
        return NotchState.Collapsed;
    }

    private void UpdateState()
    {
        var next = DesiredState();
        if (next == _state) { ApplyTargets(); return; }

        var prev = _state;
        _state = next;
        StateChanged?.Invoke(prev, next);

        // Pills wait while the notch is open or an activity pill is up; they resume (with a fresh timer) after.
        Vm.HoldPill(next is NotchState.Expanded or NotchState.Activity || _hint);
        if (next == NotchState.Expanded && Vm.IsShelfPage) Vm.Shelf.RefreshExistence();
        if (next == NotchState.Expanded && Vm.CurrentPage == PageId.Hub) _frame.Hub.OnShown();
        // An activity pill that ended under the pointer: carry on as a normal hover (open after the usual delay).
        if (prev == NotchState.Activity && next != NotchState.Expanded && _mouseOver) { _hoverActivity = false; Restart(_expandTimer, Motion.HoverExpandDelayMs); }
        if (prev == NotchState.Expanded && next != NotchState.Expanded)
        {
            Unpin();
            _frame.Shelf.ClosePreview();
            _frame.Preview.SetShown(false);
            _frame.Hub.ClosePopover();
            if (!_selecting)
            {
                Vm.Ask.CancelIfBusy();     // collapsing with a request in flight cancels it
                ReleaseFocus(restore: true); // and hands focus back to where the user was
            }
        }
        ApplyTargets();
    }

    /// <summary>Expanded size for the current page: Ask sizes itself to its state and content.</summary>
    private (double Width, double Height) ExpandedTarget() => Vm.IsAskPage
        ? _frame.Ask.DesiredNotchSize
        : (Theme.ExpandedWidth, Vm.IsMusicPage ? Theme.MusicExpandedHeight : Theme.ExpandedHeight);

    private void ApplyTargets(bool immediate = false)
    {
        bool minimal = Vm.MinimalNotch;
        bool playing = Vm.Music.IsPlaying;
        bool timer = CompactTimerShown; // the focus timer takes the collapsed right side over the music bits

        var expanded = ExpandedTarget();
        _morphFullHeight = expanded.Height;
        _shape.Props.InsertScalar("Full", (float)_morphFullHeight);

        double w, h;
        SpringPreset preset = Motion.Notch;
        switch (_state)
        {
            case NotchState.Expanded:
                (w, h) = expanded;
                if (Vm.IsAskPage && _expandedRevealed) preset = Motion.AskResize;
                break;
            case NotchState.Pill:
                w = Theme.PillWidth; h = Theme.PillHeight; preset = Motion.Pop; // pop with a little overshoot
                break;
            case NotchState.Activity:
                w = ActivityWidth(); h = Theme.ActivityHeight; preset = Motion.ActivityMorph;
                break;
            default:
                w = minimal ? Theme.MinimalWidth : playing || timer ? Theme.CollapsedPlayingWidth : Theme.CollapsedWidth;
                h = minimal ? Theme.MinimalHeight : Theme.CollapsedHeight;
                break;
        }

        if (immediate) { _width.Snap(w); _height.Snap(h); }
        else if (preset == Motion.Notch && (h != _height.Target || w != _width.Target))
        {
            // Fluid morph: opening, the width leads and the height follows (the island spreads, then drops);
            // closing, the height leads (it lifts, then draws in). Same springs, a few dozen ms apart in feel.
            bool opening = h > _height.Target + 0.5;
            _width.SetTarget(w, opening ? Motion.NotchLead : Motion.Notch);
            _height.SetTarget(h, opening ? Motion.Notch : Motion.NotchLead);
        }
        else { _width.SetTarget(w, preset); _height.SetTarget(h, preset); }

        // Content leaving the stage fades out right away (faster than it arrives); incoming content
        // waits for the container (see PlanReveal).
        if (_state != NotchState.Expanded && _expandedRevealed)
        {
            _expandedRevealed = false;
            _frame.Stagger.Hide();
        }
        if (_state != NotchState.Pill) _pillContent.SetTarget(0, Motion.FadeOut);
        if (_state != NotchState.Activity) _activityContent.SetTarget(0, Motion.FadeOut);
        // Toward the expanded notch the collapsed bits fade a little slower; toward a pill they get out of the way quickly.
        if (_state != NotchState.Collapsed || minimal)
            _collapsedContent.SetTarget(0, _state == NotchState.Expanded ? Motion.FadeIn : Motion.FadeOut);
        CollapsedMusic.Visibility = playing && !timer ? Visibility.Visible : Visibility.Collapsed;

        ExpandedLayer.IsHitTestVisible = _state == NotchState.Expanded;
        PillLayer.IsHitTestVisible = _state == NotchState.Pill && !_hint;
        ActivityLayer.IsHitTestVisible = _state == NotchState.Activity;
        UpdateCompactActivity();
        _frame.SetAskActive(_state == NotchState.Expanded && Vm.IsAskPage);

        Bars.IsPlaying = _state == NotchState.Collapsed && playing && !minimal && !timer;
        _theme?.SetAlbumColor(_albumColor, _state == NotchState.Expanded && Vm.IsMusicPage, Vm.Music.IsPlaying);
        Vm.Music.IsVisible = _state == NotchState.Expanded && Vm.IsMusicPage;
        Vm.Usage.IsVisible = _state == NotchState.Expanded && Vm.CurrentPage == PageId.Usage;
        UpdateCompactTimer();
        _frame.HeaderRing.ShowNumber = _state == NotchState.Expanded; // the % shows while the notch is open
        _frame.Shelf.SetDropActive(_dragIn);
        UpdateMatchedFlags();
        UpdateGlowLevel();

        UpdateHitArea();
        UpdateKeyCapture();
        PlanReveal();
        UpdateRegion();
    }

    // ---- Content reveal: at the same morph thresholds as the WPF app, timed from the spring model ----
    private static double Progress(double v, double from, double to) => to == from ? 1 : (v - from) / (to - from);

    /// <summary>0 = collapsed height, 1 = fully expanded for the current page.</summary>
    private double MorphProgress(double h) => Math.Clamp(Progress(h, Theme.CollapsedHeight, _morphFullHeight), 0, 1);

    /// <summary>
    /// Starts incoming content once the container is far enough through its morph. Instead of checking
    /// every frame, it asks the height spring when the threshold will be crossed and sets one timer.
    /// </summary>
    private void PlanReveal()
    {
        _revealTimer.Stop();
        Func<double, bool>? ready = null;
        Action? reveal = null;
        switch (_state)
        {
            case NotchState.Expanded when !_expandedRevealed:
                ready = h => MorphProgress(h) >= Motion.ContentRevealThreshold;
                reveal = () => { _expandedRevealed = true; _frame.Stagger.Show(); LogMusicCentres(); };
                break;
            case NotchState.Pill when _pillContent.Target < 1:
                ready = h => Progress(h, Theme.CollapsedHeight, Theme.PillHeight) >= Motion.ContentRevealThreshold;
                reveal = () => _pillContent.SetTarget(1, Motion.Pop);
                break;
            case NotchState.Activity when _activityContent.Target < 1:
                ready = h => Progress(h, Theme.CollapsedHeight, Theme.ActivityHeight) >= Motion.ContentRevealThreshold;
                reveal = () => { _activityContent.SetTarget(1, Motion.Pop); _activityView.Enter(); };
                break;
            case NotchState.Collapsed when !Vm.MinimalNotch && _collapsedContent.Target < 1:
                ready = h => MorphProgress(h) < 0.3 && h > Theme.CollapsedHeight * 0.6;
                reveal = () => _collapsedContent.SetTarget(1, Motion.FadeIn);
                break;
        }
        if (ready == null) return;
        double now = SpringModel.Now;
        double? at = _height.Model.TimeWhen(ready, now);
        if (at is not { } t) return;
        if (t <= now + 0.004) reveal!();
        else Restart(_revealTimer, (t - now) * 1000);
    }

    // ---- Hit testing and the click-through region ---------------------------------------------------
    private (double W, double H) TargetBody() => (_width.Target, _height.Target);

    /// <summary>Hover targets follow the state's target size (one layout change per state change, never per frame).</summary>
    private void UpdateHitArea()
    {
        var (w, h) = TargetBody();
        double s = Math.Min(Theme.CornerRadius, h / 2); // shoulders
        HitBody.Width = w + 2 * s;
        HitBody.Height = h;
        HitStrip.Width = (Vm.MinimalNotch ? Theme.MinimalWidth : Theme.CollapsedWidth) + Theme.HoverStripExtra;
    }

    /// <summary>
    /// The window region is the only part of the window that is drawn and takes the mouse; everything
    /// else is click-through. It covers the hover targets plus the largest the animated shape will get
    /// before it settles (springs overshoot), and shrinks again when the morph is done.
    /// </summary>
    private void UpdateRegion()
    {
        if (_hwnd == IntPtr.Zero) return;
        _regionTimer.Stop();
        double now = SpringModel.Now;
        var (tw, th) = TargetBody();
        double maxW = Math.Max(tw, _width.Model.MaxFrom(now));
        double maxH = Math.Max(th, _height.Model.MaxFrom(now));

        var rects = new List<Native.RECT>();
        // Body + shoulders at the largest size still to come, plus room for the shadow once it's bigger than a bar.
        double s = Math.Min(Theme.CornerRadius, maxH / 2);
        double shadow = maxH > Theme.CollapsedHeight + 1 ? Theme.ShadowMargin : 0;
        rects.Add(Centered(maxW + 2 * s + 2 * shadow, maxH + shadow));
        // Hover strip along the top edge.
        rects.Add(Centered(HitStrip.Width, Theme.HoverStripHeight));
        Native.SetRegion(_hwnd, rects);

        double settle = Math.Max(_width.Model.SettleDuration() - _width.Model.Elapsed,
                                 _height.Model.SettleDuration() - _height.Model.Elapsed);
        if (settle > 0) Restart(_regionTimer, settle * 1000 + 30);
    }

    private Native.RECT Centered(double widthDip, double heightDip)
    {
        int w = (int)Math.Ceiling(widthDip * _scale), h = (int)Math.Ceiling(heightDip * _scale);
        int hostW = (int)Math.Round(Theme.HostWidth * _scale);
        int l = (hostW - w) / 2;
        return new Native.RECT(Math.Max(0, l), 0, Math.Min(hostW, l + w), h);
    }

    /// <summary>Hit-tests the real cursor position against the notch's target body.</summary>
    private bool IsPointerOverNotch(double margin = 2)
    {
        if (!Native.GetCursorPos(out var pt)) return false;
        double x = (pt.X - _winLeft) / _scale, y = (pt.Y - _winTop) / _scale;
        var (w, h) = TargetBody();
        double r = Theme.CornerRadius;
        double left = (Theme.HostWidth - w) / 2 - r, right = (Theme.HostWidth + w) / 2 + r;
        return x >= left - margin && x <= right + margin && y >= 0 && y <= Math.Max(h, Theme.HoverStripHeight) + margin;
    }

    // =========================================================================================
    // View-model reactions
    // =========================================================================================
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.PageIndex):
                _page.SetTarget(Vm.PageIndex, Motion.Page);
                if (Vm.IsShelfPage) Vm.Shelf.RefreshExistence();
                else _frame.Shelf.ClosePreview();
                if (!Vm.IsAskPage) _frame.Preview.SetShown(false);
                if (!Vm.IsAskPage && _hasFocus) ReleaseFocus(restore: true); // no text boxes on the other pages
                Vm.Music.IsVisible = _state == NotchState.Expanded && Vm.IsMusicPage;
                if (_state == NotchState.Expanded && Vm.CurrentPage == PageId.Hub) _frame.Hub.OnShown();
                if (Vm.CurrentPage != PageId.Hub) _frame.Hub.ClosePopover();
                ApplyTargets(); // the notch height differs per page
                break;
            case nameof(MainViewModel.CurrentPill):
                if (!_hint) ShowPill(Vm.CurrentPill);
                UpdateState();
                break;
            case nameof(MainViewModel.MinimalNotch):
                ApplyTargets();
                break;
        }
    }

    private async void OnMusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("" or null or nameof(MusicViewModel.Artwork) or nameof(MusicViewModel.IsPlaying))) return;
        UpdateGlowColor(animate: true);
        ApplyTargets(); // collapsed width, glow level, shared-element flags
        var art = Vm.Music.Artwork;
        var source = await ImageLoader.ToSourceAsync(art);
        if (ReferenceEquals(art, Vm.Music.Artwork)) MiniArtBrush.ImageSource = source;
    }

    private void OnPagesReconfigured(double from, bool currentRemoved)
    {
        if (!Vm.Pages.IsEnabled(PageId.Ask))
        {
            Vm.Ask.CancelIfBusy();
            if (_hasFocus) ReleaseFocus(restore: true);
        }
        _frame.SetPages();
        ApplyDropTarget();
        _page.Snap(from); // same visual spot in the new layout; PageIndex then springs on from here
        UpdateHotkey();
        ApplyTargets();
    }

    private async void ShowPill(PillRequest? p)
    {
        if (p == null) return;
        bool isApp = p.Source == PillSource.App;
        PillGlyph.Data = p.Logo != null ? null : p.Glyph ?? Icons.Bell;
        PillLogo.Visibility = p.Logo != null ? Visibility.Visible : Visibility.Collapsed;
        PillLogoBrush.ImageSource = null;

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

        if (p.Logo != null)
        {
            var source = await ImageLoader.ToSourceAsync(p.Logo);
            if (ReferenceEquals(p, Vm.CurrentPill)) PillLogoBrush.ImageSource = source;
        }
    }

    // =========================================================================================
    // Hover / pin / keyboard
    // =========================================================================================
    private void Notch_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _mouseOver = true;
        _hoverWatch.Start();
        _collapseTimer.Stop();
        NotchRing.ShowNumber = true;
        UpdateKeyCapture();
        if (_state == NotchState.Pill)
        {
            _hoverPill = true;
            Vm.HoldPill(true);
            return;
        }
        if (OnActivityPointerEntered()) return; // hovering an activity pill holds it instead of opening the notch
        if (!_hover) Restart(_expandTimer, Motion.HoverExpandDelayMs);
    }

    private void Notch_PointerExited(object sender, PointerRoutedEventArgs e) => OnPointerLeft();

    /// <summary>After a missed exit XAML won't raise "entered" again, so movement over the notch counts as entering.</summary>
    private void Notch_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_mouseOver) Notch_PointerEntered(sender, e);
    }

    private void OnPointerLeft()
    {
        if (!_mouseOver) return;
        _hoverWatch.Stop();
        _mouseOver = false;
        _expandTimer.Stop();
        NotchRing.ShowNumber = false;
        UpdateKeyCapture();
        if (_hoverPill)
        {
            _hoverPill = false;
            Vm.HoldPill(false);
        }
        OnActivityPointerLeft();
        ScheduleCollapseCheck();
    }

    private void Pill_PointerEntered(object sender, PointerRoutedEventArgs e) => Vm.HoldPill(true);
    private void Pill_PointerExited(object sender, PointerRoutedEventArgs e) { if (_state == NotchState.Pill) Vm.HoldPill(false); }

    private void Pill_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Vm.DismissPillCommand.Execute(null);
        e.Handled = true;
    }

    private void ScheduleCollapseCheck() => Restart(_collapseTimer, Motion.HoverCollapseDelayMs);

    private void OnCollapseTimer()
    {
        if (_pinned || _dragIn || _outgoingDrag || _selecting) return;
        // Typing into Ask, or waiting on an answer: stay open even if the mouse wandered off.
        if (_hasFocus || Vm.Ask.IsBusy) return;
        if (IsPointerOverNotch()) { ScheduleCollapseCheck(); return; } // still there
        _hover = false;
        UpdateState();
    }

    private void Notch_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_state is NotchState.Pill or NotchState.Activity) return; // pills handle their own click (dismiss)
        if (!_pinned) Pin();
    }

    /// <summary>Dev: `--open [page]` pins the notch open on start (for screenshots without driving the mouse).</summary>
    public void PinOpen(int page)
    {
        DevHoldOpen = true; // outside clicks don't unpin a dev-opened notch (measurements aren't interrupted)
        Vm.PageIndex = page;
        Pin();
    }

    /// <summary>Keeps the notch open (until Esc or a click elsewhere) without taking focus.</summary>
    private void Pin()
    {
        _pinned = true;
        _hover = true;
        if (_keys != null)
        {
            double h = Math.Max(Theme.ExpandedHeight, _morphFullHeight);
            double half = Theme.ExpandedWidth / 2 + Theme.CornerRadius;
            int cx = _winLeft + (int)Math.Round(Theme.HostWidth * _scale / 2);
            _keys.SetPinned(true, cx - (int)(half * _scale), _winTop, cx + (int)(half * _scale), _winTop + (int)(h * _scale));
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
        _keys.PlayPauseActive = !_hasFocus && _mouseOver && open && Vm.IsMusicPage && Vm.Music.HasMedia;
        _keys.EscapeActive = !_hasFocus && _pinned;
    }

    private void OnHookKey(NotchKey key)
    {
        switch (key)
        {
            case NotchKey.Left:
                if (_state != NotchState.Expanded) { _hover = true; UpdateState(); }
                Vm.PageIndex -= 1;
                break;
            case NotchKey.Right:
                if (_state != NotchState.Expanded) { _hover = true; UpdateState(); }
                Vm.PageIndex += 1;
                break;
            case NotchKey.PlayPause:
                if (Vm.Music.PlayPauseCommand.CanExecute(null)) Vm.Music.PlayPauseCommand.Execute(null);
                break;
            case NotchKey.Escape:
                if (!EscapeClosesPopover()) CollapseAsk();
                break;
        }
    }

    // =========================================================================================
    // Ask hotkey (Ctrl+Shift+Space): registered only while the Ask page is on
    // =========================================================================================
    /// <summary>Ctrl+Shift+Space exists only while the Ask page is switched on.</summary>
    private void UpdateHotkey()
    {
        if (_messages == null) return;
        bool want = Vm.Pages.IsEnabled(PageId.Ask);
        if (want && !_hotkeyRegistered)
        {
            _hotkeyRegistered = Native.RegisterHotKey(_messages.Handle, AskHotkeyId, AppConfig.AskHotkeyModifiers, AppConfig.AskHotkeyVirtualKey);
            if (!_hotkeyRegistered) System.Diagnostics.Debug.WriteLine($"{AppConfig.AskHotkeyLabel} is already used by another app.");
        }
        else if (!want && _hotkeyRegistered)
        {
            Native.UnregisterHotKey(_messages.Handle, AskHotkeyId);
            _hotkeyRegistered = false;
        }
    }
    // (The hotkey itself, focus ownership and Select area: NotchWindow.Ask.cs.)
}

/// <summary>
/// A fully transparent system backdrop: with the DWM frame extended over the whole window
/// (Native.MakeSeeThroughPopup), the WinUI content is composited over the desktop with per-pixel alpha.
/// </summary>
internal sealed class TransparentBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? _systemCompositor;
    private static IntPtr _dispatcherQueueController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        EnsureSystemDispatcherQueue();
        _systemCompositor ??= new Windows.UI.Composition.Compositor();
        target.SystemBackdrop = _systemCompositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        target.SystemBackdrop = null;
    }

    /// <summary>The backdrop brush comes from the system compositor, which needs a Windows.System dispatcher queue.</summary>
    private static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        var options = new DispatcherQueueOptions { dwSize = System.Runtime.InteropServices.Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
        CreateDispatcherQueueController(options, out _dispatcherQueueController);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

    [System.Runtime.InteropServices.DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
}
