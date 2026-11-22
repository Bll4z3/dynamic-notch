using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using DynamicNotch.Views;
using Microsoft.UI.Dispatching;

namespace DynamicNotch;

/// <summary>
/// The screenshot shelf's window-level rules: a new screenshot springs the notch open onto the Screenshots
/// page (unless the user is busy in the notch, it's hidden for a fullscreen app, or auto-open is off) and,
/// if the pointer never comes near, closes again after <see cref="Motion.ShotPeekMs"/>; the screenshot stays
/// on the shelf. Dragging a screenshot out keeps the notch open; Mark up opens the editor; Ask Claude carries
/// the screenshot over to the Ask page as its attachment with the question box focused.
/// </summary>
public sealed partial class NotchWindow
{
    private DispatcherQueueTimer _peekTimer = null!;
    private bool _peeking;

    private void BuildScreenshots()
    {
        var page = _frame.Screenshots;
        page.OutgoingDrag += active => { _outgoingDrag = active; if (!active) ScheduleCollapseCheck(); else UpdateState(); };
        page.MarkupRequested += OpenMarkup;
        page.AskRequested += item => _ = AskAboutScreenshotAsync(item);
        _peekTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _peekTimer.IsRepeating = false;
        _peekTimer.Interval = TimeSpan.FromMilliseconds(Motion.ShotPeekMs);
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); EndPeek(); };
    }

    private void OpenMarkup(ScreenshotItem item)
    {
        try
        {
            MarkupWindow.Open(item.Path, async path =>
            {
                await Vm.Screenshots.ReplaceAsync(item, path);
                Vm.Screenshots.Selected = item;
            });
        }
        catch (Exception ex) { App.LogError(ex); Vm.Screenshots.Notify("Couldn't open the editor", true); }
    }

    /// <summary>Dev (`--markup`): opens the editor on the selected screenshot.</summary>
    public void DevMarkup() { if (Vm.Screenshots.Selected is { } s) OpenMarkup(s); }

    /// <summary>A screenshot just landed on the shelf.</summary>
    public void OnScreenshotAdded(ScreenshotItem item, bool autoOpen)
    {
        if (!autoOpen || _fullscreenHidden || _selecting || _hasFocus || _pinned && Vm.CurrentPage != PageId.Screenshots)
        {
            if (Vm.CurrentPage == PageId.Screenshots) _frame.Screenshots.PlayArrival();
            return;
        }
        var placement = Vm.Pages.PlacementOf(PageId.Screenshots);
        if (placement == Placement.Page)
        {
            if (!Vm.GoTo(PageId.Screenshots)) return;
            _page.SetTarget(Vm.PageIndex, Motion.Page);
        }
        else if (placement == Placement.Icon) _frame.Popup.Open(PageId.Screenshots);
        else return;
        _peeking = true;
        _hover = true;
        UpdateState();
        _frame.Screenshots.OnShown();
        _frame.Screenshots.PlayArrival();
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    /// <summary>The peek's time is up: close unless the user is now using the notch.</summary>
    private void EndPeek()
    {
        if (!_peeking) return;
        _peeking = false;
        if (_pinned || _mouseOver || _outgoingDrag || _hasFocus) return;
        _hover = false;
        UpdateState();
    }

    private async Task AskAboutScreenshotAsync(ScreenshotItem item)
    {
        var shot = await ScreenshotShelfViewModel.ToAskAttachmentAsync(item);
        if (shot == null) return;
        Vm.Ask.Attach(shot);
        _overlayFromHotkey = false;
        OpenFeature(PageId.Ask);
    }
}
