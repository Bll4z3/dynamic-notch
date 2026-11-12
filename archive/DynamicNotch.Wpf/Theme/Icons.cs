using System.Windows;
using System.Windows.Media;

namespace DynamicNotch.Design;

/// <summary>
/// The app's one icon set: Lucide (https://lucide.dev, ISC licence) path data on a 24×24 grid.
/// Icons are drawn as vector strokes with round caps/joins at <see cref="Theme.IconStroke"/>, so
/// every icon has the same stroke weight at any size. Data starting with "F1" is a filled variant
/// (used for active states such as play/pause); everything else is outline.
/// </summary>
public static class Icons
{
    // Transport (filled = active controls)
    public const string Play = "F1 M7 4.6 v14.8 a1 1 0 0 0 1.5 .87 l12.4 -7.4 a1 1 0 0 0 0 -1.74 L8.5 3.73 A1 1 0 0 0 7 4.6 z";
    public const string Pause = "F1 M6.5 4 h2.5 a1 1 0 0 1 1 1 v14 a1 1 0 0 1 -1 1 h-2.5 a1 1 0 0 1 -1 -1 v-14 a1 1 0 0 1 1 -1 z M15 4 h2.5 a1 1 0 0 1 1 1 v14 a1 1 0 0 1 -1 1 h-2.5 a1 1 0 0 1 -1 -1 v-14 a1 1 0 0 1 1 -1 z";
    public const string Prev = "M19 20 L9 12 L19 4 Z M5 19 V5";
    public const string Next = "M5 4 L15 12 L5 20 Z M19 5 V19";
    public const string Music = "M9 18 V5 l12 -2 v13 M9 18 a3 3 0 1 1 -6 0 a3 3 0 1 1 6 0 z M21 16 a3 3 0 1 1 -6 0 a3 3 0 1 1 6 0 z";

    // Status / pills
    public const string Headphone = "M3 14 h3 a2 2 0 0 1 2 2 v3 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 v-7 a9 9 0 0 1 18 0 v7 a2 2 0 0 1 -2 2 h-1 a2 2 0 0 1 -2 -2 v-3 a2 2 0 0 1 2 -2 h3";
    public const string Bluetooth = "M7 7 L17 17 L12 22 V2 L17 7 L7 17";
    public const string Wifi = "M12 20 h.01 M2 8.82 a15 15 0 0 1 20 0 M5 12.86 a10 10 0 0 1 14 0 M8.5 16.43 a5 5 0 0 1 7 0";
    public const string Vpn = "M20 13 c0 5 -3.5 7.5 -7.66 8.95 a1 1 0 0 1 -.67 -.01 C7.5 20.5 4 18 4 13 V6 a1 1 0 0 1 1 -1 c2 0 4.5 -1.2 6.24 -2.72 a1.17 1.17 0 0 1 1.52 0 C14.51 3.81 17 5 19 5 a1 1 0 0 1 1 1 z M9 12 l2 2 l4 -4";
    public const string Bolt = "F1 M13 2 L4 14 h7 l-1 8 l9 -12 h-7 z";
    public const string Battery = "M4 6 h12 a2 2 0 0 1 2 2 v8 a2 2 0 0 1 -2 2 H4 a2 2 0 0 1 -2 -2 V8 a2 2 0 0 1 2 -2 z M22 11 v2 M6 10 v4 M10 10 v4";
    public const string BatteryLow = "M4 6 h12 a2 2 0 0 1 2 2 v8 a2 2 0 0 1 -2 2 H4 a2 2 0 0 1 -2 -2 V8 a2 2 0 0 1 2 -2 z M22 11 v2 M6 10 v4";
    public const string Bell = "M10.27 21 a2 2 0 0 0 3.46 0 M3.26 15.33 A1 1 0 0 0 4 17 h16 a1 1 0 0 0 .74 -1.67 C19.41 13.96 18 12.5 18 8 A6 6 0 0 0 6 8 c0 4.5 -1.41 5.96 -2.74 7.33";

