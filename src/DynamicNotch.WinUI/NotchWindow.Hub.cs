using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The header pop-up (Notifications / Quick actions) and Quick actions' own popovers (Wi-Fi / Bluetooth):
/// a popover's password box takes keyboard focus only when it asks (the previous window gets it back
/// afterwards); Esc closes the innermost open pop-up before it closes the notch; changing page or closing
/// the notch closes them. While the pop-up is open the notch keeps its full height (the music page is shorter).
/// </summary>
public sealed partial class NotchWindow
{
    private void BuildHub()
    {
        var quick = _frame.QuickActions;
        quick.FocusRequested += () => { TakeFocus(); Services.Ui.Post(UpdateKeyCapture); };
        quick.FocusReleased += () => { if (_hasFocus) ReleaseFocus(restore: true); };
        quick.PopoverClosed += () => { if (_hasFocus && !Vm.IsAskPage) ReleaseFocus(restore: true); };
        _frame.Popup.Changed += panel =>
        {
            if (panel == null && _hasFocus && !Vm.IsAskPage && !Vm.IsPaletteOpen) ReleaseFocus(restore: true);
            if (panel != null && !_pinned) Pin(); // stays open while you use it, like a click
            ApplyTargets();
        };
    }

    /// <summary>Esc: an open pop-up closes first. Returns true when it handled the key.</summary>
    private bool EscapeClosesPopover() => _state == NotchState.Expanded && _frame.Popup.Close();

    /// <summary>Dev: opens a feature as the layout says (null closes the pop-up).</summary>
    internal void DevHeaderPopup(PageId? panel)
    {
        if (panel is not { } id) { _frame.Popup.CloseAll(); return; }
        OpenFeature(id, toggle: false);
    }

    /// <summary>Dev (--perf-popovers): Quick actions' Wi-Fi / Bluetooth popover.</summary>
    internal Views.QuickActionsPanel DevQuickActions => _frame.QuickActions;
}
