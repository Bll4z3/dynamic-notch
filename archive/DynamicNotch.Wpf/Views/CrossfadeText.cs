using System.Windows;
using System.Windows.Controls;
using DynamicNotch.Animation;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// A TextBlock that crossfades (with a small vertical slide) whenever its text changes.
/// Used for the clock so minute changes read smoothly.
/// </summary>
public sealed class CrossfadeText : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CrossfadeText), new PropertyMetadata("", (d, e) => ((CrossfadeText)d).OnTextChanged((string?)e.NewValue ?? "")));

    public static readonly DependencyProperty TextStyleProperty = DependencyProperty.Register(
        nameof(TextStyle), typeof(Style), typeof(CrossfadeText), new PropertyMetadata(null, (d, e) =>
        {
            var c = (CrossfadeText)d;
            c._a.Style = c._b.Style = (Style?)e.NewValue;
        }));

    private readonly TextBlock _a = new(), _b = new();
    private readonly System.Windows.Media.TranslateTransform _ta = new(), _tb = new();
    private readonly Spring _t = new(Motion.Reveal, 1);
    private bool _aIsFront = true;

    public CrossfadeText()
    {
        _a.RenderTransform = _ta;
        _b.RenderTransform = _tb;
        _b.Opacity = 0;
        Children.Add(_a);
        Children.Add(_b);
        ClipToBounds = true;
        _t.ValueChanged += Apply;
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Style? TextStyle { get => (Style?)GetValue(TextStyleProperty); set => SetValue(TextStyleProperty, value); }

    private void OnTextChanged(string text)
    {
        var front = _aIsFront ? _a : _b;
        if (string.IsNullOrEmpty(front.Text) || !IsLoaded)
        {
            front.Text = text;
            return;
        }
        if (front.Text == text) return;
        _aIsFront = !_aIsFront;
        (_aIsFront ? _a : _b).Text = text;
        _t.Snap(0);
        _t.SetTarget(1, Motion.Reveal);
    }

    private void Apply(double v)
    {
        double p = Math.Clamp(v, 0, 1);
        var (inc, incT, outg, outT) = _aIsFront ? (_a, _ta, _b, _tb) : (_b, _tb, _a, _ta);
        inc.Opacity = p;
        outg.Opacity = 1 - p;
        incT.Y = (1 - p) * 6;
        outT.Y = -p * 6;
        if (_t.IsSettled && p >= 1) outg.Text = ""; // stop the old text from affecting layout width
    }
}
