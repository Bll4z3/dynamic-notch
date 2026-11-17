namespace DynamicNotch;

/// <summary>
/// Page 4's popups (Wi-Fi / Bluetooth): a popup's password box takes keyboard focus only when it asks
/// (the previous window gets it back afterwards); Esc closes an open popup before it closes the notch;
/// leaving the page or closing the notch closes any popup.
/// </summary>
public sealed partial class NotchWindow
{
    private void BuildHub()
    {
        var hub = _frame.Hub;
        hub.FocusRequested += () => { TakeFocus(); Services.Ui.Post(UpdateKeyCapture); };
        hub.FocusReleased += () => { if (_hasFocus) ReleaseFocus(restore: true); };
        hub.PopoverClosed += () => { if (_hasFocus && !Vm.IsAskPage) ReleaseFocus(restore: true); };
    }

    /// <summary>Esc: an open popup closes first. Returns true when it handled the key.</summary>
    private bool EscapeClosesPopover() => _state == NotchState.Expanded && _frame.Hub.ClosePopover();
}
