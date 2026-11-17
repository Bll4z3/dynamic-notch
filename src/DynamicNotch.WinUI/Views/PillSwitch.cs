using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>
/// On/off switch (the WPF PillSwitch): a fully round track (radius = height / 2) with a circular
/// thumb that springs across, and a track that blends from dark grey (off) to white (on). All of it
/// is compositor motion: the "on" track fades over the "off" one and the thumb's two colours swap at
/// halfway. Focusable: Space / Enter toggle it, and a focus ring shows for keyboard users.
/// <see cref="Shake"/> plays a short "no" shake.
/// </summary>
public sealed class PillSwitch : UserControl
{
    private readonly Border _ring = new(), _off = new(), _on = new();
    private readonly Ellipse _thumbOff = new(), _thumbOn = new();
    private readonly CompositionPropertySet _p;
    private readonly CompSpring _t, _shake;
    private bool _isOn;

    public PillSwitch()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Width = Theme.SwitchWidth + 4;
        Height = Theme.SwitchHeight + 4;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

        var root = new Grid { Background = Theme.TransparentBrush };
        var radius = new CornerRadius(Theme.SwitchHeight / 2);
        _ring.CornerRadius = new CornerRadius((Theme.SwitchHeight + 4) / 2);
        _ring.BorderBrush = Theme.AccentBrush;
        _ring.BorderThickness = new Thickness(1.5);
        _ring.Visibility = Visibility.Collapsed;
        var track = new Grid { Width = Theme.SwitchWidth, Height = Theme.SwitchHeight, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _off.CornerRadius = _on.CornerRadius = radius;
        _off.Background = new SolidColorBrush(Theme.SwitchOffColor);
        _on.Background = Theme.AccentBrush; // "on" carries the accent (live)
        track.Children.Add(_off);
        track.Children.Add(_on);
        foreach (var (e, colour) in new[] { (_thumbOff, Theme.SwitchThumbOffColor), (_thumbOn, Theme.SwitchThumbOnColor) })
        {
            e.Width = e.Height = Theme.SwitchThumbSize;
            e.Fill = e == _thumbOn ? Theme.OnAccentBrush : new SolidColorBrush(colour);
            e.HorizontalAlignment = HorizontalAlignment.Left;
            e.VerticalAlignment = VerticalAlignment.Center;
            e.Margin = new Thickness(Theme.SwitchThumbInset, 0, 0, 0);
            track.Children.Add(e);
        }
        root.Children.Add(_ring);
        root.Children.Add(track);
        Content = root;

        var tv = Anim.VisualOf(track, translation: true);
        var c = tv.Compositor;
        _p = c.CreatePropertySet();
        _t = new CompSpring(_p, "t", Motion.SwitchThumb, 0);
        _shake = new CompSpring(_p, "s", Motion.Shake, 0, precision: 20);
        tv.StartAnimation("Translation", Anim.Expr(c, "Vector3(p.s, 0, 0)", ("p", _p)));
        Anim.VisualOf(_on).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.t, 0, 1)", ("p", _p)));
        float travel = (float)(Theme.SwitchWidth - 2 * Theme.SwitchThumbInset - Theme.SwitchThumbSize);
        foreach (var (e, on) in new[] { (_thumbOff, false), (_thumbOn, true) })
        {
            var v = Anim.VisualOf(e, translation: true);
            var move = Anim.Expr(c, "Vector3(D * Clamp(p.t, -0.1, 1.1), 0, 0)", ("p", _p));
            move.SetScalarParameter("D", travel);
            v.StartAnimation("Translation", move);
            v.StartAnimation("Opacity", Anim.Expr(c, on ? "p.t > 0.5 ? 1 : 0" : "p.t > 0.5 ? 0 : 1", ("p", _p)));
        }

        Tapped += (_, e) => { e.Handled = true; Toggle(); };
        KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Space or VirtualKey.Enter) { Toggle(); e.Handled = true; }
        };
        GotFocus += (_, _) => _ring.Visibility = FocusState == FocusState.Keyboard ? Visibility.Visible : Visibility.Collapsed;
        LostFocus += (_, _) => _ring.Visibility = Visibility.Collapsed;
    }

    /// <summary>The user flipped the switch (the owner decides whether it sticks; see <see cref="IsOn"/>).</summary>
    public event Action<bool>? Toggled;
    /// <summary>Any switch in the app was flipped by the user (for the toggle sound).</summary>
    public static event Action<bool>? UserToggled;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            if (IsLoaded) _t.SetTarget(value ? 1 : 0, Motion.SwitchThumb);
            else _t.Snap(value ? 1 : 0);
        }
    }

    public void Shake()
    {
        if (!Anim.Enabled) return;
        _shake.Snap(0);
        _shake.Impulse(Motion.ShakeImpulse);
    }

    private void Toggle()
    {
        IsOn = !IsOn;
        UserToggled?.Invoke(IsOn);
        Toggled?.Invoke(IsOn);
    }
}