    // Clipboard / shelf
    public const string Pin = "M12 17 v5 M9 10.76 a2 2 0 0 1 -1.11 1.79 l-1.78 .9 A2 2 0 0 0 5 15.24 V16 a1 1 0 0 0 1 1 h12 a1 1 0 0 0 1 -1 v-.76 a2 2 0 0 0 -1.11 -1.79 l-1.78 -.9 A2 2 0 0 1 15 10.76 V7 a1 1 0 0 1 1 -1 a2 2 0 0 0 0 -4 H8 a2 2 0 0 0 0 4 a1 1 0 0 1 1 1 z";
    /// <summary>Filled pin = pinned (active state); clicking it unpins.</summary>
    public const string Unpin = "F1 " + Pin;
    public const string Delete = "M3 6 h18 M19 6 v14 c0 1 -1 2 -2 2 H7 c-1 0 -2 -1 -2 -2 V6 M8 6 V4 c0 -1 1 -2 2 -2 h4 c1 0 2 1 2 2 v2";
    public const string Close = "M18 6 L6 18 M6 6 L18 18";
    public const string Link = "M10 13 a5 5 0 0 0 7.54 .54 l3 -3 a5 5 0 0 0 -7.07 -7.07 l-1.72 1.71 M14 11 a5 5 0 0 0 -7.54 -.54 l-3 3 a5 5 0 0 0 7.07 7.07 l1.71 -1.71";
    public const string Text = "M17 6.1 H3 M21 12.1 H3 M15.1 18 H3";
    public const string Image = "M5 3 h14 a2 2 0 0 1 2 2 v14 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 V5 a2 2 0 0 1 2 -2 z M11 9 a2 2 0 1 1 -4 0 a2 2 0 1 1 4 0 z M21 15 l-3.09 -3.09 a2 2 0 0 0 -2.83 0 L6 21";
    public const string File = "M15 2 H6 a2 2 0 0 0 -2 2 v16 a2 2 0 0 0 2 2 h12 a2 2 0 0 0 2 -2 V7 z M14 2 v4 a2 2 0 0 0 2 2 h4";
    public const string Clipboard = "M9 2 h6 a1 1 0 0 1 1 1 v2 a1 1 0 0 1 -1 1 H9 a1 1 0 0 1 -1 -1 V3 a1 1 0 0 1 1 -1 z M16 4 h2 a2 2 0 0 1 2 2 v14 a2 2 0 0 1 -2 2 H6 a2 2 0 0 1 -2 -2 V6 a2 2 0 0 1 2 -2 h2";
    public const string Shelf = "M22 12 h-6 l-2 3 h-4 l-2 -3 H2 M5.45 5.11 L2 12 v6 a2 2 0 0 0 2 2 h16 a2 2 0 0 0 2 -2 v-6 l-3.45 -6.89 A2 2 0 0 0 16.76 4 H7.24 a2 2 0 0 0 -1.79 1.11 z";
    public const string Upload = "M12 3 v12 M7 8 l5 -5 l5 5 M21 15 v4 a2 2 0 0 1 -2 2 H5 a2 2 0 0 1 -2 -2 v-4";

