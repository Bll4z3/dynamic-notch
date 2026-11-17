using System.ComponentModel;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.ViewModels;
using DynamicNotch.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace DynamicNotch;

/// <summary>
/// The activity pill host. Collapsed (or showing a notification pill), the notch itself springs into
/// the activity pill: the body morphs to the kind's width and <see cref="Theme.ActivityHeight"/>, and the
/// content fades and rises in once the shape is far enough along. Open, a slim twin appears at the
/// top of the notch (centred in the header) without moving the page. Hovering the pill keeps it up
/// (and lets the level track be dragged) instead of opening the notch; clicking it dismisses it.
/// </summary>
public sealed partial class NotchWindow
{
    private ActivityPillView _activityView = null!, _activityCompact = null!;
    private CompSpring _activityContent = null!, _compactShow = null!;
    private bool _hoverActivity;

    private void BuildActivity()
    {
        var c = _shape.Compositor;
        var n = _shape.Props;

        // Collapsed form: the notch body is the background.
        _activityView = new ActivityPillView(compact: false) { Width = Theme.ActivityWidthSlider };
        ActivityLayer.Children.Add(_activityView);
        ActivityLayer.Width = Theme.ActivityWidthSlider;
        ActivityLayer.Height = Theme.ActivityHeight;
        _activityContent = new CompSpring(n, "Act", Motion.Pop, 0);
        var lv = Anim.VisualOf(ActivityLayer);
        lv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(N.Act, 0, 1)", ("N", n)));
        var s = Anim.Expr(c, "Vector3(S + (1 - S) * Clamp(N.Act, 0, 1.1), S + (1 - S) * Clamp(N.Act, 0, 1.1), 1)", ("N", n));
        s.SetScalarParameter("S", (float)Motion.PillStartScale);
        lv.StartAnimation("Scale", s);
        ActivityLayer.SizeChanged += (_, e) => lv.CenterPoint = new Vector3((float)e.NewSize.Width / 2, 0, 0);
        ActivityLayer.Tapped += (_, e) => { e.Handled = true; Vm.Activity.Click(); };

        // Open form: a slim capsule in the header's free centre.
        _activityCompact = new ActivityPillView(compact: true)
        {
            Width = Theme.ActivityCompactWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, Theme.ExpandedPadding.Top + (Theme.HeaderHeight - Theme.ActivityCompactHeight) / 2, 0, 0),
        };
        ExpandedLayer.Children.Add(_activityCompact);
        var (show, set) = Motions.Value(c, Motion.Pop, 0);
        _compactShow = show;
        var cv = Anim.VisualOf(_activityCompact, translation: true);
        cv.CenterPoint = new Vector3((float)Theme.ActivityCompactWidth / 2, (float)Theme.ActivityCompactHeight / 2, 0);
        cv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
        cv.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.85 + 0.15 * p.v, 0.85 + 0.15 * p.v, 1)", ("p", set)));
        cv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (1 - p.v) * -6, 0)", ("p", set)));
        _activityCompact.IsHitTestVisible = false;
        _activityCompact.Tapped += (_, e) => { e.Handled = true; Vm.Activity.Click(); };
        _activityCompact.PointerEntered += (_, _) => Vm.Activity.Hold(true);
        _activityCompact.PointerExited += (_, _) => { if (!_activityCompact.Track.IsDragging) Vm.Activity.Hold(false); };

        _activityView.Track.HoverChanged += over => { if (!over && !_mouseOver) Vm.Activity.Hold(false); };
        Vm.Activity.Updated += OnActivityUpdated;
        Vm.Activity.PropertyChanged += OnActivityChanged;
    }

    private double ActivityWidth() => Vm.Activity.Current is { } a ? ActivityPillView.WidthFor(a.Kind) : Theme.ActivityWidthSlider;

    private void OnActivityUpdated(Activity a, bool sameKind)
    {
        double w = ActivityPillView.WidthFor(a.Kind);
        ActivityLayer.Width = w;
        _activityView.Width = w;
        // A new kind while the pill is already up: cross-morph (the shape re-targets in ApplyTargets).
        bool entering = _state != NotchState.Activity;
        _activityView.Apply(a, sameKind, entering);
        _activityCompact.Apply(a, sameKind, entering: _compactShow.Target < 1);
        if (_state == NotchState.Activity && !sameKind) ApplyTargets();
        UpdateCompactActivity();
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActivityViewModel.HasActivity)) return;
        if (!Vm.Activity.HasActivity) _hoverActivity = false;
        UpdateState();
        UpdateCompactActivity();
    }

    /// <summary>The slim twin is visible only while the notch is open and an activity is up.</summary>
    private void UpdateCompactActivity()
    {
        bool show = _state == NotchState.Expanded && Vm.Activity.HasActivity;
        if (show && _compactShow.Target < 1) _activityCompact.Enter();
        _compactShow.SetTarget(show ? 1 : 0, show ? Motion.Pop : Motion.FadeOut);
        _activityCompact.IsHitTestVisible = show;
    }

    /// <summary>Hovering the collapsed activity pill holds it (no expand); leaving releases it.</summary>
    private bool OnActivityPointerEntered()
    {
        if (_state != NotchState.Activity) return false;
        _hoverActivity = true;
        Vm.Activity.Hold(true);
        return true;
    }

    private void OnActivityPointerLeft()
    {
        if (!_hoverActivity) return;
        _hoverActivity = false;
        if (!_activityView.Track.IsDragging) Vm.Activity.Hold(false);
    }
}
