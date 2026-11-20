using DynamicNotch.Interop;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace DynamicNotch.Views;

/// <summary>
/// Draws the notch's soft drop shadow in its own see-through, click-through window, kept exactly over the
/// notch window and directly under it in the z-order.
/// <para>
/// Why a second window: the notch window is clipped to the notch by a window region, because a region is
/// also what decides where the window takes the mouse (everything outside it is click-through). The region
/// ended at the notch's bottom edge (collapsed) or 16 px below it (open), while the shadow needs about 22 px,
/// so the shadow was cut off along the bottom and only showed at the sides. Growing the region would have
/// made a band under the notch swallow clicks meant for the app below. This window ignores the mouse entirely
/// (layered + transparent), so the shadow can spread freely and nothing under it is blocked.
/// </para>
/// The shadow visual is the one <see cref="NotchShape"/> builds, so it follows the notch's size springs, radius
/// and shadow strength through the same compositor expressions, and costs nothing per frame.
/// </summary>
internal sealed class NotchShadowWindow : Window
{
    private readonly IntPtr _hwnd;
    private readonly Grid _host = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private bool _shown;

    /// <param name="shadow">The shadow visual, laid out in the notch host's coordinates.</param>
    /// <param name="follow">The notch's root visual: the shadow takes its opacity (the fullscreen fade).</param>
    public NotchShadowWindow(ContainerVisual shadow, Visual follow, double hostWidth, double hostHeight)
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Dynamic Notch shadow";
        SystemBackdrop = new TransparentBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        Native.MakeSeeThroughPopup(_hwnd);
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST, Native.WS_EX_APPWINDOW);
        Native.MakeClickThrough(_hwnd);

        _host.Width = hostWidth;
        _host.Height = hostHeight;
        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(_host);
        Content = root;

        var fade = shadow.Compositor.CreateExpressionAnimation("f.Opacity");
        fade.SetReferenceParameter("f", follow);
        shadow.StartAnimation("Opacity", fade);
        ElementCompositionPreview.SetElementChildVisual(_host, shadow);
    }

    /// <summary>Moves / resizes the window onto the notch window's rectangle (physical pixels), drawn at the notch scale.</summary>
    public void Place(IntPtr notch, int x, int y, int width, int height, double notchScale)
    {
        _host.RenderTransform = notchScale == 1 ? null : new ScaleTransform { ScaleX = notchScale, ScaleY = notchScale };
        Native.PlaceBelow(_hwnd, notch, x, y, width, height, move: true);
    }

    /// <summary>Shows it (without activating) right under the notch window.</summary>
    public void Show(IntPtr notch)
    {
        if (!_shown)
        {
            _shown = true;
            AppWindow.Show(activateWindow: false);
            Native.MakeSeeThroughPopup(_hwnd); // showing re-applies the presenter frame
            Native.MakeClickThrough(_hwnd);
        }
        KeepBelow(notch);
    }

    public void Hide()
    {
        if (!_shown) return;
        _shown = false;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
    }

    /// <summary>Re-stacks it directly under the notch window (after the notch re-asserts topmost).</summary>
    public void KeepBelow(IntPtr notch)
    {
        if (_shown) Native.PlaceBelow(_hwnd, notch, 0, 0, 0, 0, move: false);
    }
}
