using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Interop;
using Microsoft.UI.Dispatching;

namespace DynamicNotch;

/// <summary>
/// Hiding while another app is fullscreen (Services/FullscreenService decides when): the notch closes,
/// fades out quickly and its window is hidden, so it can't sit over a video or game or take a click.
/// When fullscreen ends it's shown again (topmost, without activating) and fades back in.
/// </summary>
public sealed partial class NotchWindow
{
    private CompSpring? _visibleFade;
    private DispatcherQueueTimer? _hideTimer;
    private bool _fullscreenHidden;

    /// <summary>The overlay's window handle (the fullscreen check ignores it and uses its monitor).</summary>
    public IntPtr Handle => _hwnd;

    public void SetFullscreenHidden(bool hidden)
    {
        if (hidden == _fullscreenHidden) return;
        _fullscreenHidden = hidden;
        if (_visibleFade == null)
        {
            var v = Anim.VisualOf(Root);
            var (spring, set) = Motions.Value(v.Compositor, Motion.FadeOut, 1);
            _visibleFade = spring;
            v.StartAnimation("Opacity", Anim.Expr(v.Compositor, "Clamp(p.v, 0, 1)", ("p", set)));
            _hideTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _hideTimer.IsRepeating = false;
            _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (_fullscreenHidden) Native.ShowWindow(_hwnd, Native.SW_HIDE); };
        }
        _hideTimer!.Stop();

        if (hidden)
        {
            // Close the notch so it comes back collapsed.
            _expandTimer.Stop();
            _hover = false;
            Unpin();
            if (_hasFocus) ReleaseFocus(restore: false);
            UpdateState();
            _visibleFade.SetTarget(0, Motion.FadeOut);
            _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.05, _visibleFade.Model.RemainingDuration));
            _hideTimer.Start();
        }
        else
        {
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            Native.MakeSeeThroughPopup(_hwnd);
            Native.ReassertTopmost(_hwnd);
            UpdateRegion();
            _visibleFade.SetTarget(1, Motion.FadeIn);
        }
    }
}