    // Device types
    public const string Speaker = "M6 2 h12 a2 2 0 0 1 2 2 v16 a2 2 0 0 1 -2 2 H6 a2 2 0 0 1 -2 -2 V4 a2 2 0 0 1 2 -2 z M12 6 h.01 M16 14 a4 4 0 1 1 -8 0 a4 4 0 1 1 8 0 z M12 14 h.01";
    public const string Mouse = "M12 2 a7 7 0 0 1 7 7 v6 a7 7 0 0 1 -14 0 V9 a7 7 0 0 1 7 -7 z M12 6 v4";
    public const string Keyboard = "M4 4 h16 a2 2 0 0 1 2 2 v12 a2 2 0 0 1 -2 2 H4 a2 2 0 0 1 -2 -2 V6 a2 2 0 0 1 2 -2 z M6 8 h.01 M10 8 h.01 M14 8 h.01 M18 8 h.01 M8 12 h.01 M12 12 h.01 M16 12 h.01 M7 16 h10";
    public const string Controller = "M6 11 h4 M8 9 v4 M15 12 h.01 M18 10 h.01 M17.32 5 H6.68 a4 4 0 0 0 -3.98 3.59 C2.6 9.42 2 14.46 2 16 a3 3 0 0 0 3 3 c1 0 1.5 -.5 2 -1 l1.41 -1.41 A2 2 0 0 1 9.83 16 h4.34 a2 2 0 0 1 1.41 .59 L17 18 c.5 .5 1 1 2 1 a3 3 0 0 0 3 -3 c0 -1.55 -.6 -6.58 -.69 -7.26 A4 4 0 0 0 17.32 5 z";
    public const string Phone = "M7 2 h10 a2 2 0 0 1 2 2 v16 a2 2 0 0 1 -2 2 H7 a2 2 0 0 1 -2 -2 V4 a2 2 0 0 1 2 -2 z M12 18 h.01";
    public const string Watch = "M12 10 v2.2 l1.6 1 M16.13 7.66 l-.81 -4.05 a2 2 0 0 0 -2 -1.61 h-2.68 a2 2 0 0 0 -2 1.61 l-.78 4.05 M7.88 16.36 l.8 4 a2 2 0 0 0 2 1.61 h2.72 a2 2 0 0 0 2 -1.61 l.81 -4.05 M18 12 a6 6 0 1 1 -12 0 a6 6 0 1 1 12 0 z";

