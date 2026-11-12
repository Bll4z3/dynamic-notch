using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// The official Claude logo, loaded from <c>Assets/claude-logo.svg</c> (or <c>.png</c>) next to the app.
/// Nothing is drawn or approximated here: the SVG's own path data (or the PNG's pixels) is rendered.
/// A single-colour mark is tinted with <see cref="Foreground"/> (white). When the file is missing the
/// app's sparkle icon is shown instead.
/// </summary>
public sealed class ClaudeLogo : FrameworkElement
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ClaudeLogo),
        new FrameworkPropertyMetadata(Theme.IconSmall, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(ClaudeLogo), new FrameworkPropertyMetadata(Theme.PrimaryText,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public ClaudeLogo()
    {
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var asset = LogoAsset.Instance;
        double s = Size;
        var box = new Rect(0, 0, s, s);
        if (asset.Shapes is { Count: > 0 } shapes)
        {
            // Fit the SVG viewBox into the icon box, keeping its aspect.
            var vb = asset.ViewBox;
            double k = Math.Min(s / vb.Width, s / vb.Height);
            dc.PushTransform(new MatrixTransform(k, 0, 0, k,
                (s - vb.Width * k) / 2 - vb.X * k, (s - vb.Height * k) / 2 - vb.Y * k));
            foreach (var (geometry, fill) in shapes)
                dc.DrawGeometry(asset.SingleColour || fill == null ? Foreground : fill, null, geometry);
            dc.Pop();
        }
        else if (asset.Bitmap is { } bmp)
        {
            double k = Math.Min(s / bmp.Width, s / bmp.Height);
            var r = new Rect((s - bmp.Width * k) / 2, (s - bmp.Height * k) / 2, bmp.Width * k, bmp.Height * k);
            if (asset.SingleColour)
            {
                // Use the logo's alpha as a mask over a white fill (tint).
                dc.PushOpacityMask(new ImageBrush(bmp) { Viewport = r, ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill });
                dc.DrawRectangle(Foreground, null, r);
                dc.Pop();
            }
            else dc.DrawImage(bmp, r);
        }
        else
        {
            Icons.Draw(dc, Icons.SparkleFilled, new Point(s / 2, s / 2), s, Foreground);
        }
    }
}

/// <summary>Loads and caches Assets/claude-logo.svg or .png once.</summary>
public sealed class LogoAsset
{
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Assets");
    public static readonly string[] FileNames = { "claude-logo.svg", "claude-logo.png" };

    private static LogoAsset? _instance;
    public static LogoAsset Instance => _instance ??= Load();

    public List<(Geometry geometry, Brush? fill)>? Shapes { get; private init; }
    public Rect ViewBox { get; private init; }
    public BitmapSource? Bitmap { get; private init; }
    public bool SingleColour { get; private init; }
    public string? LoadedFrom { get; private init; }
    public bool IsAvailable => Shapes is { Count: > 0 } || Bitmap != null;

    private static LogoAsset Load()
    {
        foreach (var name in FileNames)
        {
            var path = Path.Combine(Folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                return name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? LoadSvg(path) : LoadPng(path);
            }
            catch { /* unreadable file: try the next one, then fall back to the sparkle */ }
        }
        return new LogoAsset();
    }

    // ---- SVG: <path d>, <circle>, <ellipse>, <rect>, <polygon> elements and the viewBox ------------
    private static LogoAsset LoadSvg(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root!;
        Rect viewBox = ParseViewBox((string?)root.Attribute("viewBox"))
                       ?? new Rect(0, 0, Num((string?)root.Attribute("width"), 24), Num((string?)root.Attribute("height"), 24));

        var shapes = new List<(Geometry, Brush?)>();
        var colours = new HashSet<string>();
        foreach (var el in root.Descendants())
        {
            Geometry? g = el.Name.LocalName switch
            {
                "path" => (string?)el.Attribute("d") is { } d ? Geometry.Parse(NormalizePath(d)) : null,
                "circle" => new EllipseGeometry(new Point(Num(el, "cx"), Num(el, "cy")), Num(el, "r"), Num(el, "r")),
                "ellipse" => new EllipseGeometry(new Point(Num(el, "cx"), Num(el, "cy")), Num(el, "rx"), Num(el, "ry")),
                "rect" => new RectangleGeometry(new Rect(Num(el, "x"), Num(el, "y"), Num(el, "width"), Num(el, "height")), Num(el, "rx"), Num(el, "ry")),
                "polygon" => (string?)el.Attribute("points") is { } pts ? Geometry.Parse("M" + NormalizePath(pts) + "Z") : null,
                _ => null,
            };
            if (g == null) continue;
            if ((string?)el.Attribute("transform") is { } t && ParseTransform(t) is { } m) g.Transform = new MatrixTransform(m);
            if (g.CanFreeze) g.Freeze();

            string? fillText = FillOf(el);
            if (fillText == "none") continue;
            Brush? fill = null;
            if (fillText != null)
            {
                try { fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fillText)); fill.Freeze(); colours.Add(fillText.ToLowerInvariant()); }
                catch { /* gradients / currentColor: tint */ }
            }
            shapes.Add((g, fill));
        }
        return new LogoAsset { Shapes = shapes, ViewBox = viewBox, SingleColour = colours.Count <= 1, LoadedFrom = path };
    }

    private static string? FillOf(XElement el)
    {
        for (var e = el; e != null; e = e.Parent)
        {
            if ((string?)e.Attribute("fill") is { } f) return f.Trim();
            if ((string?)e.Attribute("style") is { } st && Regex.Match(st, @"fill\s*:\s*([^;]+)") is { Success: true } m) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    private static Rect? ParseViewBox(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var n = Regex.Matches(v, NumberPattern).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
        return n.Length == 4 && n[2] > 0 && n[3] > 0 ? new Rect(n[0], n[1], n[2], n[3]) : null;
    }

    private static Matrix? ParseTransform(string t)
    {
        var m = Matrix.Identity;
        foreach (Match op in Regex.Matches(t, @"(matrix|translate|scale|rotate)\s*\(([^)]*)\)"))
        {
            var a = Regex.Matches(op.Groups[2].Value, NumberPattern).Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture)).ToArray();
            var step = Matrix.Identity;
            switch (op.Groups[1].Value)
            {
                case "matrix" when a.Length == 6: step = new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]); break;
                case "translate" when a.Length >= 1: step.Translate(a[0], a.Length > 1 ? a[1] : 0); break;
                case "scale" when a.Length >= 1: step.Scale(a[0], a.Length > 1 ? a[1] : a[0]); break;
                case "rotate" when a.Length == 1: step.Rotate(a[0]); break;
                case "rotate" when a.Length == 3: step.RotateAt(a[0], a[1], a[2]); break;
            }
            m = step * m;
        }
        return m;
    }

    private const string NumberPattern = @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";

    private static double Num(XElement el, string attr) => Num((string?)el.Attribute(attr), 0);
    private static double Num(string? s, double fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var m = Regex.Match(s, NumberPattern);
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : fallback;
    }

    /// <summary>
    /// Rewrites SVG path data into the explicit form WPF's parser accepts: compact numbers such as
    /// "1.5.5" or "1-2" are split, and arc flags written without separators ("a1 1 0 011 1") are expanded.
    /// </summary>
    private static string NormalizePath(string d)
    {
        var sb = new StringBuilder();
        char cmd = ' ';
        int argIndex = 0;
        int i = 0;
        while (i < d.Length)
        {
            char c = d[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (char.IsLetter(c) && c != 'e' && c != 'E')
            {
                cmd = c;
                argIndex = 0;
                sb.Append(' ').Append(c);
                i++;
                continue;
            }
            // Arc flags (4th and 5th arguments of each 7-argument arc) are single 0/1 characters.
            if ((cmd == 'a' || cmd == 'A') && (argIndex % 7 == 3 || argIndex % 7 == 4) && (c == '0' || c == '1'))
            {
                sb.Append(' ').Append(c);
                argIndex++;
                i++;
                continue;
            }
            var m = Regex.Match(d.Substring(i), "^" + NumberPattern);
            if (!m.Success) { i++; continue; }
            sb.Append(' ').Append(m.Value);
            argIndex++;
            i += m.Length;
        }
        return sb.ToString().Trim();
    }

    // ---- PNG ---------------------------------------------------------------------------------------
    private static LogoAsset LoadPng(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(path);
        img.EndInit();
        img.Freeze();
        return new LogoAsset { Bitmap = img, SingleColour = IsSingleColour(img), LoadedFrom = path };
    }

    /// <summary>True when every visible pixel is (roughly) the same colour, e.g. a one-colour mark.</summary>
    private static bool IsSingleColour(BitmapSource img)
    {
        var bgra = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        int? r0 = null, g0 = null, b0 = null;
        for (int i = 0; i < px.Length; i += 4 * Math.Max(1, (w * h) / 4000))
        {
            if (px[i + 3] < 128) continue; // skip transparent / antialiased edges
            int b = px[i], g = px[i + 1], r = px[i + 2];
            if (r0 == null) { r0 = r; g0 = g; b0 = b; continue; }
            if (Math.Abs(r - r0.Value) + Math.Abs(g - g0!.Value) + Math.Abs(b - b0!.Value) > 60) return false;
        }
        return true;
    }
}
