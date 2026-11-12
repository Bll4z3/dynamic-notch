using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DynamicNotch.Services;

/// <summary>
/// Images in services are decoded <see cref="SoftwareBitmap"/>s (BGRA8, premultiplied): thread-agnostic
/// and readable pixel by pixel (album colour). Views turn them into an ImageSource on the UI thread.
/// </summary>
public static class ImageLoader
{
    /// <summary>Decodes a stream reference, downscaled so its width is at most <paramref name="maxWidth"/>.</summary>
    public static async Task<SoftwareBitmap?> LoadAsync(IRandomAccessStreamReference? reference, uint maxWidth)
    {
        if (reference == null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform();
            if (decoder.OrientedPixelWidth > maxWidth && decoder.OrientedPixelWidth > 0)
            {
                double k = (double)maxWidth / decoder.OrientedPixelWidth;
                transform.ScaledWidth = maxWidth;
                transform.ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.OrientedPixelHeight * k));
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        }
        catch { return null; }
    }

    private static readonly ConditionalWeakTable<SoftwareBitmap, SoftwareBitmapSource> Sources = new();

    /// <summary>An ImageSource for a bitmap (UI thread). Cached per bitmap.</summary>
    public static async Task<ImageSource?> ToSourceAsync(SoftwareBitmap? bitmap)
    {
        if (bitmap == null) return null;
        if (Sources.TryGetValue(bitmap, out var cached)) return cached;
        try
        {
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            Sources.AddOrUpdate(bitmap, source);
            return source;
        }
        catch { return null; }
    }
}
