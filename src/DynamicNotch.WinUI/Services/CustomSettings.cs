using DynamicNotch.Design;

namespace DynamicNotch.Services;

/// <summary>
/// Everything in Settings ▸ Customisation (persisted inside settings.json as "Custom"). Plain values only,
/// so it round-trips through JSON (export / import) and can be snapshotted as a "Look".
/// <see cref="Sanitize"/> clamps anything a hand-edited or imported file could get wrong.
/// </summary>
public sealed class CustomSettings
{
    // ---- Accent ----
    /// <summary>Preset id (see <see cref="AccentPresets"/>) or "custom".</summary>
    public string AccentPreset { get; set; } = "pink";
    public string CustomAccent { get; set; } = "#FF375F";
    public List<string> RecentColors { get; set; } = new();
    public bool MatchWindowsAccent { get; set; }
    public bool FollowAlbumColor { get; set; }

    // ---- Shape ----
    public double CornerRadius { get; set; } = Theme.DefaultCornerRadius;
    public double TileRadius { get; set; } = Theme.DefaultCornerRadiusSmall;
    public bool LinkTileRadius { get; set; } = true;

    // ---- Sounds ----
    public bool SoundsEnabled { get; set; } = true;
    /// <summary>off (default), soft, glass, pop, bubble, retro, mechanical.</summary>
    public string SoundPreset { get; set; } = "off";
    public double SoundVolume { get; set; } = 0.6;
    /// <summary>Per-event switches (missing = the event's default).</summary>
    public Dictionary<string, bool> SoundEvents { get; set; } = new();

    // ---- Motion ----
    /// <summary>smooth (default), snappy, bouncy, calm, reduced.</summary>
    public string MotionFeel { get; set; } = "smooth";
    public double MotionSpeed { get; set; } = 1.0;

    // ---- Notch look ----
    public double Highlight { get; set; } = Theme.DefaultHighlight;
    public double ShadowIntensity { get; set; } = 1.0;
    public bool GlowEnabled { get; set; } = true;
    public double GlowIntensity { get; set; } = 1.0;
    /// <summary>bars (default), wave, off.</summary>
    public string Visualizer { get; set; } = "bars";
    /// <summary>album (default) or accent.</summary>
    public string GlowSource { get; set; } = "album";
    public double NotchScale { get; set; } = 1.0;

    // ---- Collapsed notch ----
    public bool ShowBatteryRing { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowDate { get; set; }
    /// <summary>system (default), 12, 24.</summary>
    public string ClockFormat { get; set; } = "system";

    // ---- Behaviour ----
    public int HoverDelayMs { get; set; } = Motion.HoverExpandDelayMs;
    public int CollapseDelayMs { get; set; } = Motion.HoverCollapseDelayMs;
    public double TopOffset { get; set; }
    /// <summary>primary (default) or mouse (the monitor under the pointer at start-up / display change).</summary>
    public string Monitor { get; set; } = "primary";

    // ---- Performance ----
    /// <summary>auto (default: Low power on battery saver and in remote sessions), standard, low.</summary>
    public string PerformanceMode { get; set; } = "auto";

    // ---- Looks ----
    public List<SavedLook> SavedLooks { get; set; } = new();

    public CustomSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<CustomSettings>(json)!;
    }

    /// <summary>Clamps every value into its safe range and fills anything missing.</summary>
    public CustomSettings Sanitize()
    {
        static double C(double v, double lo, double hi, double def) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : def;
        static string Pick(string? v, string def, params string[] allowed) => v != null && allowed.Contains(v) ? v : def;
        AccentPreset = AccentPresets.All.Any(p => p.Id == AccentPreset) || AccentPreset == "custom" ? AccentPreset : "pink";
        CustomAccent = ColorUtil.TryParse(CustomAccent, out _) ? CustomAccent : "#FF375F";
        RecentColors = (RecentColors ?? new()).Where(c => ColorUtil.TryParse(c, out _)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        CornerRadius = C(CornerRadius, Theme.MinCornerRadius, Theme.MaxCornerRadius, Theme.DefaultCornerRadius);
        TileRadius = C(TileRadius, Theme.MinCornerRadiusSmall, Theme.MaxCornerRadiusSmall, Theme.DefaultCornerRadiusSmall);
        SoundPreset = Pick(SoundPreset, "off", SoundPresets.Ids);
        SoundVolume = C(SoundVolume, 0, 1, 0.6);
        SoundEvents ??= new();
        MotionFeel = Pick(MotionFeel, "smooth", "smooth", "snappy", "bouncy", "calm", "reduced");
        MotionSpeed = C(MotionSpeed, 0.5, 1.5, 1);
        Highlight = C(Highlight, 0, 0.2, Theme.DefaultHighlight);
        ShadowIntensity = C(ShadowIntensity, 0, 1.5, 1);
        GlowIntensity = C(GlowIntensity, 0, 1.5, 1);
        Visualizer = Pick(Visualizer, "bars", "bars", "wave", "off");
        GlowSource = Pick(GlowSource, "album", "album", "accent");
        NotchScale = C(NotchScale, 0.85, 1.25, 1);
        ClockFormat = Pick(ClockFormat, "system", "system", "12", "24");
        HoverDelayMs = (int)C(HoverDelayMs, 0, 600, Motion.HoverExpandDelayMs);
        CollapseDelayMs = (int)C(CollapseDelayMs, 150, 2000, Motion.HoverCollapseDelayMs);
        TopOffset = C(TopOffset, 0, 40, 0);
        Monitor = Pick(Monitor, "primary", "primary", "mouse");
        PerformanceMode = Pick(PerformanceMode, "auto", "auto", "standard", "low");
        SavedLooks = (SavedLooks ?? new()).Where(l => !string.IsNullOrWhiteSpace(l?.Name) && l.Values != null).Take(20).ToList();
        foreach (var l in SavedLooks) l.Values.CopyLookTo(l.Values); // normalises nested values
        return this;
    }

    /// <summary>Copies only the visual "look" values (what a Look bundle carries) onto <paramref name="target"/>.</summary>
    public void CopyLookTo(LookValues target)
    {
        target.AccentPreset = AccentPreset; target.CustomAccent = CustomAccent;
        target.CornerRadius = CornerRadius; target.TileRadius = TileRadius; target.LinkTileRadius = LinkTileRadius;
        target.MotionFeel = MotionFeel; target.MotionSpeed = MotionSpeed;
        target.Highlight = Highlight; target.ShadowIntensity = ShadowIntensity; target.GlowEnabled = GlowEnabled; target.GlowIntensity = GlowIntensity;
    }

    public void ApplyLook(LookValues l)
    {
        AccentPreset = l.AccentPreset; CustomAccent = l.CustomAccent;
        CornerRadius = l.CornerRadius; TileRadius = l.TileRadius; LinkTileRadius = l.LinkTileRadius;
        MotionFeel = l.MotionFeel; MotionSpeed = l.MotionSpeed;
        Highlight = l.Highlight; ShadowIntensity = l.ShadowIntensity; GlowEnabled = l.GlowEnabled; GlowIntensity = l.GlowIntensity;
    }
}

/// <summary>The values a Look bundle sets (accent, shape, motion, notch look).</summary>
public sealed class LookValues
{
    public string AccentPreset { get; set; } = "pink";
    public string CustomAccent { get; set; } = "#FF375F";
    public double CornerRadius { get; set; } = Theme.DefaultCornerRadius;
    public double TileRadius { get; set; } = Theme.DefaultCornerRadiusSmall;
    public bool LinkTileRadius { get; set; } = true;
    public string MotionFeel { get; set; } = "smooth";
    public double MotionSpeed { get; set; } = 1;
    public double Highlight { get; set; } = Theme.DefaultHighlight;
    public double ShadowIntensity { get; set; } = 1;
    public bool GlowEnabled { get; set; } = true;
    public double GlowIntensity { get; set; } = 1;

