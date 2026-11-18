using Windows.UI;

namespace DynamicNotch.Design;

/// <summary>
/// The small palette every accent-coloured element draws from, generated from one colour (the chosen
/// accent, or the album colour when that mode is active). Pure maths on plain colours, so it's usable from
/// any thread; <see cref="Theme.ApplyAccent"/> pushes it into the shared brushes.
/// - <see cref="Base"/>: the colour itself (fills: switches, slider tracks, active tiles).
/// - <see cref="Light"/> / <see cref="Dark"/>: tints either side (gradients, hover / pressed).
/// - <see cref="Glow"/>: a soft, semi-transparent lighter tone for halos and edge highlights.
/// - <see cref="Tint"/>: a low-opacity wash for selected rows and backgrounds.
/// - <see cref="Border"/>: a hairline outline for selected / focused surfaces.
/// - <see cref="Readable"/>: the accent lifted until it reads as text or an icon on the black notch (WCAG 4.5:1).
/// - <see cref="OnAccent"/>: black or white, whichever reads better across the whole Light → Base gradient.
/// </summary>
public readonly record struct AccentPalette(
    Color Base, Color Light, Color Dark, Color Glow, Color Tint, Color Border, Color Readable, Color OnAccent)
{
    private static readonly Color Black = Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0C);
    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color Surface = Color.FromArgb(0xFF, 0, 0, 0);

    public static AccentPalette From(Color c)
    {
        c = Color.FromArgb(0xFF, c.R, c.G, c.B);
        var (h, s, l) = ToHsl(c);
        // Very pale or very dark accents still get a visible spread either side.
        var light = FromHsl(h, Math.Min(1, s * 1.04), Math.Min(0.86, l + 0.13));
        var dark = FromHsl(h, Math.Min(1, s * 1.08), Math.Max(0.12, l - 0.17));
        var glowBase = FromHsl(h, Math.Min(1, s * 1.1), Math.Clamp(l + 0.06, 0.45, 0.78));

        // Readable: raise lightness until it clears 4.5:1 against the notch.
        var readable = c;
        for (double ll = l; Theme.Contrast(readable, Surface) < 4.5 && ll < 0.95; ll += 0.03)
            readable = FromHsl(h, s, ll);

        // On-accent text: black or white, whichever reads better on the base. With white text the light end of
        // the gradient is pulled back until white reads on it as well as on the base (capped at AA, 4.5:1), so
        // text stays readable across the whole gradient.
        var on = Theme.Contrast(Black, c) >= Theme.Contrast(White, c) ? Black : White;
        if (on == White)
        {
            double need = Math.Min(4.5, Theme.Contrast(White, c));
            for (double ll = Math.Min(0.86, l + 0.13); Theme.Contrast(White, light) < need && ll > l; ll -= 0.01)
                light = FromHsl(h, Math.Min(1, s * 1.04), Math.Max(l, ll));
            if (Theme.Contrast(White, light) < need) light = c;
        }

        return new AccentPalette(
            Base: c,
            Light: light,
            Dark: dark,
            Glow: WithAlpha(glowBase, 0x8C),
            Tint: WithAlpha(c, 0x24),
            Border: WithAlpha(light, 0x66),
            Readable: readable,
            OnAccent: on);
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    // ---- HSL --------------------------------------------------------------------------------------
    public static (double h, double s, double l) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, Math.Clamp(s, 0, 1), l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double ch = (1 - Math.Abs(2 * l - 1)) * s, x = ch * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - ch / 2;
        var (r, g, b) = h < 60 ? (ch, x, 0.0) : h < 120 ? (x, ch, 0.0) : h < 180 ? (0.0, ch, x)
                      : h < 240 ? (0.0, x, ch) : h < 300 ? (x, 0.0, ch) : (ch, 0.0, x);
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return Color.FromArgb(0xFF, B(r + m), B(g + m), B(b + m));
    }
}
