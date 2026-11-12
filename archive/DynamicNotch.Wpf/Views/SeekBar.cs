using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DynamicNotch.Animation;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// Seekable progress bar: a thin rounded track that thickens on hover and grows a draggable handle,
/// both with springs. Dragging scrubs; the position is committed on release. Fill uses
/// <see cref="FillBrush"/> (the music page's album-art accent).
/// </summary>
public sealed class SeekBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsSeekableProperty = DependencyProperty.Register(
        nameof(IsSeekable), typeof(bool), typeof(SeekBar), new PropertyMetadata(true));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Theme.PrimaryText, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Spring _hover = new(Motion.Hover, 0) { Precision = 0.5 };
    private bool _dragging;
    private double _dragValue;

    public SeekBar()
    {
        Height = Theme.SeekHeight;
        Cursor = Cursors.Hand;
        _hover.ValueChanged += _ => InvalidateVisual();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsSeekable { get => (bool)GetValue(IsSeekableProperty); set => SetValue(IsSeekableProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    public event Action? ScrubStarted;
    public event Action<double>? Scrubbing;
    public event Action<double>? ScrubCommitted;

    protected override void OnMouseEnter(MouseEventArgs e) { if (IsSeekable) _hover.SetTarget(1, Motion.Hover); }
    protected override void OnMouseLeave(MouseEventArgs e) { if (!_dragging) _hover.SetTarget(0, Motion.Hover); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsSeekable) return;
        _dragging = true;
        CaptureMouse();
        ScrubStarted?.Invoke();
        Update(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Update(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (!IsMouseOver) _hover.SetTarget(0, Motion.Hover);
        ScrubCommitted?.Invoke(_dragValue);
        e.Handled = true;
    }

    private void Update(MouseEventArgs e)
    {
        _dragValue = ActualWidth > 0 ? Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1) : 0;
        Scrubbing?.Invoke(_dragValue);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent full-height hit area so the bar is easy to grab.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        double hv = Math.Clamp(_hover.Value, 0, 1.2);
        double t = Theme.SeekThickness + (Theme.SeekThicknessHover - Theme.SeekThickness) * hv;
        double y = (ActualHeight - t) / 2;
        double r = Math.Min(Theme.CornerRadius, t / 2);
        double v = Math.Clamp(_dragging ? _dragValue : Value, 0, 1);
        double w = ActualWidth;

        dc.DrawRoundedRectangle(Theme.TrackBrush, null, new Rect(0, y, w, t), r, r);
        if (v > 0)
            dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, y, Math.Max(t, w * v), t), r, r);

        // Handle: grows in on hover (and stays while dragging).
        double hr = Theme.SeekHandleSize / 2 * Math.Clamp(hv, 0, 1.1);
        if (hr > 0.3 && IsSeekable)
            dc.DrawEllipse(Theme.PrimaryText, null, new Point(Math.Clamp(w * v, hr, w - hr), ActualHeight / 2), hr, hr);
    }
}
