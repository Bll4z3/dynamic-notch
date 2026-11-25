using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// The music glow's textures, baked once with Win2D so nothing is blurred or generated per frame:
/// <list type="bullet">
/// <item>White alpha masks (soft disc, feathered ring, sharp and soft particle) whose alpha follows a smooth curve to
/// exactly zero at the edge, with ±1 level of triangular noise in the alpha so large gradients never band. Each glow
/// shape is a colour brush seen through one of these (a CompositionMaskBrush), so its colour stays a cheap,
/// animatable brush colour.</item>
/// <item>The blurred album art (per track): the cover drawn at 320 px, Gaussian-blurred heavily, given a soft round
/// fade so its edges never show, and dithered the same way.</item>
/// </list>
/// Everything is redrawn if the graphics device is lost.
/// </summary>
internal sealed class GlowTextures
{
    // ---- Tuning ----
    private const int ArtSize = 320;        // the blurred art's resolution (it's scaled up a lot; blur hides that)
    private const float ArtBlur = 22f;      // Gaussian blur radius at that size (≈ 7% of the cover)
    private const float ArtFadeFrom = 0.5f; // where (0 = centre, 1 = edge) the art starts fading to transparent
    private const float ArtLiftTo = 0.32f;  // coloured areas are lifted toward this-or-brighter (colour boost on dark covers)
    private const float ArtSaturation = 1.2f, VividSaturation = 1.75f; // the normal and the vivid (high-energy) cover

    private readonly Compositor _c;
    private readonly CompositionGraphicsDevice _device;
    private readonly List<Action> _redraw = new();

    public GlowTextures(Compositor c)
    {
        _c = c;
        _device = CanvasComposition.CreateCompositionGraphicsDevice(c, CanvasDevice.GetSharedDevice());
        _device.RenderingDeviceReplaced += (_, _) => { foreach (var r in _redraw) try { r(); } catch { } };
        Disc = Mask(256, r => MathF.Pow(1 - r * r, 2.6f));
        Ring = Mask(256, r => r >= 1 ? 0 : MathF.Exp(-MathF.Pow((r - 0.74f) / 0.1f, 2)) * Math.Clamp((1 - r) / 0.08f, 0, 1));
        Sharp = Mask(64, r => r >= 1 ? 0 : r < 0.2f ? 1 : MathF.Pow(1 - (r - 0.2f) / 0.8f, 2.4f));
        Soft = Mask(64, r => MathF.Pow(1 - r * r, 2));
        // The scrim: a flat core then a long smooth fall.
        Scrim = Mask(256, r => { float t = Math.Clamp((r - 0.5f) / 0.5f, 0, 1); return 1 - t * t * (3 - 2 * t); });
    }

    public CompositionSurfaceBrush Disc { get; }
    public CompositionSurfaceBrush Ring { get; }
    public CompositionSurfaceBrush Sharp { get; }
    public CompositionSurfaceBrush Soft { get; }
    public CompositionSurfaceBrush Scrim { get; }

    /// <summary>A colour seen through a mask (the colour brush can be shared and animated).</summary>
    public CompositionMaskBrush Tinted(CompositionColorBrush colour, CompositionSurfaceBrush mask)
    {
        var b = _c.CreateMaskBrush();
        b.Source = colour;
        b.Mask = mask;
        return b;
    }

    private CompositionSurfaceBrush Mask(int size, Func<float, float> profile)
    {
        var surface = _device.CreateDrawingSurface(new Size(size, size),
            Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
        void Draw()
        {
            var rng = new Random(size * 31 + 7);
            var bytes = new byte[size * size * 4];
            float half = size / 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                float a = r >= 1 ? 0 : Math.Clamp(profile(r), 0, 1);
                byte v = a <= 0 ? (byte)0 : Dither(a * 255, rng);
                int o = (y * size + x) * 4;
                bytes[o] = bytes[o + 1] = bytes[o + 2] = bytes[o + 3] = v; // premultiplied white
            }
            Upload(surface, bytes, size, size);
        }
        Draw();
        _redraw.Add(Draw);
        var brush = _c.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        return brush;
    }

