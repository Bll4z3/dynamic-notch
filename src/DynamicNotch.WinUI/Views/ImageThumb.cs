using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;

namespace DynamicNotch.Views;

/// <summary>
/// Screenshot thumbnail in the captured image's own shape: fitted (Uniform) inside a max box (the
/// attached chip or the question block), clipped to the small radius, with a hairline border.
/// Hover raises <see cref="PreviewRequested"/> / <see cref="PreviewEnded"/> for the enlarged card.
/// </summary>
public sealed class ImageThumb : Grid
{
    private readonly Border _frame = new();
    private readonly ImageBrush _fill = new() { Stretch = Stretch.UniformToFill };
    private readonly double _maxW, _maxH;
    private SoftwareBitmap? _bitmap;

    public ImageThumb(double maxWidth = Theme.ThumbMaxWidth, double maxHeight = Theme.ThumbMaxHeight)
    {
        _maxW = maxWidth;
        _maxH = maxHeight;
        HorizontalAlignment = HorizontalAlignment.Left;
        _frame.Background = _fill;
        _frame.BorderBrush = Theme.TileBorderBrush;
        _frame.BorderThickness = new Thickness(1);
        Children.Add(_frame);
        Visibility = Visibility.Collapsed;
        PointerEntered += (_, _) => { if (Source != null) PreviewRequested?.Invoke(this); };
        PointerExited += (_, _) => PreviewEnded?.Invoke();
    }

    public event Action<ImageThumb>? PreviewRequested;
    public event Action? PreviewEnded;

    /// <summary>The full preview-sized image (also used by the hover card).</summary>
    public ImageSource? Source { get; private set; }
    public SoftwareBitmap? Bitmap => _bitmap;

    public async void SetBitmap(SoftwareBitmap? bitmap)
    {
        _bitmap = bitmap;
        if (bitmap is not { PixelWidth: > 0, PixelHeight: > 0 })
        {
            Source = _fill.ImageSource = null;
            Visibility = Visibility.Collapsed;
            return;
        }
        double k = Math.Min(_maxW / bitmap.PixelWidth, _maxH / bitmap.PixelHeight);
        Width = Math.Round(bitmap.PixelWidth * k);
        Height = Math.Round(bitmap.PixelHeight * k);
        _frame.CornerRadius = new CornerRadius(Math.Min(Theme.CornerRadiusSmall, Math.Min(Width, Height) / 2));
        Visibility = Visibility.Visible;
        var source = await ImageLoader.ToSourceAsync(bitmap);
        if (!ReferenceEquals(bitmap, _bitmap)) return;
        Source = _fill.ImageSource = source;
    }
}
