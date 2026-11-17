using System.ComponentModel;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using DynamicNotch.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch;

/// <summary>
/// Stage 4 of the notch window: the Ask page's window-level rules (same as the WPF app).
/// The notch never takes keyboard focus except when the user deliberately invokes Ask (the hotkey,
/// or a click into its text box / answer); focus then goes back to the previous window when the
/// notch closes (Esc, the hotkey again, collapse). While it owns focus the keyboard hook stays out of
/// the way entirely. "Select area" shrinks the notch to a hint pill, shows the region selector,
/// captures the region (with the notch excluded) and comes back to Ask with the screenshot attached.
/// </summary>
public sealed partial class NotchWindow
{
    private bool _hasFocus, _selecting, _hint;
    private IntPtr _previousForeground;

    private void BuildAsk()
    {
        var page = _frame.Ask;
        page.NotchSizeChanged += () => { if (_state == NotchState.Expanded && Vm.IsAskPage) ApplyTargets(); };
        page.FocusRequested += () => { TakeFocus(); Ui.Post(UpdateKeyCapture); };
        page.Dots.PageClicked += i => Vm.PageIndex = i;
        page.ThumbPreviewRequested += ShowImagePreview;
        page.ThumbPreviewEnded += () => _frame.Preview.SetShown(false);
        Vm.Ask.PropertyChanged += OnAskChanged;
        Vm.Ask.SelectAreaRequested += () => _ = SelectAreaAsync();

        // Esc while the notch owns focus (keys reach XAML only then; the hook handles it otherwise).
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != VirtualKey.Escape || !_hasFocus) return;
            if (!EscapeClosesPopover()) CollapseAsk();
            e.Handled = true;
        }), handledEventsToo: true);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) OnDeactivated();
        };
    }

    private void OnAskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AskViewModel.State) && _hasFocus)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, _frame.Ask.KeepFocus);
        if (e.PropertyName != nameof(AskViewModel.IsBusy) || Vm.Ask.IsBusy) return;
        // The answer finished: if nobody is looking, the notch may close again as usual.
        if (!_pinned && !_hasFocus && !_mouseOver) ScheduleCollapseCheck();
    }

    // ---- Hotkey ------------------------------------------------------------------------------------
    /// <summary>Ctrl+Shift+Space: open straight onto Ask with the box focused; again closes it.</summary>
    private void OnAskHotkey()
    {
        if (_selecting) return;
        if (_state == NotchState.Expanded && Vm.IsAskPage && (_pinned || _hasFocus))
        {
            CollapseAsk();
            return;
        }
        if (!Vm.GoTo(PageId.Ask)) return; // Ask switched off in Settings ▸ Pages
        _page.SetTarget(Vm.PageIndex, Motion.Page);
        Pin();
        TakeFocus();
        _frame.Ask.FocusInput();
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

    // ---- Focus ownership ---------------------------------------------------------------------------
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
        Native.SetForegroundWindow(_hwnd);
        UpdateKeyCapture();
    }

    /// <summary>Drop focus; optionally hand it back to the window the user was in.</summary>
    private void ReleaseFocus(bool restore)
    {
        if (!_hasFocus) return;
        _hasFocus = false;
        _frame.Ask.ClearFocus();
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
        if (!Vm.Ask.IsBusy) { Unpin(); ScheduleCollapseCheck(); }
    }

    // ---- Select area -------------------------------------------------------------------------------
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

        Native.RECT? region = null;
        try { region = await RegionSelectorWindow.SelectAsync(); }
        catch (Exception ex) { App.LogError(ex); } // overlay failed: treat as cancel

        _hint = false;
        if (region is { } r)
        {
            await Task.Delay(60); // let the overlay disappear before the capture
            try { Vm.Ask.Attach(await ScreenCaptureService.CaptureAsync(r, new[] { _hwnd })); }
            catch (Exception ex) { App.LogError(ex); } // capture failed: nothing attached
        }

        _selecting = false;
        if (Vm.CurrentPill != null) ShowPill(Vm.CurrentPill);
        Vm.GoTo(PageId.Ask);
        Pin();
        if (hadFocus || region != null) { TakeFocus(); _frame.Ask.FocusInput(); }
        UpdateKeyCapture();
    }

    // ---- Screenshot hover preview ------------------------------------------------------------------
    /// <summary>Ask screenshot hover: the full capture, fitted up to the preview size and inside the notch.</summary>
    private void ShowImagePreview(ImageThumb thumb)
    {
        if (_state != NotchState.Expanded || !Vm.IsAskPage || thumb.Source is not { } img || thumb.Bitmap is not { } bmp) return;
        bool wasHidden = !_frame.Preview.IsShown;
        double chrome = 2 * Theme.PreviewPadding.Top + 2;
        double maxH = Math.Min(Theme.ThumbPreviewMaxHeight, _height.Target - 2 * Theme.PreviewInset - chrome);
        double maxW = Math.Min(Theme.ThumbPreviewMaxWidth, _width.Target - 2 * Theme.PreviewInset - chrome);
        _frame.Preview.ShowImage(img, bmp.PixelWidth, bmp.PixelHeight, Math.Max(40, maxW), Math.Max(40, maxH));
        _frame.Preview.SetShown(true, snapFromZero: wasHidden);
        _frame.Preview.UpdateLayout();
        PositionPreview(snap: wasHidden);
    }

    /// <summary>Dev: opens Ask pinned and focused, as the hotkey does.</summary>
    public void InvokeAskHotkey() => OnAskHotkey();

    /// <summary>Dev (`--ask-demo`): outside clicks don't unpin, so screenshots aren't interrupted by the user working.</summary>
    public bool DevHoldOpen { get; set; }

    /// <summary>Dev (`--toggle`): opens (pinned) or closes the notch, for measuring the morph without a mouse.</summary>
    public void DevToggle()
    {
        if (_pinned) { DevHoldOpen = false; CollapseAsk(); }
        else PinOpen(Vm.PageIndex);
    }
}
