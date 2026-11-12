using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DynamicNotch.Interop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DynamicNotch.Services;

/// <summary>A captured screen region: JPEG for the API (memory only) and a preview-sized copy for the UI.</summary>
public sealed record Screenshot(string Base64Jpeg, SoftwareBitmap Thumbnail, int PixelWidth, int PixelHeight);

/// <summary>
/// Captures a region of the screen in physical pixels (the app is per-monitor DPI aware, so screen
/// coordinates are physical on every monitor). The app's own windows are excluded from the image with
/// WDA_EXCLUDEFROMCAPTURE while capturing. The image is downscaled (long edge ≤ 1568 px), encoded as
/// JPEG q85 and base64'd. Nothing is saved. (The WPF app used System.Drawing; this is GDI + the WinRT
/// encoder, so nothing extra ships.)
/// </summary>
internal static class ScreenCaptureService
{
    public static async Task<Screenshot> CaptureAsync(Native.RECT physical, IEnumerable<IntPtr> exclude)
    {
        int w = physical.Width, h = physical.Height;
        if (w < 2 || h < 2) throw new ArgumentException("Region too small");

        // Keep our own windows (the notch) out of the picture.
        var excluded = exclude.Where(hwnd => hwnd != IntPtr.Zero && Native.ExcludeFromCapture(hwnd, true)).ToList();
        Native.WaitForCompositor();
        Native.WaitForCompositor();
        byte[] pixels;
        try { pixels = Grab(physical); }
        finally { foreach (var hwnd in excluded) Native.ExcludeFromCapture(hwnd, false); }

        using var full = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);

        // Downscaled JPEG for the API.
        double scale = Math.Min(1.0, (double)AppConfig.ScreenshotMaxEdge / Math.Max(w, h));
        var (apiW, apiH) = Scaled(w, h, scale);
        string base64;
        using (var jpeg = new InMemoryRandomAccessStream())
        {
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(AppConfig.ScreenshotJpegQuality / 100.0, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, jpeg, options);
            encoder.SetSoftwareBitmap(full);
            Fit(encoder, apiW, apiH);
            await encoder.FlushAsync();
            var bytes = new byte[jpeg.Size];
            jpeg.Seek(0);
            await jpeg.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            base64 = Convert.ToBase64String(bytes);
        }

        // Preview-sized copy for the thumbnails and the hover preview (memory only).
        double tScale = Math.Min(1.0, Math.Min((double)Design.Theme.ScreenshotPreviewPixelsW / w, (double)Design.Theme.ScreenshotPreviewPixelsH / h));
        var (tw, th) = Scaled(w, h, tScale);
        SoftwareBitmap thumb;
        using (var tmp = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, tmp);
            encoder.SetSoftwareBitmap(full);
            Fit(encoder, tw, th);
            await encoder.FlushAsync();
            tmp.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(tmp);
            thumb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        return new Screenshot(base64, thumb, apiW, apiH);
    }

    private static (int w, int h) Scaled(int w, int h, double k) =>
        (Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));

    private static void Fit(BitmapEncoder encoder, int w, int h)
    {
        encoder.BitmapTransform.ScaledWidth = (uint)w;
        encoder.BitmapTransform.ScaledHeight = (uint)h;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
    }

    // ---- GDI: copy the screen rectangle into a top-down 32-bit DIB ---------------------------------------
    private static byte[] Grab(Native.RECT r)
    {
        int w = r.Width, h = r.Height;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("Screen copy failed");
            GdiFlush();
            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // the screen has no alpha
            return pixels;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
