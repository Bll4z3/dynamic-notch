using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.Views;

/// <summary>
/// Text that crossfades (with a small vertical slide) whenever it changes, used for the clock so
/// minute changes read smoothly. The fade and slide are compositor springs on the two text visuals.
/// </summary>
public sealed class CrossfadeText : Grid
{
    private readonly TextBlock _a = new(), _b = new();
    private readonly RectangleGeometry _clip = new();
    private bool _aIsFront = true;
    private string _text = "";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _cleanup; // held: unreferenced timers are collected
    private TextBlock? _cleanupTarget;

    private void OnCleanup(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object e)
    {
        t.Stop();
        if (_cleanupTarget != null && !ReferenceEquals(_cleanupTarget, _aIsFront ? _a : _b)) _cleanupTarget.Text = "";
    }

    public CrossfadeText()
    {
        Children.Add(_a);
        Children.Add(_b);
        Clip = _clip;
        SizeChanged += (_, e) => _clip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        Loaded += (_, _) => Anim.VisualOf(_b).Opacity = 0;
    }

    /// <summary>Applies the same look to both text layers.</summary>
    public void SetStyle(Action<TextBlock> style)
    {
        style(_a);
        style(_b);
    }

    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (value == _text) return;
            _text = value;
            var front = _aIsFront ? _a : _b;
            if (string.IsNullOrEmpty(front.Text) || !IsLoaded)
            {
                front.Text = value;
                return;
            }
            _aIsFront = !_aIsFront;
            var (incoming, outgoing) = _aIsFront ? (_a, _b) : (_b, _a);
            incoming.Text = value;
            var vi = Anim.VisualOf(incoming, translation: true);
            var vo = Anim.VisualOf(outgoing, translation: true);
            Anim.SpringTo(vi, "Opacity", 0, 1, Motion.Reveal);
            Anim.SpringTo(vo, "Opacity", 1, 0, Motion.Reveal);
            Anim.SpringTo(vi, "Translation.Y", 6, 0, Motion.Reveal, precision: 10);
            Anim.SpringTo(vo, "Translation.Y", 0, -6, Motion.Reveal, precision: 10);
            // Drop the old text once it's gone so it stops affecting the layout width.
            var old = outgoing;
            _cleanup ??= DispatcherQueue.CreateTimer();
            _cleanup.Stop();
            _cleanup.Interval = TimeSpan.FromMilliseconds(450);
            _cleanup.IsRepeating = false;
            _cleanupTarget = old;
            _cleanup.Tick -= OnCleanup;
            _cleanup.Tick += OnCleanup;
            _cleanup.Start();
        }
    }
}
