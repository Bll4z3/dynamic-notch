using System.Windows;
using System.Windows.Media;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// Screenshot thumbnail that takes the captured image's own shape: Uniform scaling inside
/// <see cref="Theme.ThumbMaxWidth"/> × <see cref="Theme.ThumbMaxHeight"/>, with the small radius
/// applied as a clip to the image itself and a hairline border.
/// </summary>
public sealed class ImageThumb : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ImageThumb),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    private static readonly Pen Border = Frozen(new Pen(Theme.TileBorderBrush, 1));

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Source is not { Width: > 0, Height: > 0 } src) return new Size(0, 0);
        // MaxWidth / MaxHeight pick the box (chip vs question block); Uniform fit inside it.
        double mw = double.IsInfinity(MaxWidth) ? Theme.ThumbMaxWidth : MaxWidth;
        double mh = double.IsInfinity(MaxHeight) ? Theme.ThumbMaxHeight : MaxHeight;
        double k = Math.Min(mw / src.Width, mh / src.Height);
        return new Size(Math.Round(src.Width * k), Math.Round(src.Height * k));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Source == null || ActualWidth <= 0) return;
        var r = new Rect(0, 0, ActualWidth, ActualHeight);
        double radius = Math.Min(Theme.CornerRadiusSmall, Math.Min(r.Width, r.Height) / 2);
        dc.PushClip(new RectangleGeometry(r, radius, radius));
        dc.DrawImage(Source, r);
        dc.Pop();
        var inner = new Rect(0.5, 0.5, r.Width - 1, r.Height - 1);
        dc.DrawRoundedRectangle(null, Border, inner, radius, radius);
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