    /// <summary>
    /// The cover, blurred heavily with a soft round fade, as a brush (null if it can't be drawn). Called once per track.
    /// </summary>
    public (CompositionSurfaceBrush Normal, CompositionSurfaceBrush Vivid)? BlurredArt(SoftwareBitmap art)
    {
        try
        {
            CompositionDrawingSurface NewSurface() => _device.CreateDrawingSurface(new Size(ArtSize, ArtSize),
                Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            var normal = NewSurface();
            var vivid = NewSurface();
            var source = art.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && art.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                ? art : SoftwareBitmap.Convert(art, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            void Draw()
            {
                var device = CanvasComposition.GetCanvasDevice(_device);
                using var bmp = CanvasBitmap.CreateFromSoftwareBitmap(device, source);
                using var target = new CanvasRenderTarget(device, ArtSize, ArtSize, 96);
                using (var scaled = new CanvasCommandList(device))
                {
                    using (var ds = scaled.CreateDrawingSession()) ds.DrawImage(bmp, new Rect(0, 0, ArtSize, ArtSize), bmp.Bounds);
                    using var blur = new GaussianBlurEffect { Source = scaled, BlurAmount = ArtBlur, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Quality };
                    using var ds2 = target.CreateDrawingSession();
                    ds2.Clear(Color.FromArgb(255, 0, 0, 0));
                    ds2.DrawImage(blur);
                }
                var blurred = target.GetPixelBytes();
                Upload(normal, Grade(blurred, ArtSaturation, ArtLiftTo, seed: 11), ArtSize, ArtSize);
                Upload(vivid, Grade(blurred, VividSaturation, ArtLiftTo + 0.1f, seed: 13), ArtSize, ArtSize);
            }
            Draw();
            _redraw.Add(Draw);
            if (_redraw.Count > 12) _redraw.RemoveAt(5); // keep the masks (first five) and the latest arts
            CompositionSurfaceBrush Brush(CompositionDrawingSurface s) { var b = _c.CreateSurfaceBrush(s); b.Stretch = CompositionStretch.Fill; return b; }
            return (Brush(normal), Brush(vivid));
        }
        catch (Exception ex) { App.LogError(new InvalidOperationException("Blurred album art unavailable", ex)); return null; }
    }

    /// <summary>
    /// Colour-grades the blurred cover (no effects at run time): saturation × <paramref name="saturation"/> around each
    /// pixel's luminance, the coloured parts lifted toward a glowing brightness (by how saturated they are, so blacks
    /// and greys stay dark: a dark cover's colours glow instead of turning brown), the soft round fade, and dithering.
    /// Output is valid premultiplied alpha (colour never above alpha).
    /// </summary>
    private static byte[] Grade(byte[] src, float saturation, float liftTo, int seed)
    {
        var bytes = new byte[src.Length];
        var rng = new Random(seed);
        float half = ArtSize / 2f;
        for (int y = 0; y < ArtSize; y++)
        for (int x = 0; x < ArtSize; x++)
        {
            float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
            float t = Math.Clamp((MathF.Sqrt(dx * dx + dy * dy) - ArtFadeFrom) / (1 - ArtFadeFrom), 0, 1);
            float fade = 1 - t * t * (3 - 2 * t);
            int o = (y * ArtSize + x) * 4;
            if (fade <= 0) continue; // fully transparent
            float b = src[o] / 255f, g = src[o + 1] / 255f, r = src[o + 2] / 255f;
            float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            r = Math.Clamp(luma + (r - luma) * saturation, 0, 1);
            g = Math.Clamp(luma + (g - luma) * saturation, 0, 1);
            b = Math.Clamp(luma + (b - luma) * saturation, 0, 1);
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
            float sat = max <= 0 ? 0 : (max - min) / max;
            float lifted = max + (liftTo + (1 - liftTo) * MathF.Sqrt(max) - max) * Math.Clamp(sat * 1.4f, 0, 1);
            float gain = max > 0.004f ? lifted / max : 1;
            byte a = Dither(255 * fade, rng);
            bytes[o] = Math.Min(a, Dither(Math.Min(1, b * gain) * 255 * fade, rng));
            bytes[o + 1] = Math.Min(a, Dither(Math.Min(1, g * gain) * 255 * fade, rng));
            bytes[o + 2] = Math.Min(a, Dither(Math.Min(1, r * gain) * 255 * fade, rng));
            bytes[o + 3] = a;
        }
        return bytes;
    }

    /// <summary>Rounds with ±1 level of triangular noise: smooth gradients without visible steps.</summary>
    private static byte Dither(float v, Random rng)
    {
        float n = (float)(rng.NextDouble() - rng.NextDouble()); // triangular, -1..1
        return (byte)Math.Clamp(MathF.Round(v + n), 0, 255);
    }

    private void Upload(CompositionDrawingSurface surface, byte[] bytes, int w, int h)
    {
        var device = CanvasComposition.GetCanvasDevice(_device);
        using var bmp = CanvasBitmap.CreateFromBytes(device, bytes, w, h, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var ds = CanvasComposition.CreateDrawingSession(surface);
        ds.Clear(Color.FromArgb(0, 0, 0, 0));
        ds.DrawImage(bmp);
    }
}