    // Ask / settings
    public const string Sparkle = "M9.94 15.5 A2 2 0 0 0 8.5 14.06 l-6.14 -1.58 a.5 .5 0 0 1 0 -.96 L8.5 9.94 A2 2 0 0 0 9.94 8.5 l1.58 -6.14 a.5 .5 0 0 1 .96 0 L14.06 8.5 A2 2 0 0 0 15.5 9.94 l6.14 1.58 a.5 .5 0 0 1 0 .96 L15.5 14.06 a2 2 0 0 0 -1.44 1.44 l-1.58 6.14 a.5 .5 0 0 1 -.96 0 z M20 3 v4 M22 5 h-4 M4 17 v2 M5 18 H3";
    public const string SparkleFilled = "F1 " + Sparkle;
    public const string Crop = "M6 2 v14 a2 2 0 0 0 2 2 h14 M18 22 V8 a2 2 0 0 0 -2 -2 H2";
    public const string ArrowUp = "M5 12 L12 5 L19 12 M12 19 V5";
    public const string Stop = "F1 M7 6 h10 a1 1 0 0 1 1 1 v10 a1 1 0 0 1 -1 1 H7 a1 1 0 0 1 -1 -1 V7 a1 1 0 0 1 1 -1 z";
    public const string Copy = "M10 8 h10 a2 2 0 0 1 2 2 v10 a2 2 0 0 1 -2 2 H10 a2 2 0 0 1 -2 -2 V10 a2 2 0 0 1 2 -2 z M4 16 c-1.1 0 -2 -.9 -2 -2 V4 c0 -1.1 .9 -2 2 -2 h10 c1.1 0 2 .9 2 2";
    public const string NewChat = "M12 3 H5 a2 2 0 0 0 -2 2 v14 a2 2 0 0 0 2 2 h14 a2 2 0 0 0 2 -2 v-7 M18.38 2.63 a1 1 0 0 1 3 3 l-9.01 9.01 a2 2 0 0 1 -.85 .51 l-2.87 .84 a.5 .5 0 0 1 -.62 -.62 l.84 -2.87 a2 2 0 0 1 .51 -.85 z";
    public const string Settings = "M12.22 2 h-.44 a2 2 0 0 0 -2 2 v.18 a2 2 0 0 1 -1 1.73 l-.43 .25 a2 2 0 0 1 -2 0 l-.15 -.08 a2 2 0 0 0 -2.73 .73 l-.22 .38 a2 2 0 0 0 .73 2.73 l.15 .1 a2 2 0 0 1 1 1.72 v.51 a2 2 0 0 1 -1 1.74 l-.15 .09 a2 2 0 0 0 -.73 2.73 l.22 .38 a2 2 0 0 0 2.73 .73 l.15 -.08 a2 2 0 0 1 2 0 l.43 .25 a2 2 0 0 1 1 1.73 V20 a2 2 0 0 0 2 2 h.44 a2 2 0 0 0 2 -2 v-.18 a2 2 0 0 1 1 -1.73 l.43 -.25 a2 2 0 0 1 2 0 l.15 .08 a2 2 0 0 0 2.73 -.73 l.22 -.39 a2 2 0 0 0 -.73 -2.73 l-.15 -.08 a2 2 0 0 1 -1 -1.74 v-.5 a2 2 0 0 1 1 -1.74 l.15 -.09 a2 2 0 0 0 .73 -2.73 l-.22 -.38 a2 2 0 0 0 -2.73 -.73 l-.15 .08 a2 2 0 0 1 -2 0 l-.43 -.25 a2 2 0 0 1 -1 -1.73 V4 a2 2 0 0 0 -2 -2 z M15 12 a3 3 0 1 1 -6 0 a3 3 0 1 1 6 0 z";
    public const string Eye = "M2.06 12.35 a1 1 0 0 1 0 -.7 a10.75 10.75 0 0 1 19.88 0 a1 1 0 0 1 0 .7 a10.75 10.75 0 0 1 -19.88 0 z M15 12 a3 3 0 1 1 -6 0 a3 3 0 1 1 6 0 z";
    public const string EyeOff = Eye + " M3 3 L21 21";
    public const string Check = "M20 6 L9 17 L4 12";
    public const string Key = "M12 16 a4 4 0 1 1 -8 0 a4 4 0 1 1 8 0 z M10.8 13.2 L21 3 M17 7 l3 3 M14.5 9.5 l2 2";
    public const string Alert = "M22 12 a10 10 0 1 1 -20 0 a10 10 0 1 1 20 0 z M12 8 v4 M12 16 h.01";
    public const string Retry = "M3 12 a9 9 0 1 0 9 -9 a9.75 9.75 0 0 0 -6.74 2.74 L3 8 M3 3 v5 h5";
    public const string Paste = "M9 2 h6 a1 1 0 0 1 1 1 v2 a1 1 0 0 1 -1 1 H9 a1 1 0 0 1 -1 -1 V3 a1 1 0 0 1 1 -1 z M16 4 h2 a2 2 0 0 1 2 2 v14 a2 2 0 0 1 -2 2 H6 a2 2 0 0 1 -2 -2 V6 a2 2 0 0 1 2 -2 h2 M12 11 v6 M9 14 l3 3 l3 -3";

    // ---- Rendering ---------------------------------------------------------------------------
    private static readonly Dictionary<string, Geometry> Cache = new();

    public static bool IsFilled(string data) => data.StartsWith("F1", StringComparison.Ordinal);

    public static Geometry? Parse(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        if (Cache.TryGetValue(data, out var g)) return g;
        try { g = Geometry.Parse(data); g.Freeze(); }
        catch { g = Geometry.Empty; }
        Cache[data] = g;
        return g;
    }

    /// <summary>Draws an icon centred on <paramref name="center"/> at <paramref name="size"/> DIPs.</summary>
    public static void Draw(DrawingContext dc, string? data, Point center, double size, Brush brush)
    {
        var g = Parse(data);
        if (g == null || g.IsEmpty()) return;
        double s = size / 24.0;
        var pen = new Pen(brush, Theme.IconStroke / s)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };
        dc.PushTransform(new MatrixTransform(s, 0, 0, s, center.X - size / 2, center.Y - size / 2));
        dc.DrawGeometry(IsFilled(data!) ? brush : null, pen, g);
        dc.Pop();
    }
}
