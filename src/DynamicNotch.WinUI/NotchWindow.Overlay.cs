using System.Runtime.InteropServices;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using DynamicNotch.Views;

namespace DynamicNotch;

/// <summary>
/// The overlays that slide in over the pages from the header icons: Ask Claude and the command palette.
/// Each has a spring on the notch's property set ("AskOv" / "PalOv") that slides its layer in from the right
/// while the pages fade out underneath, and the notch resizes to what the overlay asks for. Both take keyboard
/// focus (the previous window gets it back when they close). Opened from a hotkey (Ctrl+Shift+Space for Ask,
/// Alt+Space by default for the palette), closing collapses the notch again; opened from an icon, Ask closes
/// back to the pages. The palette always collapses the notch when it's done (it's a quick, transient tool).
/// </summary>
public sealed partial class NotchWindow
{
    private const int PaletteHotkeyId = 0x4E50; // "NP"
    private CompSpring _askOv = null!, _palOv = null!;
    private bool _overlayFromHotkey, _paletteHotkeyRegistered;
    private (uint mods, uint key) _paletteHotkey;

    /// <summary>False when the palette's hotkey couldn't be registered (another app owns it); Settings shows a warning.</summary>
    public static bool PaletteHotkeyAvailable { get; private set; } = true;

    private void BuildOverlays()
    {
        _askOv = new CompSpring(_shape.Props, "AskOv", Motion.HeaderPopupOpen, 0);
        _palOv = new CompSpring(_shape.Props, "PalOv", Motion.HeaderPopupOpen, 0);
        _frame.OverlayRequested += o => { _overlayFromHotkey = false; SetOverlay(o); };
        _frame.FeatureRequested += id => OpenFeature(id, toggle: true);
        var pal = _frame.Palette;
        pal.CloseRequested += () => CloseOverlay(collapse: true);
        pal.NotchSizeChanged += () => { if (_state == NotchState.Expanded && Vm.IsPaletteOpen) ApplyTargets(); };
        pal.FocusRequested += () => { TakeFocus(); Ui.Post(UpdateKeyCapture); };
        Vm.Palette.FeatureRequested += OnPaletteFeature;
        Vm.Palette.PasteRequested += text => { var paste = PasteAsync(); };
        Vm.Settings.Changed += UpdatePaletteHotkey;
        Vm.QuickTimer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QuickTimerViewModel.Display)) UpdateCompactText();
            else ApplyTargets();
        };
        Vm.QuickTimer.Finished += d => Vm.EnqueuePill(new PillRequest(PillSource.System, "Timer done",
            $"{(d.TotalHours >= 1 ? $"{(int)d.TotalHours} h " : "")}{(d.Minutes > 0 ? $"{d.Minutes} min " : "")}{(d.Seconds > 0 ? $"{d.Seconds} s" : "")}".Trim() + " is up", Icons.Timer));
    }

    /// <summary>Shows an overlay (or None for the pages), with focus where it belongs.</summary>
    private void SetOverlay(NotchOverlay overlay)
    {
        var was = Vm.Overlay;
        if (was == NotchOverlay.Palette && overlay != NotchOverlay.Palette) { Vm.Palette.Closed(); _frame.Palette.Hidden(); }
        Vm.Overlay = overlay;
        _askOv.SetTarget(overlay == NotchOverlay.Ask ? 1 : 0, overlay == NotchOverlay.Ask ? Motion.HeaderPopupOpen : Motion.HeaderPopupClose);
        _palOv.SetTarget(overlay == NotchOverlay.Palette ? 1 : 0, overlay == NotchOverlay.Palette ? Motion.HeaderPopupOpen : Motion.HeaderPopupClose);
        if (overlay != NotchOverlay.None) _frame.Popup.CloseAll();
        _frame.UpdateIconStates(overlay);
        switch (overlay)
        {
            case NotchOverlay.Ask:
                Pin();
                TakeFocus();
                _frame.Ask.FocusInput();
                break;
            case NotchOverlay.Palette:
                Pin();
                TakeFocus();
                _frame.Palette.Reset();
                break;
            default:
                _frame.Preview.SetShown(false);
                if (_hasFocus) ReleaseFocus(restore: true);
                break;
        }
        UpdateKeyCapture();
        ApplyTargets();
    }

    /// <summary>A palette action already took care of where the notch goes (a feature opened, a paste).</summary>
    private bool _paletteHandled;

    /// <summary>
    /// Opens a feature where the layout puts it: a page → go to it; Ask / the palette as an icon (or hidden) →
    /// slide in over the pages; anything else → the header pop-up (<paramref name="toggle"/>: a second click on
    /// its icon closes it).
    /// </summary>
    internal void OpenFeature(PageId id, bool toggle = false, bool fromHotkey = false)
    {
        bool wasOpen = _state == NotchState.Expanded;
        if (Vm.Pages.IsEnabled(id))
        {
            if (Vm.Overlay != NotchOverlay.None) SetOverlay(NotchOverlay.None);
            _frame.Popup.CloseAll();
            if (Vm.GoTo(id)) _page.SetTarget(Vm.PageIndex, Motion.Page);
            _overlayFromHotkey = fromHotkey && !wasOpen;
            Pin();
            if (id == PageId.Ask) { TakeFocus(); _frame.Ask.FocusInput(); }
            else if (id == PageId.Palette) { TakeFocus(); _frame.Palette.Reset(); }
            UpdateKeyCapture();
            return;
        }
        if (id is PageId.Ask or PageId.Palette)
        {
            var o = id == PageId.Ask ? NotchOverlay.Ask : NotchOverlay.Palette;
            if (toggle && Vm.Overlay == o) { CloseOverlay(); return; }
            _overlayFromHotkey = fromHotkey && !wasOpen;
            SetOverlay(o);
            return;
        }
        if (Vm.Overlay != NotchOverlay.None) SetOverlay(NotchOverlay.None);
        Pin();
        if (toggle) _frame.Popup.Toggle(id, null);
        else _frame.Popup.Open(id);
    }

    /// <summary>Esc / × / done: back to the pages, or (opened by a hotkey, or <paramref name="collapse"/>) close the notch.</summary>
    private void CloseOverlay(bool collapse = false)
    {
        if (_paletteHandled) { _paletteHandled = false; return; }
        if (Vm.Overlay == NotchOverlay.None)
        {
            // Ask / the palette as a page: done means the notch closes (when asked to, or opened by the hotkey).
            if (collapse || _overlayFromHotkey) { _overlayFromHotkey = false; CollapseAsk(); }
            return;
        }
        if (collapse || _overlayFromHotkey)
        {
            _overlayFromHotkey = false;
            CollapseAsk(); // unpin, give focus back, close (the overlay resets as the notch collapses)
            return;
        }
        SetOverlay(NotchOverlay.None);
    }

    /// <summary>The notch is closing: overlays snap shut so the next open starts on the pages.</summary>
    private void ResetOverlayOnCollapse()
    {
        if (Vm.Overlay == NotchOverlay.None) return;
        if (Vm.Overlay == NotchOverlay.Palette) { Vm.Palette.Closed(); _frame.Palette.Hidden(); }
        Vm.Overlay = NotchOverlay.None;
        _askOv.SetTarget(0, Motion.FadeOut);
        _palOv.SetTarget(0, Motion.FadeOut);
        _overlayFromHotkey = false;
    }

    /// <summary>Dev (`--palette [text]`): opens the palette with that text typed.</summary>
    public void DevPalette(string text)
    {
        DevHoldOpen = true;
        _overlayFromHotkey = false;
        OpenFeature(PageId.Palette);
        if (text.Length > 0) _frame.Palette.DevType(text);
    }

    // ---- Palette hotkey -------------------------------------------------------------------------------
    private void OnPaletteHotkey()
    {
        if (_selecting) return;
        if (_state == NotchState.Expanded && Vm.IsPaletteOpen) { CloseOverlay(collapse: true); return; }
        OpenFeature(PageId.Palette, fromHotkey: true);
    }

    /// <summary>(Re-)registers the palette hotkey from Settings; turning the palette's icon off doesn't remove the hotkey.</summary>
    private void UpdatePaletteHotkey()
    {
        if (_messages == null) return;
        var want = (Vm.Settings.Current.Palette.HotkeyModifiers, Vm.Settings.Current.Palette.HotkeyKey);
        if (_paletteHotkeyRegistered && want == _paletteHotkey) return;
        if (_paletteHotkeyRegistered) { Native.UnregisterHotKey(_messages.Handle, PaletteHotkeyId); _paletteHotkeyRegistered = false; }
        _paletteHotkey = want;
        if (want.Item2 == 0) { PaletteHotkeyAvailable = true; return; }
        _paletteHotkeyRegistered = Native.RegisterHotKey(_messages.Handle, PaletteHotkeyId, want.Item1 | 0x4000 /* MOD_NOREPEAT */, want.Item2);
        PaletteHotkeyAvailable = _paletteHotkeyRegistered;
    }

    // ---- Palette actions that need the window ------------------------------------------------------------
    private void OnPaletteFeature(string feature)
    {
        _overlayFromHotkey = false; // the notch stays open on what was picked
        _paletteHandled = true;     // ...so the palette's own "close" afterwards doesn't collapse it
        if (Vm.IsPaletteOpen && Vm.Overlay == NotchOverlay.None) { Vm.Palette.Closed(); _frame.Palette.Hidden(); }
        switch (feature)
        {
            case "ask": OpenFeature(PageId.Ask); break;
            case "quick": OpenFeature(PageId.QuickActions); break;
            case "notifications": OpenFeature(PageId.Notifications); break;
            case "settings": SetOverlay(NotchOverlay.None); Vm.OpenSettingsCommand.Execute(null); break;
            default:
                if (feature.StartsWith("page:") && Enum.TryParse<PageId>(feature[5..], out var page)) OpenFeature(page);
                else if (feature.StartsWith("screenshot:"))
                {
                    var item = Vm.Screenshots.Items.FirstOrDefault(i => i.Path == feature[11..]);
                    if (item != null) Vm.Screenshots.Selected = item;
                    OpenFeature(PageId.Screenshots);
                }
                break;
        }
    }

    /// <summary>Clipboard history → paste: the item is on the clipboard; close, hand focus back, then press Ctrl+V there.</summary>
    private async Task PasteAsync()
    {
        _overlayFromHotkey = false;
        _paletteHandled = true;
        CollapseAsk();
        await Task.Delay(140); // the previous window is active again
        SendCtrlV();
    }

    private static void SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11, VK_V = 0x56;
        const uint KEYUP = 0x0002;
        var inputs = new[]
        {
            Key(VK_CONTROL, 0), Key(VK_V, 0), Key(VK_V, KEYUP), Key(VK_CONTROL, KEYUP),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        static INPUT Key(ushort vk, uint flags) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };
    }

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