    internal void CopyLookTo(LookValues t)
    {
        t.CornerRadius = Math.Clamp(CornerRadius, Theme.MinCornerRadius, Theme.MaxCornerRadius);
        t.TileRadius = Math.Clamp(TileRadius, Theme.MinCornerRadiusSmall, Theme.MaxCornerRadiusSmall);
        t.MotionSpeed = Math.Clamp(MotionSpeed, 0.5, 1.5);
        t.Highlight = Math.Clamp(Highlight, 0, 0.2);
        t.ShadowIntensity = Math.Clamp(ShadowIntensity, 0, 1.5);
        t.GlowIntensity = Math.Clamp(GlowIntensity, 0, 1.5);
        if (!AccentPresets.All.Any(p => p.Id == AccentPreset) && AccentPreset != "custom") t.AccentPreset = "pink";
    }
}

public sealed class SavedLook
{
    public string Name { get; set; } = "";
    public LookValues Values { get; set; } = new();
}

/// <summary>The one-click Looks.</summary>
public static class BuiltInLooks
{
    public static readonly (string Id, string Name, string Description, LookValues Values)[] All =
    {
        ("classic", "Classic", "The original look", new LookValues()),
        ("soft", "Soft", "Rounder, calmer motion", new LookValues { AccentPreset = "purple", CornerRadius = 32, TileRadius = 16, MotionFeel = "calm", MotionSpeed = 0.9, Highlight = 0.06, ShadowIntensity = 1.2 }),
        ("sharp", "Sharp", "Smaller radius, snappy", new LookValues { AccentPreset = "blue", CornerRadius = 12, TileRadius = 6, MotionFeel = "snappy", MotionSpeed = 1.15, Highlight = 0.12, ShadowIntensity = 0.8 }),
        ("neon", "Neon", "Bright accent, stronger glow", new LookValues { AccentPreset = "teal", CornerRadius = 24, TileRadius = 12, MotionFeel = "bouncy", Highlight = 0.18, ShadowIntensity = 1.3, GlowIntensity = 1.5 }),
    };
}

public sealed record AccentPreset(string Id, string Name, Windows.UI.Color Color);

public static class AccentPresets
{
    private static Windows.UI.Color C(uint rgb) => Windows.UI.Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    public static readonly AccentPreset[] All =
    {
        new("pink", "Pink", C(0xFF375F)),
        new("red", "Red", C(0xFF453A)),
        new("orange", "Orange", C(0xFF9F0A)),
        new("amber", "Amber", C(0xFFD60A)),
        new("green", "Green", C(0x30D158)),
        new("teal", "Teal", C(0x40C8E0)),
        new("blue", "Blue", C(0x0A84FF)),
        new("indigo", "Indigo", C(0x5E5CE6)),
        new("purple", "Purple", C(0xBF5AF2)),
        new("mono", "Mono White", C(0xF5F5F7)),
    };
}

public static class SoundPresets
{
    public static readonly string[] Ids = { "off", "soft", "glass", "pop", "bubble", "retro", "mechanical" };
    public static readonly string[] Names = { "Off", "Soft", "Glass", "Pop", "Bubble", "Retro", "Mechanical" };
}

public static class ColorUtil
{
    public static bool TryParse(string? hex, out Windows.UI.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = Windows.UI.Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static string ToHex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static (double h, double s, double v) ToHsv(Windows.UI.Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Windows.UI.Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Windows.UI.Color.FromArgb(0xFF, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
