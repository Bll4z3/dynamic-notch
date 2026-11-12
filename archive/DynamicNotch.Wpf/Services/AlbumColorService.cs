using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicNotch.Design;

namespace DynamicNotch.Services;

/// <summary>
/// Picks the dominant vibrant colour of album art: downscale to 32×32, ignore near-black and
/// near-white pixels, bucket the rest by hue and weight each pixel by saturation and closeness to
/// mid lightness. The winning bucket's average is lifted to a readable lightness on black.
/// Falls back to a neutral soft white when there's no art or nothing vibrant in it.
/// </summary>
public static class AlbumColorService
{
    private const int Size = 32, HueBuckets = 24;
    private static BitmapSource? _lastArt;
    private static Color _lastColor;

    public static Color Extract(BitmapSource? art)
    {
        if (art == null) return Theme.GlowNeutral;
        if (ReferenceEquals(art, _lastArt)) return _lastColor;
        var color = Compute(art);
        _lastArt = art;
        _lastColor = color;
        return color;
    }

    private static Color Compute(BitmapSource art)
    {
        try
        {
            var small = new TransformedBitmap(art, new ScaleTransform((double)Size / art.PixelWidth, (double)Size / art.PixelHeight));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            var weight = new double[HueBuckets];
            var sumR = new double[HueBuckets]; var sumG = new double[HueBuckets]; var sumB = new double[HueBuckets];
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                var (hue, s, l) = ToHsl(r, g, b);
                if (l < 0.12 || l > 0.92 || s < 0.18) continue; // near-black, near-white, greys
                double wgt = s * s * (1 - Math.Abs(l - 0.5) * 1.5);
                if (wgt <= 0) continue;
                int bucket = Math.Min(HueBuckets - 1, (int)(hue * HueBuckets));
                weight[bucket] += wgt;
                sumR[bucket] += r * wgt; sumG[bucket] += g * wgt; sumB[bucket] += b * wgt;
            }

            int best = -1;
            double bestWeight = 0;
            for (int k = 0; k < HueBuckets; k++)
            {
                // Include neighbours so a hue split across two buckets still wins.
                double wk = weight[k] + 0.5 * (weight[(k + 1) % HueBuckets] + weight[(k + HueBuckets - 1) % HueBuckets]);
                if (wk > bestWeight) { bestWeight = wk; best = k; }
            }
            if (best < 0 || weight[best] < 0.5) return Theme.GlowNeutral;

            var (hh, ss, ll) = ToHsl(sumR[best] / weight[best], sumG[best] / weight[best], sumB[best] / weight[best]);
            ss = Math.Max(ss, Theme.AccentMinSaturation);
            ll = Math.Clamp(Math.Max(ll, Theme.AccentMinLightness), 0, 0.78);
            return FromHsl(hh, ss, ll);
        }
        catch
        {
            return Theme.GlowNeutral;
        }
    }

    private static (double h, double s, double l) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-6) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double C(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
        return Color.FromRgb((byte)(C(h + 1.0 / 3) * 255), (byte)(C(h) * 255), (byte)(C(h - 1.0 / 3) * 255));
    }
}
