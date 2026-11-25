using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using DynamicNotch.Design;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace DynamicNotch.Services;

/// <summary>
/// Picks the dominant vibrant colour of album art (same rules as the WPF app): downscale to 32×32, ignore near-black and
/// near-white pixels, bucket the rest by hue and weight each pixel by saturation and closeness to
/// mid lightness. The winning bucket's average is lifted to a readable lightness on black.
/// Falls back to a neutral soft white when there's no art or nothing vibrant in it.
/// </summary>
public static class AlbumColorService
{
    private const int Size = 32, HueBuckets = 24;
    private static SoftwareBitmap? _lastArt;
    private static Color _lastColor;

    public static Color Extract(SoftwareBitmap? art)
    {
        if (art == null) return Theme.GlowNeutral;
        if (ReferenceEquals(art, _lastArt)) return _lastColor;
        var color = Compute(art);
        _lastArt = art;
        _lastColor = color;
        return color;
    }

    private static Color Compute(SoftwareBitmap art)
    {
        try
        {
            var px = Sample(art, Size);

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

    // ---- Glow palette (music-reactive glow) ---------------------------------------------------------------------
    private static SoftwareBitmap? _paletteArt;
    private static GlowPalette? _palette;

    /// <summary>
    /// The music glow's colours from the art: a main colour and three accents that contrast with it and each other.
    /// Pixels are clustered with k-means in OKLab (a perceptual space, so clusters are colours people see as one), each
    /// weighted by vibrancy (chroma); a cluster's score is its total weight, i.e. vibrancy × how much of the cover it
    /// covers. Near-black, near-white and grey are ignored unless the cover is mostly those, in which case the glow is
    /// a tasteful silver / white with the cover's faint tint. Every colour is then set to a glowing lightness and a
    /// healthy chroma in OKLCh, inside the sRGB gamut, so nothing is muddy or brown. Clusters are never averaged into
    /// each other. Cached per artwork.
    /// </summary>
    public static GlowPalette? ExtractGlowPalette(SoftwareBitmap? art)
    {
        if (art == null) return null;
        if (ReferenceEquals(art, _paletteArt)) return _palette;
        GlowPalette? result = null;
        try { result = ComputeGlowPalette(art); } catch { }
        _paletteArt = art;
        _palette = result;
        return result;
    }

    private static GlowPalette ComputeGlowPalette(SoftwareBitmap art)
    {
        const int size = 40;
        var px = Sample(art, size);
        var colourful = new List<(Vector3 lab, float w)>();
        int total = 0;
        Vector3 tint = Vector3.Zero;
        for (int i = 0; i < px.Length; i += 4)
        {
            var lab = ToOkLab(px[i + 2] / 255f, px[i + 1] / 255f, px[i] / 255f);
            float chroma = MathF.Sqrt(lab.Y * lab.Y + lab.Z * lab.Z);
            total++;
            tint += lab;
            bool nearBlack = lab.X < 0.2f, nearWhite = lab.X > 0.93f && chroma < 0.05f, grey = chroma < 0.045f;
            if (nearBlack || nearWhite || grey) continue;
            colourful.Add((lab, MathF.Pow(chroma, 1.4f) * (0.4f + lab.X))); // vibrancy (and a little lightness)
        }

        // Mostly black / white / grey: a silver-white glow, faintly tinted by the cover's own cast.
        if (colourful.Count < total * 0.08f)
        {
            var avg = tint / Math.Max(1, total);
            float hue = MathF.Atan2(avg.Z, avg.Y);
            Color Silver(float l, float c, float dh) => FromOkLch(l, c, hue + dh);
            return new GlowPalette(Silver(0.86f, 0.02f, 0), new[] { Silver(0.94f, 0.012f, 0.4f), Silver(0.78f, 0.03f, -0.4f), Silver(0.9f, 0.018f, 1.2f) }, Monochrome: true);
        }

        // k-means (k = 5) in OKLab, weighted; deterministic k-means++ style seeding (heaviest, then farthest).
        const int k = 5;
        var centres = new List<Vector3> { colourful.OrderByDescending(c => c.w).First().lab };
        while (centres.Count < k)
        {
            var next = colourful.OrderByDescending(c => c.w * centres.Min(m => Vector3.DistanceSquared(m, c.lab))).First();
            if (centres.Min(m => Vector3.DistanceSquared(m, next.lab)) < 1e-5f) break;
            centres.Add(next.lab);
        }
        var weight = new float[centres.Count];
        for (int iter = 0; iter < 10; iter++)
        {
            var sum = new Vector3[centres.Count];
            Array.Clear(weight);
            foreach (var (lab, w) in colourful)
            {
                int best = 0;
                for (int c = 1; c < centres.Count; c++) if (Vector3.DistanceSquared(centres[c], lab) < Vector3.DistanceSquared(centres[best], lab)) best = c;
                sum[best] += lab * w;
                weight[best] += w;
            }
            for (int c = 0; c < centres.Count; c++) if (weight[c] > 0) centres[c] = sum[c] / weight[c];
        }

        // Main = the strongest cluster; accents = strong clusters that also differ from everything already picked.
        var order = Enumerable.Range(0, centres.Count).Where(c => weight[c] > 0).OrderByDescending(c => weight[c]).ToList();
        var picked = new List<Vector3> { centres[order[0]] };
        float top = weight[order[0]];
        while (picked.Count < 4)
        {
            int best = -1;
            float bestScore = 0;
            foreach (int c in order)
            {
                float dist = picked.Min(p => Hue(p, centres[c]));
                if (dist < 0.08f || weight[c] < top * 0.04f) continue; // too close to a picked colour, or a speck
                float score = weight[c] * MathF.Min(1, dist / 0.25f);
                if (score > bestScore) { bestScore = score; best = c; }
            }
            if (best < 0) break;
            picked.Add(centres[best]);
            order.Remove(best);
        }
        // Too few distinct colours: accents from the main colour's hue, turned ±55° and opposite.
        var (l0, c0, h0) = ToLch(picked[0]);
        float[] turns = { 0.95f, -0.95f, 2.6f };
        for (int i = 0; picked.Count < 4; i++) picked.Add(FromLch(l0, c0, h0 + turns[i % turns.Length]));

        var glow = picked.Select(Glowing).ToArray();
        return new GlowPalette(glow[0], glow.Skip(1).ToArray(), Monochrome: false);

        // Perceptual distance between two OKLab colours, ignoring a little of the lightness difference.
        static float Hue(Vector3 a, Vector3 b) => MathF.Sqrt(0.3f * (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    }

    /// <summary>A colour set to glow on black: lightness 0.72–0.84 (keeping its relative lightness), chroma lifted to
    /// at least 0.11, hue kept, then pulled into the sRGB gamut by lowering chroma (never by shifting the hue).</summary>
    private static Color Glowing(Vector3 lab)
    {
        var (l, c, h) = ToLch(lab);
        l = 0.72f + 0.12f * Math.Clamp((l - 0.3f) / 0.6f, 0, 1);
        c = Math.Max(c * 1.15f, 0.11f);
        return FromOkLch(l, c, h);
    }

    /// <summary>A palette around one colour (accent glow, no art): it, its neighbours and its opposite.</summary>
    public static GlowPalette PaletteAround(Color c)
    {
        var lab = ToOkLab(c.R / 255f, c.G / 255f, c.B / 255f);
        var (l, ch, h) = ToLch(lab);
        if (ch < 0.03f) return new GlowPalette(FromOkLch(0.88f, 0.015f, h), new[] { FromOkLch(0.94f, 0.01f, h), FromOkLch(0.8f, 0.02f, h), FromOkLch(0.9f, 0.015f, h) }, true);
        return new GlowPalette(Glowing(lab), new[] { Glowing(FromLchLab(l, ch, h + 0.7f)), Glowing(FromLchLab(l, ch, h - 0.7f)), Glowing(FromLchLab(l, ch, h + 2.6f)) }, false);
    }

    // ---- OKLab (Björn Ottosson) ----
    private static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    private static float Gam(float c) => c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

    private static Vector3 ToOkLab(float r, float g, float b)
    {
        r = Lin(r); g = Lin(g); b = Lin(b);
        float l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        float m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        float s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        return new Vector3(0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                           1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                           0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    private static (float r, float g, float b) FromOkLab(Vector3 lab)
    {
        float l = lab.X + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
        float m = lab.X - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
        float s = lab.X - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
        l = l * l * l; m = m * m * m; s = s * s * s;
        return (4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
               -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
               -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    private static (float l, float c, float h) ToLch(Vector3 lab) => (lab.X, MathF.Sqrt(lab.Y * lab.Y + lab.Z * lab.Z), MathF.Atan2(lab.Z, lab.Y));
    private static Vector3 FromLchLab(float l, float c, float h) => new(l, c * MathF.Cos(h), c * MathF.Sin(h));
    private static Vector3 FromLch(float l, float c, float h) => FromLchLab(l, c, h);

    /// <summary>OKLCh → sRGB, lowering chroma until it fits the gamut.</summary>
    private static Color FromOkLch(float l, float c, float h)
    {
        for (int i = 0; i < 24; i++, c *= 0.9f)
        {
            var (r, g, b) = FromOkLab(FromLchLab(l, c, h));
            if (r >= -0.001f && r <= 1.001f && g >= -0.001f && g <= 1.001f && b >= -0.001f && b <= 1.001f || i == 23)
                return Color.FromArgb(255, B(Gam(Math.Clamp(r, 0, 1))), B(Gam(Math.Clamp(g, 0, 1))), B(Gam(Math.Clamp(b, 0, 1))));
        }
        return Color.FromArgb(255, 255, 255, 255);
        static byte B(float v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    /// <summary>Nearest-neighbour downscale of a BGRA8 bitmap to size×size (premultiplied alpha is fine: art is opaque).</summary>
    private static byte[] Sample(SoftwareBitmap art, int size)
    {
        var bmp = art.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? art : SoftwareBitmap.Convert(art, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var all = new byte[w * h * 4];
        bmp.CopyToBuffer(all.AsBuffer());
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int sx = Math.Min(w - 1, (int)((x + 0.5) * w / size)), sy = Math.Min(h - 1, (int)((y + 0.5) * h / size));
            Buffer.BlockCopy(all, (sy * w + sx) * 4, px, (y * size + x) * 4, 4);
        }
        return px;
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
        return Color.FromArgb(255, (byte)(C(h + 1.0 / 3) * 255), (byte)(C(h) * 255), (byte)(C(h - 1.0 / 3) * 255));
    }
}

/// <summary>The music glow's colours: the main colour and three accents (all set to glow on black). Monochrome = the
/// cover is mostly black / white / grey, so these are silver-white.</summary>
public sealed record GlowPalette(Color Main, Color[] Accents, bool Monochrome);
