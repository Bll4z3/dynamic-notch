using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DynamicNotch.Interop;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace DynamicNotch.Services;

/// <summary>A captured screen region: JPEG for the API (memory only) and a small thumbnail for the UI.</summary>
public sealed record Screenshot(string Base64Jpeg, BitmapSource Thumbnail, int PixelWidth, int PixelHeight);

/// <summary>
/// Captures a region of the screen in physical pixels (the app is per-monitor DPI aware, so screen
/// coordinates are physical on every monitor). The app's own windows are excluded from the image with
/// WDA_EXCLUDEFROMCAPTURE while capturing, or briefly made invisible where that isn't supported.
/// The image is downscaled (long edge ≤ 1568 px), encoded as JPEG q85 and base64'd. Nothing is saved.
/// </summary>
public static class ScreenCaptureService
{
    public static Screenshot Capture(Int32Rect physical, IEnumerable<Window> exclude)
    {
        if (physical.Width < 2 || physical.Height < 2) throw new ArgumentException("Region too small");

        // Keep our own windows (the notch) out of the picture.
        var hidden = new List<(Window w, double opacity)>();
        var excluded = new List<IntPtr>();
        foreach (var w in exclude)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero || !w.IsVisible) continue;
            if (Native.ExcludeFromCapture(hwnd, true)) excluded.Add(hwnd);
            else { hidden.Add((w, w.Opacity)); w.Opacity = 0; }
        }
        Native.WaitForCompositor();
        Native.WaitForCompositor();

        Drawing.Bitmap shot;
        try
        {
            shot = new Drawing.Bitmap(physical.Width, physical.Height, Imaging.PixelFormat.Format32bppRgb);
            using var g = Drawing.Graphics.FromImage(shot);
            g.CopyFromScreen(physical.X, physical.Y, 0, 0, new Drawing.Size(physical.Width, physical.Height), Drawing.CopyPixelOperation.SourceCopy);
        }
        finally
        {
            foreach (var hwnd in excluded) Native.ExcludeFromCapture(hwnd, false);
            foreach (var (w, opacity) in hidden) w.Opacity = opacity;
        }

        using (shot)
        {
            // Downscale for the API.
            double scale = Math.Min(1.0, (double)AppConfig.ScreenshotMaxEdge / Math.Max(shot.Width, shot.Height));
            using var forApi = Resize(shot, scale);
            string base64 = ToJpegBase64(forApi, AppConfig.ScreenshotJpegQuality);

            // Preview-sized copy for the thumbnails and the hover preview (memory only).
            double tScale = Math.Min(1.0, Math.Min((double)Design.Theme.ScreenshotPreviewPixelsW / shot.Width, (double)Design.Theme.ScreenshotPreviewPixelsH / shot.Height));
            using var thumb = Resize(shot, tScale);
            var thumbSource = ToBitmapSource(thumb);

            return new Screenshot(base64, thumbSource, forApi.Width, forApi.Height);
        }
    }

    private static Drawing.Bitmap Resize(Drawing.Bitmap src, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new Drawing.Bitmap(w, h, Imaging.PixelFormat.Format24bppRgb);
        using var g = Drawing.Graphics.FromImage(dst);
        g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.CompositingQuality = Drawing.Drawing2D.CompositingQuality.HighQuality;
        g.DrawImage(src, new Drawing.Rectangle(0, 0, w, h));
        return dst;
    }

    private static string ToJpegBase64(Drawing.Bitmap bmp, long quality)
    {
        var codec = Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == Imaging.ImageFormat.Jpeg.Guid);
        using var parameters = new Imaging.EncoderParameters(1);
        parameters.Param[0] = new Imaging.EncoderParameter(Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, parameters);
        return Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static BitmapSource ToBitmapSource(Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
