using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// Renders one icon from <see cref="Icons"/> as vector geometry. Foreground is inherited like text,
/// so an icon inside a button follows the button's colour.
/// </summary>
public sealed class VectorIcon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(string), typeof(VectorIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(VectorIcon),
        new FrameworkPropertyMetadata(Theme.IconMedium, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(VectorIcon), new FrameworkPropertyMetadata(Theme.PrimaryText,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public VectorIcon()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
    }

    public string? Data { get => (string?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc) =>
        Icons.Draw(dc, Data, new Point(ActualWidth / 2, ActualHeight / 2), Size, Foreground);
}
