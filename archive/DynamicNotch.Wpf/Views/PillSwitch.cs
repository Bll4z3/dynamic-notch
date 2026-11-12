using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DynamicNotch.Animation;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// On/off switch: a fully round track (radius = height / 2) with a circular thumb that springs
/// across, and a track colour that blends from dark grey (off) to white (on). It's a ToggleButton, so
/// Space toggles it when focused, and it draws a visible focus ring for keyboard users. Bump
/// <see cref="ShakeTrigger"/> to play a short "no" shake.
/// </summary>
public sealed class PillSwitch : ToggleButton
{
    public static readonly DependencyProperty ShakeTriggerProperty = DependencyProperty.Register(
        nameof(ShakeTrigger), typeof(int), typeof(PillSwitch),
        new PropertyMetadata(0, (d, _) => ((PillSwitch)d).Shake()));

    private readonly Spring _thumb = new(Motion.SwitchThumb, 0);
    private readonly Spring _shake = new(Motion.Shake, 0) { Precision = 20 };

    public PillSwitch()
    {
        Template = new ControlTemplate(typeof(PillSwitch)); // drawn in OnRender
        FocusVisualStyle = null;                           // our own ring below
        Cursor = Cursors.Hand;
        Focusable = true;
        IsTabStop = true;
        _thumb.ValueChanged += _ => InvalidateVisual();
        _shake.ValueChanged += _ => InvalidateVisual();
        Checked += (_, _) => _thumb.SetTarget(1, Motion.SwitchThumb);
        Unchecked += (_, _) => _thumb.SetTarget(0, Motion.SwitchThumb);
        Loaded += (_, _) => _thumb.Snap(IsChecked == true ? 1 : 0);
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    public int ShakeTrigger { get => (int)GetValue(ShakeTriggerProperty); set => SetValue(ShakeTriggerProperty, value); }

    private void Shake()
    {
        if (!SpringAnimator.AnimationsEnabled) return;
        _shake.Snap(0);
        _shake.Impulse(Motion.ShakeImpulse);
    }

    protected override Size MeasureOverride(Size constraint) => new(Theme.SwitchWidth + 4, Theme.SwitchHeight + 4);

    protected override void OnRender(DrawingContext dc)
    {
        double t = Math.Clamp(_thumb.Value, 0, 1);
        double dx = _shake.Value;
        var track = new Rect(2 + dx, 2, Theme.SwitchWidth, Theme.SwitchHeight);
        double r = track.Height / 2; // fully round ends

        // Transparent hit area, focus ring, track, thumb.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (IsKeyboardFocused)
        {
            var ring = track;
            ring.Inflate(2, 2);
            dc.DrawRoundedRectangle(null, new Pen(Theme.FocusRingBrush, 1.5), ring, ring.Height / 2, ring.Height / 2);
        }
        var colour = Theme.Lerp(Theme.SwitchOffColor, Theme.SwitchOnColor, t);
        dc.DrawRoundedRectangle(new SolidColorBrush(colour), null, track, r, r);

        double d = Theme.SwitchThumbSize, inset = Theme.SwitchThumbInset;
        double x0 = track.X + inset + d / 2, x1 = track.Right - inset - d / 2;
        var centre = new Point(x0 + (x1 - x0) * Math.Clamp(_thumb.Value, -0.1, 1.1), track.Y + track.Height / 2);
        dc.DrawEllipse(t > 0.5 ? Theme.SwitchThumbOnBrush : Theme.SwitchThumbOffBrush, null, centre, d / 2, d / 2);
    }
}
